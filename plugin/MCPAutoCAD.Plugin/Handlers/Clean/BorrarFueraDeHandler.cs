using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Clean;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Clean
{
    /// <summary>
    /// borrar_fuera_de (protocol.md §5F.1). Hilo principal; requiere documento; ESCRITURA (solo con confirmar: true).
    /// Borra, en el espacio del contorno, toda entidad cuya caja 2D queda FUERA del polígono del contorno (o también
    /// las que lo CRUZAN con criterio caja_dentro). Flujo de §5F.0:
    /// <list type="number">
    /// <item>Parse (sin AutoCAD; hilo del pipe y otra vez en el principal): <see cref="CleanWrite.ReadCommon"/> + propios.</item>
    /// <item><see cref="CleanWrite.ReadUnits"/> (L3 desde 0.8.3: INSUNITS = 0 solo advierte; el contorno es del propio dibujo).</item>
    /// <item>Lectura con StartOpenCloseTransaction (sin bloqueo del documento, sin UndoGroup, sin efectos): contorno, polígono,
    /// clasificación de TODAS las entidades del espacio, capas bloqueadas/xref (rechazo), viewports de papel afectados.
    /// Todos los rechazos salen aquí (L7), sin paso de deshacer.</item>
    /// <item>Previsualización → inventario. Escritura → <see cref="UndoGroup.Run"/> con UNA Transaction que solo vuelve a
    /// comprobar cada ObjectId (IsErased, Entity, propietario = espacio, no es el contorno) y hace Erase.</item>
    /// </list>
    /// Rendimiento (§3.7, L8): en el plano madre la lectura abre ~37 000 entidades y calcula su GeometricExtents
    /// (la Fase 1 midió 347 ms para tipo/capa/caja; con la clasificación por polígono, estimación 0,4–1 s). La escritura
    /// abre ForWrite y borra ~32 000 entidades en una transacción: estimación 2–5 s (el coste dominante es el registro de
    /// deshacer de cada Erase). No se recalcula ninguna caja ni se abren capas en la escritura. Riesgo: si lectura +
    /// escritura superan los 10 s de §3.7 el cliente recibe INTERNAL "timeout", pero el borrado termina y queda como UN
    /// paso de deshacer (el gate lo mide y lo comprueba con consultar_entidades).
    /// </summary>
    internal sealed class BorrarFueraDeHandler : IToolHandler
    {
        private const double ClosureTolerance = 1e-9;
        private const double ZeroArea = 1e-9;
        private const int DefaultSample = 10;

        private static readonly string[] Allowed =
        {
            "contorno", "espacio", "criterio", "excluir_capas", "muestra", "confirmar", "preview",
        };

        public string Name => "borrar_fuera_de";

        public string Description =>
            "BORRA todas las entidades de un espacio que quedan FUERA de un contorno (handle de una polilínea cerrada o un círculo), " +
            "juzgando cada una por su caja: por defecto conserva las que cruzan el borde (criterio caja_cruza). Sin confirmar: true solo " +
            "PREVISUALIZA (inventario por tipo y capa, muestra de handles y viewports afectados) y no modifica nada; con confirmar: true " +
            "borra en un solo paso de deshacer. Nunca borra el contorno, viewports ni entidades sin caja; avisa de los viewports de papel " +
            "que miran la zona borrada.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""contorno"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Handle del contorno: LWPOLYLINE cerrada (Closed, o primer vértice = último con al menos 3) o CIRCLE, en el modelo o en un layout (no dentro de un bloque). Nunca se borra."" },
    ""espacio"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""'modelo' o nombre de un layout. Por defecto, el espacio donde vive el contorno; si se pasa, el contorno debe estar en él."" },
    ""criterio"": { ""type"": ""string"", ""enum"": [""caja_cruza"", ""caja_dentro""], ""default"": ""caja_cruza"", ""description"": ""caja_cruza = se conservan las entidades cuya caja cruza el borde (solo se borra lo totalmente fuera); caja_dentro = se borran también (solo se conserva lo totalmente dentro)."" },
    ""excluir_capas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Capas (sin distinguir mayúsculas) cuyas entidades nunca se borran; deben existir."" },
    ""muestra"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50, ""default"": 10, ""description"": ""Cuántos handles de ejemplo de lo que se borra devolver."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""contorno""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""contorno"": { ""type"": ""object"" },
    ""espacio"": { ""type"": ""string"" },
    ""criterio"": { ""type"": ""string"" },
    ""n_total"": { ""type"": ""integer"" },
    ""n_borrar"": { ""type"": ""integer"" },
    ""n_conservar"": { ""type"": ""integer"" },
    ""n_cruzan"": { ""type"": ""integer"" },
    ""n_sin_caja"": { ""type"": ""integer"" },
    ""n_excluidas"": { ""type"": ""integer"" },
    ""n_borradas"": { ""type"": [""integer"", ""null""] },
    ""por_tipo"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""por_capa"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""excluidas_por_capa"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""muestra"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""viewports"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""duracion_ms"": { ""type"": ""object"" },
    ""origen_texto"": { ""type"": ""string"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""contorno"", ""espacio"", ""criterio"", ""n_total"", ""n_borrar"", ""n_conservar"", ""n_cruzan"", ""n_sin_caja"", ""n_excluidas"", ""n_borradas"", ""por_tipo"", ""por_capa"", ""excluidas_por_capa"", ""muestra"", ""viewports"", ""duracion_ms"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Parámetros leídos (sin AutoCAD).</summary>
        private sealed class Params
        {
            public Params(string contorno, string? espacio, OutsideCriterion criterio, List<string>? excluirCapas, int muestra, CleanWrite.Common common)
            {
                Contorno = contorno;
                Espacio = espacio;
                Criterio = criterio;
                ExcluirCapas = excluirCapas;
                Muestra = muestra;
                Common = common;
            }

            public string Contorno { get; }

            public string? Espacio { get; }

            public OutsideCriterion Criterio { get; }

            public List<string>? ExcluirCapas { get; }

            public int Muestra { get; }

            public CleanWrite.Common Common { get; }
        }

        /// <summary>Datos de una capa leídos una vez (caché por LayerId).</summary>
        internal sealed class LayerInfo
        {
            public LayerInfo(string name, bool locked, bool xref, bool hidden)
            {
                Name = name;
                Locked = locked;
                Xref = xref;
                Hidden = hidden;
            }

            public string Name { get; }

            public bool Locked { get; }

            public bool Xref { get; }

            /// <summary>Congelada o apagada.</summary>
            public bool Hidden { get; }
        }

        /// <summary>
        /// Inventario de la lectura (L5: el mismo en ambos modos). Guarda solo ObjectId de lo que se borra (sin cadenas
        /// por entidad); los conteos por tipo y capa ya están agregados.
        /// </summary>
        private sealed class Inventory
        {
            public ObjectId ContourId { get; set; }

            public ObjectId SpaceId { get; set; }

            public string Espacio { get; set; } = SpaceResolver.ModelName;

            public string Criterio { get; set; } = BoxPolygon.CriterioCajaCruza;

            public Dictionary<string, object?> Contorno { get; set; } = new Dictionary<string, object?>();

            public int NTotal { get; set; }

            public int NConservar { get; set; }

            public int NCruzan { get; set; }

            public int NSinCaja { get; set; }

            public int NExcluidas { get; set; }

            public List<ObjectId> ToDelete { get; } = new List<ObjectId>();

            public Dictionary<string, int> PorTipo { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            public Dictionary<string, int> PorCapa { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            public Dictionary<string, int> ExcluidasPorCapa { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            public List<string> Muestra { get; } = new List<string>();

            public List<Dictionary<string, object?>> Viewports { get; } = new List<Dictionary<string, object?>>();
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string contorno = ParamReader.OptionalHandle(p, "contorno") ?? throw ToolException.InvalidParams("contorno: obligatorio");
            string? espacio = ParamReader.OptionalString(p, "espacio", 1, 255);
            string? criterioText = ParamReader.OptionalString(p, "criterio", 0);
            OutsideCriterion criterio;
            try
            {
                criterio = BoxPolygon.ParseCriterion(criterioText);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            List<string>? excluir = ParamReader.OptionalStringArray(p, "excluir_capas", 1, 100, 1);
            int muestra = ParamReader.OptionalInt(p, "muestra", 1, 50) ?? DefaultSample;
            CleanWrite.Common common = CleanWrite.ReadCommon(p);
            return new Params(contorno, espacio, criterio, excluir, muestra, common);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db); // L3 desde 0.8.3: no interpreta unidades, INSUNITS = 0 solo advierte

            // Lectura previa (L7): inventario completo y todos los rechazos, sin efectos ni paso de deshacer.
            Stopwatch read = Stopwatch.StartNew();
            Inventory inv;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                inv = Read(db, tr, p, warnings);
                tr.Commit();
            }

            long readMs = read.ElapsedMilliseconds;
            if (mode == CleanWrite.Mode.Preview)
            {
                return BuildResponse(ctx.Pid, mode, ins, warnings, inv, readMs, null, null);
            }

            return UndoGroup.Run(doc, d => Write(d, ctx.Pid, mode, ins, warnings, inv, readMs));
        }

        // ------------------------------------------------------------------ lectura

        private static Inventory Read(Database db, Transaction tr, Params p, List<string> warnings)
        {
            var inv = new Inventory
            {
                Criterio = p.Criterio == OutsideCriterion.CajaDentro ? BoxPolygon.CriterioCajaDentro : BoxPolygon.CriterioCajaCruza,
            };

            // ---- Contorno.
            Entity? contour = AcadUtil.TryGetEntityByHandle(db, p.Contorno, tr);
            if (contour == null)
            {
                throw ToolException.EntityNotFound(p.Contorno);
            }

            string h = AcadUtil.HandleToString(contour.Handle);
            string tipo = AcadUtil.DxfName(contour);
            List<(double x, double y)> polygon;
            int curved = 0;
            if (contour is Polyline pl)
            {
                polygon = PolylineVertices(pl, h, out curved);
            }
            else if (contour is Circle circle)
            {
                polygon = BoxPolygon.CirclePolygon(circle.Center.X, circle.Center.Y, circle.Radius);
            }
            else
            {
                throw ToolException.InvalidParams(
                    "contorno: la entidad " + h + " no es una polilínea cerrada ni un círculo (" + tipo + ")");
            }

            // Propietario: modelo o layout (IsLayout también es true para *Model_Space); si no, está en un bloque.
            BlockTableRecord? owner = null;
            try
            {
                owner = contour.OwnerId.IsNull ? null : tr.GetObject(contour.OwnerId, OpenMode.ForRead, false) as BlockTableRecord;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                owner = null;
            }

            if (owner == null || !owner.IsLayout)
            {
                throw ToolException.InvalidParams("contorno: la entidad " + h + " está dentro de una definición de bloque");
            }

            if (p.Espacio != null)
            {
                SpaceResolver.ResolvedSpace requested = SpaceResolver.Resolve(db, tr, p.Espacio); // NOT_FOUND data.espacio
                if (requested.Btr.ObjectId != owner.ObjectId)
                {
                    throw ToolException.InvalidParams("contorno: la entidad " + h + " no está en el espacio '" + p.Espacio + "'");
                }
            }

            string espacio = SpaceResolver.SpaceName(owner, tr);
            inv.Espacio = espacio;
            inv.SpaceId = owner.ObjectId;
            inv.ContourId = contour.ObjectId;

            // Polígono: duplicados consecutivos colapsados (advertencia de §5E.3.3), ≥ 3 vértices, simple, área ≠ 0.
            List<(double x, double y)> collapsed = PolygonMetrics.CollapseConsecutiveDuplicates(polygon, out int removed);
            if (removed > 0)
            {
                warnings.Add(PolygonMetrics.CollapsedDuplicatesWarning(h, removed));
            }

            if (collapsed.Count < 3)
            {
                throw ToolException.InvalidGeometry("contorno: se necesitan al menos 3 vértices", new Dictionary<string, object?> { ["handle"] = h });
            }

            (int a, int b)? hit = PolylineValidator.FindSelfIntersection(collapsed);
            if (hit != null)
            {
                throw ToolException.InvalidGeometry(
                    string.Format(CultureInfo.InvariantCulture, "contorno: la polilínea se autointerseca (tramos {0} y {1})", hit.Value.a, hit.Value.b),
                    new Dictionary<string, object?> { ["handle"] = h });
            }

            double area = Math.Abs(PolygonMetrics.SignedArea(collapsed));
            if (!(area > ZeroArea))
            {
                throw ToolException.InvalidGeometry("contorno: área 0", new Dictionary<string, object?> { ["handle"] = h });
            }

            if (curved > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "contorno: la polilínea tiene {0} tramo(s) curvo(s); se evaluaron por sus cuerdas.",
                    curved));
            }

            BBox2? contourBox = AcadUtil.TryBBox2(contour);
            inv.Contorno = new Dictionary<string, object?>
            {
                ["handle"] = h,
                ["tipo"] = tipo,
                ["capa"] = AuditSupport.Layer(contour),
                ["n_vertices"] = collapsed.Count,
                ["area"] = area,
                ["bbox"] = contourBox?.ToArray2D(),
            };

            // ---- excluir_capas: deben existir (NOT_FOUND con todas las inexistentes).
            var excluded = new HashSet<ObjectId>();
            if (p.ExcluirCapas != null)
            {
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var missing = new List<string>();
                foreach (string name in p.ExcluirCapas)
                {
                    ObjectId layerId = FindLayer(layers, name);
                    if (layerId.IsNull)
                    {
                        if (!missing.Contains(name))
                        {
                            missing.Add(name);
                        }
                    }
                    else
                    {
                        excluded.Add(layerId);
                    }
                }

                if (missing.Count > 0)
                {
                    string detail = "excluir_capas: capas inexistentes: " + string.Join(", ", missing);
                    throw new ToolException(
                        ErrorCodes.NotFound,
                        detail,
                        new Dictionary<string, object?> { ["detalle"] = detail, ["capas"] = missing });
                }
            }

            // ---- Clasificación de todas las entidades del espacio (sin el contorno).
            var layerCache = new Dictionary<ObjectId, LayerInfo>();
            var deleteByLayer = new Dictionary<ObjectId, int>();
            var excludedByLayer = new Dictionary<ObjectId, int>();
            var noBoxTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var deleteBoxes = new List<BBox2>();
            var keptBoxes = new List<BBox2>();
            int keptViewports = 0;
            foreach (ObjectId id in owner)
            {
                if (id.IsNull || id.IsErased || id == inv.ContourId)
                {
                    continue;
                }

                inv.NTotal++;
                Entity? e = AuditSupport.Open(tr, id);
                if (e == null)
                {
                    // Objeto ilegible: no se borra (se trata como sin caja).
                    inv.NSinCaja++;
                    CleanWrite.Increment(noBoxTypes, AcadUtil.DxfNameFromId(id) ?? "?");
                    continue;
                }

                if (e is Viewport)
                {
                    // Los viewports nunca se borran (como §5C.10); cuentan en n_conservar y n_excluidas.
                    keptViewports++;
                    inv.NConservar++;
                    inv.NExcluidas++;
                    continue;
                }

                ObjectId layerId = e.LayerId;
                if (excluded.Contains(layerId))
                {
                    inv.NConservar++;
                    inv.NExcluidas++;
                    Increment(excludedByLayer, layerId);
                    BBox2? kept = AcadUtil.TryBBox2(e);
                    if (kept != null)
                    {
                        keptBoxes.Add(kept.Value);
                    }

                    continue;
                }

                BBox2? box = AcadUtil.TryBBox2(e);
                if (box == null)
                {
                    inv.NSinCaja++;
                    CleanWrite.Increment(noBoxTypes, AcadUtil.DxfName(e));
                    continue;
                }

                BoxRelation rel = BoxPolygon.Classify(collapsed, box.Value);
                if (rel == BoxRelation.Cruza)
                {
                    inv.NCruzan++;
                }

                if (BoxPolygon.ShouldDelete(rel, p.Criterio))
                {
                    inv.ToDelete.Add(id);
                    deleteBoxes.Add(box.Value);
                    Increment(deleteByLayer, layerId);
                    CleanWrite.Increment(inv.PorTipo, AcadUtil.DxfName(e));
                    if (inv.Muestra.Count < p.Muestra)
                    {
                        inv.Muestra.Add(AuditSupport.Handle(id));
                    }
                }
                else
                {
                    inv.NConservar++;
                    keptBoxes.Add(box.Value);
                }
            }

            // ---- Capas de lo que se borra: bloqueadas / xref → rechazo (L7); congeladas o apagadas → advertencia.
            var blocked = new List<(string name, int n)>();
            int hidden = 0;
            foreach (KeyValuePair<ObjectId, int> kv in deleteByLayer)
            {
                LayerInfo info = GetLayer(tr, kv.Key, layerCache);
                inv.PorCapa[info.Name] = kv.Value;
                if (info.Locked || info.Xref)
                {
                    blocked.Add((info.Name, kv.Value));
                }
                else if (info.Hidden)
                {
                    hidden += kv.Value;
                }
            }

            if (blocked.Count > 0)
            {
                blocked.Sort((x, y) =>
                {
                    int c = y.n.CompareTo(x.n);
                    return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(x.name, y.name);
                });
                var parts = new List<string>(blocked.Count);
                var names = new List<string>(blocked.Count);
                foreach ((string name, int n) in blocked)
                {
                    parts.Add(name + " (" + n.ToString(CultureInfo.InvariantCulture) + ")");
                    names.Add(name);
                }

                throw ToolException.InvalidParams(
                    "capas bloqueadas o de xref con entidades a borrar: " + string.Join(", ", parts)
                    + "; desbloquéelas o páselas en excluir_capas",
                    new Dictionary<string, object?> { ["capas"] = names });
            }

            foreach (KeyValuePair<ObjectId, int> kv in excludedByLayer)
            {
                inv.ExcluidasPorCapa[GetLayer(tr, kv.Key, layerCache).Name] = kv.Value;
            }

            // ---- Viewports de papel que miran la zona (advertencia OBLIGATORIA en ambos modos; solo si el espacio es el modelo).
            if (string.Equals(espacio, SpaceResolver.ModelName, StringComparison.Ordinal))
            {
                foreach (LayoutViewports.ViewportWindowInfo w in LayoutViewports.Windows(db, tr))
                {
                    int nb = CountIntersecting(deleteBoxes, w.Ventana);
                    int nc = CountIntersecting(keptBoxes, w.Ventana);
                    string estado = ViewportImpact.Classify(nb, nc);
                    string? warning = ViewportImpact.Warning(w.Layout, w.Handle, estado, w.Ventana, nb, nc);
                    if (warning != null)
                    {
                        warnings.Add(warning);
                    }

                    inv.Viewports.Add(new Dictionary<string, object?>
                    {
                        ["layout"] = w.Layout,
                        ["handle"] = w.Handle,
                        ["ventana"] = Round2(w.Ventana),
                        ["estado"] = estado,
                        ["n_borradas"] = nb,
                        ["n_conservadas"] = nc,
                    });
                }
            }

            if (inv.NSinCaja > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} entidad(es) sin caja calculable se conservaron ({1}).",
                    inv.NSinCaja,
                    TypeList(noBoxTypes)));
            }

            if (keptViewports > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Se conservaron {0} viewport(s) del layout {1}: borrar_fuera_de no borra viewports.",
                    keptViewports,
                    espacio));
            }

            if (hidden > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} entidad(es) en capas congeladas o apagadas se borraron.",
                    hidden));
            }

            return inv;
        }

        /// <summary>
        /// Vértices WCS (X, Y) de la LWPOLYLINE cerrada (GetPoint3dAt: WCS aunque la normal no sea Z). Cerrada =
        /// Closed, o primer = último vértice (tolerancia 1e-9) con ≥ 3 vértices; en ese caso se quita el repetido.
        /// <paramref name="curved"/> = tramos con bulge ≠ 0 (se evalúan por sus cuerdas). Abierta → INVALID_PARAMS.
        /// </summary>
        private static List<(double x, double y)> PolylineVertices(Polyline pl, string h, out int curved)
        {
            int n = pl.NumberOfVertices;
            var pts = new List<(double x, double y)>(n);
            for (int i = 0; i < n; i++)
            {
                Point3d q = pl.GetPoint3dAt(i);
                pts.Add((q.X, q.Y));
            }

            int segments;
            if (pl.Closed)
            {
                segments = n;
            }
            else if (n >= 3 && Distance(pts[0], pts[n - 1]) <= ClosureTolerance)
            {
                // Cerrada por repetición: el último vértice es el primero; su tramo de cierre no existe.
                pts.RemoveAt(n - 1);
                segments = n - 1;
            }
            else
            {
                throw ToolException.InvalidParams("contorno: la polilínea " + h + " no está cerrada");
            }

            curved = 0;
            for (int i = 0; i < segments && i < n; i++)
            {
                if (pl.GetBulgeAt(i) != 0.0)
                {
                    curved++;
                }
            }

            return pts;
        }

        private static double Distance((double x, double y) a, (double x, double y) b)
        {
            double dx = a.x - b.x;
            double dy = a.y - b.y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        /// <summary>Capa por nombre sin distinguir mayúsculas (no borrada), o ObjectId.Null.</summary>
        internal static ObjectId FindLayer(LayerTable layers, string name)
        {
            try
            {
                if (layers.Has(name))
                {
                    ObjectId id = layers[name];
                    if (!id.IsNull && !id.IsErased)
                    {
                        return id;
                    }
                }
            }
            catch (Exception)
            {
                // Nombre no válido como símbolo: no existe.
            }

            return ObjectId.Null;
        }

        internal static LayerInfo GetLayer(Transaction tr, ObjectId layerId, Dictionary<ObjectId, LayerInfo> cache)
        {
            if (cache.TryGetValue(layerId, out LayerInfo? info))
            {
                return info;
            }

            info = new LayerInfo("?", false, false, false);
            try
            {
                if (!layerId.IsNull && tr.GetObject(layerId, OpenMode.ForRead, false) is LayerTableRecord ltr)
                {
                    string name = ltr.Name;
                    info = new LayerInfo(
                        name,
                        ltr.IsLocked,
                        ltr.IsDependent || name.IndexOf('|') >= 0,
                        ltr.IsFrozen || ltr.IsOff);
                }
            }
            catch (Exception)
            {
                // Capa ilegible: se informa como "?".
            }

            cache[layerId] = info;
            return info;
        }

        private static void Increment(Dictionary<ObjectId, int> counts, ObjectId key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        internal static int CountIntersecting(List<BBox2> boxes, BBox2 window)
        {
            int n = 0;
            foreach (BBox2 b in boxes)
            {
                if (b.Intersects(window))
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>Ventana [[x, y], [x, y]] redondeada a 2 decimales (como el ejemplo de §5F.1).</summary>
        internal static double[][] Round2(BBox2 b) => new[]
        {
            new[] { Math.Round(b.Xmin, 2), Math.Round(b.Ymin, 2) },
            new[] { Math.Round(b.Xmax, 2), Math.Round(b.Ymax, 2) },
        };

        /// <summary>"REGION 6, OLE2FRAME 3": por conteo descendente y nombre.</summary>
        internal static string TypeList(Dictionary<string, int> counts)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, object?> kv in CleanWrite.SortedCounts(counts))
            {
                parts.Add(kv.Key + " " + Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
            }

            return string.Join(", ", parts);
        }

        // ------------------------------------------------------------------ escritura

        /// <summary>
        /// Borrado dentro de UNA transacción (dentro de UndoGroup.Run: un CTRL+Z). Solo revalida cada ObjectId de la
        /// lectura (IsErased, Entity, propietario = espacio del contorno, no es el contorno) y hace Erase; no recalcula
        /// cajas ni abre capas (L8). Si algo lanza, la transacción se descarta y no se borra nada.
        /// </summary>
        private static object Write(Document doc, int pid, CleanWrite.Mode mode, int ins, List<string> warnings, Inventory inv, long readMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Database db = doc.Database;
            int erased = 0;
            int skipped = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in inv.ToDelete)
                {
                    if (id.IsNull || id.IsErased || id == inv.ContourId)
                    {
                        skipped++;
                        continue;
                    }

                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity e) || e.OwnerId != inv.SpaceId)
                    {
                        skipped++;
                        continue;
                    }

                    e.Erase();
                    erased++;
                }

                tr.Commit();
            }

            long writeMs = sw.ElapsedMilliseconds;
            PluginLog.Info(string.Format(
                CultureInfo.InvariantCulture, "borrar_fuera_de: {0} entidades borradas en {1} ms", erased, writeMs));
            if (skipped > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} entidad(es) previstas ya no existían o ya no estaban en el espacio '{1}' y no se borraron.",
                    skipped,
                    inv.Espacio));
            }

            return BuildResponse(pid, mode, ins, warnings, inv, readMs, erased, writeMs);
        }

        // ------------------------------------------------------------------ respuesta

        private static Dictionary<string, object?> BuildResponse(
            int pid, CleanWrite.Mode mode, int ins, List<string> warnings, Inventory inv, long readMs, int? nBorradas, long? writeMs)
        {
            Dictionary<string, object?> r = CleanWrite.Response(pid, mode, ins, warnings);
            r["contorno"] = inv.Contorno;
            r["espacio"] = inv.Espacio;
            r["criterio"] = inv.Criterio;
            r["n_total"] = inv.NTotal;
            r["n_borrar"] = inv.ToDelete.Count;
            r["n_conservar"] = inv.NConservar;
            r["n_cruzan"] = inv.NCruzan;
            r["n_sin_caja"] = inv.NSinCaja;
            r["n_excluidas"] = inv.NExcluidas;
            r["n_borradas"] = nBorradas;
            r["por_tipo"] = CleanWrite.SortedCounts(inv.PorTipo);
            r["por_capa"] = CleanWrite.SortedCounts(inv.PorCapa);
            r["excluidas_por_capa"] = CleanWrite.SortedCounts(inv.ExcluidasPorCapa);
            r["muestra"] = inv.Muestra;
            r["viewports"] = inv.Viewports;
            r["duracion_ms"] = new Dictionary<string, object?> { ["lectura"] = readMs, ["escritura"] = writeMs };
            r["origen_texto"] = "contenido_del_plano";
            return r;
        }
    }
}
