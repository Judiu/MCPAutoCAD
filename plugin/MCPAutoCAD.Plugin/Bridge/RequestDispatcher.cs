using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.Plugin.Handlers;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using ToolContext = MCPAutoCAD.Plugin.Handlers.ExecutionContext;

namespace MCPAutoCAD.Plugin.Bridge
{
    /// <summary>
    /// Recibe un mensaje (bytes JSON) desde el hilo del pipe y devuelve la respuesta (bytes JSON).
    /// - ping y tools/manifest: se responden en el hilo del pipe, sin tocar AutoCAD.
    /// - resto: se encola al hilo principal; si no lo TOMA en PluginInfo.QueueTakeTimeoutMs (1500 ms) → BUSY "cola" y el trabajo queda cancelado
    ///   (si el hilo principal lo toma después, lo descarta sin ejecutarlo). Una vez tomado se espera hasta 10 s;
    ///   si se excede → INTERNAL "timeout" (la operación no se interrumpe).
    /// Thread-safe: lo usan hasta 4 conexiones a la vez; todas pasan por la misma cola del hilo principal.
    /// </summary>
    internal sealed class RequestDispatcher
    {
        private readonly Dictionary<string, IToolHandler> _handlers;
        private readonly MainThreadInvoker _invoker;
        private readonly BusyState _busy;
        private readonly PluginStats _stats;
        private readonly int _pid;

        // Solo se lee/escribe en el hilo principal, salvo el reset desde la continuación (de ahí volatile).
        private volatile bool _deferredWriteInProgress;

        // true mientras un trabajo está dentro de Execute (incluido el tiempo en que bombea mensajes y puede entrar otro).
        // Se lee y escribe en ExecuteJob, que corre en el hilo principal; volatile porque la continuación de una escritura
        // diferida también lo pone a false y no hay garantía de que llegue en el mismo hilo.
        private volatile bool _executing;

        /// <summary>
        /// Trabajos que pueden entrar ANIDADOS mientras otro trabajo bombea mensajes: cancelar_comando (la parada de emergencia
        /// debe poder ejecutarse durante un diálogo) y estado degradado (solo lee). Ningún otro handler.
        /// </summary>
        private static bool EsExenta(IToolHandler h) => h.RunsDuringCommand || EsDegradable(h);

        /// <summary>Lectura degradada con comando activo: solo estado (marca IDegradableDuringCommand, sin escritura).</summary>
        private static bool EsDegradable(IToolHandler h) =>
            h is IDegradableDuringCommand && !h.IsWrite && string.Equals(h.Name, "estado", System.StringComparison.Ordinal);

        public RequestDispatcher(ToolRegistry registry, MainThreadInvoker invoker, BusyState busy, PluginStats stats, int pid)
        {
            _handlers = registry.ByMethod;
            foreach (IToolHandler h in _handlers.Values)
            {
                if (h is IDegradableDuringCommand && !EsDegradable(h))
                {
                    PluginLog.Warn("'" + h.Name + "' implementa IDegradableDuringCommand pero no es estado de solo lectura: se ignora la marca (BUSY con comando activo).");
                }
            }

            _invoker = invoker;
            _busy = busy;
            _stats = stats;
            _pid = pid;
        }

        /// <summary>Atiende un mensaje completo. Nunca lanza: cualquier fallo se convierte en respuesta de error.</summary>
        public byte[] Handle(byte[] requestBytes)
        {
            var sw = Stopwatch.StartNew();
            JsonRpcRequest? request = null;
            string outcome = "ok";
            try
            {
                request = JsonRpcRequest.Parse(requestBytes);
                object result = Dispatch(request);
                return JsonRpcResponse.Success(request.Id, result);
            }
            catch (JsonRpcParseException ex)
            {
                outcome = ErrorCodes.ParseError;
                return JsonRpcResponse.Error(ex.Id, ToolException.ParseError(ex.Message));
            }
            catch (ToolException ex)
            {
                outcome = ex.Code;
                return SafeError(request, ex);
            }
            catch (Exception ex)
            {
                outcome = ErrorCodes.Internal;
                PluginLog.Error("Excepción no prevista atendiendo '" + (request?.Method ?? "?") + "'", ex);
                return SafeError(request, ToolException.FromUnexpected(ex));
            }
            finally
            {
                sw.Stop();
                string method = request?.Method ?? "?";
                string id = request?.Id.GetRawText() ?? "null";
                _stats.RecordRequest(method, id, outcome, sw.ElapsedMilliseconds);
                PluginLog.Info(string.Create(
                    CultureInfo.InvariantCulture,
                    $"peticion metodo={method} id={id} duracion_ms={sw.ElapsedMilliseconds} resultado={outcome}"));
            }
        }

