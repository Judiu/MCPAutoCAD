using System;
using System.IO;
using System.Text.Json;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Copia en disco del tools/manifest (protocol.md §4.1): %LOCALAPPDATA%\MCPAutoCAD\manifest.json.
    /// El servidor MCP la publica cuando AutoCAD está cerrado, para que el cliente vea las herramientas
    /// desde el arranque. Se reescribe en cada carga del plugin, así coincide con la versión instalada.
    /// Nunca lanza: un fallo solo se registra en el log.
    /// </summary>
    internal static class ManifestFile
    {
        public const string FileName = "manifest.json";

        public static void Write(ToolRegistry registry)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPAutoCAD");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, FileName);

                // El manifest no depende de la instancia: pid 0 indica copia en disco.
                object manifest = new ManifestHandler(registry.Tools, 0).Execute(default, null!);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonRpcResponse.SerializerOptions);

                // Escritura atómica: varias instancias de AutoCAD pueden arrancar a la vez.
                string tmp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(tmp, json);
                File.Move(tmp, path, overwrite: true);
                PluginLog.Info("Manifest escrito en " + path);
            }
            catch (Exception ex)
            {
                PluginLog.Error("No se pudo escribir el manifest en disco", ex);
            }
        }
    }
}
