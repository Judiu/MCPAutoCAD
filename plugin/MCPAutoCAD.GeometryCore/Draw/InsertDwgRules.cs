using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Reglas puras de <c>insertar_dwg</c> (fallo 56: unificar archivos DWG nativos conservando bloques, capas y sus
    /// propiedades de color y transparencia). Sin AutoCAD.
    /// </summary>
    public static class InsertDwgRules
    {
        public const long MaxFileBytes = ImportGeometryRules.MaxFileBytes;
        public const int MaxEntities = ImportGeometryRules.MaxEntities;
        public const int MaxEffectiveEntities = ImportGeometryRules.MaxEffectiveEntities;
        public const int BatchSize = ImportGeometryRules.BatchSize;
        public const int MaxFilterLayers = ImportGeometryRules.MaxFilterLayers;
        public const int MaxPrefixLength = 80;
        public const int MaxSampleHandles = 50;

        public const string ModoEntidades = "entidades";
        public const string ModoBloque = "bloque";

        public static readonly string[] ValidModes = { ModoEntidades, ModoBloque };

        private const string WildcardChars = "*?<>\"|";

        /// <summary>Valida la ruta de entrada: unidad local o asignada, sin UNC, '..' ni comodines, extensión .dwg.</summary>
        public static string? ValidatePath(string ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                return "ruta: no puede estar vacía";
            }

            if (ruta.StartsWith("\\\\", StringComparison.Ordinal) || ruta.StartsWith("//", StringComparison.Ordinal))
            {
                return "ruta: no se admiten rutas UNC (\\\\servidor\\...): use una unidad (local o asignada)";
            }

            if (!Regex.IsMatch(ruta, @"^[A-Za-z]:[\\/]"))
            {
                return "ruta: debe ser absoluta en una unidad (por ejemplo C:\\... o Z:\\...)";
            }

            if (ruta.IndexOf(':', 2) >= 0)
            {
                return "ruta: ':' solo puede aparecer como unidad (no se admiten flujos de datos NTFS)";
            }

            if (ruta.Any(c => char.IsControl(c)) || ruta.IndexOfAny(WildcardChars.ToCharArray()) >= 0)
            {
                return "ruta: no se admiten comodines ni caracteres de control";
            }

            if (ruta.Split('\\', '/').Any(seg => seg == ".."))
            {
                return "ruta: no se admiten '..'";
            }

            if (!ruta.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                return "ruta: solo se admiten archivos .dwg";
            }

            return null;
        }

        /// <summary>Valida el tamaño máximo del archivo DWG (200 MB).</summary>
        public static string? ValidateSize(long bytes) => ImportGeometryRules.ValidateSize(bytes);

        /// <summary>Valida un prefijo opcional para capas o definiciones de bloque.</summary>
        public static string? ValidatePrefix(string? prefix, string fieldName)
        {
            if (prefix == null)
            {
                return null;
            }

            if (prefix.Length == 0 || prefix.Length > MaxPrefixLength)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: debe tener entre 1 y {1} caracteres",
                    fieldName, MaxPrefixLength);
            }

            char? bad = ImportGeometryRules.CaracterInvalidoEnCapa(prefix);
            if (bad.HasValue)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: contiene el carácter no válido '{1}'",
                    fieldName, bad.Value);
            }

            return null;
        }

        /// <summary>Normaliza listas de patrones de capa (filtrar_capas / excluir_capas) para comodines * y ?.</summary>
        public static List<string>? NormalizeLayerPatterns(IReadOnlyList<string>? patrones, string fieldName, out string? error)
        {
            error = null;
            if (patrones == null)
            {
                return null;
            }

            if (patrones.Count == 0 || patrones.Count > MaxFilterLayers)
            {
                error = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: debe tener entre 1 y {1} elementos",
                    fieldName, MaxFilterLayers);
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>(patrones.Count);
            foreach (string pat in patrones)
            {
                if (string.IsNullOrWhiteSpace(pat))
                {
                    error = fieldName + ": hay un patrón vacío";
                    return null;
                }

                string trimmed = pat.Trim();
                if (seen.Add(trimmed))
                {
                    list.Add(trimmed);
                }
            }

            return list;
        }

        /// <summary>Nombre efectivo de capa aplicando prefijo opcional salvo capas de sistema 0/DEFPOINTS y xrefs.</summary>
        public static string ApplyLayerPrefix(string originalLayer, string? prefix)
        {
            if (string.IsNullOrEmpty(prefix)
                || string.Equals(originalLayer, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(originalLayer, "DEFPOINTS", StringComparison.OrdinalIgnoreCase)
                || originalLayer.IndexOf('|') >= 0)
            {
                return originalLayer;
            }

            return prefix + originalLayer;
        }

        /// <summary>Nombre efectivo de definición de bloque aplicando prefijo opcional salvo anónimos (*...) y layouts.</summary>
        public static string ApplyBlockPrefix(string originalBlock, string? prefix)
        {
            if (string.IsNullOrEmpty(prefix) || originalBlock.StartsWith("*", StringComparison.Ordinal))
            {
                return originalBlock;
            }

            return prefix + originalBlock;
        }
    }
}
