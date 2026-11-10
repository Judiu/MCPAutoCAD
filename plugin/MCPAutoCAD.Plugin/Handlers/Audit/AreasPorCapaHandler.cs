using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// areas_por_capa (protocol.md §5C.4). Hilo principal; requiere documento; solo lectura. Suma por capa el área de
    /// las entidades cerradas del espacio (LWPOLYLINE/POLYLINE 2D cerradas, CIRCLE, ELLIPSE cerrada, HATCH, REGION). No es
    /// regla de auditar_dibujo.
    /// </summary>
    internal sealed class AreasPorCapaHandler : IToolHandler
    {
        public const int MaxLayerRows = 500;

        private static readonly string[] Allowed = { "capas", "espacio", "detalle", "limit", "cursor" };

        public string Name => "areas_por_capa";

        public string Description =>
            "Suma por capa el área de las entidades cerradas de un espacio del dibujo activo (polilíneas cerradas, círculos, " +
            "elipses cerradas, hatches y regiones), en unidades de dibujo al cuadrado. Con detalle: true devuelve además la " +
            "lista paginada de entidades con su área.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""capas"": " + ParamReader.SchemaCapas + @",
    ""espacio"": " + ParamReader.SchemaEspacio + @",
    ""detalle"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true añade la lista paginada de entidades con su área (limit/cursor solo aplican entonces)."" },
    ""limit"": " + ParamReader.SchemaLimit + @",
    ""cursor"": " + ParamReader.SchemaCursor + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""espacio"": { ""type"": ""string"" },
    ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""capa"": { ""type"": ""string"" }, ""n"": { ""type"": ""integer"" }, ""area_total"": { ""type"": ""number"" },
      ""n_sin_area"": { ""type"": ""integer"" }, ""por_tipo"": { ""type"": ""object"" } } } },
    ""capas_truncadas"": { ""type"": ""boolean"" },
    ""unidad_area"": { ""type"": ""string"" },
    ""entidades"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""total"": { ""type"": ""integer"" },
    ""cursor_siguiente"": { ""type"": [""string"", ""null""] },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""espacio"", ""capas"", ""capas_truncadas"", ""unidad_area"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Query
        {
            public List<string>? Capas;
            public string? Espacio;
            public bool Detalle;
            public int Limit;
            public int Start;
        }

        private sealed class LayerArea
        {
            public LayerArea(string capa)
            {
                Capa = capa;
            }

            public string Capa { get; }
            public int N;
            public double AreaTotal;
            public int NSinArea;
            public Dictionary<string, int> PorTipo { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Query Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var q = new Query
            {
                Capas = ParamReader.OptionalStringArray(p, "capas", 1, 100, 1),
                Espacio = ParamReader.OptionalString(p, "espacio", 1, 255),
                Detalle = ParamReader.OptionalBool(p, "detalle") ?? false,
                Limit = ParamReader.Limit(p),
            };

            if (!q.Detalle && ParamReader.Has(p, "cursor"))
            {
                throw ToolException.InvalidParams("cursor: solo aplica con detalle");
            }

            q.Start = ParamReader.OptionalCursor(p);
            return q;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Query q = Parse(parameters);
            Database db = ctx.RequireDocument().Database;
            Dictionary<string, object?> result = AuditSupport.Begin(ctx.Pid);

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, q.Espacio);
                result["espacio"] = space.Name;

                var byLayer = new Dictionary<string, LayerArea>(StringComparer.OrdinalIgnoreCase);
                var layerOrder = new List<LayerArea>();
                var entities = new List<(ObjectId id, string tipo, string capa, double? area)>();
                foreach (ObjectId id in space.Btr)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    string? quick = AcadUtil.DxfNameFromId(id);
                    if (quick != null && !AuditSupport.AreaTypes.Contains(quick))
                    {
                        continue;
                    }

                    Entity? e = AuditSupport.Open(tr, id);
                    if (e == null || !AuditSupport.TryArea(e, out double? area))
                    {
                        continue;
                    }

                    string capa = AuditSupport.Layer(e);
                    if (!EntityFilter.MatchesAny(capa, q.Capas))
                    {
                        continue;
                    }

                    string tipo = quick ?? AcadUtil.DxfName(e);
                    if (!byLayer.TryGetValue(capa, out LayerArea? la))
                    {
                        la = new LayerArea(capa);
                        byLayer.Add(capa, la);
                        layerOrder.Add(la);
                    }

                    la.N++;
                    AuditSupport.Increment(la.PorTipo, tipo);
                    if (area == null)
                    {
                        la.NSinArea++;
                    }
                    else
                    {
                        la.AreaTotal += area.Value;
                    }

                    if (q.Detalle)
                    {
                        entities.Add((id, tipo, capa, area));
                    }
                }

                layerOrder.Sort((a, b) =>
                {
                    int c = b.AreaTotal.CompareTo(a.AreaTotal);
                    return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.Capa, b.Capa);
                });

                bool truncated = layerOrder.Count > MaxLayerRows;
                var rows = new List<Dictionary<string, object?>>();
                for (int i = 0; i < layerOrder.Count && i < MaxLayerRows; i++)
                {
                    LayerArea la = layerOrder[i];
                    rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["capa"] = la.Capa,
                        ["n"] = la.N,
                        ["area_total"] = AcadUtil.NumberOrNull(la.AreaTotal),
                        ["n_sin_area"] = la.NSinArea,
                        ["por_tipo"] = AuditSupport.SortedCounts(la.PorTipo),
                    });
                }

                int ins = AcadUtil.InsUnits(db);
                result["capas"] = rows;
                result["capas_truncadas"] = truncated;
                result["unidad_area"] = ins == 0 || !UnitsTable.IsInRange(ins) ? UnitsTable.Unknown : UnitsTable.Name(ins) + "²";

                if (q.Detalle)
                {
                    int total = entities.Count;
                    (int count, string? next) = ParamReader.Slice(total, q.Start, q.Limit);
                    var page = new List<Dictionary<string, object?>>(count);
                    for (int i = q.Start; i < q.Start + count; i++)
                    {
                        (ObjectId id, string tipo, string capa, double? area) = entities[i];
                        Entity? e = AuditSupport.Open(tr, id);
                        page.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["handle"] = AuditSupport.Handle(id),
                            ["tipo"] = tipo,
                            ["capa"] = capa,
                            ["area"] = area,
                            ["bbox"] = e == null ? null : AcadUtil.ExtentsOrNull(e),
                        });
                    }

                    result["entidades"] = page;
                    result["total"] = total;
                    result["cursor_siguiente"] = next;
                }

                tr.Commit();
            }

            AuditSupport.Finish(result, db, null);
            return result;
        }
    }
}
