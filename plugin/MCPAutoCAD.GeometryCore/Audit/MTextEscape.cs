using System;
using System.Text;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>
    /// Escapado de texto literal para <c>MText.Contents</c> (protocol.md §5C.9): el texto del cliente nunca se
    /// interpreta como códigos de formato. <c>\</c> → <c>\\</c>, <c>{</c> → <c>\{</c>, <c>}</c> → <c>\}</c>;
    /// los saltos de línea (<c>\r\n</c>, <c>\n</c> o <c>\r</c> sueltos) se conservan como párrafo <c>\P</c>.
    /// </summary>
    public static class MTextEscape
    {
        public static string Escape(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var sb = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '{':
                        sb.Append("\\{");
                        break;
                    case '}':
                        sb.Append("\\}");
                        break;
                    case '\r':
                        sb.Append("\\P");
                        if (i + 1 < text.Length && text[i + 1] == '\n')
                        {
                            i++;
                        }

                        break;
                    case '\n':
                        sb.Append("\\P");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
