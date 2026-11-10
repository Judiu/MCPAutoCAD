using System;
using System.Collections.Generic;
using System.Text;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>Resultado global de <see cref="TextReplacePlanner.Plan"/> para un texto.</summary>
    public enum ReplaceOutcome
    {
        /// <summary>No hay ninguna coincidencia en el texto visible.</summary>
        SinCoincidencias,

        /// <summary>Al menos una coincidencia se puede reemplazar (puede haber otras omitidas: ver <see cref="ReplaceResult.Omitidas"/>).</summary>
        Reemplazar,

        /// <summary>No se toca el texto: todas las coincidencias están omitidas, o el contenido no es seguro (<see cref="ReplaceResult.Error"/>).</summary>
        Omitida,
    }

    /// <summary>Detalle de <see cref="TextReplacePlanner.Plan"/> para un texto (TEXT, MTEXT o ATTRIB).</summary>
    public sealed class ReplaceResult
    {
        public ReplaceResult(ReplaceOutcome outcome, string raw)
        {
            Outcome = outcome;
            Raw = raw;
            Motivos = new List<string>();
            VisibleAntes = "";
        }

        public ReplaceOutcome Outcome { get; internal set; }

        /// <summary>Contenido bruto original (tal como lo guarda AutoCAD).</summary>
        public string Raw { get; }

        /// <summary>Coincidencias encontradas en el texto visible.</summary>
        public int Coincidencias { get; internal set; }

        /// <summary>Coincidencias reemplazadas.</summary>
        public int Reemplazos { get; internal set; }

        /// <summary>Coincidencias NO tocadas (cruzan un código de formato o contienen un elemento no sustituible).</summary>
        public int Omitidas { get; internal set; }

        /// <summary>Motivos distintos de las omisiones, en orden de aparición.</summary>
        public List<string> Motivos { get; }

        /// <summary>Contenido bruto nuevo (null si no hay nada que escribir). Ya verificado: se interpreta igual que el esperado.</summary>
        public string? NuevoRaw { get; internal set; }

        /// <summary>Texto visible antes.</summary>
        public string VisibleAntes { get; internal set; }

        /// <summary>Texto visible después (null si no hay reemplazos).</summary>
        public string? VisibleDespues { get; internal set; }

        /// <summary>Motivo por el que la entidad entera no es segura (contenido MTEXT mal formado o verificación fallida). null si no aplica.</summary>
        public string? Error { get; internal set; }
    }

    /// <summary>
    /// Búsqueda y reemplazo de una subcadena dentro del TEXTO VISIBLE de un TEXT, MTEXT o ATTRIB, conservando los códigos
    /// de formato que lo rodean (lógica pura, protocol.md §5D.0 / §5C.9).
    /// <para>
    /// Mecánica: el contenido bruto se parte en <see cref="TextUnit"/> (<see cref="TextFormatParser"/>); la búsqueda se hace
    /// sobre el texto visible y cada coincidencia se mapea a sus unidades. Solo se reemplaza si TODAS las unidades de la
    /// coincidencia son <see cref="UnitKind.Literal"/>: entonces el rango bruto de esas unidades (contiguo, sin códigos
    /// dentro) se sustituye por el texto escrito en el formato del tipo. Si la coincidencia cruza un código de formato
    /// (<c>\L</c>, <c>{</c>, <c>\f…;</c>, <c>%%u</c>) o contiene algo no sustituible (<c>\S</c>, <c>\~</c>, <c>\P</c>), NO se
    /// toca y se cuenta como omitida con su motivo. Antes de dar un resultado por bueno se vuelve a interpretar el contenido
    /// nuevo y se comprueba que el texto visible y los códigos son exactamente los esperados; si no, la entidad entera se
    /// omite (<see cref="ReplaceResult.Error"/>).
    /// </para>
    /// </summary>
    public static class TextReplacePlanner
    {
        public const string MotivoCruzaFormato = "la coincidencia cruza un código de formato (se conserva el formato)";
        public const string MotivoNoSustituible = "la coincidencia contiene un elemento no sustituible (\\S, \\~, \\P o un salto)";
        public const string MotivoVerificacion = "el resultado no se pudo verificar; no se escribe";

        /// <summary>
        /// Planifica el reemplazo de <paramref name="buscar"/> por <paramref name="reemplazar"/> en <paramref name="raw"/>.
        /// Ninguno de los dos puede contener saltos de línea; <paramref name="buscar"/> no puede estar vacío.
        /// </summary>
        public static ReplaceResult Plan(string raw, TextFlavor flavor, string buscar, string reemplazar, bool ignoreCase, bool palabraCompleta)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }

            if (string.IsNullOrEmpty(buscar))
            {
                throw new ArgumentException("buscar no puede estar vacío.", nameof(buscar));
            }

            if (reemplazar == null)
            {
                throw new ArgumentNullException(nameof(reemplazar));
            }

            if (buscar.IndexOfAny(new[] { '\r', '\n' }) >= 0 || reemplazar.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                throw new ArgumentException("buscar y reemplazar no pueden contener saltos de línea.");
            }

            List<TextUnit> units;
            try
            {
                units = TextFormatParser.Parse(raw, flavor);
            }
            catch (TextFormatException ex)
            {
                var bad = new ReplaceResult(ReplaceOutcome.Omitida, raw)
                {
                    Error = "contenido con códigos no reconocidos: " + ex.Message,
                };
                return bad;
            }

            // Mapa carácter visible → unidad.
            var chars = new StringBuilder();
            var charUnit = new List<int>();
            for (int k = 0; k < units.Count; k++)
            {
                string v = units[k].Visible;
                for (int c = 0; c < v.Length; c++)
                {
                    chars.Append(v[c]);
                    charUnit.Add(k);
                }
            }

            string vis = chars.ToString();
            var result = new ReplaceResult(ReplaceOutcome.SinCoincidencias, raw) { VisibleAntes = vis };

            // Coincidencias no solapadas, de izquierda a derecha.
            var matches = new List<int>();
            int pos = 0;
            while (pos + buscar.Length <= vis.Length)
            {
                if (MatchesAt(vis, pos, buscar, ignoreCase) && (!palabraCompleta || IsWholeWord(vis, pos, buscar.Length)))
                {
                    matches.Add(pos);
                    pos += buscar.Length;
                }
                else
                {
                    pos++;
                }
            }

            result.Coincidencias = matches.Count;
            if (matches.Count == 0)
            {
                return result;
            }

            string escaped = EscapeReplacement(reemplazar, flavor);
            var edits = new List<(int start, int end, int visStart)>();
            foreach (int s in matches)
            {
                int a = charUnit[s];
                int b = charUnit[s + buscar.Length - 1];
                string? motivo = null;
                for (int k = a; k <= b && motivo == null; k++)
                {
                    if (units[k].Kind == UnitKind.Format)
                    {
                        motivo = MotivoCruzaFormato;
                    }
                    else if (units[k].Kind == UnitKind.Opaque)
                    {
                        motivo = MotivoNoSustituible;
                    }
                }

                if (motivo != null)
                {
                    result.Omitidas++;
                    if (!result.Motivos.Contains(motivo))
                    {
                        result.Motivos.Add(motivo);
                    }

                    continue;
                }

                edits.Add((units[a].RawStart, units[b].RawEnd, s));
                result.Reemplazos++;
            }

            if (edits.Count == 0)
            {
                result.Outcome = ReplaceOutcome.Omitida;
                return result;
            }

            // Texto visible esperado: el original con cada coincidencia reemplazada (en el texto visible, el reemplazo es literal).
            var expected = new StringBuilder();
            int cursor = 0;
            foreach (var e in edits)
            {
                expected.Append(vis, cursor, e.visStart - cursor);
                expected.Append(reemplazar);
                cursor = e.visStart + buscar.Length;
            }

            expected.Append(vis, cursor, vis.Length - cursor);
            string expectedVis = expected.ToString();

            // Contenido bruto nuevo: se empalma de derecha a izquierda para que los índices brutos sigan válidos.
            string nuevo = raw;
            for (int k = edits.Count - 1; k >= 0; k--)
            {
                var e = edits[k];
                nuevo = nuevo.Substring(0, e.start) + escaped + nuevo.Substring(e.end);
            }

            if (!Verify(raw, nuevo, flavor, expectedVis))
            {
                result.Outcome = ReplaceOutcome.Omitida;
                result.Reemplazos = 0;
                result.Error = MotivoVerificacion;
                return result;
            }

            result.Outcome = ReplaceOutcome.Reemplazar;
            result.NuevoRaw = nuevo;
            result.VisibleDespues = expectedVis;
            return result;
        }

        /// <summary>Texto que escribe <paramref name="texto"/> como literal en el formato del tipo (MTEXT: <c>\</c>, <c>{</c>, <c>}</c>; TEXT: <c>%%</c>).</summary>
        public static string EscapeReplacement(string texto, TextFlavor flavor)
        {
            return flavor == TextFlavor.Mtext ? MTextEscape.Escape(texto) : TextEscape.EscapeDbText(texto);
        }

        /// <summary>true si el contenido nuevo se interpreta con el texto visible esperado y con los mismos códigos de formato y elementos no sustituibles.</summary>
        private static bool Verify(string oldRaw, string newRaw, TextFlavor flavor, string expectedVis)
        {
            try
            {
                List<TextUnit> before = TextFormatParser.Parse(oldRaw, flavor);
                List<TextUnit> after = TextFormatParser.Parse(newRaw, flavor);
                if (!string.Equals(TextFormatParser.VisibleText(after), expectedVis, StringComparison.Ordinal))
                {
                    return false;
                }

                var codigosAntes = new List<string>();
                foreach (TextUnit u in before)
                {
                    if (u.Kind != UnitKind.Literal)
                    {
                        codigosAntes.Add(u.Raw);
                    }
                }

                var codigosDespues = new List<string>();
                foreach (TextUnit u in after)
                {
                    if (u.Kind != UnitKind.Literal)
                    {
                        codigosDespues.Add(u.Raw);
                    }
                }

                return codigosAntes.Count == codigosDespues.Count && SequenceEqual(codigosAntes, codigosDespues);
            }
            catch (TextFormatException)
            {
                return false;
            }
        }

        private static bool SequenceEqual(List<string> a, List<string> b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Coincidencia ordinal (o sin distinguir mayúsculas invariantes, carácter a carácter: los acentos cuentan).</summary>
        private static bool MatchesAt(string vis, int pos, string buscar, bool ignoreCase)
        {
            for (int k = 0; k < buscar.Length; k++)
            {
                char x = vis[pos + k];
                char y = buscar[k];
                if (x == y)
                {
                    continue;
                }

                if (!ignoreCase || char.ToUpperInvariant(x) != char.ToUpperInvariant(y))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsWholeWord(string vis, int pos, int len)
        {
            bool before = pos > 0 && IsWordChar(vis[pos - 1]);
            bool after = pos + len < vis.Length && IsWordChar(vis[pos + len]);
            return !before && !after;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);
    }
}
