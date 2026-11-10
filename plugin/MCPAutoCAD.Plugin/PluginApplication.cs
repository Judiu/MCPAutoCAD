using System;
using Autodesk.AutoCAD.Runtime;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers;
using MCPAutoCAD.Plugin.Logging;

[assembly: ExtensionApplication(typeof(MCPAutoCAD.Plugin.PluginApplication))]
[assembly: CommandClass(typeof(MCPAutoCAD.Plugin.Commands.DiagnosticCommands))]

namespace MCPAutoCAD.Plugin
{
    /// <summary>
    /// Punto de entrada del plugin. Initialize() corre en el hilo principal de AutoCAD y NUNCA debe lanzar:
    /// una excepción aquí impide cargar el plugin.
    /// </summary>
    public sealed class PluginApplication : IExtensionApplication
    {
        /// <summary>Estado en ejecución del plugin, o null si no se inicializó (lo consulta MCPESTADO).</summary>
        internal static PluginRuntime? Runtime { get; private set; }

        public void Initialize()
        {
            try
            {
                PluginLog.Open();
                PluginLog.Info("Inicializando MCPAutoCAD " + PluginInfo.PluginVersion + " (protocolo " + PluginInfo.ProtocolVersion + ")");
                Runtime = PluginRuntime.Start();
            }
            catch (System.Exception ex)
            {
                PluginLog.Error("Fallo al inicializar el plugin; el pipe no estará disponible", ex);
            }
        }

        public void Terminate()
        {
            try
            {
                Runtime?.Stop();
                Runtime = null;
                PluginLog.Info("Plugin terminado");
            }
            catch (System.Exception ex)
            {
                PluginLog.Error("Fallo al terminar el plugin", ex);
            }
        }
    }

    /// <summary>Componentes vivos del plugin: invocador del hilo principal, estado ocupado y servidor de pipe.</summary>
    internal sealed class PluginRuntime
    {
        private PluginRuntime(int pid, string userName, MainThreadInvoker invoker, BusyState busy, PluginStats stats, PipeServer server)
        {
            Pid = pid;
            UserName = userName;
            Invoker = invoker;
            Busy = busy;
            Stats = stats;
            Server = server;
        }

        public int Pid { get; }

        public string UserName { get; }

        public MainThreadInvoker Invoker { get; }

        public BusyState Busy { get; }

        public PluginStats Stats { get; }

        public PipeServer Server { get; }

        /// <summary>Arranca todo. Llamar en el hilo principal.</summary>
        public static PluginRuntime Start()
        {
            int pid = Environment.ProcessId;
            string userName = Environment.UserName;

            MainThreadInvoker invoker = MainThreadInvoker.CreateOnMainThread();
            PluginLog.Info("Despacho al hilo principal: " + invoker.Mechanism);

            var busy = new BusyState();
            try
            {
                busy.Subscribe();
            }
            catch (System.Exception ex)
            {
                // Sin eventos se sigue confirmando con CMDACTIVE en el hilo principal.
                PluginLog.Error("No se pudieron suscribir los eventos de comando", ex);
            }

            var stats = new PluginStats();
            var registry = new ToolRegistry(pid);
            ManifestFile.Write(registry);
            var dispatcher = new RequestDispatcher(registry, invoker, busy, stats, pid);
            var server = new PipeServer(PluginInfo.BuildPipeName(userName, pid), dispatcher, stats);
            server.Start();

            return new PluginRuntime(pid, userName, invoker, busy, stats, server);
        }

        /// <summary>Detiene el servidor y desuscribe eventos. Llamar en el hilo principal.</summary>
        public void Stop()
        {
            try
            {
                Server.Dispose();
            }
            catch (System.Exception ex)
            {
                PluginLog.Error("Error al detener el servidor de pipe", ex);
            }

            try
            {
                Busy.Unsubscribe();
            }
            catch (System.Exception ex)
            {
                PluginLog.Error("Error al desuscribir eventos", ex);
            }

            try
            {
                Invoker.Dispose();
            }
            catch (System.Exception ex)
            {
                PluginLog.Error("Error al liberar el invocador del hilo principal", ex);
            }
        }
    }
}