        private static byte[] SafeError(JsonRpcRequest? request, ToolException ex)
        {
            try
            {
                return JsonRpcResponse.Error(request?.Id, ex);
            }
            catch (Exception serializeEx)
            {
                PluginLog.Error("No se pudo serializar el error", serializeEx);
                return JsonRpcResponse.Error(request?.Id, new ToolException(ErrorCodes.Internal, "Error interno del plugin de AutoCAD."));
            }
        }

        private object Dispatch(JsonRpcRequest request)
        {
            if (!_handlers.TryGetValue(request.Method, out IToolHandler? handler))
            {
                throw ToolException.MethodNotFound(request.Method);
            }

            // Fallo 25: documento_esperado lo gestiona el despachador; el handler recibe los parámetros sin él.
            JsonElement parametros = DocumentoEsperado.Strip(handler, request.Params, out string? esperado);

            // Validación contra el schema, en el hilo del pipe y ANTES de encolar (protocol.md §4).
            handler.ValidateParams(parametros);

            if (handler.RunsOnPipeThread)
            {
                return handler.Execute(parametros, new ToolContext(_pid, null, _busy, 0));
            }

            return RunOnMainThread(handler, request, parametros, esperado);
        }

        // ------------------------------------------------------------------ hilo del pipe

        private object RunOnMainThread(IToolHandler handler, JsonRpcRequest request, JsonElement parametros, string? esperado)
        {
            var job = new MainThreadJob(handler, parametros) { DocumentoEsperado = esperado };
            try
            {
                _invoker.Post(() => ExecuteJob(job));
            }
            catch (Exception ex)
            {
                throw ToolException.FromUnexpected(ex);
            }

            if (!job.WaitTaken(PluginInfo.QueueTakeTimeoutMs))
            {
                if (job.TryCancel())
                {
                    PluginLog.Warn("El hilo principal no tomó '" + request.Method + "' en " + PluginInfo.QueueTakeTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms: BUSY cola; trabajo cancelado.");
                    throw ToolException.BusyQueue();
                }

                // Se tomó justo en el límite: se sigue esperando su resultado.
            }

            Task<object> completion = job.Completion;
            int espera = job.Handler.MainThreadTimeoutMs;
            if (!((IAsyncResult)completion).AsyncWaitHandle.WaitOne(espera))
            {
                PluginLog.Warn("'" + request.Method + "' superó " + (espera / 1000).ToString(CultureInfo.InvariantCulture) + " s en el hilo principal; se responde INTERNAL timeout.");
                throw ToolException.Timeout(espera / 1000, job.Handler.TimeoutHint(job.Params));
            }

            return completion.GetAwaiter().GetResult();
        }

        // ------------------------------------------------------------------ hilo principal

