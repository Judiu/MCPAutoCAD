using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// resaltar (0.8.1). Hilo principal; requiere documento. Marca o desmarca entidades (<c>Entity.Highlight</c> /
    /// <c>Entity.Unhighlight</c>) para que el usuario las localice. No modifica las entidades ni abre grupo de deshacer;
    /// el resaltado es visual y no se guarda en el dibujo.
    /// </summary>
    internal sealed class ResaltarHandler : IToolHandler
    {
        private const int MaxHandles = 500;

        private static readonly string[] Allowed = { "handles", "activar" };

        public string Name => "resaltar";

        public string Description =>
            "RESALTA (o quita el resaltado de) entidades por handle para localizarlas en pantalla. No modifica las entidades, no crea paso de deshacer y no se guarda en el dibujo. " +
            "Rechaza handles inexistentes.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Entidades a resaltar (1–500, sin repetidos)."" },
    ""activar"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true = resaltar; false = quitar el resaltado."" }
  },
  ""required"": [""handles""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""n"": { ""type"": ""integer"" },
    ""activar"": { ""type"": ""boolean"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""handles"", ""n"", ""activar"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Params
        {
            public Params(List<string> handles, bool activar)
            {
                Handles = handles;
                Activar = activar;
            }

            public List<string> Handles { get; }

            public bool Activar { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            List<string> handles = ParamReader.RequiredHandles(p, "handles", 1, MaxHandles);
            bool activar = ParamReader.OptionalBool(p, "activar") ?? true;
            return new Params(handles, activar);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            Editor ed = doc.Editor;
            int ins = AcadUtil.InsUnits(db);

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                // Todas las entidades se localizan antes de tocar ninguna: un handle inexistente no deja nada a medias.
                var entities = new List<Entity>(p.Handles.Count);
                foreach (string h in p.Handles)
                {
                    entities.Add(AcadUtil.TryGetEntityByHandle(db, h, tr) ?? throw ToolException.EntityNotFound(h));
                }

                foreach (Entity e in entities)
                {
                    if (p.Activar)
                    {
                        e.Highlight();
                    }
                    else
                    {
                        e.Unhighlight();
                    }
                }

                tr.Commit();
            }

            ed.UpdateScreen();

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = ctx.Pid,
                ["handles"] = p.Handles,
                ["n"] = p.Handles.Count,
                ["activar"] = p.Activar,
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = new List<string>(),
            };
        }
    }
}
