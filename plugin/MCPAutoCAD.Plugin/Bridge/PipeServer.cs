using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Bridge
{
    /// <summary>
    /// Servidor del named pipe (protocol.md §1). Un hilo dedicado crea instancias del pipe (máximo 4 activas) y
    /// espera conexiones; cada conexión se atiende en su propia Task con framing [uint32 LE][JSON UTF-8].
    /// Este código NUNCA toca la API de AutoCAD: todo pasa por <see cref="RequestDispatcher"/>.
    /// </summary>
    internal sealed class PipeServer : IDisposable
    {
        private const int BufferSize = 65536;

        private readonly RequestDispatcher _dispatcher;
        private readonly PluginStats _stats;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly SemaphoreSlim _slots = new SemaphoreSlim(PluginInfo.MaxPipeInstances, PluginInfo.MaxPipeInstances);
        private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _streams = new ConcurrentDictionary<NamedPipeServerStream, byte>();
        private Thread? _acceptThread;

        public PipeServer(string pipeName, RequestDispatcher dispatcher, PluginStats stats)
        {
            PipeName = pipeName;
            _dispatcher = dispatcher;
            _stats = stats;
        }

        /// <summary>Nombre del pipe sin el prefijo \\.\pipe\.</summary>
        public string PipeName { get; }

        public string FullPipeName => @"\\.\pipe\" + PipeName;

        public void Start()
        {
            if (_acceptThread != null)
            {
                return;
            }

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "MCPAutoCAD pipe " + PipeName,
            };
            _acceptThread.Start();
            PluginLog.Info("Servidor de pipe iniciado: " + FullPipeName);
        }

        public void Stop()
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _cts.Cancel();
            foreach (NamedPipeServerStream s in _streams.Keys)
            {
                SafeDispose(s);
            }

            _acceptThread?.Join(2000);
            PluginLog.Info("Servidor de pipe detenido: " + FullPipeName);
        }

        public void Dispose()
        {
            // El CancellationTokenSource no se libera: las tareas de conexión pueden seguir consultando su token.
            Stop();
        }

        /// <summary>ACL con UNA sola entrada: el SID del usuario actual con FullControl (protocol.md §1.1).</summary>
        private static PipeSecurity BuildSecurity()
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("No se pudo obtener el SID del usuario actual.");
            var security = new PipeSecurity();
            // Sin herencia ni reglas heredadas: solo la regla explícita del usuario.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        private NamedPipeServerStream CreateInstance() =>
            NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                PluginInfo.MaxPipeInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                BufferSize,
                BufferSize,
                BuildSecurity());

        private void AcceptLoop()
        {
            CancellationToken token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _slots.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                NamedPipeServerStream? stream = null;
                try
                {
                    stream = CreateInstance();
                    _streams.TryAdd(stream, 0);
                    stream.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    if (stream != null)
                    {
                        _streams.TryRemove(stream, out _);
                        SafeDispose(stream);
                    }

                    _slots.Release();
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    PluginLog.Error("Error creando o esperando una instancia del pipe; se reintenta en 1 s", ex);
                    token.WaitHandle.WaitOne(1000);
                    continue;
                }

                NamedPipeServerStream connected = stream;
                Task.Factory.StartNew(
                    () => ServeConnection(connected, token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }

        private void ServeConnection(NamedPipeServerStream stream, CancellationToken token)
        {
            _stats.ConnectionOpened();
            try
            {
                var header = new byte[4];
                while (!token.IsCancellationRequested && stream.IsConnected)
                {
                    ReadResult r = ReadExactly(stream, header, token);
                    if (r == ReadResult.CleanEof)
                    {
                        break;
                    }

                    if (r == ReadResult.Truncated)
                    {
                        PluginLog.Warn("Framing roto (cabecera incompleta); se cierra la conexión.");
                        break;
                    }

                    uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
                    if (length > PluginInfo.MaxMessageBytes)
                    {
                        PluginLog.Warn("Mensaje de " + length + " bytes supera 16 MiB; se cierra la conexión.");
                        break;
                    }

                    var body = new byte[length];
                    if (length > 0 && ReadExactly(stream, body, token) != ReadResult.Ok)
                    {
                        PluginLog.Warn("Framing roto (cuerpo incompleto); se cierra la conexión.");
                        break;
                    }

                    byte[] response = _dispatcher.Handle(body);
                    if (response.Length > PluginInfo.MaxMessageBytes)
                    {
                        PluginLog.Error("La respuesta supera 16 MiB; se cierra la conexión.");
                        break;
                    }

                    var frame = new byte[4 + response.Length];
                    BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)response.Length);
                    Buffer.BlockCopy(response, 0, frame, 4, response.Length);
                    stream.WriteAsync(frame, 0, frame.Length, token).GetAwaiter().GetResult();
                    stream.Flush();
                }
            }
            catch (OperationCanceledException)
            {
                // Parada del servidor.
            }
            catch (IOException ex)
            {
                PluginLog.Info("Conexión cerrada por el cliente: " + ex.Message);
            }
            catch (ObjectDisposedException)
            {
                // Parada del servidor.
            }
            catch (Exception ex)
            {
                PluginLog.Error("Error atendiendo una conexión del pipe", ex);
            }
            finally
            {
                _streams.TryRemove(stream, out _);
                try
                {
                    if (stream.IsConnected)
                    {
                        stream.Disconnect();
                    }
                }
                catch
                {
                    // Ignorar: se libera a continuación.
                }

                SafeDispose(stream);
                _stats.ConnectionClosed();
                try
                {
                    _slots.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Servidor ya liberado.
                }
            }
        }

        private enum ReadResult
        {
            Ok,
            CleanEof,
            Truncated,
        }

        private static ReadResult ReadExactly(Stream stream, byte[] buffer, CancellationToken token)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int n = stream.ReadAsync(buffer, offset, buffer.Length - offset, token).GetAwaiter().GetResult();
                if (n == 0)
                {
                    return offset == 0 ? ReadResult.CleanEof : ReadResult.Truncated;
                }

                offset += n;
            }

            return ReadResult.Ok;
        }

        private static void SafeDispose(IDisposable d)
        {
            try
            {
                d.Dispose();
            }
            catch
            {
                // Ignorar errores al cerrar.
            }
        }
    }
}
