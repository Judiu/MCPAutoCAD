using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// descartar_preview (protocol.md §5E.5). Hilo principal; requiere documento; ESCRITURA. Borra, en UN paso de
    /// deshacer (<see cref="UndoGroup.Run"/>), las entidades del registro que sigan existiendo y sigan en MCP-PREVIEW, y
    /// olvida el registro. Si no queda ninguna que borrar NO abre paso de deshacer (no escribe nada).
    /// </summary>
    internal sealed class DescartarPreviewHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "preview_id" };

        public string Name => "descartar_preview";

        public string Description =>
            "BORRA las entidades de MCP-PREVIEW de una previsualización (preview_id) y la olvida, en un solo paso de deshacer (nada si ya no queda ninguna). Las previsualizaciones caducadas o de sesiones anteriores se limpian con limpiar_observaciones y capa MCP-PREVIEW.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""preview_id"": " + ConfirmarHandler.SchemaPreviewId + @"
  },
  ""required"": [""preview_id""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""preview_id"": { ""type"": ""string"" },
    ""descartada"": { ""type"": ""boolean"" },
    ""n_borradas"": { ""type"": ""integer"" },
    ""n_no_encontradas"": { ""type"": ""integer"", ""description"": ""Handles de la previsualización que ya no existen o ya no están en MCP-PREVIEW (no se tocan)."" },
    ""herramienta"": { ""type"": ""string"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview_id"", ""descartada"", ""n_borradas"", ""n_no_encontradas"", ""herramienta"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => ConfirmarHandler.ParsePreviewId(parameters, Allowed);

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            string id = ConfirmarHandler.ParsePreviewId(parameters, Allowed);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            PreviewRecord rec = ConfirmarHandler.RequireRecord(id, doc);
            int ins = AcadUtil.InsUnits(db);
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int pid = ctx.Pid;

            // Lectura previa (sin efectos): ¿queda algo que borrar?
            int vivas;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                vivas = LivePreviewHandles(db, tr, rec).Count;
                tr.Commit();
            }

            int total = rec.HandlesPreview.Count;
            if (vivas == 0)
            {
                // Nada que borrar: se olvida el registro sin abrir paso de deshacer.
                PreviewStore.Registry.Remove(id);
                return Response(pid, rec, 0, total, ins, warnings);
            }

            return UndoGroup.Run(doc, d =>
            {
                Database ddb = d.Database;
                int borradas = 0;
                using (Transaction tr = ddb.TransactionManager.StartTransaction())
                {
                    foreach (Entity e in LivePreviewHandles(ddb, tr, rec))
                    {
                        e.UpgradeOpen();
                        e.Erase();
                        borradas++;
                    }

                    tr.Commit();
                }

                PreviewStore.Registry.Remove(id);
                return Response(pid, rec, borradas, total - borradas, ins, warnings);
            });
        }

        /// <summary>Entidades del registro que siguen existiendo y siguen en MCP-PREVIEW (abiertas para lectura en <paramref name="tr"/>).</summary>
        private static List<Entity> LivePreviewHandles(Database db, Transaction tr, PreviewRecord rec)
        {
            var list = new List<Entity>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string h in rec.HandlesPreview)
            {
                if (!seen.Add(h))
                {
                    continue;
                }

                Entity? e = AcadUtil.TryGetEntityByHandle(db, h, tr);
                if (e != null && string.Equals(e.Layer, PreviewStore.PreviewLayer, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(e);
                }
            }

            return list;
        }

        private static Dictionary<string, object?> Response(int pid, PreviewRecord rec, int borradas, int noEncontradas, int ins, List<string> warnings) =>
            new Dictionary<string, object?>
            {
                ["pid"] = pid,
                ["preview_id"] = rec.Id,
                ["descartada"] = true,
                ["n_borradas"] = borradas,
                ["n_no_encontradas"] = noEncontradas,
                ["herramienta"] = rec.Herramienta,
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = warnings,
            };
    }
}