        /// <summary>Corre en el hilo principal. Jamás deja escapar una excepción a AutoCAD.</summary>
        private void ExecuteJob(MainThreadJob job)
        {
            try
            {
                if (!job.TryTake())
                {
                    // Cancelado por timeout de cola: NUNCA ejecutar escrituras tardías.
                    PluginLog.Warn("Trabajo '" + job.Handler.Name + "' descartado: se tomó después del timeout de cola.");
                    return;
                }

                // Reentrada: DocumentCollection.Open, MdiActiveDocument= y el diálogo LISP (ShowModalDialog) bombean mensajes, y
                // la cola del hilo principal puede ejecutar otra petición ANIDADA dentro del trabajo en curso. Mientras hay un
                // trabajo ejecutándose (_executing) o una escritura diferida (_deferredWriteInProgress), todo trabajo → BUSY cola,
                // salvo los de EsExenta (ver ahí por qué).
                if (_deferredWriteInProgress || (_executing && !EsExenta(job.Handler)))
                {
                    job.Fail(ToolException.BusyQueue());
                    return;
                }

                // Primero el documento: con cero documentos (pantalla de inicio) CMDACTIVE
                // puede lanzar eNoDocument, y estado debe devolver documento: null (§5.2).
                Document? doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    if (job.Handler.RequiresDocument)
                    {
                        job.Fail(ToolException.NoDoc());
                        return;
                    }
                }

                int cmdActive = doc == null ? 0 : ReadCmdActive();
                if (doc != null)
                {
                    // Fallo 26: OPEN residual en la pila de eventos con CMDACTIVE = 0 → se descarta antes de decidir BUSY.
                    _busy.ReconcileStale(cmdActive);
                }

                // Con comando activo solo pasan cancelar_comando (RunsDuringCommand) y los de lectura degradada (estado):
                // estos últimos reciben ctx.CmdActive/ctx.Busy para saber que deben omitir lo que necesita la base de datos.
                if ((cmdActive != 0 || _busy.CommandActive) && !job.Handler.RunsDuringCommand && !EsDegradable(job.Handler))
                {
                    PluginLog.Info("BUSY por comando: CMDACTIVE=" + cmdActive.ToString(CultureInfo.InvariantCulture)
                        + ", eventos=" + (_busy.CurrentCommand ?? "(ninguno)") + ", herramienta=" + job.Handler.Name + ".");
                    job.Fail(ToolException.BusyCommand(_busy.CurrentCommand ?? ""));
                    return;
                }

                // Fallo 25: con documento_esperado, el documento activo debe ser ese; si no, nada se ejecuta.
                if (job.DocumentoEsperado != null && doc != null)
                {
                    try
                    {
                        DocumentoEsperado.Check(AcApp.DocumentManager, doc, job.DocumentoEsperado);
                    }
                    catch (Exception ex)
                    {
                        job.Fail(MapException(ex));
                        return;
                    }
                }

                bool anterior = _executing;
                _executing = true;
                bool diferido = false;
                try
                {
                    // anterior == true: este trabajo entra anidado dentro de otro (ver _executing). Estado lo usa para degradar.
                    object result = job.Handler.Execute(job.Params, new ToolContext(_pid, doc, _busy, cmdActive) { Anidado = anterior });
                    if (result is Task<object> deferred)
                    {
                        // La escritura diferida conserva _executing hasta su continuación (no hay otro trabajo en medio).
                        diferido = true;
                        _deferredWriteInProgress = true;
                        deferred.ContinueWith(
                            t =>
                            {
                                _deferredWriteInProgress = false;
                                _executing = false;
                                if (t.IsCompletedSuccessfully)
                                {
                                    job.Complete(t.Result);
                                }
                                else
                                {
                                    job.Fail(MapException(t.Exception?.GetBaseException() ?? new InvalidOperationException("tarea cancelada")));
                                }
                            },
                            TaskContinuationOptions.ExecuteSynchronously);
                        return;
                    }

                    job.Complete(result);
                }
                finally
                {
                    if (!diferido)
                    {
                        _executing = anterior;
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    job.Fail(MapException(ex));
                }
                catch
                {
                    // Nada puede escapar al hilo principal de AutoCAD.
                }
            }
        }

        /// <summary>Lee CMDACTIVE; si AutoCAD lanza (p. ej. eNoDocument) se registra y se asume 0.</summary>
        private static int ReadCmdActive()
        {
            try
            {
                object? value = AcApp.GetSystemVariable("CMDACTIVE");
                return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("No se pudo leer CMDACTIVE (" + ex.GetType().Name + ": " + ex.Message + "); se asume 0.");
                return 0;
            }
        }

        private static ToolException MapException(Exception ex)
        {
            if (ex is ToolException tool)
            {
                return tool;
            }

            if (ex is AcRuntimeException acad)
            {
                PluginLog.Error("Excepción de AutoCAD (" + acad.ErrorStatus + ")", acad);
            }
            else
            {
                PluginLog.Error("Excepción en el hilo principal", ex);
            }

            return ToolException.FromUnexpected(ex);
        }

        /// <summary>Trabajo encolado al hilo principal, con estados Pendiente → Tomado | Cancelado.</summary>
        private sealed class MainThreadJob
        {
            private const int Pending = 0;
            private const int Taken = 1;
            private const int Cancelled = 2;

            private readonly ManualResetEventSlim _taken = new ManualResetEventSlim(false);
            private readonly TaskCompletionSource<object> _completion =
                new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _state = Pending;

            public MainThreadJob(IToolHandler handler, System.Text.Json.JsonElement parameters)
            {
                Handler = handler;
                Params = parameters;
            }

            public IToolHandler Handler { get; }

            public System.Text.Json.JsonElement Params { get; }

            /// <summary>Fallo 25: documento_esperado de la petición (ya retirado de <see cref="Params"/>), o null.</summary>
            public string? DocumentoEsperado { get; init; }

            public Task<object> Completion => _completion.Task;

            /// <summary>Hilo principal: marca el inicio de ejecución. false si ya estaba cancelado.</summary>
            public bool TryTake()
            {
                bool ok = Interlocked.CompareExchange(ref _state, Taken, Pending) == Pending;
                if (ok)
                {
                    _taken.Set();
                }

                return ok;
            }

            /// <summary>Hilo del pipe: cancela si aún no se tomó.</summary>
            public bool TryCancel() => Interlocked.CompareExchange(ref _state, Cancelled, Pending) == Pending;

            public bool WaitTaken(int timeoutMs) => _taken.Wait(timeoutMs);

            public void Complete(object result) => _completion.TrySetResult(result);

            public void Fail(Exception ex) => _completion.TrySetException(ex);
        }
    }
}
