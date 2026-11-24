using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// <c>activar_layout</c> (fallo 50, plugin 0.10.5). Hilo principal; requiere documento; <c>IsWrite = false</c>
    /// (cambia la pestaña activa del usuario —CTAB/TILEMODE—, sin modificar entidades ni abrir <see cref="UndoGroup"/>).
    /// Útil para inspeccionar otro layout con <c>vista_png</c> o para inicializar los viewports de un layout copiado.
    /// </summary>
    internal sealed class ActivarLayoutHandler : IToolHandler
    {
        private const string ModelTabName = "Model";
        private static readonly string[] Allowed = { "nombre" };

        public string Name => "activar_layout";

        public string Description =>
            "Cambia la pestaña activa en pantalla de AutoCAD (CTAB) al layout de papel indicado o a 'modelo' ('Model'). " +
            "No modifica entidades del dibujo, pero SÍ cambia lo que ve el usuario en AutoCAD (se avisa en la respuesta).";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre del layout de papel o 'modelo' ('Model'), sin distinguir mayúsculas."" }
  },
  ""required"": [""nombre""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""cambio"": { ""type"": ""boolean"" },
    ""activo"": { ""type"": ""string"" },
    ""anterior"": { ""type"": [""string"", ""null""] },
    ""espacio"": { ""type"": ""string"" },
    ""disponibles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""cambio"", ""activo"", ""anterior"", ""espacio"", ""disponibles"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static string Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            return ParamReader.OptionalString(p, "nombre", 1, 255)
                ?? throw ToolException.InvalidParams("nombre: obligatorio");
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            string nombre = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = AcadUtil.InsUnits(db);

            string? anterior = LayoutWrite.CurrentTab();
            string resolvedName;
            string targetTab;
            List<string> disponibles;

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                string lookup = string.Equals(nombre, ModelTabName, StringComparison.OrdinalIgnoreCase)
                    ? SpaceResolver.ModelName
                    : nombre;
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, lookup);
                resolvedName = resolved.Name;
                targetTab = string.Equals(resolvedName, SpaceResolver.ModelName, StringComparison.Ordinal)
                    ? ModelTabName
                    : resolvedName;
                disponibles = SpaceResolver.AvailableSpaces(db, tr, includeModel: true);
                tr.Commit();
            }

            bool alreadyCurrent = LayoutWrite.IsCurrent(targetTab, anterior);
            if (alreadyCurrent)
            {
                warnings.Add("La pestaña '" + targetTab + "' ya era la activa; no cambió nada.");
            }
            else
            {
                using (doc.LockDocument())
                {
                    LayoutManager.Current.CurrentLayout = targetTab;
                }

                warnings.Add("Se cambió la pestaña visible en AutoCAD de '" + (anterior ?? "?") + "' a '" + targetTab + "'.");
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = ctx.Pid,
                ["cambio"] = !alreadyCurrent,
                ["activo"] = targetTab,
                ["anterior"] = anterior,
                ["espacio"] = resolvedName,
                ["disponibles"] = disponibles,
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = warnings,
            };
        }
    }
}
