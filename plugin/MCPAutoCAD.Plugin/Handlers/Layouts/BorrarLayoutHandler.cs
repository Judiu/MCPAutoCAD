using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// borrar_layout (plugin 0.9.0, fase M2). Sin confirmar: true solo previsualiza (cuántas entidades y viewports se
    /// perderían). Con confirmar: true borra el layout con LayoutManager.DeleteLayout en un paso de deshacer. No borra el
    /// layout activo (cambiaría la pestaña) ni el único layout de papel. El espacio modelo no se toca.
    /// </summary>
    internal sealed class BorrarLayoutHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "nombre", "confirmar", "preview" };

        public string Name => "borrar_layout";

        public string Description =>
            "Borra un layout de papel con todo lo que tiene en el papel (viewports, rótulo, textos). Sin confirmar: true solo " +
            "previsualiza cuántas entidades y viewports se perderían. Con confirmar: true lo borra en un paso de deshacer. No borra " +
            "el layout activo ni el único layout. El espacio modelo no se toca.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": " + LayoutWrite.SchemaLayout + @",
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""nombre""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""nombre"": { ""type"": ""string"" },
    ""entidades_papel"": { ""type"": ""integer"" },
    ""viewports"": { ""type"": ""integer"" },
    ""layouts_restantes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""nombre"", ""entidades_papel"", ""viewports"", ""layouts_restantes""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (string nombre, CleanWrite.Common common) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            return (LayoutWrite.ReadLayoutName(p, "nombre"), CleanWrite.ReadCommon(p));
        }

        /// <summary>Comprobaciones de lectura; devuelve el nombre real, los conteos y los layouts que quedarían.</summary>
        private static (string real, int entidades, int viewports, List<string> restantes) Inspect(Database db, Transaction tr, string nombre)
        {
            LayoutWrite.Found found = LayoutWrite.FindLayout(db, tr, nombre);
            if (LayoutWrite.IsCurrent(found.Name, LayoutWrite.CurrentTab()))
            {
                throw ToolException.InvalidParams("nombre: '" + found.Name + "' es el layout activo; active otra pestaña antes de borrarlo");
            }

            var restantes = new List<string>();
            foreach ((string n, ObjectId _, int _) in LayoutWrite.PaperLayouts(db, tr))
            {
                if (!string.Equals(n, found.Name, System.StringComparison.OrdinalIgnoreCase))
                {
                    restantes.Add(n);
                }
            }

            if (restantes.Count == 0)
            {
                throw ToolException.InvalidParams("nombre: '" + found.Name + "' es el único layout de papel; AutoCAD exige al menos uno");
            }

            (int entidades, int viewports) = LayoutWrite.Count(tr, found.Layout);
            return (found.Name, entidades, viewports, restantes);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (string nombre, CleanWrite.Common common) = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(common.Confirmar, common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            var warnings = new List<string>(AcadUtil.UnitWarnings(db));

            Dictionary<string, object?> Build(CleanWrite.Mode m, (string real, int entidades, int viewports, List<string> restantes) info)
            {
                Dictionary<string, object?> r = CleanWrite.Response(ctx.Pid, m, ins, warnings);
                r["nombre"] = info.real;
                r["entidades_papel"] = info.entidades;
                r["viewports"] = info.viewports;
                r["layouts_restantes"] = info.restantes;
                return r;
            }

            if (mode == CleanWrite.Mode.Preview)
            {
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var info = Inspect(db, tr, nombre);
                    tr.Commit();
                    if (info.entidades > 0)
                    {
                        warnings.Add("Se perderán " + info.entidades + " entidad(es) del papel de '" + info.real + "' (" + info.viewports + " viewport(s)); el espacio modelo no se toca. CTRL+Z lo revierte.");
                    }

                    return Build(mode, info);
                }
            }

            // Lectura previa fuera del paso de deshacer: un rechazo no deja un paso vacío.
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                Inspect(db, tr, nombre);
                tr.Commit();
            }

            return UndoGroup.Run(doc, d =>
            {
                // Revalidación dentro del paso de deshacer; la Transaction se cierra antes de que LayoutManager borre el
                // Layout (no queda abierto un objeto que después se borra).
                (string real, int entidades, int viewports, List<string> restantes) info;
                using (Transaction tr = d.Database.TransactionManager.StartTransaction())
                {
                    info = Inspect(d.Database, tr, nombre);
                    tr.Commit();
                }

                LayoutManager.Current.DeleteLayout(info.real);
                return Build(CleanWrite.Mode.Write, info);
            });
        }
    }
}
