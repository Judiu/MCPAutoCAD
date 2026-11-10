using System;
using System.Text;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Texto literal para entidades TEXT y atributos de una línea (protocol.md §5D.0, R7). AutoCAD interpreta en
    /// TEXT los códigos <c>%%d</c>, <c>%%c</c>, <c>%%p</c>, <c>%%u</c>, <c>%%o</c>, <c>%%nnn</c> y <c>%%%</c>
    /// (= un signo %). Para que el texto del cliente se vea tal cual, cada pareja <c>%%</c> se sustituye por
    /// <c>%%%%%%</c> (dos veces <c>%%%</c> = dos signos % literales): "%%d" → "%%%%%%d" se muestra como "%%d".
    /// Un % suelto no cambia ("50%"). Para MTEXT se usa <see cref="Audit.MTextEscape"/>.
    /// </summary>
    public static class TextEscape
    {
        public static string EscapeDbText(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (text.IndexOf("%%", StringComparison.Ordinal) < 0)
            {
                return text;
            }

            var sb = new StringBuilder(text.Length + 16);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '%' && i + 1 < text.Length && text[i + 1] == '%')
                {
                    sb.Append("%%%%%%");
                    i += 2;
                }
                else
                {
                    sb.Append(text[i]);
                    i++;
                }
            }

            return sb.ToString();
        }

        /// <summary>TEXT es de una sola línea: saltos de línea (\r\n, \n, \r) → un espacio. true si hubo alguno.</summary>
        public static string CollapseNewlines(string text, out bool had)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            had = text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0;
            if (!had)
            {
                return text;
            }

            return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        }
    }
}
