using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// limpiar_observaciones (protocol.md §5C.10). Hilo principal; requiere documento; ESCRITURA.
    /// Borra todas las entidades de una capa MCP-* en *Model_Space y en todos los layouts (nunca dentro de
    /// definiciones de bloque), en una Transaction dentro de <see cref="UndoGroup.Run"/> (un CTRL+Z).
    /// La capa no se borra.
    /// </summary>
    internal sealed class LimpiarObservacionesHandler : IToolHandler
    {
        public const string ConfirmRequired = "confirmar: debe ser true";
        private static readonly string[] Allowed = { "confirmar", "capa" };

        public string Name => "limpiar_observaciones";

        public string Description =>
            "BORRA del dibujo todas las entidades de una capa MCP-* (por defecto MCP-OBSERVACIONES) en el modelo y en " +
            "todos los layouts, en un solo paso de deshacer (un CTRL+Z las recupera); la capa se conserva. " +
            "Exige confirmar: true; solo admite capas que empiecen por MCP-.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""confirmar"": { ""type"": ""boolean"", ""const"": true, ""description"": ""Obligatorio y debe ser true: confirma el borrado."" },
    ""capa"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""pattern"": ""^[Mm][Cc][Pp]-"", ""default"": ""MCP-OBSERVACIONES"", ""description"": ""Capa MCP-* cuyas entidades se borran (debe existir)."" }
  },
  ""required"": [""confirmar""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""capa"": { ""type"": ""string"" },
    ""n_borradas"": { ""type"": ""integer"" },
    ""por_espacio"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""capa"", ""n_borradas"", ""por_espacio"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        /// <summary>Valida params y devuelve la capa pedida. Sin AutoCAD.</summary>
        private static string Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            if (!p.TryGetProperty("confirmar", out JsonElement confirm) || confirm.ValueKind != JsonValueKind.True)
            {
                throw ToolException.InvalidParams(ConfirmRequired);
            }

            string layer = ParamReader.OptionalString(p, "capa", 1, 255) ?? PluginInfo.ObservationsLayer;
            McpLayerFactory.RequireMcpLayerName(layer);
            return layer;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            string layer = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = AcadUtil.InsUnits(db);
            List<string> warnings = AcadUtil.UnitWarnings(db);
            return UndoGroup.Run(doc, d => Write(d, layer, ctx.Pid, ins, warnings));
        }

        /// <summary>
        /// Borrado dentro de UNA transacción: si algo falla se descarta (Dispose sin Commit) y no se borra nada.
        /// </summary>
        private static object Write(Document doc, string requestedLayer, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId layerId = McpLayerFactory.FindLayer(table, requestedLayer);
                if (layerId.IsNull)
                {
                    throw ToolException.LayerNotFound(requestedLayer);
                }

                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                if (layer.IsLocked)
                {
                    throw ToolException.InvalidParams(McpLayerFactory.LockedLayer, new Dictionary<string, object?> { ["capa"] = layer.Name });
                }

                var perSpace = new Dictionary<string, object?>(StringComparer.Ordinal);
                int total = 0;
                int keptViewports = 0;
                foreach ((ObjectId spaceId, string spaceName) in Spaces(db, tr))
                {
                    var btr = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    int erased = 0;
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsNull || id.IsErased)
                        {
                            continue;
                        }

                        if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity e) || e.LayerId != layerId)
                        {
                            continue;
                        }

                        if (e is Viewport)
                        {
                            // Los viewports de un layout no son observaciones: borrarlos rompería la presentación.
                            keptViewports++;
                            continue;
                        }

                        e.UpgradeOpen();
                        e.Erase();
                        erased++;
                    }

                    if (erased > 0)
                    {
                        perSpace.TryGetValue(spaceName, out object? prev);
                        perSpace[spaceName] = (prev is int p ? p : 0) + erased;
                        total += erased;
                    }
                }

                if (keptViewports > 0)
                {
                    warnings.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "Se conservaron {0} viewport(s) de la capa {1}: limpiar_observaciones no borra viewports.",
                        keptViewports,
                        layer.Name));
                }

                tr.Commit();

                return new Dictionary<string, object?>
                {
                    ["pid"] = pid,
                    ["capa"] = layer.Name,
                    ["n_borradas"] = total,
                    ["por_espacio"] = perSpace,
                    ["insunits"] = ins,
                    ["unidades"] = UnitsTable.Name(ins),
                    ["advertencias"] = warnings,
                };
            }
        }

        /// <summary>
        /// "Todos los espacios" de protocol.md §5C: *Model_Space y después los layouts del LayoutDictionary (sin
        /// Model) por TabOrder. Nunca otras definiciones de bloque.
        /// </summary>
        private static List<(ObjectId id, string name)> Spaces(Database db, Transaction tr)
        {
            var result = new List<(ObjectId id, string name)>
            {
                (SymbolUtilityServices.GetBlockModelSpaceId(db), SpaceResolver.ModelName),
            };

            var layouts = new List<(int order, string name, ObjectId btr)>();
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dict)
            {
                if (entry.Value.IsNull || entry.Value.IsErased)
                {
                    continue;
                }

                if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout) || layout.ModelType || layout.BlockTableRecordId.IsNull)
                {
                    continue;
                }

                layouts.Add((layout.TabOrder, layout.LayoutName, layout.BlockTableRecordId));
            }

            layouts.Sort((a, b) =>
            {
                int c = a.order.CompareTo(b.order);
                return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.name, b.name);
            });

            foreach ((_, string name, ObjectId btr) in layouts)
            {
                result.Add((btr, name));
            }

            return result;
        }
    }
}
