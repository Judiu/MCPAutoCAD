using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Clean
{
    /// <summary>
    /// Lectura en UNA pasada del grafo de referencias entre símbolos que usa la previsualización de purgar
    /// (protocol.md §5F.2). Solo lectura, hilo principal, dentro de la transacción del llamador (StartOpenCloseTransaction):
    /// nunca abre nada para escritura ni ejecuta comandos.
    /// <para>Símbolos: todos los registros no borrados de BlockTable (los BTR de layout, incluido *Model_Space, se
    /// registran con la categoría <see cref="SpaceCategory"/>), LayerTable, LinetypeTable, TextStyleTable (los estilos
    /// de forma <c>IsShapeFile</c> son registros normales), DimStyleTable y RegAppTable (los regapps no tienen aristas:
    /// los evalúa <c>Database.Purge</c>).</para>
    /// <para>Aristas "quien referencia → referenciado" (índices de <see cref="Symbols"/>), sin repetir:</para>
    /// <list type="bullet">
    /// <item>por cada entidad de cada espacio y del interior de cada definición (con nombre y anónimas; no las de xref):
    /// propietario → capa; → tipo de línea (salvo ByLayer/ByBlock); → estilo de texto (DBText, AttributeDefinition,
    /// MText, MLeader y cada AttributeReference de un INSERT, que además aporta su capa y su tipo de línea); → estilo de
    /// cota (Dimension, Leader) y los bloques de flecha/estilo de texto/tipos de línea sobrescritos en la cota;
    /// → bloque (BlockReference.BlockTableRecord, el DynamicBlockTableRecord de un bloque dinámico, Dimension.DimBlockId,
    /// el bloque de contenido de un MLeader);</item>
    /// <item>capa → su tipo de línea; tipo de línea → estilos de forma/texto de sus trazos complejos;</item>
    /// <item>estilo de cota → Dimtxsty, Dimblk, Dimblk1, Dimblk2, Dimldrblk, Dimltype, Dimltex1, Dimltex2;</item>
    /// <item>bloque dinámico → nada; cada representación anónima suya (GetAnonymousBlockIds) → la definición dinámica
    /// (así la previsualización conserva la definición con nombre mientras viva alguna representación, como
    /// Database.Purge; si la API no las devuelve, la escritura lo cubre igualmente con Database.Purge).</item>
    /// </list>
    /// Coste: abre cada entidad de modelo, layouts y definiciones una vez (en el plano madre ~37 000 del modelo más el
    /// interior de 101 definiciones con nombre —una de 36 948 entidades— y de 7 413 anónimas: del orden de 10⁵ aperturas,
    /// estimación 1–3 s). No se aplica tope de coste: un grafo parcial daría una purga falsa (L8).
    /// </summary>
    internal static class ReferenceGraphReader
    {
        /// <summary>Categoría interna de los BTR de layout (*Model_Space, *Paper_Space*): siempre raíz.</summary>
        public const string SpaceCategory = "espacio";

        /// <summary>Un símbolo leído.</summary>
        internal sealed class Symbol
        {
            public Symbol(ObjectId id, string categoria, string nombre, bool dependiente, bool xref, bool anonimo, int unidades)
            {
                Id = id;
                Categoria = categoria;
                Nombre = nombre;
                Dependiente = dependiente;
                Xref = xref;
                Anonimo = anonimo;
                Unidades = unidades;
            }

            public ObjectId Id { get; }

            /// <summary>Una de <see cref="PurgeCategories.All"/> o <see cref="SpaceCategory"/>.</summary>
            public string Categoria { get; }

            public string Nombre { get; }

            /// <summary>IsDependent (símbolo que viene de una xref) o nombre con '|'.</summary>
            public bool Dependiente { get; }

            /// <summary>Bloques: IsFromExternalReference o IsDependent (lo que PurgeRules.IsProtected llama esXref).</summary>
            public bool Xref { get; }

            /// <summary>Bloques: definición anónima (*U, *D, *X, *A, *T…).</summary>
            public bool Anonimo { get; }

            /// <summary>Bloques: (int)BlockTableRecord.Units; 0 en el resto.</summary>
            public int Unidades { get; }

            /// <summary>Bloques: ids no borrados del BTR (sin abrir su interior); 0 en el resto.</summary>
            public int NEntidades { get; set; }

            /// <summary>Clave del grafo para PurgeRules.FixedPoint.</summary>
            public (string categoria, string nombre) Key => (Categoria, Nombre);
        }

        /// <summary>Resultado de la lectura.</summary>
        internal sealed class Graph
        {
            public List<Symbol> Symbols { get; } = new List<Symbol>();

            public Dictionary<ObjectId, int> IndexById { get; } = new Dictionary<ObjectId, int>();

            /// <summary>Aristas (desde, hacia) como índices de <see cref="Symbols"/>; sin repetidas ni bucles.</summary>
            public HashSet<(int desde, int hacia)> Edges { get; } = new HashSet<(int desde, int hacia)>();

            /// <summary>Índices (en <see cref="Symbols"/>) de los RegAppTableRecord no borrados (fuera del grafo).</summary>
            public List<int> RegApps { get; } = new List<int>();
        }

        /// <summary>Lee todos los símbolos y todas las aristas. Nunca lanza por un objeto ilegible (se omite).</summary>
        public static Graph Read(Database db, Transaction tr)
        {
            var g = new Graph();
            ReadBlocks(db, tr, g);
            ReadTable(tr, db.LayerTableId, PurgeCategories.Capas, g);
            ReadTable(tr, db.LinetypeTableId, PurgeCategories.TiposLinea, g);
            ReadTable(tr, db.TextStyleTableId, PurgeCategories.EstilosTexto, g);
            ReadTable(tr, db.DimStyleTableId, PurgeCategories.EstilosCota, g);
            ReadTable(tr, db.RegAppTableId, PurgeCategories.Regapps, g);

            ObjectId byLayer = SafeId(() => SymbolUtilityServices.GetLinetypeByLayerId(db));
            ObjectId byBlock = SafeId(() => SymbolUtilityServices.GetLinetypeByBlockId(db));
            var walker = new EntityWalker(tr, g, byLayer, byBlock);

            // Aristas de los símbolos (capa → tipo de línea; tipo de línea → estilos; estilo de cota → texto/bloques/tipos).
            int count = g.Symbols.Count;
            for (int i = 0; i < count; i++)
            {
                Symbol s = g.Symbols[i];
                try
                {
                    switch (s.Categoria)
                    {
                        case PurgeCategories.Capas:
                            if (tr.GetObject(s.Id, OpenMode.ForRead, false) is LayerTableRecord layer)
                            {
                                walker.Edge(i, layer.LinetypeObjectId, skipByLayerBlock: true);
                            }

                            break;
                        case PurgeCategories.TiposLinea:
                            if (tr.GetObject(s.Id, OpenMode.ForRead, false) is LinetypeTableRecord lt)
                            {
                                int dashes = lt.NumDashes;
                                for (int d = 0; d < dashes; d++)
                                {
                                    walker.Edge(i, SafeId(() => lt.ShapeStyleAt(d)), skipByLayerBlock: false);
                                }
                            }

                            break;
                        case PurgeCategories.EstilosCota:
                            if (tr.GetObject(s.Id, OpenMode.ForRead, false) is DimStyleTableRecord ds)
                            {
                                walker.Edge(i, SafeId(() => ds.Dimtxsty), false);
                                walker.Edge(i, SafeId(() => ds.Dimblk), false);
                                walker.Edge(i, SafeId(() => ds.Dimblk1), false);
                                walker.Edge(i, SafeId(() => ds.Dimblk2), false);
                                walker.Edge(i, SafeId(() => ds.Dimldrblk), false);
                                walker.Edge(i, SafeId(() => ds.Dimltype), true);
                                walker.Edge(i, SafeId(() => ds.Dimltex1), true);
                                walker.Edge(i, SafeId(() => ds.Dimltex2), true);
                            }

                            break;
                    }
                }
                catch (Exception)
                {
                    // Símbolo ilegible: sin aristas propias.
                }
            }

            // Entidades de cada espacio y del interior de cada definición (no xref) + representaciones de bloques dinámicos.
            for (int i = 0; i < count; i++)
            {
                Symbol s = g.Symbols[i];
                if (s.Categoria != PurgeCategories.Bloques && s.Categoria != SpaceCategory)
                {
                    continue;
                }

                BlockTableRecord? btr;
                try
                {
                    btr = tr.GetObject(s.Id, OpenMode.ForRead, false) as BlockTableRecord;
                }
                catch (Exception)
                {
                    btr = null;
                }

                if (btr == null)
                {
                    continue;
                }

                if (!s.Xref)
                {
                    int n = 0;
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsNull || id.IsErased)
                        {
                            continue;
                        }

                        n++;
                        walker.Entity(i, id);
                    }

                    s.NEntidades = n;
                }

                if (s.Categoria == PurgeCategories.Bloques && SafeBool(() => btr.IsDynamicBlock))
                {
                    ObjectIdCollection? reps = null;
                    try
                    {
                        reps = btr.GetAnonymousBlockIds();
                    }
                    catch (Exception)
                    {
                        reps = null;
                    }

                    if (reps != null)
                    {
                        foreach (ObjectId rep in reps)
                        {
                            if (!rep.IsNull && !rep.IsErased && g.IndexById.TryGetValue(rep, out int repIndex))
                            {
                                g.Edges.Add((repIndex, i)); // representación anónima → definición dinámica
                            }
                        }
                    }
                }
            }

            g.Edges.RemoveWhere(e => e.desde == e.hacia);
            return g;
        }

        private static void ReadBlocks(Database db, Transaction tr, Graph g)
        {
            BlockTable table;
            try
            {
                table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            }
            catch (Exception)
            {
                return;
            }

            foreach (ObjectId id in table)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                try
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is BlockTableRecord btr))
                    {
                        continue;
                    }

                    string name = btr.Name;
                    bool dependent = btr.IsDependent || name.IndexOf('|') >= 0;
                    bool xref = btr.IsFromExternalReference || dependent;
                    string cat = btr.IsLayout ? SpaceCategory : PurgeCategories.Bloques;
                    int units = 0;
                    try
                    {
                        units = (int)btr.Units;
                    }
                    catch (Exception)
                    {
                        units = 0;
                    }

                    Add(g, new Symbol(id, cat, name, dependent, xref, btr.IsAnonymous || name.StartsWith("*", StringComparison.Ordinal), units));
                }
                catch (Exception)
                {
                    // Definición ilegible: se omite.
                }
            }
        }

        private static void ReadTable(Transaction tr, ObjectId tableId, string categoria, Graph g)
        {
            SymbolTable table;
            try
            {
                table = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
            }
            catch (Exception)
            {
                return;
            }

            foreach (ObjectId id in table)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                try
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is SymbolTableRecord rec))
                    {
                        continue;
                    }

                    string name = rec.Name;
                    bool dependent = rec.IsDependent || name.IndexOf('|') >= 0;
                    int index = Add(g, new Symbol(id, categoria, name, dependent, dependent, false, 0));
                    if (categoria == PurgeCategories.Regapps)
                    {
                        g.RegApps.Add(index);
                    }
                }
                catch (Exception)
                {
                    // Registro ilegible: se omite.
                }
            }
        }

        private static int Add(Graph g, Symbol s)
        {
            int index = g.Symbols.Count;
            g.Symbols.Add(s);
            g.IndexById[s.Id] = index;
            return index;
        }

        private static ObjectId SafeId(Func<ObjectId> get)
        {
            try
            {
                return get();
            }
            catch (Exception)
            {
                return ObjectId.Null;
            }
        }

        private static bool SafeBool(Func<bool> get)
        {
            try
            {
                return get();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Recorrido de entidades: añade las aristas de una entidad a su propietario.</summary>
        private sealed class EntityWalker
        {
            private readonly Transaction _tr;
            private readonly Graph _g;
            private readonly ObjectId _byLayer;
            private readonly ObjectId _byBlock;

            public EntityWalker(Transaction tr, Graph g, ObjectId byLayer, ObjectId byBlock)
            {
                _tr = tr;
                _g = g;
                _byLayer = byLayer;
                _byBlock = byBlock;
            }

            /// <summary>Arista owner → target si target es un símbolo conocido (ByLayer/ByBlock omitidos si se pide).</summary>
            public void Edge(int owner, ObjectId target, bool skipByLayerBlock)
            {
                if (target.IsNull)
                {
                    return;
                }

                if (skipByLayerBlock && (target == _byLayer || target == _byBlock))
                {
                    return;
                }

                if (_g.IndexById.TryGetValue(target, out int t) && t != owner)
                {
                    _g.Edges.Add((owner, t));
                }
            }

            public void Entity(int owner, ObjectId id)
            {
                Entity? e;
                try
                {
                    e = _tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                }
                catch (Exception)
                {
                    return;
                }

                if (e == null)
                {
                    return;
                }

                try
                {
                    Common(owner, e);
                    switch (e)
                    {
                        case DBText text: // incluye AttributeDefinition
                            Edge(owner, text.TextStyleId, false);
                            break;
                        case MText mtext:
                            Edge(owner, mtext.TextStyleId, false);
                            break;
                        case Dimension dim:
                            DimensionEdges(owner, dim);
                            break;
                        case Leader leader:
                            Edge(owner, SafeId(() => leader.DimensionStyle), false);
                            break;
                        case MLeader ml:
                            Edge(owner, SafeId(() => ml.TextStyleId), false);
                            Edge(owner, SafeId(() => ml.BlockContentId), false);
                            break;
                        case BlockReference br: // incluye Table (bloque *T)
                            BlockReferenceEdges(owner, br);
                            break;
                    }
                }
                catch (Exception)
                {
                    // Propiedad ilegible: se conservan las aristas ya añadidas.
                }
            }

            private void Common(int owner, Entity e)
            {
                Edge(owner, e.LayerId, false);
                Edge(owner, SafeId(() => e.LinetypeId), true);
            }

            private void DimensionEdges(int owner, Dimension dim)
            {
                Edge(owner, SafeId(() => dim.DimensionStyle), false);
                Edge(owner, SafeId(() => dim.DimBlockId), false);

                // Sobrescrituras de la cota (si no hay sobrescritura devuelven el valor del estilo: arista redundante).
                Edge(owner, SafeId(() => dim.TextStyleId), false);
                Edge(owner, SafeId(() => dim.Dimblk), false);
                Edge(owner, SafeId(() => dim.Dimblk1), false);
                Edge(owner, SafeId(() => dim.Dimblk2), false);
                Edge(owner, SafeId(() => dim.Dimldrblk), false);
                Edge(owner, SafeId(() => dim.Dimltype), true);
                Edge(owner, SafeId(() => dim.Dimltex1), true);
                Edge(owner, SafeId(() => dim.Dimltex2), true);
            }

            private void BlockReferenceEdges(int owner, BlockReference br)
            {
                Edge(owner, br.BlockTableRecord, false);
                if (SafeBool(() => br.IsDynamicBlock))
                {
                    Edge(owner, SafeId(() => br.DynamicBlockTableRecord), false);
                }

                AttributeCollection? atts;
                try
                {
                    atts = br.AttributeCollection;
                }
                catch (Exception)
                {
                    atts = null;
                }

                if (atts == null)
                {
                    return;
                }

                foreach (ObjectId attId in atts)
                {
                    if (attId.IsNull || attId.IsErased)
                    {
                        continue;
                    }

                    try
                    {
                        if (_tr.GetObject(attId, OpenMode.ForRead, false) is AttributeReference att)
                        {
                            Common(owner, att);
                            Edge(owner, att.TextStyleId, false);
                        }
                    }
                    catch (Exception)
                    {
                        // Atributo ilegible: se omite.
                    }
                }
            }
        }
    }
}
