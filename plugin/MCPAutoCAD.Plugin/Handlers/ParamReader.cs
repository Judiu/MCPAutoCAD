using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Validación manual de parámetros, equivalente al JSON Schema publicado en el manifest
    /// (tipos, obligatorios, additionalProperties: false, rangos). Toda violación → INVALID_PARAMS
    /// con data.detalle = "&lt;campo&gt;: &lt;motivo&gt;".
    /// </summary>
    internal static class ParamReader
    {
        // ---- Fragmentos de JSON Schema compartidos por los métodos de Fase 1 (protocol.md §2.4, §2.5, §5B) ----

        public const int DefaultLimit = 200;
        public const int MaxLimit = 1000;

        public const string SchemaLimit =
            @"{ ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""default"": 200, ""description"": ""Máximo de filas en la página."" }";

        public const string SchemaCursor =
            @"{ ""type"": ""string"", ""pattern"": ""^[0-9]+$"", ""description"": ""Valor de cursor_siguiente de la página anterior."" }";

        public const string SchemaBbox =
            @"{ ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }, ""description"": ""Caja [[xmin, ymin], [xmax, ymax]] en WCS (z opcional, se ignora). Cumple la entidad cuya caja interseca esta (el contacto en el borde cuenta)."" }";

        public const string SchemaEspacio =
            @"{ ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""'modelo' o nombre de un layout (pestaña). Por defecto, el espacio actual."" }";

        public const string SchemaCapas =
            @"{ ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Filtro por nombre de capa (sin distinguir mayúsculas)."" }";

        public const string SchemaHandle =
            @"{ ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Handle hexadecimal de la entidad (p. ej. '2A3F')."" }";

        /// <summary>limit opcional (1–1000, defecto 200).</summary>
        public static int Limit(JsonElement obj) => OptionalInt(obj, "limit", 1, MaxLimit) ?? DefaultLimit;

        /// <summary>Exige que params sea un objeto sin propiedades fuera de <paramref name="allowed"/>.</summary>
        public static void RequireObject(JsonElement parameters, IReadOnlyCollection<string> allowed)
        {
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw ToolException.InvalidParams("params: debe ser un objeto");
            }

            RejectUnknown(parameters, allowed, "");
        }

        /// <summary>additionalProperties: false para un objeto anidado (prefijo = ruta del objeto, p. ej. "lineas[0].").</summary>
        public static void RejectUnknown(JsonElement obj, IReadOnlyCollection<string> allowed, string prefix)
        {
            foreach (JsonProperty prop in obj.EnumerateObject())
            {
                bool known = false;
                foreach (string name in allowed)
                {
                    if (string.Equals(name, prop.Name, StringComparison.Ordinal))
                    {
                        known = true;
                        break;
                    }
                }

                if (!known)
                {
                    throw ToolException.InvalidParams(prefix + prop.Name + ": propiedad no permitida");
                }
            }
        }

        public static bool Has(JsonElement obj, string name) => obj.TryGetProperty(name, out _);

        public static bool? OptionalBool(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (el.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            throw ToolException.InvalidParams(name + ": debe ser booleano");
        }

        /// <summary>Entero (JSON Schema "integer": se admite 5.0) dentro de [min, max].</summary>
        public static int? OptionalInt(JsonElement obj, string name, int min, int max)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            string rangeMsg = string.Format(CultureInfo.InvariantCulture, "{0}: debe ser un entero entre {1} y {2}", name, min, max);
            if (el.ValueKind != JsonValueKind.Number)
            {
                throw ToolException.InvalidParams(rangeMsg);
            }

            if (el.TryGetInt64(out long l))
            {
                if (l < min || l > max)
                {
                    throw ToolException.InvalidParams(rangeMsg);
                }

                return (int)l;
            }

            if (el.TryGetDouble(out double d) && double.IsFinite(d) && Math.Floor(d) == d && d >= min && d <= max)
            {
                return (int)d;
            }

            throw ToolException.InvalidParams(rangeMsg);
        }

        public static string? OptionalString(JsonElement obj, string name, int minLength)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind != JsonValueKind.String)
            {
                throw ToolException.InvalidParams(name + ": debe ser una cadena");
            }

            string value = el.GetString() ?? "";
            if (value.Length < minLength)
            {
                throw ToolException.InvalidParams(
                    string.Format(CultureInfo.InvariantCulture, "{0}: debe tener al menos {1} carácter(es)", name, minLength));
            }

            return value;
        }

        /// <summary>
        /// Array opcional de cadenas con [minItems, maxItems] elementos, cada una con al menos <paramref name="minLen"/>
        /// caracteres. null si el campo no está.
        /// </summary>
        public static List<string>? OptionalStringArray(JsonElement obj, string name, int minItems, int maxItems, int minLen)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            string arrayMsg = string.Format(
                CultureInfo.InvariantCulture, "{0}: debe ser un array de {1} a {2} cadenas", name, minItems, maxItems);
            if (el.ValueKind != JsonValueKind.Array)
            {
                throw ToolException.InvalidParams(arrayMsg);
            }

            int n = el.GetArrayLength();
            if (n < minItems || n > maxItems)
            {
                throw ToolException.InvalidParams(arrayMsg);
            }

            var list = new List<string>(n);
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                string path = name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw ToolException.InvalidParams(path + ": debe ser una cadena");
                }

                string value = item.GetString() ?? "";
                if (value.Length < minLen)
                {
                    throw ToolException.InvalidParams(
                        string.Format(CultureInfo.InvariantCulture, "{0}: debe tener al menos {1} carácter(es)", path, minLen));
                }

                list.Add(value);
                i++;
            }

            return list;
        }

        /// <summary>Cadena opcional con longitud máxima (además de la mínima).</summary>
        public static string? OptionalString(JsonElement obj, string name, int minLength, int maxLength)
        {
            string? value = OptionalString(obj, name, minLength);
            if (value != null && value.Length > maxLength)
            {
                throw ToolException.InvalidParams(
                    string.Format(CultureInfo.InvariantCulture, "{0}: debe tener como máximo {1} caracteres", name, maxLength));
            }

            return value;
        }

        /// <summary>Cadena opcional que debe ser uno de <paramref name="allowed"/> (comparación exacta).</summary>
        public static string? OptionalEnum(JsonElement obj, string name, string[] allowed)
        {
            string? value = OptionalString(obj, name, 0);
            if (value == null)
            {
                return null;
            }

            foreach (string a in allowed)
            {
                if (string.Equals(a, value, StringComparison.Ordinal))
                {
                    return value;
                }
            }

            throw ToolException.InvalidParams(name + ": debe ser uno de " + string.Join(", ", allowed));
        }

        /// <summary>Handle opcional: cadena hexadecimal ^[0-9A-Fa-f]{1,16}$ (protocol.md §5B.2). Se devuelve tal cual.</summary>
        public static string? OptionalHandle(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            string invalid = name + ": formato inválido";
            if (el.ValueKind != JsonValueKind.String)
            {
                throw ToolException.InvalidParams(invalid);
            }

            string value = el.GetString() ?? "";
            if (value.Length < 1 || value.Length > 16)
            {
                throw ToolException.InvalidParams(invalid);
            }

            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
                if (!hex)
                {
                    throw ToolException.InvalidParams(invalid);
                }
            }

            return value;
        }

        /// <summary>
        /// Cursor opcional (protocol.md §2.4): índice de inicio, 0 si falta. Formato inválido → INVALID_PARAMS
        /// "cursor: valor inválido".
        /// </summary>
        public static int OptionalCursor(JsonElement obj)
        {
            // Tipo distinto de cadena → "cursor: debe ser una cadena" (misma conducta que listar_capas en 0.1).
            string? cursor = OptionalString(obj, "cursor", 0);
            try
            {
                return Paging.ParseCursor(cursor);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }

        /// <summary>Página [start, start+count) con los errores de cursor convertidos a INVALID_PARAMS.</summary>
        public static (int count, string? nextCursor) Slice(int total, int start, int limit)
        {
            try
            {
                return Paging.Slice(total, start, limit);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }

        /// <summary>
        /// Caja (protocol.md §2.5): [[xmin, ymin], [xmax, ymax]] o con z (ignorada). Mínimo &gt; máximo →
        /// INVALID_PARAMS "bbox: mínimo mayor que máximo".
        /// </summary>
        public static BBox2 ReadBbox(JsonElement el, string path)
        {
            string shape = path + ": debe ser [[xmin, ymin], [xmax, ymax]]";
            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 2)
            {
                throw ToolException.InvalidParams(shape);
            }

            var corners = new Point3[2];
            int i = 0;
            foreach (JsonElement corner in el.EnumerateArray())
            {
                if (corner.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams(shape);
                }

                corners[i] = ReadPoint(corner, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                i++;
            }

            try
            {
                return BBox2.FromCorners(corners[0].X, corners[0].Y, corners[1].X, corners[1].Y);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }

        /// <summary>Caja opcional en el campo <paramref name="name"/>; null si falta.</summary>
        public static BBox2? OptionalBbox(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            return ReadBbox(el, name);
        }

        // ---- Fase 3 (protocol.md §5D): helpers comunes de las herramientas de dibujo ----

        /// <summary>
        /// Número opcional finito en [min, max] (<paramref name="exclusiveMin"/> → &gt; min). Mensaje:
        /// "&lt;name&gt;: debe ser un número mayor que 0" (exclusivo, min 0, max infinito) o
        /// "&lt;name&gt;: debe ser un número entre a y b".
        /// </summary>
        public static double? OptionalNumber(JsonElement obj, string name, double min, double max, bool exclusiveMin = false)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            string msg = exclusiveMin && min == 0 && double.IsPositiveInfinity(max)
                ? name + ": debe ser un número mayor que 0"
                : string.Format(CultureInfo.InvariantCulture, "{0}: debe ser un número entre {1} y {2}", name, min, max);
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double v) || !double.IsFinite(v))
            {
                throw ToolException.InvalidParams(msg);
            }

            if (v > max || v < min || (exclusiveMin && v <= min))
            {
                throw ToolException.InvalidParams(msg);
            }

            return v;
        }

        /// <summary>Número obligatorio (como <see cref="OptionalNumber"/>; ausente → "&lt;name&gt;: obligatorio").</summary>
        public static double RequiredNumber(JsonElement obj, string name, double min, double max, bool exclusiveMin = false)
        {
            if (!obj.TryGetProperty(name, out _))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            return OptionalNumber(obj, name, min, max, exclusiveMin)!.Value;
        }

        /// <summary>Punto obligatorio en el campo <paramref name="name"/>.</summary>
        public static Point3 RequiredPoint(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            return ReadPoint(el, name);
        }

        /// <summary>Punto opcional; null si falta.</summary>
        public static Point3? OptionalPoint(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out JsonElement el) ? ReadPoint(el, name) : null;
        }

        /// <summary>
        /// Array de puntos [minItems, maxItems] en el campo <paramref name="name"/> (obligatorio). Mensaje de forma:
        /// "&lt;name&gt;: debe ser un array de a a b puntos"; cada punto se valida con <see cref="ReadPoint"/> ("name[i]").
        /// </summary>
        public static List<Point3> RequiredPoints(JsonElement obj, string name, int minItems, int maxItems)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            string shape = string.Format(CultureInfo.InvariantCulture, "{0}: debe ser un array de {1} a {2} puntos", name, minItems, maxItems);
            if (el.ValueKind != JsonValueKind.Array)
            {
                throw ToolException.InvalidParams(shape);
            }

            int n = el.GetArrayLength();
            if (n < minItems || n > maxItems)
            {
                throw ToolException.InvalidParams(shape);
            }

            var list = new List<Point3>(n);
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                list.Add(ReadPoint(item, name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]"));
                i++;
            }

            return list;
        }

        /// <summary>
        /// Array obligatorio de handles [minItems, maxItems] (§2.1, patrón ^[0-9A-Fa-f]{1,16}$), devueltos en mayúsculas
        /// y sin repetidos (repetido → "&lt;name&gt;[i]: handle repetido"; formato → "&lt;name&gt;[i]: formato inválido").
        /// </summary>
        public static List<string> RequiredHandles(JsonElement obj, string name, int minItems, int maxItems)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            string shape = string.Format(CultureInfo.InvariantCulture, "{0}: debe ser un array de {1} a {2} handles", name, minItems, maxItems);
            if (el.ValueKind != JsonValueKind.Array)
            {
                throw ToolException.InvalidParams(shape);
            }

            int n = el.GetArrayLength();
            if (n < minItems || n > maxItems)
            {
                throw ToolException.InvalidParams(shape);
            }

            var list = new List<string>(n);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                string path = name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                string invalid = path + ": formato inválido";
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw ToolException.InvalidParams(invalid);
                }

                string value = item.GetString() ?? "";
                if (value.Length < 1 || value.Length > 16)
                {
                    throw ToolException.InvalidParams(invalid);
                }

                foreach (char c in value)
                {
                    bool hex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
                    if (!hex)
                    {
                        throw ToolException.InvalidParams(invalid);
                    }
                }

                string upper = value.ToUpperInvariant();
                if (!seen.Add(upper))
                {
                    throw ToolException.InvalidParams(path + ": handle repetido");
                }

                list.Add(upper);
                i++;
            }

            return list;
        }

        /// <summary>
        /// Cadena obligatoria con longitud en caracteres Unicode (puntos de código) en [minLen, maxLen]:
        /// "&lt;name&gt;: debe tener entre a y b caracteres".
        /// </summary>
        public static string RequiredText(JsonElement obj, string name, int minLen, int maxLen)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            if (el.ValueKind != JsonValueKind.String)
            {
                throw ToolException.InvalidParams(name + ": debe ser una cadena");
            }

            string value = el.GetString() ?? "";
            int chars = CodePointCount(value);
            if (chars < minLen || chars > maxLen)
            {
                throw ToolException.InvalidParams(
                    string.Format(CultureInfo.InvariantCulture, "{0}: debe tener entre {1} y {2} caracteres", name, minLen, maxLen));
            }

            return value;
        }

        /// <summary>Longitud en caracteres Unicode (como maxLength de JSON Schema), no en unidades UTF-16.</summary>
        public static int CodePointCount(string s)
        {
            int count = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                }

                count++;
            }

            return count;
        }

        /// <summary>Punto: array de 2 o 3 números finitos (protocol.md §2.1). <paramref name="path"/> = ruta del campo.</summary>
        public static Point3 ReadPoint(JsonElement el, string path)
        {
            if (el.ValueKind != JsonValueKind.Array)
            {
                throw ToolException.InvalidParams(path + ": debe ser un array de 2 o 3 números");
            }

            int count = el.GetArrayLength();
            if (count != 2 && count != 3)
            {
                throw ToolException.InvalidParams(path + ": debe ser un array de 2 o 3 números");
            }

            var components = new double[count];
            int i = 0;
            foreach (JsonElement c in el.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Number)
                {
                    throw ToolException.InvalidParams(path + ": debe ser un array de 2 o 3 números");
                }

                if (!c.TryGetDouble(out double v) || !double.IsFinite(v))
                {
                    throw ToolException.InvalidParams(path + ": coordenada no finita");
                }

                components[i++] = v;
            }

            if (!Point3.TryFromArray(components, out Point3 point, out string? error))
            {
                throw ToolException.InvalidParams(path + ": " + error);
            }

            return point;
        }
    }
}
