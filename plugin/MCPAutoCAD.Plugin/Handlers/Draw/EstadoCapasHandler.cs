using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// estado_capas (fallo 39). Hilo principal; requiere documento; ESCRITURA (solo con confirmar: true).
    /// <para>Apaga/enciende (ON/OFF), congela/descongela, bloquea/desbloquea y pone plot on/off a las capas que coinciden con
    /// los patrones 'capas' (comodines '*' y '?', sin distinguir mayúsculas). Al menos una bandera es obligatoria. Reglas puras
    /// (patrones, validación, plan de cambios) en <see cref="LayerStateRules"/>.</para>
    /// <para>Previsualización (lectura pura, StartOpenCloseTransaction): sin confirmar: true no escribe nada y devuelve estado
    /// antes/después de cada capa coincidente. Escritura: UNA Transaction dentro de <see cref="UndoGroup.Run"/>, un CTRL+Z.
    /// Tras congelar o descongelar se hace <c>Editor.Regen</c> (fuera de la Transaction, tras el commit) para que el dibujo
    /// refleje el cambio. Sin coincidencias → INVALID_PARAMS en ambos modos. Capas de xref se omiten con advertencia; la capa
    /// actual nunca se congela (AutoCAD lo prohíbe) y su congelado se descarta con advertencia.</para>
    /// </summary>
    internal sealed class EstadoCapasHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "capas", "apagada", "congelada", "bloqueada", "imprimir", "confirmar", "preview" };

        public string Name => "estado_capas";

        public string Description =>
            "CAMBIA EL ESTADO de capas por patrón (comodines * y ?, sin distinguir mayúsculas): apagada/encendida (ON/OFF), " +
            "congelada/descongelada, bloqueada/desbloqueada y plot on/off (imprimir). Sin confirmar: true solo PREVISUALIZA " +
            "(estado antes/después de cada capa y patrones sin coincidencia) y no escribe. Con confirmar: true aplica todo en un " +
            "solo paso de deshacer. No congela la capa actual (AutoCAD lo prohíbe; se avisa) ni toca capas de xref (se omiten " +
            "con aviso). Sin coincidencias → INVALID_PARAMS.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""capas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": " + LayerStateRules.MaxPatrones + @", ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": " + LayerStateRules.MaxLongitudPatron + @" }, ""description"": ""Nombres o patrones de capa con comodines '*' (cualquier secuencia) y '?' (un carácter); no admite listas con ',' ni otros comodines de AutoCAD (#, @, ~, [ ]). Sin distinguir mayúsculas. Ej.: [\""*_A-AREA-PATT\""]. Un nombre sin comodines coincide solo con esa capa."" },
    ""apagada"": { ""type"": ""boolean"", ""description"": ""true = apagar la capa (ON/OFF); false = encenderla. Sin el campo no cambia."" },
    ""congelada"": { ""type"": ""boolean"", ""description"": ""true = congelar; false = descongelar. La capa actual no se puede congelar (se omite con aviso). Sin el campo no cambia."" },
    ""bloqueada"": { ""type"": ""boolean"", ""description"": ""true = bloquear; false = desbloquear. Sin el campo no cambia."" },
    ""imprimir"": { ""type"": ""boolean"", ""description"": ""Plot on (true) / plot off (false). Sin el campo no cambia."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = ESCRIBIR el nuevo estado de las capas coincidentes en un solo paso de deshacer. Sin él solo se previsualiza y no se modifica nada."" },
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""capas""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""cambio"": { ""type"": ""object"", ""description"": ""Banderas pedidas (solo las que se pasaron)."", ""properties"": {
      ""apagada"": { ""type"": ""boolean"" }, ""congelada"": { ""type"": ""boolean"" }, ""bloqueada"": { ""type"": ""boolean"" }, ""imprimir"": { ""type"": ""boolean"" } } },
    ""capas"": { ""type"": ""array"", ""description"": ""Capas que coinciden, en orden de la tabla de capas."", ""items"": { ""type"": ""object"", ""properties"": {
      ""capa"": { ""type"": ""string"" },
      ""estado"": { ""type"": ""string"", ""enum"": [""cambia"", ""sin_cambio"", ""omitida""] },
      ""antes"": { ""type"": ""object"", ""properties"": {
        ""apagada"": { ""type"": ""boolean"" }, ""congelada"": { ""type"": ""boolean"" }, ""bloqueada"": { ""type"": ""boolean"" }, ""imprimir"": { ""type"": ""boolean"" } } },
      ""despues"": { ""type"": ""object"", ""properties"": {
        ""apagada"": { ""type"": ""boolean"" }, ""congelada"": { ""type"": ""boolean"" }, ""bloqueada"": { ""type"": ""boolean"" }, ""imprimir"": { ""type"": ""boolean"" } } },
      ""motivo"": { ""type"": ""string"", ""description"": ""Solo con estado omitida."" } } } },
    ""patrones_sin_coincidencia"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""n_coincidencias"": { ""type"": ""integer"" },
    ""n_cambian"": { ""type"": ""integer"" },
    ""n_sin_cambio"": { ""type"": ""integer"" },
    ""n_omitidas"": { ""type"": ""integer"" },
    ""regenerado"": { ""type"": ""boolean"", ""description"": ""Solo al escribir con cambios: true si se hizo Editor.Regen (vista actual) tras cambiar las capas; tras un CTRL+Z puede hacer falta REGEN."" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""cambio"", ""capas"", ""patrones_sin_coincidencia"", ""n_coincidencias"", ""n_cambian"", ""n_sin_cambio"", ""n_omitidas""]
}");

        /// <summary>Parámetros ya leídos (sin AutoCAD).</summary>
        private sealed class Params
        {
            public Params(List<string> patrones, LayerStateChange cambio, CleanWrite.Mode mode)
            {
                Patrones = patrones;
                Cambio = cambio;
                Mode = mode;
            }

            public List<string> Patrones { get; }

            public LayerStateChange Cambio { get; }

            public CleanWrite.Mode Mode { get; }
        }

        /// <summary>Estado de todas las capas leído en una pasada: instantáneas, IDs por nombre y capa actual.</summary>
        private sealed class Lectura
        {
            public List<LayerStateSnapshot> Snaps { get; } = new List<LayerStateSnapshot>();

            public Dictionary<string, ObjectId> Ids { get; } = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);

            public string? Actual { get; set; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            List<string>? capas = ParamReader.OptionalStringArray(p, "capas", 1, LayerStateRules.MaxPatrones, 1);
            string? errorCapas = LayerStateRules.ValidatePatterns(capas);
            if (errorCapas != null)
            {
                throw ToolException.InvalidParams(errorCapas);
            }

            var cambio = new LayerStateChange(
                ParamReader.OptionalBool(p, "apagada"),
                ParamReader.OptionalBool(p, "congelada"),
                ParamReader.OptionalBool(p, "bloqueada"),
                ParamReader.OptionalBool(p, "imprimir"));
            string? errorCambio = LayerStateRules.ValidateChange(cambio);
            if (errorCambio != null)
            {
                throw ToolException.InvalidParams(errorCambio);
            }

            CleanWrite.Common common = CleanWrite.ReadCommon(p);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(common.Confirmar, common.Preview);
            return new Params(capas!, cambio, mode);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            List<string> advertencias = AcadUtil.UnitWarnings(db);

            // Lectura previa SIN efectos: el plan de lo que se haría (también es la previsualización).
            LayerStatePlan plan;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                Lectura lec = Leer(db, tr);
                plan = LayerStateRules.Plan(lec.Snaps, p.Patrones, p.Cambio, lec.Actual);
                tr.Commit();
            }

            if (plan.Coincidencias == 0)
            {
                throw SinCoincidencias(plan);
            }

            if (p.Mode == CleanWrite.Mode.Preview)
            {
                return Respuesta(ctx.Pid, p, ins, advertencias, plan, escrito: false, regenerado: null);
            }

            if (plan.Cambian == 0)
            {
                // Auditoría: nada que cambiar → sin grupo de deshacer y escrito:false.
                advertencias.Add("estado_capas: ninguna capa cambia (ya tienen ese estado, son de xref o es la capa actual): no se escribió nada.");
                Dictionary<string, object?> sinCambios = Respuesta(ctx.Pid, p, ins, advertencias, plan, escrito: false, regenerado: null);
                sinCambios["preview"] = false;   // se pidió confirmar: no es una previsualización (re-auditoría)
                return sinCambios;
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, advertencias));
        }

        /// <summary>Escritura: re-planifica dentro de la Transaction, aplica el estado a las capas que cambian y regenera si hace falta.</summary>
        private static object Write(Document doc, Params p, int pid, int ins, List<string> advertencias)
        {
            Database db = doc.Database;
            LayerStatePlan plan;
            bool visibleCambia = false;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Lectura lec = Leer(db, tr);
                plan = LayerStateRules.Plan(lec.Snaps, p.Patrones, p.Cambio, lec.Actual);
                if (plan.Coincidencias == 0)
                {
                    throw SinCoincidencias(plan);
                }

                foreach (LayerStateItem item in plan.Capas.Where(c => c.Estado == LayerStateOutcome.Cambia))
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lec.Ids[item.Nombre], OpenMode.ForWrite);
                    LayerStateSnapshot d = item.Despues;
                    if (ltr.IsOff != d.Apagada)
                    {
                        ltr.IsOff = d.Apagada;
                    }

                    if (ltr.IsFrozen != d.Congelada)
                    {
                        ltr.IsFrozen = d.Congelada;
                    }

                    if (ltr.IsLocked != d.Bloqueada)
                    {
                        ltr.IsLocked = d.Bloqueada;
                    }

                    if (ltr.IsPlottable != d.Imprimir)
                    {
                        ltr.IsPlottable = d.Imprimir;
                    }

                    if (ltr.IsOff != item.Antes.Apagada || ltr.IsFrozen != item.Antes.Congelada || ltr.IsLocked != item.Antes.Bloqueada)
                    {
                        visibleCambia = true;   // cambio visible: regenerar (apagar/bloquear tampoco repintan solos); imprimir no
                    }

                    PluginLog.Info("estado_capas: '" + item.Nombre + "' actualizada.");
                }

                tr.Commit();
            }

            bool? regenerado = null;
            if (visibleCambia)
            {
                // Fuera de la Transaction y tras el commit: REGEN recalcula la visibilidad de las capas congeladas.
                try
                {
                    doc.Editor.Regen();
                    regenerado = true;
                }
                catch (Exception ex)
                {
                    regenerado = false;
                    advertencias.Add("regen: no se pudo regenerar el dibujo tras cambiar las capas (" + ex.Message + "); use REGEN.");
                    PluginLog.Warn("estado_capas: Editor.Regen falló (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
            }

            return Respuesta(pid, p, ins, advertencias, plan, escrito: true, regenerado: regenerado);
        }

        /// <summary>Lee instantáneas, IDs y capa actual. Solo lectura.</summary>
        private static Lectura Leer(Database db, Transaction tr)
        {
            var lec = new Lectura();
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                bool dependiente = ltr.IsDependent || ltr.Name.IndexOf('|') >= 0;
                lec.Snaps.Add(new LayerStateSnapshot(ltr.Name, ltr.IsOff, ltr.IsFrozen, ltr.IsLocked, ltr.IsPlottable, dependiente));
                lec.Ids[ltr.Name] = id;
            }

            var actual = (LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead);
            lec.Actual = actual.Name;
            return lec;
        }

        private static ToolException SinCoincidencias(LayerStatePlan plan) =>
            ToolException.InvalidParams(
                LayerStateRules.SinCoincidencias,
                new Dictionary<string, object?> { ["patrones_sin_coincidencia"] = plan.PatronesSinCoincidencia.ToList() });

        private static Dictionary<string, object?> Respuesta(
            int pid, Params p, int ins, List<string> advertencias, LayerStatePlan plan, bool escrito, bool? regenerado)
        {
            var todas = new List<string>(advertencias);
            todas.AddRange(plan.Advertencias);
            Dictionary<string, object?> r = CleanWrite.Response(pid, escrito ? CleanWrite.Mode.Write : CleanWrite.Mode.Preview, ins, todas);

            var cambio = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (p.Cambio.Apagada.HasValue) cambio["apagada"] = p.Cambio.Apagada.Value;
            if (p.Cambio.Congelada.HasValue) cambio["congelada"] = p.Cambio.Congelada.Value;
            if (p.Cambio.Bloqueada.HasValue) cambio["bloqueada"] = p.Cambio.Bloqueada.Value;
            if (p.Cambio.Imprimir.HasValue) cambio["imprimir"] = p.Cambio.Imprimir.Value;

            r["cambio"] = cambio;
            r["capas"] = plan.Capas.Select(c => (object)CapaDict(c)).ToList();
            r["patrones_sin_coincidencia"] = plan.PatronesSinCoincidencia.ToList();
            r["n_coincidencias"] = plan.Coincidencias;
            r["n_cambian"] = plan.Cambian;
            r["n_sin_cambio"] = plan.SinCambio;
            r["n_omitidas"] = plan.Omitidas;
            if (regenerado.HasValue)
            {
                r["regenerado"] = regenerado.Value;
            }

            return r;
        }

        private static Dictionary<string, object?> CapaDict(LayerStateItem c)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["capa"] = c.Nombre,
                ["estado"] = EstadoTexto(c.Estado),
                ["antes"] = Banderas(c.Antes),
                ["despues"] = Banderas(c.Despues),
            };
            if (c.Motivo != null)
            {
                d["motivo"] = c.Motivo;
            }

            return d;
        }

        private static string EstadoTexto(LayerStateOutcome e) => e switch
        {
            LayerStateOutcome.Cambia => "cambia",
            LayerStateOutcome.SinCambio => "sin_cambio",
            _ => "omitida",
        };

        private static Dictionary<string, object?> Banderas(LayerStateSnapshot s) => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["apagada"] = s.Apagada,
            ["congelada"] = s.Congelada,
            ["bloqueada"] = s.Bloqueada,
            ["imprimir"] = s.Imprimir,
        };
    }
}
