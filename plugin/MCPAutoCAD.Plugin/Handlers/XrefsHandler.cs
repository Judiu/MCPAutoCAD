using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>xrefs (protocol.md §5B.6). Hilo principal; requiere documento; solo lectura; sin paginación.</summary>
    internal sealed class XrefsHandler : IToolHandler
    {
        private static readonly string[] Allowed = Array.Empty<string>();

        public string Name => "xrefs";

        public string Description =>
            "Lista las referencias externas (xrefs) del dibujo activo con la ruta guardada, la ruta resuelta, el estado " +
            "(resuelta, no_encontrada, descargada...), si es attach u overlay, si está anidada y cuántas inserciones tiene.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(
            @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""xrefs"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""nombre"": { ""type"": ""string"" },
      ""ruta_guardada"": { ""type"": [""string"", ""null""] },
      ""ruta_resuelta"": { ""type"": [""string"", ""null""] },
      ""estado"": { ""type"": ""string"", ""enum"": [""resuelta"", ""no_encontrada"", ""descargada"", ""sin_cargar"", ""sin_referencias"", ""desconocido""] },
      ""tipo"": { ""type"": ""string"", ""enum"": [""attach"", ""overlay""] },
      ""n_inserciones"": { ""type"": ""integer"" },
      ""anidado"": { ""type"": ""boolean"" } } } },
    ""total"": { ""type"": ""integer"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""xrefs"", ""total"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => ParamReader.RequireObject(parameters, Allowed);

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            ParamReader.RequireObject(parameters, Allowed);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            var rows = new List<Dictionary<string, object?>>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    try
                    {
                        if (id.IsNull || id.IsErased)
                        {
                            continue;
                        }

                        var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead, false);
                        if (!btr.IsFromExternalReference)
                        {
                            continue;
                        }

                        string? saved = Safe(() => btr.PathName);
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["nombre"] = btr.Name,
                            ["ruta_guardada"] = saved,
                            ["ruta_resuelta"] = Resolve(saved, db),
                            ["estado"] = MapStatus(btr),
                            ["tipo"] = SafeBool(() => btr.IsFromOverlayReference) ? "overlay" : "attach",
                            ["n_inserciones"] = AcadUtil.CountInsertions(btr, tr),
                            ["anidado"] = SafeBool(() => btr.IsDependent),
                        });
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Definición ilegible: se omite.
                    }
                }

                tr.Commit();
            }

            rows.Sort((a, b) =>
            {
                string na = (string?)a["nombre"] ?? "";
                string nb = (string?)b["nombre"] ?? "";
                int c = StringComparer.OrdinalIgnoreCase.Compare(na, nb);
                return c != 0 ? c : StringComparer.Ordinal.Compare(na, nb);
            });

            var result = new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["xrefs"] = rows,
                ["total"] = rows.Count,
            };
            AcadUtil.AddUnitFields(result, db);
            return result;
        }

        internal static string MapStatus(BlockTableRecord btr)
        {
            XrefStatus status;
            try
            {
                status = btr.XrefStatus;
            }
            catch (Exception)
            {
                return "desconocido";
            }

            return status switch
            {
                XrefStatus.Resolved => "resuelta",
                XrefStatus.FileNotFound => "no_encontrada",
                XrefStatus.Unloaded => "descargada",
                XrefStatus.Unresolved => "sin_cargar",
                XrefStatus.Unreferenced => "sin_referencias",
                _ => "desconocido",
            };
        }

        /// <summary>FindFile(PathName, db, XRefDrawing); null si lanza o devuelve "".</summary>
        private static string? Resolve(string? saved, Database db)
        {
            if (string.IsNullOrEmpty(saved))
            {
                return null;
            }

            try
            {
                string found = HostApplicationServices.Current.FindFile(saved, db, FindFileHint.XRefDrawing);
                return string.IsNullOrEmpty(found) ? null : found;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? Safe(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
