using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Reglas puras compartidas por <c>editar_bloque</c> y <c>explotar</c> (fallo 55). Sin AutoCAD.
    /// </summary>
    public static class BlockEditRules
    {
        public const int MaxEntities = 20000;
        public const int MaxBlocks = 100;
        public const int MaxHandles = 500;
        public const int MaxSample = 50;
        public const int MaxReportBlocks = 100;
        public const int DefaultDepth = 10;
        public const int MaxDepth = 20;
        public const int MaxContains = 50;
        public const int MaxContainsLength = 200;

        public const string ActionDelete = "borrar";
        public const string ActionLayer = "cambiar_capa";
        public const string ActionProps = "cambiar_propiedades";

        public static readonly string[] ValidActions =
        {
            ActionDelete,
            ActionLayer,
            ActionProps,
        };

        /// <summary>Normaliza y deduplica (sin mayúsculas) los patrones de nombre de bloque.</summary>
        public static List<string> NormalizeBlockPatterns(IReadOnlyList<string> patterns, string field = "bloques")
        {
            if (patterns == null || patterns.Count == 0 || patterns.Count > MaxBlocks)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: debe tener entre 1 y {1} elementos",
                    field, MaxBlocks));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>(patterns.Count);
            for (int i = 0; i < patterns.Count; i++)
            {
                string trimmed = (patterns[i] ?? "").Trim();
                if (trimmed.Length == 0)
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}[{1}]: no puede estar vacío",
                        field, i));
                }

                if (seen.Add(trimmed))
                {
                    list.Add(trimmed);
                }
            }

            return list;
        }

        /// <summary>Comprueba n_esperado frente al recuento actual.</summary>
        public static string? CheckExpected(int? esperado, int real, string verb = "modificó")
        {
            if (!esperado.HasValue || esperado.Value == real)
            {
                return null;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "la previsualización mostró {0}, ahora coinciden {1}; no se {2} nada: repita la previsualización",
                esperado.Value, real, verb);
        }

        /// <summary>Comprueba el tope máximo por llamada.</summary>
        public static string? CheckCap(int count, string noun = "entidades")
        {
            if (count <= MaxEntities)
            {
                return null;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "coinciden {0} {1} y el máximo por llamada es {2}; acote el filtro",
                count, noun, MaxEntities);
        }

        /// <summary>Valida el filtro de borrado dentro de bloques (reutiliza las salvaguardas de borrar_por_filtro).</summary>
        public static string? ValidateDeleteFilter(IReadOnlyList<string>? capas, bool hasTipos) =>
            ValidateDeleteFilter(capas, hasTipos, hasHandles: false, hasContiene: false);

        /// <summary>
        /// Valida el filtro de borrado dentro de bloques: exige al menos un filtro concreto (<c>handles</c>,
        /// <c>contiene</c>, <c>tipos</c> o <c>capas</c> que no sea solo comodín universal sin otros filtros).
        /// </summary>
        public static string? ValidateDeleteFilter(
            IReadOnlyList<string>? capas, bool hasTipos, bool hasHandles, bool hasContiene)
        {
            if (hasHandles || hasContiene)
            {
                return null;
            }

            return BorrarPorFiltroRules.ValidateFilters(capas, hasTipos, hasBbox: false, modoBbox: null, accion: "borra");
        }

        /// <summary>Comprueba si <paramref name="text"/> contiene alguna de las subcadenas indicadas.</summary>
        public static bool MatchesAnySubstring(string? text, IReadOnlyList<string> needles, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(text) || needles == null || needles.Count == 0)
            {
                return false;
            }

            StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            for (int i = 0; i < needles.Count; i++)
            {
                string n = needles[i];
                if (!string.IsNullOrEmpty(n) && text.IndexOf(n, comparison) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Indica si un patrón contiene comodines (* o ?).</summary>
        public static bool HasWildcards(string pattern) =>
            !string.IsNullOrEmpty(pattern) && (pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0);

        /// <summary>Ordena el resumen por bloque descendente por conteo y luego nombre.</summary>
        public static IReadOnlyList<KeyValuePair<string, int>> TopBlocks(IReadOnlyDictionary<string, int> counts) =>
            counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(MaxReportBlocks)
                .ToList();
    }
}
