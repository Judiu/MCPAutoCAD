using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Qué interpretación de códigos tiene un texto del plano: TEXT y ATTRIB usan códigos <c>%%</c> (<see cref="Dbtext"/>);
    /// MTEXT usa códigos <c>\</c> y llaves (<see cref="Mtext"/>).
    /// </summary>
    public enum TextFlavor
    {
        /// <summary>TEXT y ATTRIB (<c>%%d</c>, <c>%%c</c>, <c>%%p</c>, <c>%%u</c>, <c>%%o</c>, <c>%%nnn</c>, <c>%%%</c>).</summary>
        Dbtext,

        /// <summary>MTEXT (<c>\L</c>, <c>\f…;</c>, <c>\P</c>, <c>\S…;</c>, <c>{…}</c>, …).</summary>
        Mtext,
    }

    /// <summary>Clase de un trozo del contenido bruto.</summary>
    public enum UnitKind
    {
        /// <summary>Un único carácter visible que se puede sustituir por otro texto sin tocar el formato.</summary>
        Literal,

        /// <summary>Visible, pero no sustituible con seguridad (apilado <c>\S</c>, espacio duro <c>\~</c>, salto <c>\P</c>).</summary>
        Opaque,

        /// <summary>Sin texto visible: código de formato (<c>\L</c>, <c>{</c>, <c>}</c>, <c>\f…;</c>, <c>%%u</c>). Nunca se toca ni se cruza.</summary>
        Format,
    }

    /// <summary>Un trozo del contenido bruto (<see cref="RawStart"/> … <see cref="RawEnd"/>) y su texto visible.</summary>
    public sealed class TextUnit
    {
        public TextUnit(UnitKind kind, string visible, string raw, int rawStart)
        {
            Kind = kind;
            Visible = visible;
            Raw = raw;
            RawStart = rawStart;
        }

        public UnitKind Kind { get; }

        /// <summary>Texto que se ve en pantalla: un carácter para <see cref="UnitKind.Literal"/>, "" para <see cref="UnitKind.Format"/>.</summary>
        public string Visible { get; }

        /// <summary>Trozo tal cual en el contenido bruto (incluye la barra, la llave o el «%%» del código).</summary>
        public string Raw { get; }

        public int RawStart { get; }

        public int RawEnd => RawStart + Raw.Length;
    }

    /// <summary>Código MTEXT mal formado o desconocido; <see cref="Position"/> es el índice en el contenido bruto.</summary>
    public sealed class TextFormatException : Exception
    {
        public TextFormatException(string message, int position)
            : base(message)
        {
            Position = position;
        }

        public int Position { get; }
    }

    /// <summary>
    /// Parser puro de los códigos de formato de TEXT/ATTRIB y MTEXT (protocol.md §5C.9 / §5D.0). Convierte el contenido
    /// bruto en una lista de <see cref="TextUnit"/> que cubre TODO el contenido (sin huecos ni solapes), de modo que
    /// concatenar los <see cref="TextUnit.Raw"/> reconstruye el original, y concatenar los <see cref="TextUnit.Visible"/>
    /// da el texto que se ve. Para MTEXT es estricto: un código desconocido, sin «;» de cierre, «\U+» sin 4 hex,
    /// barra final o llaves desbalanceadas lanza <see cref="TextFormatException"/>.
    /// </summary>
    public static class TextFormatParser
    {
        /// <summary>Códigos MTEXT que llevan argumento hasta «;».</summary>
        private const string MTextCodesWithArgs = "fFHhWwQTCcApS";

        /// <summary>Conmutadores MTEXT de una sola letra (subrayado, sobrelínea, tachado).</summary>
        private const string MTextToggles = "LlOoKk";

        /// <summary>Interpreta <paramref name="raw"/> según <paramref name="flavor"/>. Solo MTEXT puede lanzar.</summary>
        public static List<TextUnit> Parse(string raw, TextFlavor flavor)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }

            return flavor == TextFlavor.Mtext ? ParseMText(raw) : ParseDbText(raw);
        }

        /// <summary>Texto visible (lo que se ve en pantalla) de las unidades.</summary>
        public static string VisibleText(IReadOnlyList<TextUnit> units)
        {
            var sb = new StringBuilder();
            foreach (TextUnit u in units)
            {
                sb.Append(u.Visible);
            }

            return sb.ToString();
        }

        /// <summary>Texto visible de <paramref name="raw"/>. Lanza <see cref="TextFormatException"/> si es MTEXT mal formado.</summary>
        public static string Visible(string raw, TextFlavor flavor) => VisibleText(Parse(raw, flavor));

        /// <summary>null si <paramref name="raw"/> es un contenido MTEXT válido; si no, el motivo con su posición.</summary>
        public static string? ValidateMText(string raw)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }

            try
            {
                ParseMText(raw);
                return null;
            }
            catch (TextFormatException ex)
            {
                return ex.Message + " (posición " + ex.Position.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        private static List<TextUnit> ParseMText(string raw)
        {
            var units = new List<TextUnit>(raw.Length);
            var openBraces = new Stack<int>();
            int i = 0;
            while (i < raw.Length)
            {
                char c = raw[i];
                // Campo (FIELD): %<...>%, con anidamiento. Opaco: su texto visible no es el de la expresión.
                // Un "%<" sin ">%" de cierre es texto literal (igual que en ParseDbText).
                int fieldEnd = c == '%' && i + 1 < raw.Length && raw[i + 1] == '<' ? FieldEnd(raw, i) : -1;
                if (fieldEnd >= 0)
                {
                    Add(units, UnitKind.Opaque, "", raw, i, fieldEnd - i);
                    i = fieldEnd;
                }
                else if (c == '\\')
                {
                    i = ReadMTextCode(raw, i, units);
                }
                else if (c == '{')
                {
                    openBraces.Push(i);
                    Add(units, UnitKind.Format, "", raw, i, 1);
                    i++;
                }
                else if (c == '}')
                {
                    if (openBraces.Count == 0)
                    {
                        throw new TextFormatException("'}' sin '{' de apertura (para un corchete literal use \\}).", i);
                    }

                    openBraces.Pop();
                    Add(units, UnitKind.Format, "", raw, i, 1);
                    i++;
                }
                else if (c == '\r' || c == '\n')
                {
                    int len = c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n' ? 2 : 1;
                    Add(units, UnitKind.Opaque, "\n", raw, i, len);
                    i += len;
                }
                else
                {
                    Add(units, UnitKind.Literal, c.ToString(), raw, i, 1);
                    i++;
                }
            }

            if (openBraces.Count > 0)
            {
                throw new TextFormatException("'{' sin '}' de cierre.", openBraces.Peek());
            }

            return units;
        }

        /// <summary>Lee un código MTEXT que empieza en <paramref name="i"/> (la barra). Devuelve el índice siguiente.</summary>
        private static int ReadMTextCode(string raw, int i, List<TextUnit> units)
        {
            if (i + 1 >= raw.Length)
            {
                throw new TextFormatException("barra invertida al final del texto.", i);
            }

            char e = raw[i + 1];
            if (e == '\\' || e == '{' || e == '}')
            {
                Add(units, UnitKind.Literal, e.ToString(), raw, i, 2);
                return i + 2;
            }

            if (MTextToggles.IndexOf(e) >= 0)
            {
                Add(units, UnitKind.Format, "", raw, i, 2);
                return i + 2;
            }

            if (e == 'P' || e == 'X' || e == 'N')
            {
                Add(units, UnitKind.Opaque, "\n", raw, i, 2);
                return i + 2;
            }

            if (e == '~')
            {
                Add(units, UnitKind.Opaque, " ", raw, i, 2);
                return i + 2;
            }

            if (e == 'U')
            {
                if (i + 7 > raw.Length || raw[i + 2] != '+' || !IsHex4(raw, i + 3))
                {
                    throw new TextFormatException("\\U+ debe ir seguido de 4 dígitos hexadecimales.", i);
                }

                char ch = (char)int.Parse(raw.Substring(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                UnitKind kind = char.IsControl(ch) || char.IsSurrogate(ch) ? UnitKind.Opaque : UnitKind.Literal;
                Add(units, kind, ch.ToString(), raw, i, 7);
                return i + 7;
            }

            if (MTextCodesWithArgs.IndexOf(e) >= 0)
            {
                int semi = raw.IndexOf(';', i + 2);
                if (semi < 0)
                {
                    throw new TextFormatException("código \\" + e + " sin ';' de cierre.", i);
                }

                int len = semi - i + 1;
                if (e == 'S')
                {
                    Add(units, UnitKind.Opaque, StackedVisible(raw.Substring(i + 2, semi - i - 2)), raw, i, len);
                }
                else
                {
                    Add(units, UnitKind.Format, "", raw, i, len);
                }

                return semi + 1;
            }

            if (e == 'M' && i + 7 < raw.Length && raw[i + 2] == '+' && IsDigitsN(raw, i + 3, 5))
            {
                Add(units, UnitKind.Opaque, "", raw, i, 8);
                return i + 8;
            }

            throw new TextFormatException("código de formato desconocido \\" + e + ".", i);
        }

        /// <summary>
        /// Texto visible de una fracción apilada <c>\S top^bottom;</c>: «top/bottom» (el separador puede ser ^, # o /).
        /// Sin separador, el contenido tal cual.
        /// </summary>
        private static string StackedVisible(string content)
        {
            int sep = content.IndexOfAny(new[] { '^', '#', '/' });
            if (sep < 0)
            {
                return content;
            }

            return content.Substring(0, sep) + "/" + content.Substring(sep + 1);
        }

        /// <summary>Índice tras el «>%» que cierra el campo que empieza en <paramref name="start"/> (anidados contados), o -1.</summary>
        private static int FieldEnd(string raw, int start)
        {
            int depth = 0;
            int i = start;
            while (i + 1 < raw.Length)
            {
                if (raw[i] == '%' && raw[i + 1] == '<')
                {
                    depth++;
                    i += 2;
                }
                else if (raw[i] == '>' && raw[i + 1] == '%')
                {
                    depth--;
                    i += 2;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
                else
                {
                    i++;
                }
            }

            return -1;
        }

        private static bool IsDigitsN(string raw, int start, int count)
        {
            if (start + count > raw.Length)
            {
                return false;
            }

            for (int k = start; k < start + count; k++)
            {
                if (raw[k] < '0' || raw[k] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>null si las llaves { } están balanceadas (ignorando \{ y \}); si no, el motivo. Para texto de cota.</summary>
        public static string? ValidateBraces(string raw)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }

            int depth = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '\\' && i + 1 < raw.Length)
                {
                    i++;
                    continue;
                }

                if (raw[i] == '{')
                {
                    depth++;
                }
                else if (raw[i] == '}')
                {
                    depth--;
                    if (depth < 0)
                    {
                        return "'}' sin '{' de apertura (para un corchete literal use \\}).";
                    }
                }
            }

            return depth == 0 ? null : "'{' sin '}' de cierre.";
        }

        private static bool IsHex4(string raw, int start)
        {
            if (start + 4 > raw.Length)
            {
                return false;
            }

            for (int k = start; k < start + 4; k++)
            {
                if (!Uri.IsHexDigit(raw[k]))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<TextUnit> ParseDbText(string raw)
        {
            var units = new List<TextUnit>(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '%' && i + 1 < raw.Length && raw[i + 1] == '<' && FieldEnd(raw, i) is int fend && fend >= 0)
                {
                    Add(units, UnitKind.Opaque, "", raw, i, fend - i);
                    i = fend;
                    continue;
                }

                if (raw[i] == '%' && i + 2 < raw.Length && raw[i + 1] == '%')
                {
                    char d = raw[i + 2];
                    switch (d)
                    {
                        case '%':
                            Add(units, UnitKind.Literal, "%", raw, i, 3);
                            i += 3;
                            continue;
                        case 'd':
                        case 'D':
                            Add(units, UnitKind.Literal, "°", raw, i, 3);
                            i += 3;
                            continue;
                        case 'c':
                        case 'C':
                            Add(units, UnitKind.Literal, "⌀", raw, i, 3);
                            i += 3;
                            continue;
                        case 'p':
                        case 'P':
                            Add(units, UnitKind.Literal, "±", raw, i, 3);
                            i += 3;
                            continue;
                        case 'u':
                        case 'U':
                        case 'o':
                        case 'O':
                            Add(units, UnitKind.Format, "", raw, i, 3);
                            i += 3;
                            continue;
                    }

                    if (char.IsDigit(d) && i + 5 <= raw.Length && IsDigits3(raw, i + 2))
                    {
                        char ch = (char)int.Parse(raw.Substring(i + 2, 3), CultureInfo.InvariantCulture);
                        UnitKind kind = char.IsControl(ch) ? UnitKind.Opaque : UnitKind.Literal;
                        Add(units, kind, ch.ToString(), raw, i, 5);
                        i += 5;
                        continue;
                    }
                }

                Add(units, UnitKind.Literal, raw[i].ToString(), raw, i, 1);
                i++;
            }

            return units;
        }

        private static bool IsDigits3(string raw, int start)
        {
            for (int k = start; k < start + 3; k++)
            {
                if (raw[k] < '0' || raw[k] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static void Add(List<TextUnit> units, UnitKind kind, string visible, string raw, int start, int length)
        {
            units.Add(new TextUnit(kind, visible, raw.Substring(start, length), start));
        }
    }
}
