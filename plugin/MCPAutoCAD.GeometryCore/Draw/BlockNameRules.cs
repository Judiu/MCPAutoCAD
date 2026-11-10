using System;
using System.IO;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Nombre de bloque de insertar_bloque (protocol.md §5D.5): el cliente solo da un NOMBRE, nunca una ruta. El archivo
    /// de la biblioteca se construye siempre como <c>&lt;carpeta&gt;\&lt;nombre&gt;.dwg</c> con un nombre ya validado aquí.
    /// </summary>
    public static class BlockNameRules
    {
        /// <summary>Mensaje exacto (§5D.5, §6) para cualquier intento de ruta o extensión.</summary>
        public const string PathMessage = "nombre: solo se admite el nombre del bloque, sin rutas";

        /// <summary>Mensaje exacto (§5D.5, §6) para nombres anónimos (*U…, *D…, *Model_Space…).</summary>
        public const string AnonymousMessage = "nombre: no se insertan bloques anónimos";

        /// <summary>Extensión de los archivos de la biblioteca.</summary>
        public const string FileExtension = ".dwg";

        private static readonly char[] PathChars = { '\\', '/', ':', '|', '*', '?', '"', '<', '>' };

        private static readonly string[] Extensions = { ".dwg", ".dxf", ".dwt", ".dws" };

        private static readonly string[] DeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>true si el nombre es seguro (sin rutas, extensión ni prefijo anónimo).</summary>
        public static bool IsSafeName(string? nombre) => Validate(nombre) == null;

        /// <summary>
        /// null si el nombre es seguro; si no, el mensaje de error: empieza por '*' → <see cref="AnonymousMessage"/>;
        /// contiene \ / : | * ? " &lt; &gt;, "..", caracteres de control, termina en .dwg (o .dxf/.dwt/.dws), en punto o
        /// espacio, o es un nombre de dispositivo de Windows (CON, NUL, COM1…) → <see cref="PathMessage"/>.
        /// Vacío o null → <see cref="PathMessage"/> (el schema ya exige 1–255 caracteres).
        /// </summary>
        public static string? Validate(string? nombre)
        {
            if (string.IsNullOrEmpty(nombre))
            {
                return PathMessage;
            }

            if (nombre[0] == '*')
            {
                return AnonymousMessage;
            }

            if (nombre.IndexOfAny(PathChars) >= 0 || nombre.Contains("..", StringComparison.Ordinal))
            {
                return PathMessage;
            }

            foreach (char c in nombre)
            {
                if (char.IsControl(c))
                {
                    return PathMessage;
                }
            }

            foreach (string ext in Extensions)
            {
                if (nombre.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    return PathMessage;
                }
            }

            char last = nombre[nombre.Length - 1];
            if (last == '.' || char.IsWhiteSpace(last) || char.IsWhiteSpace(nombre[0]))
            {
                return PathMessage;
            }

            string stem = nombre;
            int dot = stem.IndexOf('.');
            if (dot >= 0)
            {
                stem = stem.Substring(0, dot);
            }

            foreach (string dev in DeviceNames)
            {
                if (string.Equals(stem.TrimEnd(), dev, StringComparison.OrdinalIgnoreCase))
                {
                    return PathMessage;
                }
            }

            return null;
        }

        /// <summary>
        /// Ruta del archivo de la biblioteca: <c>Path.Combine(carpeta, nombre + ".dwg")</c>. Nombre inseguro →
        /// ArgumentException con el mensaje de <see cref="Validate"/>. Comprobación adicional: el archivo resultante
        /// debe quedar directamente dentro de <paramref name="carpeta"/> (nunca fuera ni en una subcarpeta).
        /// </summary>
        public static string LibraryFile(string carpeta, string nombre)
        {
            if (string.IsNullOrWhiteSpace(carpeta))
            {
                throw new ArgumentException("carpeta: vacía");
            }

            string? error = Validate(nombre);
            if (error != null)
            {
                throw new ArgumentException(error);
            }

            string file = Path.Combine(carpeta, nombre + FileExtension);
            string fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta));
            string? parent = Path.GetDirectoryName(Path.GetFullPath(file));
            if (parent == null || !string.Equals(Path.TrimEndingDirectorySeparator(parent), fullDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(PathMessage);
            }

            return file;
        }
    }
}
