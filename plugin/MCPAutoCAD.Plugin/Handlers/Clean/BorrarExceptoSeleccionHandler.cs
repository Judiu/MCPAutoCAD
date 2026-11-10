using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Clean;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Clean
{
    /// <summary>
    /// borrar_excepto_seleccion (plugin 0.8.2). Hilo principal; requiere documento; ESCRITURA (solo con confirmar: true).
    /// Borra, en el espacio actual, toda entidad que NO está en la selección implícita (pickfirst) del usuario.
    /// Mismo flujo que borrar_fuera_de (§5F.0):
    /// <list type="number">
    /// <item>La selección se lee en contexto de aplicación ANTES del UndoGroup (dentro del comando vendría vacía) y se
    /// restaura enseguida.</item>
    /// <item>Lectura con StartOpenCloseTransaction: clasificación de todas las entidades del espacio, capas bloqueadas/xref
    /// (rechazo), viewports de papel afectados. Todos los rechazos salen aquí, sin paso de deshacer.</item>
    /// <item>Previsualización → inventario. Escritura → UNA transacción en <see cref="UndoGroup.Run"/> (un Ctrl+Z).</item>
    /// </list>
    /// Seguridad propia: selección vacía → rechazo (evita borrar el dibujo entero); con confirmar: true es obligatorio
    /// firma_seleccion = la de la previsualización (hash de los handles), para que un cambio de selección entre la previsualización
    /// y la confirmación (Esc, otro clic) no borre lo que el usuario quería conservar. A diferencia de borrar_fuera_de,
    /// las entidades sin caja calculable SÍ se borran (la decisión no depende de la caja); los viewports nunca. Lo que está en
    /// capas congeladas o apagadas se CONSERVA salvo incluir_ocultas: true (el usuario no pudo verlo ni seleccionarlo).
    /// </summary>
    internal sealed class BorrarExceptoSeleccionHandler : IToolHandler
    {
        private const int DefaultSample = 10;

        private static readonly string[] Allowed =
        {
            "excluir_capas", "muestra", "firma_seleccion", "incluir_ocultas", "confirmar", "preview",
        };

        public string Name => "borrar_excepto_seleccion";

        public string Description =>
            "BORRA todas las entidades del espacio actual que NO están en la selección del usuario (lo seleccionado con pinzamientos " +
            "se conserva). Sin confirmar: true solo PREVISUALIZA (n_seleccion, firma_seleccion, n_borrar, inventario por tipo y capa, " +
            "viewports afectados) y no modifica nada; con confirmar: true exige la firma_seleccion de la previsualización (si la selección " +
            "cambió, rechaza sin borrar) y borra en un solo paso de deshacer. Por defecto CONSERVA lo que está en capas congeladas o " +
            "apagadas (el usuario no pudo verlo ni seleccionarlo; incluir_ocultas: true para borrarlo). Rechaza una selección vacía y capas " +
            "bloqueadas o de xref con entidades a borrar. Nunca borra viewports; sí borra referencias externas, imágenes y underlays no seleccionados.";

        /// <summary>Si vence el plazo, la escritura diferida puede terminar igual.</summary>
        public string TimeoutHint(JsonElement parameters) =>
            "El borrado puede haber terminado en AutoCAD. Vuelva a previsualizar: si n_borrar es 0 ya se hizo; un CTRL+Z lo revierte.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        /// <summary>Borrar decenas de miles de entidades puede superar el plazo general.</summary>
        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""excluir_capas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Capas (sin distinguir mayúsculas) cuyas entidades nunca se borran aunque no estén seleccionadas; deben existir."" },
    ""muestra"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50, ""default"": 10, ""description"": ""Cuántos handles de ejemplo de lo que se borra devolver."" },
    ""firma_seleccion"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{16}$"", ""description"": ""Obligatoria con confirmar: true: la firma_seleccion que devolvió la previsualización. Si la selección cambió (aunque tenga el mismo número de entidades), se rechaza sin borrar."" },
    ""incluir_ocultas"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = borra también lo no seleccionado en capas congeladas o apagadas; por defecto se conserva y se informa en n_ocultas."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""espacio"": { ""type"": ""string"" },
    ""n_total"": { ""type"": ""integer"" },
    ""n_seleccion"": { ""type"": ""integer"" },
    ""firma_seleccion"": { ""type"": ""string"" },
    ""n_ocultas"": { ""type"": ""integer"" },
    ""ocultas_por_capa"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""n_borrar"": { ""type"": ""integer"" },
    ""n_conservar"": { ""type"": ""integer"" },
    ""n_excluidas"": { ""type"": ""integer"" },
    ""n_borradas"": { ""type"": [""integer"", ""null""] },
    ""n_esperado_despues"": { ""type"": ""integer"" },
    ""por_tipo"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""por_capa"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""excluidas_por_capa"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""muestra"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""viewports"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""duracion_ms"": { ""type"": ""object"" },
    ""origen_texto"": { ""type"": ""string"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""espacio"", ""n_total"", ""n_seleccion"", ""firma_seleccion"", ""n_ocultas"", ""ocultas_por_capa"", ""n_borrar"", ""n_conservar"", ""n_excluidas"", ""n_borradas"", ""n_esperado_despues"", ""por_tipo"", ""por_capa"", ""excluidas_por_capa"", ""muestra"", ""viewports"", ""duracion_ms"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Params
        {
            public Params(List<string>? excluirCapas, int muestra, string? firma, bool incluirOcultas, CleanWrite.Common common)
            {
                ExcluirCapas = excluirCapas;
                Muestra = muestra;
                Firma = firma;
                IncluirOcultas = incluirOcultas;
                Common = common;
            }

            public List<string>? ExcluirCapas { get; }

            public int Muestra { get; }

            public string? Firma { get; }

            public bool IncluirOcultas { get; }

            public CleanWrite.Common Common { get; }
        }

        private sealed class Inventory
        {
            public ObjectId SpaceId { get; set; }

            public string Espacio { get; set; } = SpaceResolver.ModelName;

            public int NTotal { get; set; }

            public int NSeleccion { get; set; }

            public string Firma { get; set; } = string.Empty;

            public int NOcultas { get; set; }

            public Dictionary<string, int> OcultasPorCapa { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            public int NConservar { get; set; }

            public int NExcluidas { get; set; }

            public HashSet<ObjectId> Keep { get; } = new HashSet<ObjectId>();

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
            List<string>? excluir = ParamReader.OptionalStringArray(p, "excluir_capas", 1, 100, 1);
            int muestra = ParamReader.OptionalInt(p, "muestra", 1, 50) ?? DefaultSample;
            string? firma = ParamReader.OptionalString(p, "firma_seleccion", 16, 16)?.ToUpperInvariant();
            if (firma != null && !System.Text.RegularExpressions.Regex.IsMatch(firma, "^[0-9A-F]{16}$"))
            {
                throw ToolException.InvalidParams("firma_seleccion: 16 dígitos hexadecimales");
            }

            bool incluirOcultas = ParamReader.OptionalBool(p, "incluir_ocultas") ?? false;
            CleanWrite.Common common = CleanWrite.ReadCommon(p);
            if (CleanWrite.ResolveMode(common.Confirmar, common.Preview) == CleanWrite.Mode.Write && firma == null)
            {
                throw ToolException.InvalidParams(
                    "firma_seleccion: obligatoria con confirmar: true (use la firma_seleccion de la previsualización)");
            }

            return new Params(excluir, muestra, firma, incluirOcultas, common);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db); // L3 desde 0.8.3: no interpreta unidades, INSUNITS = 0 solo advierte

            // La selección se lee aquí, en contexto de aplicación, y se restaura antes de cualquier escritura.
            List<ObjectId> selection = SelectionReader.ReadImplied(doc.Editor);
            SelectionReader.Restore(doc.Editor, selection, warnings);
            if (selection.Count == 0)
            {
                throw ToolException.InvalidParams(
                    "selección vacía: seleccione en AutoCAD lo que se conserva (con pinzamientos) antes de llamar a borrar_excepto_seleccion");
            }

            string firmaActual = Signature(selection);
            if (mode == CleanWrite.Mode.Write && !string.Equals(p.Firma, firmaActual, StringComparison.Ordinal))
            {
                throw ToolException.InvalidParams(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "la selección cambió desde la previsualización (ahora {0} entidad(es)); vuelva a previsualizar",
                        selection.Count),
                    new Dictionary<string, object?> { ["n_seleccion"] = selection.Count, ["firma_seleccion"] = firmaActual });
            }

            Stopwatch read = Stopwatch.StartNew();
            Inventory inv;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                inv = Read(db, tr, p, selection, warnings);
                inv.Firma = firmaActual;
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

        private static Inventory Read(Database db, Transaction tr, Params p, List<ObjectId> selection, List<string> warnings)
        {
            var inv = new Inventory();
            SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, null);
            BlockTableRecord owner = space.Btr;
            inv.Espacio = space.Name;
            inv.SpaceId = owner.ObjectId;

            int outsideSpace = 0;
            foreach (ObjectId id in selection)
            {
                inv.Keep.Add(id);
            }

            // ---- excluir_capas: deben existir.
            var excluded = new HashSet<ObjectId>();
            if (p.ExcluirCapas != null)
            {
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var missing = new List<string>();
                foreach (string name in p.ExcluirCapas)
                {
                    ObjectId layerId = BorrarFueraDeHandler.FindLayer(layers, name);
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

            // ---- Clasificación de todas las entidades del espacio.
            var layerCache = new Dictionary<ObjectId, BorrarFueraDeHandler.LayerInfo>();
            var deleteByLayer = new Dictionary<ObjectId, int>();
            var excludedByLayer = new Dictionary<ObjectId, int>();
            var deleteBoxes = new List<BBox2>();
            var keptBoxes = new List<BBox2>();
            bool isModel = string.Equals(inv.Espacio, SpaceResolver.ModelName, StringComparison.Ordinal);
            int keptViewports = 0;
            int unreadable = 0;
            int selectedInSpace = 0;
            foreach (ObjectId id in owner)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                inv.NTotal++;
                Entity? e = AuditSupport.Open(tr, id);
                if (inv.Keep.Contains(id))
                {
                    selectedInSpace++;
                    inv.NConservar++;
                    if (isModel && e != null)
                    {
                        BBox2? kb = AcadUtil.TryBBox2(e);
                        if (kb != null)
                        {
                            keptBoxes.Add(kb.Value);
                        }
                    }

                    continue;
                }

                if (e == null)
                {
                    // Objeto ilegible: no se borra.
                    unreadable++;
                    inv.NConservar++;
                    continue;
                }

                if (e is Viewport)
                {
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
                    IncrementId(excludedByLayer, layerId);
                    continue;
                }

                if (!p.IncluirOcultas)
                {
                    BorrarFueraDeHandler.LayerInfo li = BorrarFueraDeHandler.GetLayer(tr, layerId, layerCache);
                    if (li.Hidden)
                    {
                        inv.NConservar++;
                        inv.NOcultas++;
                        CleanWrite.Increment(inv.OcultasPorCapa, li.Name);
                        continue;
                    }
                }

                inv.ToDelete.Add(id);
                IncrementId(deleteByLayer, layerId);
                CleanWrite.Increment(inv.PorTipo, AcadUtil.DxfName(e));
                if (inv.Muestra.Count < p.Muestra)
                {
                    inv.Muestra.Add(AuditSupport.Handle(id));
                }

                if (isModel)
                {
                    BBox2? db2 = AcadUtil.TryBBox2(e);
                    if (db2 != null)
                    {
                        deleteBoxes.Add(db2.Value);
                    }
                }
            }

            inv.NSeleccion = selection.Count;
            outsideSpace = selection.Count - selectedInSpace;
            if (outsideSpace > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} entidad(es) seleccionadas no están en el espacio '{1}' y no cuentan.",
                    outsideSpace,
                    inv.Espacio));
            }

            if (selectedInSpace == 0)
            {
                throw ToolException.InvalidParams(
                    "ninguna entidad seleccionada está en el espacio actual '" + inv.Espacio + "'; no se borra nada");
            }

            // ---- Capas de lo que se borra: bloqueadas / xref → rechazo; congeladas o apagadas → advertencia (solo con incluir_ocultas: true; si no, ya se conservaron arriba).
            var blocked = new List<(string name, int n)>();
            int hidden = 0;
            foreach (KeyValuePair<ObjectId, int> kv in deleteByLayer)
            {
                BorrarFueraDeHandler.LayerInfo info = BorrarFueraDeHandler.GetLayer(tr, kv.Key, layerCache);
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
                inv.ExcluidasPorCapa[BorrarFueraDeHandler.GetLayer(tr, kv.Key, layerCache).Name] = kv.Value;
            }

            // ---- Viewports de papel que miran lo borrado (solo si el espacio es el modelo).
            if (isModel)
            {
                foreach (LayoutViewports.ViewportWindowInfo w in LayoutViewports.Windows(db, tr))
                {
                    int nb = BorrarFueraDeHandler.CountIntersecting(deleteBoxes, w.Ventana);
                    int nc = BorrarFueraDeHandler.CountIntersecting(keptBoxes, w.Ventana);
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
                        ["ventana"] = BorrarFueraDeHandler.Round2(w.Ventana),
                        ["estado"] = estado,
                        ["n_borradas"] = nb,
                        ["n_conservadas"] = nc,
                    });
                }
            }

            if (unreadable > 0)
            {
                warnings.Add(string.Format(CultureInfo.InvariantCulture, "{0} objeto(s) ilegibles se conservaron.", unreadable));
            }

            if (keptViewports > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Se conservaron {0} viewport(s) del layout {1}: borrar_excepto_seleccion no borra viewports.",
                    keptViewports,
                    inv.Espacio));
            }

            if (inv.NOcultas > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Se conservaron {0} entidad(es) no seleccionadas en capas congeladas o apagadas (ver ocultas_por_capa); incluir_ocultas: true las borra.",
                    inv.NOcultas));
            }

            if (hidden > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} entidad(es) a borrar están en capas congeladas o apagadas (no se ven en pantalla).",
                    hidden));
            }

            return inv;
        }

        /// <summary>Firma de la selección: 16 hex de SHA-256 de los handles ordenados (independiente del orden de selección).</summary>
        internal static string Signature(IEnumerable<ObjectId> ids)
        {
            var handles = new List<string>();
            foreach (ObjectId id in ids)
            {
                handles.Add(AcadUtil.HandleToString(id.Handle));
            }

            handles.Sort(StringComparer.Ordinal);
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(",", handles)));
            return Convert.ToHexString(hash, 0, 8);
        }

        private static void IncrementId(Dictionary<ObjectId, int> counts, ObjectId key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        // ------------------------------------------------------------------ escritura

        /// <summary>
        /// Borrado en UNA transacción (dentro de UndoGroup.Run: un Ctrl+Z). Revalida cada ObjectId (no borrado, Entity,
        /// propietario = espacio, no seleccionado) y hace Erase. Si algo lanza, la transacción se descarta y no se borra nada.
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
                    if (id.IsNull || id.IsErased || inv.Keep.Contains(id))
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
                CultureInfo.InvariantCulture, "borrar_excepto_seleccion: {0} entidades borradas en {1} ms", erased, writeMs));
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
            r["espacio"] = inv.Espacio;
            r["n_total"] = inv.NTotal;
            r["n_seleccion"] = inv.NSeleccion;
            r["firma_seleccion"] = inv.Firma;
            r["n_ocultas"] = inv.NOcultas;
            r["ocultas_por_capa"] = CleanWrite.SortedCounts(inv.OcultasPorCapa);
            r["n_borrar"] = inv.ToDelete.Count;
            r["n_conservar"] = inv.NConservar;
            r["n_excluidas"] = inv.NExcluidas;
            r["n_borradas"] = nBorradas;
            r["n_esperado_despues"] = inv.NTotal - inv.ToDelete.Count;
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
