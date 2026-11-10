using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>listar_capas (protocol.md §5.3). Hilo principal; requiere documento; solo lectura.</summary>
    internal sealed class ListarCapasHandler : IToolHandler
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;
        private static readonly string[] Allowed = { "limit", "cursor", "incluir_xref" };

        public string Name => "listar_capas";

        public string Description =>
            "Lista las capas del dibujo activo ordenadas por nombre, con color, tipo de línea, grosor y estado " +
            "(apagada, congelada, bloqueada, imprimible). Paginada: si cursor_siguiente no es null, vuelva a llamar " +
            "pasando ese valor en cursor. Por defecto excluye las capas de xrefs (nombre con '|').";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""default"": 200, ""description"": ""Máximo de capas en la página."" },
    ""cursor"": { ""type"": ""string"", ""pattern"": ""^[0-9]+$"", ""description"": ""Valor de cursor_siguiente de la página anterior."" },
    ""incluir_xref"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Incluir capas dependientes de xref (nombre con '|')."" }
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""capas"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""nombre"": { ""type"": ""string"" },
          ""color"": { ""type"": ""integer"" },
          ""color_true"": { ""type"": [""string"", ""null""] },
          ""tipo_linea"": { ""type"": ""string"" },
          ""grosor"": { ""type"": ""integer"" },
          ""plot"": { ""type"": ""boolean"" },
          ""apagada"": { ""type"": ""boolean"" },
          ""congelada"": { ""type"": ""boolean"" },
          ""bloqueada"": { ""type"": ""boolean"" },
          ""es_xref"": { ""type"": ""boolean"" },
          ""actual"": { ""type"": ""boolean"" }
        }
      }
    },
    ""total"": { ""type"": ""integer"" },
    ""cursor_siguiente"": { ""type"": [""string"", ""null""] },
    ""capa_actual"": { ""type"": ""string"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""capas"", ""total"", ""cursor_siguiente"", ""capa_actual"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters)
        {
            ParamReader.RequireObject(parameters, Allowed);
            ParamReader.OptionalInt(parameters, "limit", 1, MaxLimit);
            ParamReader.OptionalCursor(parameters);
            ParamReader.OptionalBool(parameters, "incluir_xref");
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            int limit = ParamReader.OptionalInt(parameters, "limit", 1, MaxLimit) ?? DefaultLimit;
            bool includeXref = ParamReader.OptionalBool(parameters, "incluir_xref") ?? false;
            int start = ParamReader.OptionalCursor(parameters);

            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            var layers = new List<LayerInfo>();
            string currentLayer = "";

            // Solo lectura: transacción open/close, sin DocumentLock.
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId clayer = db.Clayer;
                foreach (ObjectId id in table)
                {
                    if (id.IsErased)
                    {
                        continue;
                    }

                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    bool isCurrent = id == clayer;
                    if (isCurrent)
                    {
                        currentLayer = ltr.Name;
                    }

                    bool isXref = ltr.Name.IndexOf('|') >= 0;
                    if (isXref && !includeXref)
                    {
                        continue;
                    }

                    layers.Add(new LayerInfo(ltr, tr, isXref, isCurrent));
                }

                tr.Commit();
            }

            layers.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.nombre, b.nombre));
            int total = layers.Count;
            // Paginación común (GeometryCore.Paging): mismos errores "cursor: valor inválido" / "cursor: fuera de rango".
            (int count, string? next) = ParamReader.Slice(total, start, limit);
            List<LayerInfo> page = layers.GetRange(start, count);
            int ins = AcadUtil.InsUnits(db);

            return new
            {
                pid = ctx.Pid,
                capas = page,
                total,
                cursor_siguiente = next,
                capa_actual = currentLayer,
                insunits = ins,
                unidades = UnitsTable.Name(ins),
                advertencias = AcadUtil.UnitWarnings(db),
            };
        }

        /// <summary>Fila de capa con los nombres de campo exactos del contrato.</summary>
        private sealed class LayerInfo
        {
            public LayerInfo(LayerTableRecord ltr, Transaction tr, bool isXref, bool isCurrent)
            {
                nombre = ltr.Name;
                color = ltr.Color.ColorIndex;
                color_true = ltr.Color.IsByColor
                    ? string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", ltr.Color.Red, ltr.Color.Green, ltr.Color.Blue)
                    : null;
                tipo_linea = ResolveLinetype(ltr, tr);
                grosor = (int)ltr.LineWeight;
                plot = ltr.IsPlottable;
                apagada = ltr.IsOff;
                congelada = ltr.IsFrozen;
                bloqueada = ltr.IsLocked;
                es_xref = isXref;
                actual = isCurrent;
            }

#pragma warning disable IDE1006 // Nombres en minúsculas: son los campos JSON del contrato.
            public string nombre { get; }
            public int color { get; }
            public string? color_true { get; }
            public string tipo_linea { get; }
            public int grosor { get; }
            public bool plot { get; }
            public bool apagada { get; }
            public bool congelada { get; }
            public bool bloqueada { get; }
            public bool es_xref { get; }
            public bool actual { get; }
#pragma warning restore IDE1006

            private static string ResolveLinetype(LayerTableRecord ltr, Transaction tr)
            {
                ObjectId ltId = ltr.LinetypeObjectId;
                if (ltId.IsNull || ltId.IsErased)
                {
                    return "";
                }

                var lt = (LinetypeTableRecord)tr.GetObject(ltId, OpenMode.ForRead);
                return lt.Name;
            }
        }
    }
}
