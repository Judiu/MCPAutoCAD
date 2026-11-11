using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Patrones de nombre de capa con comodines: '*' (cualquier secuencia, también vacía) y '?' (exactamente un carácter).
    /// El patrón se ancla a todo el nombre y no distingue mayúsculas (cultura invariante). Un patrón sin comodines
    /// equivale a una comparación exacta sin distinguir mayúsculas. Sin regex; coste O(n·m) en el peor caso.
    /// </summary>
    public static class LayerPattern
    {
        /// <summary>true si <paramref name="name"/> cumple <paramref name="pattern"/> entero (sin distinguir mayúsculas).</summary>
        public static bool Matches(string name, string pattern)
        {
            if (name == null || pattern == null)
            {
                return false;
            }

            int n = 0;
            int p = 0;
            int star = -1;
            int mark = 0;
            while (n < name.Length)
            {
                if (p < pattern.Length && pattern[p] == '*')
                {
                    // Se recuerda el último '*' y se intenta primero el caso "no consume nada".
                    star = p++;
                    mark = n;
                }
                else if (p < pattern.Length && (pattern[p] == '?' || SameChar(pattern[p], name[n])))
                {
                    p++;
                    n++;
                }
                else if (star >= 0)
                {
                    // Retroceso: el último '*' absorbe un carácter más.
                    p = star + 1;
                    n = ++mark;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
            {
                p++;
            }

            return p == pattern.Length;
        }

        /// <summary>
        /// true si <paramref name="name"/> cumple AL MENOS un patrón del filtro. Filtro null o vacío = no hay filtro → true
        /// (misma convención que <see cref="EntityFilter.MatchesAny"/>).
        /// </summary>
        public static bool MatchesAny(string name, IReadOnlyCollection<string>? patterns)
        {
            if (patterns == null || patterns.Count == 0)
            {
                return true;
            }

            if (name == null)
            {
                return false;
            }

            foreach (string pattern in patterns)
            {
                if (Matches(name, pattern))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameChar(char a, char b) =>
            a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }
}
