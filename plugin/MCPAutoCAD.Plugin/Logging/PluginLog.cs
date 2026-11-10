using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MCPAutoCAD.Plugin.Logging
{
    /// <summary>
    /// Log de texto en %LOCALAPPDATA%\MCPAutoCAD\plugin.log con rotación simple a 5 MB
    /// (plugin.log → plugin.log.1). Nunca escribe en stdout ni abre ventanas, y nunca lanza excepciones.
    /// </summary>
    internal static class PluginLog
    {
        private const long MaxBytes = 5L * 1024 * 1024;
        private static readonly object Sync = new object();
        private static string? _path;

        public static string? FilePath => _path;

        /// <summary>Prepara la carpeta del log. Si falla, el log queda deshabilitado en silencio.</summary>
        public static void Open()
        {
            try
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(baseDir, "MCPAutoCAD");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "plugin.log");
            }
            catch
            {
                _path = null;
            }
        }

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception? ex = null)
        {
            string text = ex == null ? message : message + " | " + ex.GetType().FullName + ": " + ex.Message
                + (ex.StackTrace == null ? "" : Environment.NewLine + ex.StackTrace);
            Write("ERROR", text);
        }

        private static void Write(string level, string message)
        {
            string? path = _path;
            if (path == null)
            {
                return;
            }

            try
            {
                string line = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)
                    + " [" + level + "] " + message + Environment.NewLine;
                lock (Sync)
                {
                    RotateIfNeeded(path);
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // El log nunca debe afectar a AutoCAD.
            }
        }

        private static void RotateIfNeeded(string path)
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= MaxBytes)
            {
                string old = path + ".1";
                File.Copy(path, old, overwrite: true);
                File.Delete(path);
            }
        }
    }
}
