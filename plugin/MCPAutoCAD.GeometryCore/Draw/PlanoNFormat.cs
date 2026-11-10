using System;
using System.Text.RegularExpressions;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Atributo multilínea con formato en línea "número de" (PLANO_N de LPS_ROTULO; protocol.md §5D.5). Contenido real:
    /// <c>{\pqc;\fArial|b1|i0|c0|p34;1\fArial|b1|i0|c0|p34;\H0.500000x; de}</c>. Regla: tras el prefijo <c>{</c> + uno o
    /// más códigos <c>\…;</c>, el texto hasta el siguiente <c>\f</c> es el número; se reemplaza por
    /// <c>MTextEscape.Escape(numero)</c> conservando todo lo demás. Debe seguir <c>\f…;\H…; de}</c>.
    /// Ampliación compatible: se admiten códigos de párrafo antes de la llave (<c>\A1;{…}</c>), porque el MTEXT
    /// embebido del ATTDEF real, leído con ODA + ezdxf (2026-10-26), es <c>\A1;{\pqc;…; de}</c>.
    /// </summary>
    public static class PlanoNFormat
    {
        /// <summary>Etiqueta del atributo del rótulo LPS con este formato.</summary>
        public const string Tag = "PLANO_N";

        /// <summary>Advertencia cuando el contenido no tiene la forma esperada (§5D.5).</summary>
        public const string NotRecognizedWarning = "PLANO_N: no se reconoció el formato en línea; se escribió el valor sin formato.";

        // pre   = códigos opcionales antes de la llave (\A1;)
        // codes = uno o más códigos \X…; tras la llave
        // num   = número actual: texto sin \ { } salvo los escapes \\ \{ \} que deja MTextEscape
        // rest  = \f…;\H…; de}
        private static readonly Regex Form = new Regex(
            @"^(?<pre>(?:\\[A-Za-z][^;\\{}]*;)*)\{(?<codes>(?:\\[A-Za-z][^;]*;)+?)(?<num>(?:\\\\|\\\{|\\\}|[^\\{}])*)(?<rest>\\f[^;]*;\\H[^;]*;\s*de\s*\})$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        private static readonly Regex Suffix = new Regex(@"^(?<n>.*?)(?:\s+|(?<=\d))de$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>true si <paramref name="contents"/> tiene la forma <c>{&lt;códigos&gt;NUMERO\f…;\H…; de}</c>.</summary>
        public static bool IsRecognized(string? contents) => contents != null && Form.IsMatch(contents);

        /// <summary>
        /// Sustituye el número conservando el formato. <paramref name="numero"/> se escapa con <see cref="MTextEscape"/>
        /// (nunca se interpreta). false (y <paramref name="result"/> = <paramref name="contents"/>) si no tiene la forma.
        /// </summary>
        public static bool TrySubstitute(string contents, string numero, out string result)
        {
            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }

            if (numero == null)
            {
                throw new ArgumentNullException(nameof(numero));
            }

            Match m = Form.Match(contents);
            if (!m.Success)
            {
                result = contents;
                return false;
            }

            result = m.Groups["pre"].Value + "{" + m.Groups["codes"].Value + MTextEscape.Escape(numero) + m.Groups["rest"].Value;
            return true;
        }

        /// <summary>
        /// Número tal como lo da el cliente: se recortan espacios y el sufijo " de" (sin distinguir mayúsculas; también
        /// "3de"). "3 de" → "3", " 12 " → "12", "3 DE" → "3".
        /// </summary>
        public static string NormalizeNumber(string valor)
        {
            if (valor == null)
            {
                throw new ArgumentNullException(nameof(valor));
            }

            string t = valor.Trim();
            Match m = Suffix.Match(t);
            return m.Success ? m.Groups["n"].Value.Trim() : t;
        }
    }
}
