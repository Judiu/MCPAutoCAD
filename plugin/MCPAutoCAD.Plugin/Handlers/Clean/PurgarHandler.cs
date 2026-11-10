using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Clean;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Clean
{
    /// <summary>
    /// purgar (protocol.md §5F.2). Hilo principal; requiere documento; ESCRITURA (solo con confirmar: true).
    /// <para>Previsualización (lectura pura, StartOpenCloseTransaction): grafo de referencias en una pasada
    /// (<see cref="ReferenceGraphReader"/>) y punto fijo puro (<see cref="PurgeRules.FixedPoint"/>); nodos = símbolos
    /// candidatos no protegidos de las categorías pedidas; raíces = espacios, símbolos protegidos (exclusiones fijas,
    /// excluir, dependientes de xref) y todos los símbolos de las categorías NO pedidas (sus referencias cuentan como
    /// vivas). Los regapps se evalúan con una ronda de <c>Database.Purge</c> (que solo filtra la colección: no escribe).</para>
    /// <para>Escritura (<see cref="UndoGroup.Run"/>, UNA Transaction, un CTRL+Z): bucle con tope de 20 rondas:
    /// candidatos = símbolos no borrados de las categorías pedidas y no protegidos → <c>Database.Purge</c> (la autoridad:
    /// conoce xdata, diccionarios, estilos de tabla/directriz y bloques dinámicos) → Erase de lo que devuelve; hasta que
    /// una ronda no borra nada. La respuesta compara lo purgado con lo previsto y avisa si difiere (§5F.2).</para>
    /// Coste: ver <see cref="ReferenceGraphReader"/> (lectura del orden de 10⁵ aperturas en el plano madre, 1–3 s); la
    /// escritura abre ForWrite y borra unos miles de registros de símbolo (en el plano madre ~7 000 BTR, la mayoría
    /// anónimos; el borrado de un BTR arrastra su contenido): estimación 1–4 s. Si se superan los 10 s de §3.7 el cliente
    /// recibe INTERNAL "timeout" pero la purga termina como UN paso de deshacer (verificar en el gate con bloques).
    /// </summary>
    internal sealed class PurgarHandler : IToolHandler
    {
        /// <summary>Tope de rondas de la escritura (§5F.2).</summary>
        public const int MaxRounds = 20;

        private const int DefaultSample = 50;

        private static readonly string[] Allowed = { "categorias", "excluir", "solo", "muestra", "confirmar", "preview" };

        public string Name => "purgar";

        public string Description =>
            "PURGA símbolos sin uso (bloques con nombre y anónimos, capas, tipos de línea, estilos de texto y de cota, regapps) iterando " +
            "hasta el punto fijo, como -PURGE repetido, en un solo paso de deshacer. Nunca purga la capa 0, DEFPOINTS, la capa/estilos/tipo " +
            "de línea actuales, capas MCP-*, los espacios ni xrefs. Sin confirmar: true solo PREVISUALIZA (nombres por categoría y entidades " +
            "de cada bloque) y no modifica nada. Con 'solo' (nombres) limita la purga a esos símbolos: los que estén en uso o " +
            "protegidos se informan en no_purgables con el motivo, y el resto de símbolos sin uso no se toca.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""categorias"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 7, ""items"": { ""type"": ""string"", ""enum"": [""bloques"", ""capas"", ""tipos_linea"", ""estilos_texto"", ""estilos_cota"", ""regapps"", ""todo""] }, ""default"": [""todo""], ""description"": ""Qué purgar; todo = las seis categorías."" },
    ""excluir"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Nombres (sin distinguir mayúsculas, de cualquier categoría) que no se purgan aunque no tengan uso."" },
    ""solo"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Purga SOLO estos nombres (sin distinguir mayúsculas; de cualquier categoría pedida: capas, bloques, estilos, tipos de línea, regapps). Los que estén en uso o protegidos no se purgan y se informan en no_purgables con el motivo; los nombres que no existen generan advertencia. Ej.: [\""MURO-OLD\""] purga solo esa capa si no tiene uso."" },
    ""muestra"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200, ""default"": 50, ""description"": ""Máximo de nombres listados por categoría (n lleva siempre el total)."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""categorias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""rondas"": { ""type"": ""integer"" },
    ""purgables"": { ""type"": ""object"" },
    ""protegidos"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } },
    ""excluidos"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""no_purgables"": { ""type"": ""array"", ""description"": ""Solo con 'solo': nombres pedidos que no se purgan, con su categoría y motivo."", ""items"": { ""type"": ""object"", ""properties"": { ""nombre"": { ""type"": ""string"" }, ""categoria"": { ""type"": ""string"" }, ""motivo"": { ""type"": ""string"" } } } },
    ""purgados"": { ""type"": [""object"", ""null""] },
    ""duracion_ms"": { ""type"": ""object"" },
    ""origen_texto"": { ""type"": ""string"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""categorias"", ""rondas"", ""purgables"", ""protegidos"", ""excluidos"", ""no_purgables"", ""purgados"", ""duracion_ms"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Parámetros leídos (sin AutoCAD).</summary>
        private sealed class Params
        {
            public Params(List<string> categorias, List<string>? excluir, List<string>? solo, int muestra, CleanWrite.Common common)
            {
                Categorias = categorias;
                Excluir = excluir;
                Solo = solo;
                Muestra = muestra;
                Common = common;
            }

            /// <summary>Categorías reales pedidas, sin repetir, en el orden de <see cref="PurgeCategories.All"/>.</summary>
            public List<string> Categorias { get; }

            public List<string>? Excluir { get; }

            /// <summary>Ámbito 'solo' (null = todo). Sin distinguir mayúsculas.</summary>
            public List<string>? Solo { get; }

            public int Muestra { get; }

            public CleanWrite.Common Common { get; }
        }

        /// <summary>Resultado de la lectura: grafo, candidatos de la escritura y previsualización calculada.</summary>
        private sealed class Plan
        {
            public Plan(ReferenceGraphReader.Graph graph)
            {
                Graph = graph;
            }

            public ReferenceGraphReader.Graph Graph { get; }

            /// <summary>Índices de los símbolos candidatos (categorías pedidas, no protegidos, no espacios).</summary>
            public List<int> Candidates { get; } = new List<int>();

            public HashSet<int> CandidateSet { get; } = new HashSet<int>();

            /// <summary>Previsualización: índice del símbolo → ronda.</summary>
            public List<(int index, int ronda)> Predicted { get; } = new List<(int index, int ronda)>();

            public int Rounds { get; set; }

            public Dictionary<string, List<string>> Protegidos { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            public List<string> Excluidos { get; } = new List<string>();

            /// <summary>Solo con 'solo': símbolos nombrados que no se purgan, con categoría y motivo (§5F.2, 0.9).</summary>
            public List<(string categoria, string nombre, string motivo)> NoPurgables { get; } = new List<(string categoria, string nombre, string motivo)>();
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var requested = new HashSet<string>(StringComparer.Ordinal);
            if (p.TryGetProperty("categorias", out JsonElement cats))
            {
                if (cats.ValueKind != JsonValueKind.Array || cats.GetArrayLength() < 1 || cats.GetArrayLength() > 7)
                {
                    throw ToolException.InvalidParams("categorias: debe ser un array de 1 a 7 valores");
                }

                int i = 0;
                foreach (JsonElement item in cats.EnumerateArray())
                {
                    string? value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (string.Equals(value, PurgeCategories.Todo, StringComparison.Ordinal))
                    {
                        requested.UnionWith(PurgeCategories.All);
                    }
                    else if (value != null && Contains(PurgeCategories.All, value))
                    {
                        requested.Add(value);
                    }
                    else
                    {
                        throw ToolException.InvalidParams(PurgeCategories.InvalidCategory(i));
                    }

                    i++;
                }
            }
            else
            {
                requested.UnionWith(PurgeCategories.All);
            }

            var categorias = new List<string>();
            foreach (string c in PurgeCategories.All)
            {
                if (requested.Contains(c))
                {
                    categorias.Add(c);
                }
            }

            List<string>? excluir = ParamReader.OptionalStringArray(p, "excluir", 1, 200, 1);
            List<string>? solo = ParamReader.OptionalStringArray(p, "solo", 1, 200, 1);
            int muestra = ParamReader.OptionalInt(p, "muestra", 1, 200) ?? DefaultSample;
            CleanWrite.Common common = CleanWrite.ReadCommon(p);
            return new Params(categorias, excluir, solo, muestra, common);
        }

        private static bool Contains(IReadOnlyList<string> list, string value)
        {
            foreach (string s in list)
            {
                if (string.Equals(s, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db); // L3 desde 0.8.3: no interpreta unidades, INSUNITS = 0 solo advierte

            // Lectura (también en escritura: la previsualización es la referencia para la advertencia de diferencias).
            Stopwatch read = Stopwatch.StartNew();
            Plan plan;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                plan = Read(db, tr, p, warnings);
                tr.Commit();
            }

            long readMs = read.ElapsedMilliseconds;
            if (mode == CleanWrite.Mode.Preview)
            {
                Dictionary<string, object?> r = CleanWrite.Response(ctx.Pid, mode, ins, warnings);
                Fill(r, p, plan, plan.Rounds, null, readMs, null);
                return r;
            }

            return UndoGroup.Run(doc, d => Write(d, ctx.Pid, mode, ins, warnings, p, plan, readMs));
        }

        // ------------------------------------------------------------------ lectura

        private static Plan Read(Database db, Transaction tr, Params p, List<string> warnings)
        {
            var ctx = new PurgeContext(
                RecordName(tr, db.Clayer),
                RecordName(tr, db.Celtype),
                RecordName(tr, db.Textstyle),
                RecordName(tr, db.Dimstyle));

            ReferenceGraphReader.Graph graph = ReferenceGraphReader.Read(db, tr);
            var plan = new Plan(graph);
            var requested = new HashSet<string>(p.Categorias, StringComparer.Ordinal);
            IReadOnlyCollection<string>? excluir = p.Excluir;
            IReadOnlyCollection<string>? solo = p.Solo;

            // excluir / solo: nombres que no existen en ninguna tabla → advertencia (no es error).
            if (excluir != null || solo != null)
            {
                var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ReferenceGraphReader.Symbol s in graph.Symbols)
                {
                    allNames.Add(s.Nombre);
                }

                var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string x in excluir ?? Array.Empty<string>())
                {
                    if (!allNames.Contains(x) && warned.Add(x))
                    {
                        warnings.Add("excluir: no existe ningún símbolo llamado '" + x + "'.");
                    }
                }

                var warnedSolo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string x in solo ?? Array.Empty<string>())
                {
                    if (!allNames.Contains(x) && warnedSolo.Add(x))
                    {
                        warnings.Add("solo: no existe ningún símbolo llamado '" + x + "'.");
                    }
                }
            }

            // Clasificación: nodo (candidato) o raíz.
            int count = graph.Symbols.Count;
            var isProtected = new bool[count];
            var isFixedProtected = new bool[count];
            var nodes = new List<(string categoria, string nombre)>();
            var roots = new List<(string categoria, string nombre)>();
            for (int i = 0; i < count; i++)
            {
                ReferenceGraphReader.Symbol s = graph.Symbols[i];
                if (s.Categoria == ReferenceGraphReader.SpaceCategory)
                {
                    roots.Add(s.Key);
                    isProtected[i] = true;
                    isFixedProtected[i] = true;
                    continue;
                }

                // esXref: en bloques IsFromExternalReference/IsDependent; en el resto, dependiente de xref (decisión:
                // un símbolo de xref nunca se purga, se recarga con la xref).
                bool fixedProt = s.Dependiente || PurgeRules.IsProtected(s.Categoria, s.Nombre, ctx, s.Xref, null);
                bool prot = fixedProt || (excluir != null && PurgeRules.IsProtected(s.Categoria, s.Nombre, ctx, s.Xref, excluir));
                isProtected[i] = prot;
                isFixedProtected[i] = fixedProt;
                // 'solo' (0.9): fuera del ámbito no es candidato (se trata como raíz: sus referencias siguen vivas).
                bool wanted = requested.Contains(s.Categoria) && PurgeRules.InScope(s.Nombre, solo);
                if (wanted && !prot)
                {
                    plan.Candidates.Add(i);
                    plan.CandidateSet.Add(i);
                }

                if (s.Categoria == PurgeCategories.Regapps)
                {
                    continue; // fuera del grafo
                }

                if (wanted && !prot)
                {
                    nodes.Add(s.Key);
                }
                else
                {
                    roots.Add(s.Key);
                }
            }

            var edges = new List<((string categoria, string nombre) desde, (string categoria, string nombre) hacia)>(graph.Edges.Count);
            foreach ((int desde, int hacia) in graph.Edges)
            {
                ReferenceGraphReader.Symbol a = graph.Symbols[desde];
                ReferenceGraphReader.Symbol b = graph.Symbols[hacia];
                if (a.Categoria == PurgeCategories.Regapps || b.Categoria == PurgeCategories.Regapps)
                {
                    continue;
                }

                edges.Add((a.Key, b.Key));
            }

            // Punto fijo puro (GeometryCore).
            var byKey = new Dictionary<(string categoria, string nombre), int>();
            for (int i = 0; i < count; i++)
            {
                byKey[graph.Symbols[i].Key] = i;
            }

            var purgedPreview = new bool[count];
            IReadOnlyList<IReadOnlyList<(string categoria, string nombre)>> rounds = PurgeRules.FixedPoint(nodes, edges, roots);
            for (int r = 0; r < rounds.Count; r++)
            {
                foreach ((string categoria, string nombre) key in rounds[r])
                {
                    if (byKey.TryGetValue(key, out int index) && !purgedPreview[index])
                    {
                        purgedPreview[index] = true;
                        plan.Predicted.Add((index, r + 1));
                    }
                }
            }

            plan.Rounds = rounds.Count;

            // Protegidos sin uso: símbolos protegidos de las categorías pedidas sin ninguna arista entrante desde algo
            // que sobrevive (raíz o nodo no purgado). Los espacios y los dependientes de xref no se listan.
            var aliveTargets = new HashSet<int>();
            foreach ((int desde, int hacia) in graph.Edges)
            {
                if (!purgedPreview[desde])
                {
                    aliveTargets.Add(hacia);
                }
            }

            foreach (string cat in p.Categorias)
            {
                plan.Protegidos[cat] = new List<string>();
            }

            // Regapps: una ronda de Database.Purge sobre todos (solo filtra la colección; los protegidos se separan después).
            HashSet<int>? unusedRegApps = null;
            if (requested.Contains(PurgeCategories.Regapps) && graph.RegApps.Count > 0)
            {
                unusedRegApps = new HashSet<int>();
                var coll = new ObjectIdCollection();
                foreach (int i in graph.RegApps)
                {
                    if (PurgeRules.InScope(graph.Symbols[i].Nombre, solo))
                    {
                        coll.Add(graph.Symbols[i].Id);
                    }
                }

                if (coll.Count > 0)
                {
                    db.Purge(coll);
                }
                foreach (ObjectId id in coll)
                {
                    if (graph.IndexById.TryGetValue(id, out int i))
                    {
                        unusedRegApps.Add(i);
                    }
                }
            }

            bool regAppsPurgable = false;
            for (int i = 0; i < count; i++)
            {
                ReferenceGraphReader.Symbol s = graph.Symbols[i];
                if (!requested.Contains(s.Categoria) || s.Dependiente || !PurgeRules.InScope(s.Nombre, solo))
                {
                    continue;
                }

                bool unused;
                if (s.Categoria == PurgeCategories.Regapps)
                {
                    unused = unusedRegApps != null && unusedRegApps.Contains(i);
                    if (unused && !isProtected[i])
                    {
                        plan.Predicted.Add((i, 1));
                        regAppsPurgable = true;
                        continue;
                    }
                }
                else
                {
                    unused = !aliveTargets.Contains(i);
                }

                if (!isProtected[i] || !unused)
                {
                    continue;
                }

                if (isFixedProtected[i])
                {
                    plan.Protegidos[s.Categoria].Add(s.Nombre + (IsCurrent(s, ctx) ? " (actual)" : ""));
                }
                else
                {
                    plan.Excluidos.Add(s.Nombre);
                }
            }

            if (regAppsPurgable && plan.Rounds < 1)
            {
                plan.Rounds = 1;
            }

            // 'solo' (0.9): cada nombre pedido que NO se va a purgar, con su motivo.
            if (solo != null)
            {
                var predictedSet = new HashSet<int>();
                foreach ((int index, _) in plan.Predicted)
                {
                    predictedSet.Add(index);
                }

                for (int i = 0; i < count; i++)
                {
                    ReferenceGraphReader.Symbol s = graph.Symbols[i];
                    if (!PurgeRules.InScope(s.Nombre, solo) || predictedSet.Contains(i))
                    {
                        continue;
                    }

                    bool esEspacio = s.Categoria == ReferenceGraphReader.SpaceCategory;
                    string motivo = PurgeRules.MotivoNoPurgable(
                        esEspacio || requested.Contains(s.Categoria),
                        s.Dependiente,
                        esEspacio || isFixedProtected[i],
                        isProtected[i] && !isFixedProtected[i]);
                    plan.NoPurgables.Add((s.Categoria, s.Nombre, motivo));
                }

                plan.NoPurgables.Sort((a, b) =>
                {
                    int c = StringComparer.Ordinal.Compare(a.categoria, b.categoria);
                    return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.nombre, b.nombre);
                });
            }

            return plan;
        }

        private static string RecordName(Transaction tr, ObjectId id)
        {
            try
            {
                if (!id.IsNull && tr.GetObject(id, OpenMode.ForRead, false) is SymbolTableRecord rec)
                {
                    return rec.Name;
                }
            }
            catch (Exception)
            {
                // Sin registro actual legible.
            }

            return "";
        }

        private static bool IsCurrent(ReferenceGraphReader.Symbol s, PurgeContext ctx)
        {
            string? current = s.Categoria switch
            {
                PurgeCategories.Capas => ctx.CapaActual,
                PurgeCategories.TiposLinea => ctx.TipoLineaActual,
                PurgeCategories.EstilosTexto => ctx.EstiloTextoActual,
                PurgeCategories.EstilosCota => ctx.EstiloCotaActual,
                _ => null,
            };
            return current != null && current.Length > 0 && string.Equals(current, s.Nombre, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ escritura

        /// <summary>
        /// Purga dentro de UNA transacción (dentro de UndoGroup.Run: un CTRL+Z devuelve todas las rondas). Database.Purge
        /// decide; solo se borra lo que devuelve y que además es candidato de la lectura (revalidación: no borrado,
        /// registro de símbolo). Si algo lanza, la transacción se descarta y no se purga nada.
        /// </summary>
        private static object Write(Document doc, int pid, CleanWrite.Mode mode, int ins, List<string> warnings, Params p, Plan plan, long readMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Database db = doc.Database;
            ReferenceGraphReader.Graph graph = plan.Graph;
            var purged = new List<(int index, int ronda)>();
            int rounds = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                for (int r = 1; r <= MaxRounds; r++)
                {
                    var coll = new ObjectIdCollection();
                    foreach (int i in plan.Candidates)
                    {
                        ObjectId id = graph.Symbols[i].Id;
                        if (!id.IsNull && !id.IsErased)
                        {
                            coll.Add(id);
                        }
                    }

                    if (coll.Count == 0)
                    {
                        break;
                    }

                    db.Purge(coll); // deja en la colección solo lo purgable (no escribe)
                    int erasedThisRound = 0;
                    foreach (ObjectId id in coll)
                    {
                        if (id.IsNull || id.IsErased || !graph.IndexById.TryGetValue(id, out int i) || !plan.CandidateSet.Contains(i))
                        {
                            continue;
                        }

                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is SymbolTableRecord rec))
                        {
                            continue;
                        }

                        rec.Erase();
                        purged.Add((i, r));
                        erasedThisRound++;
                    }

                    if (erasedThisRound == 0)
                    {
                        break;
                    }

                    rounds = r;
                }

                tr.Commit();
            }

            long writeMs = sw.ElapsedMilliseconds;
            PluginLog.Info(string.Format(
                CultureInfo.InvariantCulture, "purgar: {0} símbolos purgados en {1} rondas en {2} ms", purged.Count, rounds, writeMs));

            if (rounds >= MaxRounds)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "purgar: se alcanzó el tope de {0} rondas; puede quedar algún símbolo sin uso (repita la llamada).",
                    MaxRounds));
            }

            // Diferencia con la previsualización (§5F.2).
            var predicted = new HashSet<int>();
            foreach ((int index, _) in plan.Predicted)
            {
                predicted.Add(index);
            }

            var real = new HashSet<int>();
            foreach ((int index, _) in purged)
            {
                real.Add(index);
            }

            int notPurged = 0;
            foreach (int i in predicted)
            {
                if (!real.Contains(i))
                {
                    notPurged++;
                }
            }

            int unexpected = 0;
            foreach (int i in real)
            {
                if (!predicted.Contains(i))
                {
                    unexpected++;
                }
            }

            if (notPurged > 0 || unexpected > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "La purga real difiere de la previsualización: {0} símbolo(s) previstos no se purgaron (referenciados por xdata, diccionarios o bloques dinámicos) y {1} no previstos sí.",
                    notPurged,
                    unexpected));
            }

            Dictionary<string, object?> result = CleanWrite.Response(pid, mode, ins, warnings);
            Fill(result, p, plan, rounds, purged, readMs, writeMs);
            return result;
        }

        // ------------------------------------------------------------------ respuesta

        private static void Fill(
            Dictionary<string, object?> r, Params p, Plan plan, int rounds, List<(int index, int ronda)>? purged, long readMs, long? writeMs)
        {
            r["categorias"] = p.Categorias;
            r["rondas"] = rounds;
            r["purgables"] = ByCategory(plan.Graph, p, plan.Predicted);
            var protegidos = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string cat in p.Categorias)
            {
                List<string> list = plan.Protegidos.TryGetValue(cat, out List<string>? l) ? l : new List<string>();
                list.Sort(StringComparer.OrdinalIgnoreCase);
                protegidos[cat] = list;
            }

            r["protegidos"] = protegidos;
            plan.Excluidos.Sort(StringComparer.OrdinalIgnoreCase);
            r["excluidos"] = plan.Excluidos;
            r["no_purgables"] = plan.NoPurgables.Select(x => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["nombre"] = x.nombre,
                ["categoria"] = x.categoria,
                ["motivo"] = x.motivo,
            }).ToList();
            r["purgados"] = purged == null ? null : ByCategory(plan.Graph, p, purged);
            r["duracion_ms"] = new Dictionary<string, object?> { ["lectura"] = readMs, ["escritura"] = writeMs };
            r["origen_texto"] = "contenido_del_plano";
        }

        /// <summary>
        /// Forma de purgables/purgados (§5F.2): por categoría pedida {n, nombres (hasta muestra), nombres_truncados};
        /// en bloques además n_con_nombre, n_anonimos, n_entidades y por nombre n_entidades y unidades. Orden de nombres:
        /// ronda, después n_entidades descendente (bloques) y nombre.
        /// </summary>
        private static Dictionary<string, object?> ByCategory(ReferenceGraphReader.Graph graph, Params p, List<(int index, int ronda)> items)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string cat in p.Categorias)
            {
                var list = new List<(ReferenceGraphReader.Symbol s, int ronda)>();
                foreach ((int index, int ronda) in items)
                {
                    ReferenceGraphReader.Symbol s = graph.Symbols[index];
                    if (s.Categoria == cat)
                    {
                        list.Add((s, ronda));
                    }
                }

                bool blocks = cat == PurgeCategories.Bloques;
                list.Sort((a, b) =>
                {
                    int c = a.ronda.CompareTo(b.ronda);
                    if (c == 0 && blocks)
                    {
                        c = b.s.NEntidades.CompareTo(a.s.NEntidades);
                    }

                    return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.s.Nombre, b.s.Nombre);
                });

                var nombres = new List<Dictionary<string, object?>>();
                int anon = 0;
                long entities = 0;
                foreach ((ReferenceGraphReader.Symbol s, int ronda) in list)
                {
                    if (blocks)
                    {
                        entities += s.NEntidades;
                        if (s.Anonimo)
                        {
                            anon++;
                        }
                    }

                    if (nombres.Count >= p.Muestra)
                    {
                        continue;
                    }

                    var row = new Dictionary<string, object?> { ["nombre"] = s.Nombre };
                    if (blocks)
                    {
                        row["n_entidades"] = s.NEntidades;
                    }

                    row["ronda"] = ronda;
                    if (blocks)
                    {
                        row["unidades"] = s.Unidades;
                    }

                    nombres.Add(row);
                }

                var entry = new Dictionary<string, object?> { ["n"] = list.Count };
                if (blocks)
                {
                    entry["n_con_nombre"] = list.Count - anon;
                    entry["n_anonimos"] = anon;
                    entry["n_entidades"] = entities;
                }

                entry["nombres"] = nombres;
                entry["nombres_truncados"] = list.Count > nombres.Count;
                result[cat] = entry;
            }

            return result;
        }
    }
}
