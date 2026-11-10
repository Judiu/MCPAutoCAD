using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// Utilidades compartidas por los handlers y reglas de lectura de §5C.1–§5C.7 (protocol.md). Solo hilo principal;
    /// solo lectura: todo se abre ForRead dentro de la transacción open/close que abre quien llama.
    /// </summary>
    internal static class AuditSupport
    {
        public const string OrigenTexto = "contenido_del_plano";

        /// <summary>Cada cuántas entidades se comprueba el plazo (§3.10).</summary>
        public const int CheckEvery = 500;

        /// <summary>Tolerancia de cierre (primer = último vértice) de §5C.4.</summary>
        public const double ClosureTolerance = 1e-9;

        public const string WarningNoStandard = "Estándar LPS no disponible: no se comparó con capas_permitidas.";

        public const string WarningPartial = "Se agotó el plazo de 8 s: el resultado es parcial.";

        /// <summary>Orden de reglas de la tabla de §5C.8 (para <see cref="Finding.Sort"/>).</summary>
        public static readonly IReadOnlyList<string> RuleOrder = new[]
        {
            "auditar_capas", "entidades_en_capa_0", "auditar_bloques", "verificar_cuadro_areas",
            "duplicados", "textos_fuera_de_escala", "xrefs_no_resueltas", "unidades",
        };

        private static readonly Lazy<LpsLayerRules> Rules = new Lazy<LpsLayerRules>(
            () => LpsStandard.Disponible ? LpsLayerRules.FromJson(LpsStandard.Root) : LpsLayerRules.Empty,
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Reglas de capas de lps.json (vacías si el estándar no está disponible, §2.6). Se calculan una vez.</summary>
        public static LpsLayerRules LayerRules => Rules.Value;

        // ------------------------------------------------------------------ contexto y respuesta

        /// <summary>Contexto para uso fuera de auditar_dibujo: plazo = ahora + PluginInfo.AuditBudgetMs.</summary>
        public static AuditContext CreateContext(Database db, Transaction tr, SpaceResolver.ResolvedSpace space) =>
            new AuditContext(db, tr, space, DateTime.UtcNow.AddMilliseconds(PluginInfo.AuditBudgetMs));

        /// <summary>Respuesta nueva con <c>pid</c> como primer campo.</summary>
        public static Dictionary<string, object?> Begin(int pid) =>
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["pid"] = pid };

        /// <summary>
        /// Cierra la respuesta común de §5C: origen_texto, insunits, unidades y advertencias (§2.1 + §5.2 + §2.2
        /// primero, las propias después).
        /// </summary>
        public static void Finish(Dictionary<string, object?> result, Database db, IEnumerable<string>? ownWarnings)
        {
            result["origen_texto"] = OrigenTexto;
            AcadUtil.AddUnitFields(result, db);
            if (ownWarnings != null && result["advertencias"] is List<string> warnings)
            {
                foreach (string w in ownWarnings)
                {
                    if (!string.IsNullOrEmpty(w) && !warnings.Contains(w))
                    {
                        warnings.Add(w);
                    }
                }
            }
        }

        /// <summary>Hallazgos en el formato de §5C.0 (handles recortados a 50).</summary>
        public static List<Dictionary<string, object?>> ToDictionaries(IEnumerable<Finding> findings)
        {
            var list = new List<Dictionary<string, object?>>();
            foreach (Finding f in findings)
            {
                list.Add(f.ToDictionary());
            }

            return list;
        }

        // ------------------------------------------------------------------ espacios y entidades

        /// <summary>
        /// "Todos los espacios" de §5C: *Model_Space ("modelo") y después los layouts del LayoutDictionary (sin
        /// Model) por TabOrder. Nunca definiciones de bloque.
        /// </summary>
        public static List<SpaceResolver.ResolvedSpace> AllSpaces(Database db, Transaction tr)
        {
            var spaces = new List<SpaceResolver.ResolvedSpace>();
            var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            spaces.Add(new SpaceResolver.ResolvedSpace(model, SpaceResolver.ModelName));

            var layouts = new List<(int order, string name, ObjectId btrId)>();
            try
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    try
                    {
                        if (entry.Value.IsNull || entry.Value.IsErased)
                        {
                            continue;
                        }

                        if (tr.GetObject(entry.Value, OpenMode.ForRead, false) is Layout layout
                            && !layout.ModelType && !layout.BlockTableRecordId.IsNull && !layout.BlockTableRecordId.IsErased)
                        {
                            layouts.Add((layout.TabOrder, layout.LayoutName, layout.BlockTableRecordId));
                        }
                    }
                    catch (Exception)
                    {
                        // Layout ilegible: se omite.
                    }
                }
            }
            catch (Exception)
            {
                // Sin diccionario de layouts legible: solo el modelo.
            }

            layouts.Sort((a, b) =>
            {
                int c = a.order.CompareTo(b.order);
                return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.name, b.name);
            });

            foreach ((_, string name, ObjectId btrId) in layouts)
            {
                try
                {
                    if (tr.GetObject(btrId, OpenMode.ForRead, false) is BlockTableRecord btr)
                    {
                        spaces.Add(new SpaceResolver.ResolvedSpace(btr, name));
                    }
                }
                catch (Exception)
                {
                    // Espacio ilegible: se omite.
                }
            }

            return spaces;
        }

        /// <summary>Ids no borrados de un espacio (modelo o layout), en orden de base de datos. No abre nada.</summary>
        public static List<ObjectId> LiveIds(BlockTableRecord space)
        {
            var ids = new List<ObjectId>();
            foreach (ObjectId id in space)
            {
                if (!id.IsNull && !id.IsErased)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        /// <summary>Entidad abierta ForRead (sin abrir borradas), o null si no es legible.</summary>
        public static Entity? Open(Transaction tr, ObjectId id)
        {
            try
            {
                return tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Handle de un ObjectId sin abrir el objeto (§2.1).</summary>
        public static string Handle(ObjectId id) => AcadUtil.HandleToString(id.Handle);

        /// <summary>Unión de las cajas 2D de los ids (como mucho los <paramref name="max"/> primeros); null si ninguna.</summary>
        public static BBox2? UnionBox(Transaction tr, IEnumerable<ObjectId> ids, int max = Finding.MaxHandles)
        {
            BBox2? box = null;
            int n = 0;
            foreach (ObjectId id in ids)
            {
                if (n++ >= max)
                {
                    break;
                }

                Entity? e = Open(tr, id);
                BBox2? b = e == null ? null : AcadUtil.TryBBox2(e);
                if (b != null)
                {
                    box = box == null ? b : box.Value.Union(b.Value);
                }
            }

            return box;
        }

        /// <summary>Caja 2D [[xmin, ymin], [xmax, ymax]] o null.</summary>
        public static double[][]? Box2D(BBox2? box) => box?.ToArray2D();

        /// <summary>Capa de la entidad, o "" si no se puede leer.</summary>
        public static string Layer(Entity e)
        {
            try
            {
                return e.Layer ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // ------------------------------------------------------------------ áreas (§5C.4, también contornos de §5C.5)

        /// <summary>Tipos con área candidatos de §5C.4 (filtro rápido por DxfName).</summary>
        public static readonly HashSet<string> AreaTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "LWPOLYLINE", "POLYLINE", "CIRCLE", "ELLIPSE", "HATCH", "REGION",
        };

        /// <summary>
        /// true si la entidad es candidata con área (§5C.4): LWPOLYLINE y POLYLINE 2D cerradas (Closed, o primer y
        /// último vértice coincidentes con ≥ 3 vértices), CIRCLE, ELLIPSE cerrada, HATCH y REGION. <paramref name="area"/>
        /// = área en unidades de dibujo², o null si AutoCAD lanzó al leerla (cuenta como n_sin_area).
        /// </summary>
        public static bool TryArea(Entity e, out double? area)
        {
            area = null;
            bool candidate;
            try
            {
                switch (e)
                {
                    case Polyline pl:
                        candidate = pl.Closed || (pl.NumberOfVertices >= 3
                            && pl.GetPoint3dAt(0).DistanceTo(pl.GetPoint3dAt(pl.NumberOfVertices - 1)) <= ClosureTolerance);
                        break;
                    case Polyline2d p2:
                        candidate = p2.Closed || (CountVertices(p2) >= 3 && p2.StartPoint.DistanceTo(p2.EndPoint) <= ClosureTolerance);
                        break;
                    case Circle _:
                    case Hatch _:
                    case Region _:
                        candidate = true;
                        break;
                    case Ellipse el:
                        candidate = el.Closed;
                        break;
                    default:
                        return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

            if (!candidate)
            {
                return false;
            }

            try
            {
                double a = e switch
                {
                    Hatch h => h.Area,
                    Region r => r.Area,
                    Curve c => c.Area,
                    _ => double.NaN,
                };
                area = double.IsFinite(a) ? a : null;
            }
            catch (Exception)
            {
                area = null;
            }

            return true;
        }

        /// <summary>Número de vértices de una POLYLINE 2D (ids de la propia polilínea; no abre nada).</summary>
        private static int CountVertices(Polyline2d p2)
        {
            int n = 0;
            foreach (ObjectId v in p2)
            {
                if (!v.IsErased)
                {
                    n++;
                }
            }

            return n;
        }

        // ------------------------------------------------------------------ formato

        /// <summary>Conteos ordenados por valor desc y luego nombre (ordinal), como objeto JSON.</summary>
        public static Dictionary<string, object?> SortedCounts(Dictionary<string, int> counts)
        {
            var items = new List<KeyValuePair<string, int>>(counts);
            items.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });

            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in items)
            {
                d[kv.Key] = kv.Value;
            }

            return d;
        }

        public static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        /// <summary>Número con cultura invariante y formato corto ("0.001", "1000", "0.2").</summary>
        public static string Num(double v) => v.ToString("0.############", CultureInfo.InvariantCulture);

        /// <summary>Entero con cultura invariante.</summary>
        public static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
