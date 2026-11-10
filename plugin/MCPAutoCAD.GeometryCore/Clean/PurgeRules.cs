using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Clean
{
    /// <summary>Categorías del parámetro <c>categorias</c> de purgar (protocol.md §5F.2), en el orden de la respuesta.</summary>
    public static class PurgeCategories
    {
        public const string Bloques = "bloques";
        public const string Capas = "capas";
        public const string TiposLinea = "tipos_linea";
        public const string EstilosTexto = "estilos_texto";
        public const string EstilosCota = "estilos_cota";
        public const string Regapps = "regapps";
        public const string Todo = "todo";

        /// <summary>Las seis categorías reales (sin "todo"), en el orden de la respuesta.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Bloques, Capas, TiposLinea, EstilosTexto, EstilosCota, Regapps };

        /// <summary>Texto de INVALID_PARAMS para un valor desconocido: "categorias[&lt;i&gt;]: debe ser uno de bloques, capas, …, todo".</summary>
        public static string InvalidCategory(int index) =>
            "categorias[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "]: debe ser uno de " + string.Join(", ", All) + ", " + Todo;
    }

    /// <summary>Lo que el plugin sabe del dibujo para decidir qué NUNCA se purga (§5F.2).</summary>
    public sealed record PurgeContext(
        string CapaActual,
        string TipoLineaActual,
        string EstiloTextoActual,
        string EstiloCotaActual);

    /// <summary>
    /// Exclusiones fijas de purgar (protocol.md §5F.2) y punto fijo de la purga sobre un grafo de referencias
    /// simulado (previsualización). Puro, sin AutoCAD.
    /// </summary>
    public static class PurgeRules
    {
        /// <summary>Prefijo de las capas del MCP, nunca purgables.</summary>
        public const string McpLayerPrefix = "MCP-";

        /// <summary>
        /// true si el símbolo <paramref name="nombre"/> de la categoría nunca se purga: capas "0", "DEFPOINTS", la
        /// actual y las "MCP-*"; tipos de línea "ByLayer", "ByBlock", "Continuous" y el actual; estilos de texto
        /// "Standard" y el actual; estilos de cota "Standard" y el actual; bloques "*Model_Space", "*Paper_Space*"
        /// (cualquier sufijo) y los de xref (<paramref name="esXref"/>); además cualquier nombre de
        /// <paramref name="excluir"/> (sin distinguir mayúsculas; null = ninguno). Regapps: solo "ACAD" y los de excluir.
        /// Comparaciones sin distinguir mayúsculas. Categoría desconocida (incluida "todo") → ArgumentException con
        /// <see cref="PurgeCategories.InvalidCategory"/>(0).
        /// </summary>
        public static bool IsProtected(string categoria, string nombre, PurgeContext ctx, bool esXref, IReadOnlyCollection<string>? excluir)
        {
            if (ctx == null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }

            int cat = CategoryIndex(categoria);
            if (cat < 0)
            {
                throw new ArgumentException(PurgeCategories.InvalidCategory(0));
            }

            nombre ??= "";
            if (excluir != null)
            {
                foreach (string x in excluir)
                {
                    if (Eq(x, nombre))
                    {
                        return true;
                    }
                }
            }

            switch (PurgeCategories.All[cat])
            {
                case PurgeCategories.Bloques:
                    return esXref
                        || Eq(nombre, "*Model_Space")
                        || nombre.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase);
                case PurgeCategories.Capas:
                    return Eq(nombre, "0") || Eq(nombre, "DEFPOINTS") || Eq(nombre, ctx.CapaActual)
                        || nombre.StartsWith(McpLayerPrefix, StringComparison.OrdinalIgnoreCase);
                case PurgeCategories.TiposLinea:
                    return Eq(nombre, "ByLayer") || Eq(nombre, "ByBlock") || Eq(nombre, "Continuous") || Eq(nombre, ctx.TipoLineaActual);
                case PurgeCategories.EstilosTexto:
                    return Eq(nombre, "Standard") || Eq(nombre, ctx.EstiloTextoActual);
                case PurgeCategories.EstilosCota:
                    return Eq(nombre, "Standard") || Eq(nombre, ctx.EstiloCotaActual);
                default: // regapps
                    return Eq(nombre, "ACAD");
            }
        }

        /// <summary>
        /// Parámetro 'solo' de purgar: true si el símbolo <paramref name="nombre"/> está en el ámbito pedido. Null = sin
        /// 'solo' (todo el ámbito). Comparación sin distinguir mayúsculas. Fuera de ámbito, un símbolo nunca se purga
        /// (se trata como raíz: sus referencias siguen vivas).
        /// </summary>
        public static bool InScope(string? nombre, IReadOnlyCollection<string>? solo)
        {
            if (solo == null)
            {
                return true;
            }

            foreach (string x in solo)
            {
                if (Eq(x, nombre ?? ""))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Motivo por el que un símbolo nombrado en 'solo' NO se purga (se informa en no_purgables). Orden de prioridad:
        /// categoría no pedida → dependiente de xref → protegido fijo → excluido → en uso (referenciado por algo vivo).
        /// </summary>
        public static string MotivoNoPurgable(bool categoriaPedida, bool dependienteXref, bool protegidoFijo, bool excluido)
        {
            if (!categoriaPedida)
            {
                return "categoría no pedida en 'categorias'";
            }

            if (dependienteXref)
            {
                return "dependiente de xref (se recarga con la xref)";
            }

            if (protegidoFijo)
            {
                return "protegido (capa 0/DEFPOINTS/actual, capa MCP-*, estilo o tipo de línea estándar o actual, espacio o xref)";
            }

            if (excluido)
            {
                return "excluido por 'excluir'";
            }

            return "en uso (referenciado por entidades u otros símbolos)";
        }

        /// <summary>Índice de la categoría en <see cref="PurgeCategories.All"/> (sin distinguir mayúsculas) o -1 (incluido "todo").</summary>
        public static int CategoryIndex(string? categoria)
        {
            if (categoria == null)
            {
                return -1;
            }

            for (int i = 0; i < PurgeCategories.All.Count; i++)
            {
                if (Eq(PurgeCategories.All[i], categoria))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Punto fijo de la purga sobre un grafo: <paramref name="nodos"/> = (categoria, nombre) de cada símbolo
        /// candidato; <paramref name="referencias"/> = aristas "quien referencia → referenciado" donde el origen es
        /// un nodo o una raíz; <paramref name="raices"/> = nodos que nunca se purgan (espacios, símbolos protegidos,
        /// los que algo fuera del grafo referencia). Devuelve las rondas: la ronda 1 son los nodos sin ninguna
        /// referencia viva; al purgarlos, sus referencias desaparecen y pueden liberar la ronda 2 (bloques anidados,
        /// capas usadas solo dentro de un bloque purgado, tipos de línea de esas capas…), hasta que ninguna ronda
        /// añade nada. Un nodo que es raíz nunca aparece. Orden dentro de la ronda: por categoría
        /// (<see cref="PurgeCategories.All"/>) y nombre ordinal sin mayúsculas. Sin ciclos entre nodos no alcanzables
        /// desde una raíz, el ciclo entero se purga en la misma ronda (como hace -PURGE con bloques que se
        /// referencian mutuamente sin inserción en el dibujo).
        /// Implementación: lo alcanzable desde una raíz nunca se purga; entre lo no alcanzable se agrupan los ciclos
        /// (componentes fuertemente conexas) y la ronda de cada grupo es 1 + la mayor ronda de los grupos que lo
        /// referencian (lo mismo que iterar "purgar lo que no tiene referencia viva" hasta el punto fijo). Símbolos
        /// comparados sin distinguir mayúsculas (categoría y nombre); se devuelve el nombre tal como llegó en
        /// <paramref name="nodos"/>. Se ignoran las aristas hacia algo que no es nodo, las que salen de algo que no es
        /// nodo ni raíz y las de un nodo a sí mismo.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<(string categoria, string nombre)>> FixedPoint(
            IReadOnlyCollection<(string categoria, string nombre)> nodos,
            IReadOnlyCollection<((string categoria, string nombre) desde, (string categoria, string nombre) hacia)> referencias,
            IReadOnlyCollection<(string categoria, string nombre)> raices)
        {
            if (nodos == null)
            {
                throw new ArgumentNullException(nameof(nodos));
            }

            if (referencias == null)
            {
                throw new ArgumentNullException(nameof(referencias));
            }

            if (raices == null)
            {
                throw new ArgumentNullException(nameof(raices));
            }

            var comparer = SymbolComparer.Instance;
            var rootSet = new HashSet<(string categoria, string nombre)>(raices, comparer);

            // Nodos candidatos (sin raíces), con el nombre tal como llegó la primera vez.
            var index = new Dictionary<(string categoria, string nombre), int>(comparer);
            var names = new List<(string categoria, string nombre)>();
            foreach (var n in nodos)
            {
                if (rootSet.Contains(n) || index.ContainsKey(n))
                {
                    continue;
                }

                index[n] = names.Count;
                names.Add(n);
            }

            int count = names.Count;
            var succ = new List<int>[count];
            for (int i = 0; i < count; i++)
            {
                succ[i] = new List<int>();
            }

            // Alcanzables desde las raíces: nunca se purgan.
            var reachable = new bool[count];
            var stack = new Stack<int>();
            foreach (var edge in referencias)
            {
                if (!index.TryGetValue(edge.hacia, out int to))
                {
                    continue; // destino que no es candidato (raíz o desconocido)
                }

                if (rootSet.Contains(edge.desde))
                {
                    if (!reachable[to])
                    {
                        reachable[to] = true;
                        stack.Push(to);
                    }
                }
                else if (index.TryGetValue(edge.desde, out int from) && from != to)
                {
                    succ[from].Add(to);
                }
            }

            while (stack.Count > 0)
            {
                int u = stack.Pop();
                foreach (int w in succ[u])
                {
                    if (!reachable[w])
                    {
                        reachable[w] = true;
                        stack.Push(w);
                    }
                }
            }

            int[] comp = StronglyConnected(count, succ, reachable, out int nComp);
            var compSucc = new List<int>[nComp];
            var members = new List<int>[nComp];
            var indeg = new int[nComp];
            for (int c = 0; c < nComp; c++)
            {
                compSucc[c] = new List<int>();
                members[c] = new List<int>();
            }

            var seenEdges = new HashSet<(int, int)>();
            for (int u = 0; u < count; u++)
            {
                if (reachable[u])
                {
                    continue;
                }

                members[comp[u]].Add(u);
                foreach (int w in succ[u])
                {
                    if (reachable[w])
                    {
                        continue; // referencia a algo vivo: no retrasa nada
                    }

                    int cu = comp[u], cw = comp[w];
                    if (cu != cw && seenEdges.Add((cu, cw)))
                    {
                        compSucc[cu].Add(cw);
                        indeg[cw]++;
                    }
                }
            }

            var rounds = new List<IReadOnlyList<(string categoria, string nombre)>>();
            var current = new List<int>();
            for (int c = 0; c < nComp; c++)
            {
                if (indeg[c] == 0)
                {
                    current.Add(c);
                }
            }

            while (current.Count > 0)
            {
                var round = new List<(string categoria, string nombre)>();
                var next = new List<int>();
                foreach (int c in current)
                {
                    foreach (int u in members[c])
                    {
                        round.Add(names[u]);
                    }

                    foreach (int d in compSucc[c])
                    {
                        if (--indeg[d] == 0)
                        {
                            next.Add(d);
                        }
                    }
                }

                round.Sort(CompareInRound);
                rounds.Add(round);
                current = next;
            }

            return rounds;
        }

        private static bool Eq(string? a, string? b) =>
            a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Orden dentro de una ronda: índice de categoría en All (desconocidas al final) y nombre ordinal sin mayúsculas.</summary>
        private static int CompareInRound((string categoria, string nombre) a, (string categoria, string nombre) b)
        {
            int ca = CategoryIndex(a.categoria), cb = CategoryIndex(b.categoria);
            ca = ca < 0 ? int.MaxValue : ca;
            cb = cb < 0 ? int.MaxValue : cb;
            int r = ca.CompareTo(cb);
            if (r != 0)
            {
                return r;
            }

            r = StringComparer.OrdinalIgnoreCase.Compare(a.categoria, b.categoria);
            return r != 0 ? r : StringComparer.OrdinalIgnoreCase.Compare(a.nombre, b.nombre);
        }

        /// <summary>
        /// Tarjan iterativo restringido a los nodos no alcanzables (sin recursión: miles de anónimos encadenados no
        /// desbordan la pila). Devuelve la componente de cada nodo (-1 si es alcanzable).
        /// </summary>
        private static int[] StronglyConnected(int count, List<int>[] succ, bool[] reachable, out int nComp)
        {
            var comp = new int[count];
            var low = new int[count];
            var order = new int[count];
            var onStack = new bool[count];
            for (int i = 0; i < count; i++)
            {
                comp[i] = -1;
                order[i] = -1;
            }

            var sccStack = new Stack<int>();
            var work = new Stack<(int node, int next)>();
            int counter = 0;
            nComp = 0;
            for (int s = 0; s < count; s++)
            {
                if (reachable[s] || order[s] >= 0)
                {
                    continue;
                }

                order[s] = low[s] = counter++;
                sccStack.Push(s);
                onStack[s] = true;
                work.Push((s, 0));
                while (work.Count > 0)
                {
                    (int v, int i) = work.Pop();
                    if (i < succ[v].Count)
                    {
                        work.Push((v, i + 1));
                        int w = succ[v][i];
                        if (reachable[w])
                        {
                            continue;
                        }

                        if (order[w] < 0)
                        {
                            order[w] = low[w] = counter++;
                            sccStack.Push(w);
                            onStack[w] = true;
                            work.Push((w, 0));
                        }
                        else if (onStack[w])
                        {
                            low[v] = Math.Min(low[v], order[w]);
                        }

                        continue;
                    }

                    if (low[v] == order[v])
                    {
                        int w;
                        do
                        {
                            w = sccStack.Pop();
                            onStack[w] = false;
                            comp[w] = nComp;
                        }
                        while (w != v);
                        nComp++;
                    }

                    if (work.Count > 0)
                    {
                        int parent = work.Peek().node;
                        low[parent] = Math.Min(low[parent], low[v]);
                    }
                }
            }

            return comp;
        }

        /// <summary>Igualdad de símbolos (categoría, nombre) sin distinguir mayúsculas.</summary>
        private sealed class SymbolComparer : IEqualityComparer<(string categoria, string nombre)>
        {
            public static readonly SymbolComparer Instance = new SymbolComparer();

            public bool Equals((string categoria, string nombre) a, (string categoria, string nombre) b) =>
                string.Equals(a.categoria, b.categoria, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.nombre, b.nombre, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string categoria, string nombre) s) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(s.categoria ?? ""),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(s.nombre ?? ""));
        }
    }
}
