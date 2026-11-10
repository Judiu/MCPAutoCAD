using System;
using System.IO;
using MCPAutoCAD.GeometryCore.Lisp;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Lisp
{
    /// <summary>
    /// Lee %LOCALAPPDATA%\MCPAutoCAD\lisp_permitidos.json EN CADA LLAMADA (los cambios del usuario valen sin reiniciar
    /// AutoCAD). Ninguna herramienta del puente escribe este archivo: es la única autorización de cargar_lisp y
    /// ejecutar_comando. No va en lps.json porque el instalador sobrescribe ese archivo en cada actualización.
    /// </summary>
    internal static class LispPinStore
    {
        private const long MaxBytes = 1024 * 1024;

        /// <summary>%LOCALAPPDATA%\MCPAutoCAD\lisp: lo que escribe crear_lisp. NO debe declararse de confianza en AutoCAD.</summary>
        public static string AgentFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPAutoCAD", LispRules.AgentFolderName);

        /// <summary>%LOCALAPPDATA%\MCPAutoCAD\lisp\cargados: copia exacta, por hash, de lo aprobado en el diálogo.</summary>
        public static string SnapshotFolder => Path.Combine(AgentFolder, LispRules.SnapshotFolderName);

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPAutoCAD", LispRules.PinFileName);

        /// <summary>Contenido válido (vacío si el archivo no existe o no se puede leer) y si existe.</summary>
        public static (PinFile pins, bool exists) Read()
        {
            string path = FilePath;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return (new PinFile(), false);
                }

                if (info.Length > MaxBytes)
                {
                    var big = new PinFile();
                    big.Errores.Add("el archivo supera 1 MB; no se lee");
                    return (big, true);
                }

                return (LispRules.ParsePins(File.ReadAllText(path)), true);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("No se pudo leer " + path + " (" + ex.GetType().Name + ": " + ex.Message + ")");
                var failed = new PinFile();
                failed.Errores.Add("no se pudo leer: " + ex.GetType().Name);
                return (failed, true);
            }
        }
    }
}
