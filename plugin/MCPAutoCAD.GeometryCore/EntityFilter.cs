using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>Filtros de texto de los métodos de lectura (protocol.md §5B): tipo, capa y subcadena.</summary>
    public static class EntityFilter
    {
        /// <summary>Tipo normalizado: sin espacios alrededor y en mayúsculas invariantes ("line " → "LINE").</summary>
        public static string NormalizeType(string type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            return type.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// true si <paramref name="value"/> coincide (ordinal, sin distinguir mayúsculas) con algún elemento del filtro.
        /// Filtro null o vacío = no hay filtro → true.
        /// </summary>
        public static bool MatchesAny(string value, IReadOnlyCollection<string>? filter)
        {
            if (filter == null || filter.Count == 0)
            {
                return true;
            }

            if (value == null)
            {
                return false;
            }

            foreach (string f in filter)
            {
                if (string.Equals(value, f, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Modo de filtro por bbox 'interseca' (defecto): la caja de la entidad toca o corta la caja pedida.</summary>
        public const string BboxModeIntersects = "interseca";

        /// <summary>Modo de filtro por bbox 'dentro': la extensión geométrica entera de la entidad cae dentro de la caja pedida.</summary>
        public const string BboxModeInside = "dentro";

        /// <summary>Mensaje de INVALID_PARAMS para un modo_bbox desconocido.</summary>
        public const string InvalidBboxMode = "modo_bbox: debe ser \"interseca\" o \"dentro\"";

        /// <summary>
        /// Valida y normaliza 'modo_bbox'. Null = defecto ("interseca"). Otro valor → ArgumentException(<see cref="InvalidBboxMode"/>).
        /// </summary>
        public static string NormalizeBboxMode(string? modo)
        {
            if (modo == null)
            {
                return BboxModeIntersects;
            }

            if (string.Equals(modo, BboxModeIntersects, StringComparison.Ordinal) || string.Equals(modo, BboxModeInside, StringComparison.Ordinal))
            {
                return modo;
            }

            throw new ArgumentException(InvalidBboxMode);
        }

        /// <summary>
        /// Decide si la caja de una entidad pasa el filtro bbox. 'interseca' = <see cref="BBox2.Intersects"/> (inclusivo);
        /// 'dentro' = la caja de la entidad está contenida por completo en la caja pedida (bordes incluidos).
        /// </summary>
        public static bool MatchesBox(BBox2 entity, BBox2 query, string modo)
        {
            if (string.Equals(modo, BboxModeInside, StringComparison.Ordinal))
            {
                return IsInside(entity, query);
            }

            return entity.Intersects(query);
        }

        /// <summary>true si <paramref name="inner"/> está contenida en <paramref name="outer"/> (bordes incluidos).</summary>
        public static bool IsInside(BBox2 inner, BBox2 outer) =>
            inner.Xmin >= outer.Xmin && inner.Xmax <= outer.Xmax && inner.Ymin >= outer.Ymin && inner.Ymax <= outer.Ymax;

        /// <summary>
        /// Subcadena sin distinguir mayúsculas con cultura invariante (protocol.md §5B.3): los acentos SÍ distinguen
        /// ("canción" no contiene "cancion").
        /// </summary>
        public static bool ContainsText(string text, string needle)
        {
            if (text == null || needle == null)
            {
                return false;
            }

            return CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, needle, CompareOptions.IgnoreCase) >= 0;
        }
    }
}
