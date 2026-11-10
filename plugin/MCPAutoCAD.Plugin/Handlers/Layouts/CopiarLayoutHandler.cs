using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// copiar_layout (plugin 0.9.0, fase M2). Sin confirmar: true solo previsualiza. Con confirmar: true duplica el layout
    /// (configuración de página, viewports y entidades del papel) con LayoutManager.CloneLayout y lo coloca al final, en
    /// un paso de deshacer. No cambia la pestaña activa.
    /// </summary>
    internal sealed class CopiarLayoutHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "nombre", "nuevo_nombre", "confirmar", "preview" };

        public string Name => "copiar_layout";

        public string Description =>
            "Duplica un layout de papel (configuración de página, viewports, rótulo y demás entidades del papel) con un nombre nuevo, " +
            "al final de las pestañas. Sin confirmar: true solo previsualiza. Con confirmar: true lo copia en un paso de deshacer; no " +
            "cambia la pestaña activa ni toca el espacio modelo.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": " + LayoutWrite.SchemaLayout + @",
    ""nuevo_nombre"": " + LayoutWrite.SchemaNuevoNombre + @",
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""nombre"", ""nuevo_nombre""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""origen"": { ""type"": ""string"" },
    ""nombre_nuevo"": { ""type"": ""string"" },
    ""orden"": { ""type"": ""integer"" },
    ""handle"": { ""type"": [""string"", ""null""], ""description"": ""Handle del layout nuevo (null en previsualización)."" },
    ""entidades_papel"": { ""type"": ""integer"" },
    ""viewports"": { ""type"": ""integer"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""origen"", ""nombre_nuevo"", ""orden"", ""handle"", ""entidades_papel"", ""viewports""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (string nombre, string nuevo, CleanWrite.Common common) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            return (LayoutWrite.ReadLayoutName(p, "nombre"), LayoutWrite.ReadNewName(p, "nuevo_nombre"), CleanWrite.ReadCommon(p));
        }

        private static (string real, int orden, int entidades, int viewports) Inspect(Database db, Transaction tr, string nombre, string nuevo)
        {
            LayoutWrite.Found found = LayoutWrite.FindLayout(db, tr, nombre);
            string? clash = LayoutWrite.Clash(db, tr, nuevo, null);
            if (clash != null)
            {
                throw ToolException.InvalidParams("nuevo_nombre: ya existe el layout '" + clash + "'");
            }

            int maxOrder = 0;
            foreach ((string _, ObjectId _, int order) in LayoutWrite.PaperLayouts(db, tr))
            {
                if (order > maxOrder)
                {
                    maxOrder = order;
                }
            }

            (int entidades, int viewports) = LayoutWrite.Count(tr, found.Layout);
            return (found.Name, maxOrder + 1, entidades, viewports);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (string nombre, string nuevo, CleanWrite.Common common) = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(common.Confirmar, common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            var warnings = new List<string>(AcadUtil.UnitWarnings(db));

            Dictionary<string, object?> Build(CleanWrite.Mode m, (string real, int orden, int entidades, int viewports) info, string? handle, int orden)
            {
                Dictionary<string, object?> r = CleanWrite.Response(ctx.Pid, m, ins, warnings);
                r["origen"] = info.real;
                r["nombre_nuevo"] = nuevo;
                r["orden"] = orden;
                r["handle"] = handle;
                r["entidades_papel"] = info.entidades;
                r["viewports"] = info.viewports;
                return r;
            }

            (string real, int orden, int entidades, int viewports) pre;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                pre = Inspect(db, tr, nombre, nuevo);
                tr.Commit();
            }

            if (mode == CleanWrite.Mode.Preview)
            {
                return Build(mode, pre, null, pre.orden);
            }

            return UndoGroup.Run(doc, d =>
            {
                // Revalidación en una Transaction cerrada antes de CloneLayout; el layout nuevo se lee en otra.
                (string real, int orden, int entidades, int viewports) info;
                using (Transaction tr = d.Database.TransactionManager.StartTransaction())
                {
                    info = Inspect(d.Database, tr, nombre, nuevo);
                    tr.Commit();
                }

                LayoutManager.Current.CloneLayout(info.real, nuevo, info.orden);
                using (Transaction tr = d.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    LayoutWrite.Found created = LayoutWrite.FindLayout(d.Database, tr, nuevo);
                    string handle = AcadUtil.HandleToString(created.Layout.Handle);
                    int orden = created.Layout.TabOrder;
                    (int entidades, int viewports) = LayoutWrite.Count(tr, created.Layout);
                    tr.Commit();
                    return Build(CleanWrite.Mode.Write, (info.real, info.orden, entidades, viewports), handle, orden);
                }
            });
        }
    }
}
