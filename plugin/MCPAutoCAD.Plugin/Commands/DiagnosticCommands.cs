using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MCPAutoCAD.Plugin.Commands
{
    /// <summary>Comandos de diagnóstico del plugin.</summary>
    public sealed class DiagnosticCommands
    {
        /// <summary>MCPESTADO: escribe en la línea de comandos el estado del puente MCP.</summary>
        [CommandMethod("MCPESTADO", CommandFlags.Modal)]
        public void McpEstado()
        {
            try
            {
                Document? doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    return;
                }

                Editor ed = doc.Editor;
                PluginRuntime? rt = PluginApplication.Runtime;
                if (rt == null)
                {
                    ed.WriteMessage("\nMCPAutoCAD: el plugin no se inicializó correctamente. Revise el log: "
                        + (PluginLog.FilePath ?? "(sin log)") + "\n");
                    return;
                }

                ed.WriteMessage("\n--- MCPAutoCAD " + PluginInfo.PluginVersion + " (protocolo " + PluginInfo.ProtocolVersion + ") ---");
                ed.WriteMessage("\nPipe:                   " + rt.Server.FullPipeName);
                ed.WriteMessage("\nConexiones activas:     " + rt.Stats.ActiveConnections);
                ed.WriteMessage("\nPeticiones atendidas:   " + rt.Stats.RequestsServed);
                ed.WriteMessage("\nÚltima petición:        " + rt.Stats.LastRequest);
                ed.WriteMessage("\nUseCommandContextUndo:  " + (UndoGroup.UseCommandContextUndo ? "true" : "false"));
                ed.WriteMessage("\nDespacho:               " + rt.Invoker.Mechanism);
                ed.WriteMessage("\nLog:                    " + (PluginLog.FilePath ?? "(sin log)") + "\n");
            }
            catch (System.Exception ex)
            {
                PluginLog.Error("Error en MCPESTADO", ex);
            }
        }
    }
}
