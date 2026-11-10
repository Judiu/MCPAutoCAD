using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Emparejamiento de las etiquetas que da el cliente en insertar_bloque con las del bloque (protocol.md §5D.5).
    /// Puro: el handler le pasa las etiquetas de los ATTDEF no constantes (orden de la definición), leídas del dibujo
    /// o de la base lateral de la biblioteca ANTES de escribir nada (corrección 0.4.0: Database.Insert no se revierte
    /// con la transacción, así que una etiqueta desconocida debe rechazarse antes de importar).
    /// </summary>
    public static class AttributeTags
    {
        /// <summary>Resultado de <see cref="Match"/>.</summary>
        public sealed class Result
        {
            internal Result(Dictionary<string, string>? values, string? error, IReadOnlyList<string> etiquetas)
            {
                Values = values;
                Error = error;
                Etiquetas = etiquetas;
            }

            /// <summary>TAG (sin distinguir mayúsculas) → valor del cliente; null si hay error.</summary>
            public Dictionary<string, string>? Values { get; }

            /// <summary>Detalle exacto de INVALID_PARAMS, o null si todas las etiquetas existen.</summary>
            public string? Error { get; }

            /// <summary>Etiquetas del bloque sin repetidos (sin mayúsculas), en el orden de la definición (data.etiquetas).</summary>
            public IReadOnlyList<string> Etiquetas { get; }

            public bool Ok => Error == null;
        }

        /// <summary>Etiquetas sin repetidos ignorando mayúsculas (se conserva la primera aparición y su escritura).</summary>
        public static List<string> Distinct(IEnumerable<string> tags)
        {
            if (tags == null)
            {
                throw new ArgumentNullException(nameof(tags));
            }

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string t in tags)
            {
                if (t != null && seen.Add(t))
                {
                    list.Add(t);
                }
            }

            return list;
        }

        /// <summary>
        /// Valor del cliente por etiqueta del bloque. La primera etiqueta del cliente que el bloque no define →
        /// <see cref="Result.Error"/> = "atributos: etiqueta desconocida '&lt;TAG&gt;' (etiquetas del bloque: A, B, …)"
        /// ("ninguna" si el bloque no tiene atributos variables).
        /// </summary>
        public static Result Match(IReadOnlyList<KeyValuePair<string, string>> given, IEnumerable<string> blockTags)
        {
            if (given == null)
            {
                throw new ArgumentNullException(nameof(given));
            }

            List<string> tags = Distinct(blockTags);
            var known = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in given)
            {
                if (!known.Contains(kv.Key))
                {
                    return new Result(null, UnknownMessage(kv.Key, tags), tags);
                }

                values[kv.Key] = kv.Value;
            }

            return new Result(values, null, tags);
        }

        /// <summary>Mensaje exacto de etiqueta desconocida (§5D.5).</summary>
        public static string UnknownMessage(string tag, IReadOnlyList<string> blockTags)
        {
            string list = blockTags == null || blockTags.Count == 0 ? "ninguna" : string.Join(", ", blockTags);
            return "atributos: etiqueta desconocida '" + tag + "' (etiquetas del bloque: " + list + ")";
        }
    }
}
