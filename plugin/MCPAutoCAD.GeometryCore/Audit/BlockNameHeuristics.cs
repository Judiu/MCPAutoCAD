using System;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>
    /// Heurística de nombres de bloque arbitrarios (protocol.md §5C.3 b). Se evalúa sobre el nombre en mayúsculas
    /// invariantes; "letra" = A–Z; vocales = A, E, I, O, U (la Y no es vocal). Motivos en orden (el primero gana):
    /// sin_vocales, letra_repetida, secuencia_teclado, par_repetido.
    /// </summary>
    public static class BlockNameHeuristics
    {
        public const string SinVocales = "sin_vocales";
        public const string LetraRepetida = "letra_repetida";
        public const string SecuenciaTeclado = "secuencia_teclado";
        public const string ParRepetido = "par_repetido";

        private static readonly string[] KeyboardRows = { "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };

        /// <summary>
        /// Motivo de sospecha o null. Nombres vacíos, anónimos ('*…') o de AutoCAD ('A$C…') → null (no se evalúan).
        /// </summary>
        public static string? Suspicion(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)
                || nombre.StartsWith("*", StringComparison.Ordinal)
                || nombre.StartsWith("A$C", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string s = nombre.ToUpperInvariant();

            if (HasConsonantRun(s, 5))
            {
                return SinVocales;
            }

            if (HasRepeatedLetter(s, 4))
            {
                return LetraRepetida;
            }

            if (HasKeyboardSequence(s))
            {
                return SecuenciaTeclado;
            }

            if (HasRepeatedPair(s))
            {
                return ParRepetido;
            }

            return null;
        }

        private static bool IsLetter(char c) => c >= 'A' && c <= 'Z';

        private static bool IsVowel(char c) => c == 'A' || c == 'E' || c == 'I' || c == 'O' || c == 'U';

        /// <summary>Racha de ≥ <paramref name="min"/> letras consecutivas sin vocal (otros caracteres cortan).</summary>
        private static bool HasConsonantRun(string s, int min)
        {
            int run = 0;
            foreach (char c in s)
            {
                if (IsLetter(c) && !IsVowel(c))
                {
                    run++;
                    if (run >= min)
                    {
                        return true;
                    }
                }
                else
                {
                    run = 0;
                }
            }

            return false;
        }

        /// <summary>La misma letra ≥ <paramref name="min"/> veces seguidas.</summary>
        private static bool HasRepeatedLetter(string s, int min)
        {
            int run = 0;
            char prev = '\0';
            foreach (char c in s)
            {
                if (IsLetter(c) && c == prev)
                {
                    run++;
                }
                else
                {
                    run = IsLetter(c) ? 1 : 0;
                }

                prev = c;
                if (run >= min)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// ≥ 4 letras consecutivas que son subcadena de una fila del teclado, o el nombre completo (solo letras) de
        /// 3 letras subcadena de una fila.
        /// </summary>
        private static bool HasKeyboardSequence(string s)
        {
            if (s.Length == 3 && IsLetter(s[0]) && IsLetter(s[1]) && IsLetter(s[2]) && InRow(s))
            {
                return true;
            }

            // Si una ventana de 5+ letras es subcadena de una fila, su prefijo de 4 también: basta mirar ventanas de 4.
            for (int i = 0; i + 4 <= s.Length; i++)
            {
                if (IsLetter(s[i]) && IsLetter(s[i + 1]) && IsLetter(s[i + 2]) && IsLetter(s[i + 3])
                    && InRow(s.Substring(i, 4)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool InRow(string fragment)
        {
            foreach (string row in KeyboardRows)
            {
                if (row.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Un par de letras distintas repetido ≥ 2 veces seguidas: (XY){2,}.</summary>
        private static bool HasRepeatedPair(string s)
        {
            for (int i = 0; i + 4 <= s.Length; i++)
            {
                char x = s[i];
                char y = s[i + 1];
                if (IsLetter(x) && IsLetter(y) && x != y && s[i + 2] == x && s[i + 3] == y)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
