using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Layouts;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// ordenar_layouts (plugin 0.9.0, fase M2). Los layouts de 'orden' van primero, en ese orden; los demás siguen
    /// detrás en su orden actual (<see cref="ViewportPlanner.Reorder"/>). Sin confirmar: true solo previsualiza; con
    /// confirmar: true fija Layout.TabOrder (1…n) en un paso de deshacer. Si el orden ya es ese, no escribe.
    /// </summary>
    internal sealed class OrdenarLayoutsHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "orden", "confirmar", "preview" };

        public string Name => "ordenar_layouts";

        public string Description =>
            "Reordena las pestañas de layout: los nombres de 'orden' van primero, en ese orden, y los demás detrás en su orden " +
            "actual. Sin confirmar: true solo previsualiza el orden resultante. Con confirmar: true lo aplica en un paso de deshacer.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""orden"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 255, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 }, ""description"": ""Nombres de layout (sin distinguir mayúsculas) en el orden deseado; los que falten quedan detrás en su orden actual."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""orden""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""antes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""despues"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""cambia"": { ""type"": ""boolean"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""antes"", ""despues"", ""cambia""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (List<string> orden, CleanWrite.Common common) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            List<string> orden = ParamReader.OptionalStringArray(p, "orden", 1, 255, 1)
                ?? throw ToolException.InvalidParams("orden: obligatorio");
            for (int i = 0; i < orden.Count; i++)
            {
                if (ParamReader.CodePointCount(orden[i]) > LayoutRules.MaxNameLength)
                {
                    throw ToolException.InvalidParams("orden[" + i + "]: debe tener como máximo 255 caracteres");
                }
            }

            return (orden, CleanWrite.ReadCommon(p));
        }

        private static (List<string> antes, List<string> despues, Dictionary<string, ObjectId> ids) Plan(Database db, Transaction tr, List<string> orden)
        {
            var antes = new List<string>();
            var ids = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            foreach ((string n, ObjectId id, int _) in LayoutWrite.PaperLayouts(db, tr))
            {
                antes.Add(n);
                ids[n] = id;
            }

            List<string> despues = LayoutWrite.Checked(() => ViewportPlanner.Reorder(antes, orden));
            return (antes, despues, ids);
        }

        private static bool Same(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (List<string> orden, CleanWrite.Common common) = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(common.Confirmar, common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            var warnings = new List<string>(AcadUtil.UnitWarnings(db));

            Dictionary<string, object?> Build(CleanWrite.Mode m, List<string> antes, List<string> despues, bool cambia)
            {
                Dictionary<string, object?> r = CleanWrite.Response(ctx.Pid, m, ins, warnings);
                r["antes"] = antes;
                r["despues"] = despues;
                r["cambia"] = cambia;
                return r;
            }

            (List<string> antes, List<string> despues, Dictionary<string, ObjectId> _) pre;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                pre = Plan(db, tr, orden);
                tr.Commit();
            }

            bool cambia = !Same(pre.antes, pre.despues);
            if (mode == CleanWrite.Mode.Preview || !cambia)
            {
                if (!cambia)
                {
                    warnings.Add("Los layouts ya están en ese orden; no se escribe nada.");
                }

                Dictionary<string, object?> r = Build(mode, pre.antes, pre.despues, cambia);
                r["escrito"] = false; // confirmado pero sin cambios: preview refleja lo pedido, escrito que no hubo escritura
                return r;
            }

            return UndoGroup.Run(doc, d =>
            {
                using (Transaction tr = d.Database.TransactionManager.StartTransaction())
                {
                    var plan = Plan(d.Database, tr, orden);
                    for (int i = 0; i < plan.despues.Count; i++)
                    {
                        var layout = (Layout)tr.GetObject(plan.ids[plan.despues[i]], OpenMode.ForWrite);
                        if (layout.TabOrder != i + 1)
                        {
                            layout.TabOrder = i + 1;
                        }
                    }

                    tr.Commit();
                    return Build(CleanWrite.Mode.Write, plan.antes, plan.despues, true);
                }
            });
        }
    }
}
