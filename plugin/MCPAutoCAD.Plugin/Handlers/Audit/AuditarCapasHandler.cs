using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// auditar_capas (protocol.md §5C.1). Hilo principal; requiere documento; solo lectura (transacción open/close).
    /// La lógica vive en <see cref="CapasCheck.Analyze"/>; aquí solo se filtra, pagina y arma el JSON.
    /// </summary>
    internal sealed class AuditarCapasHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "clasificacion", "incluir_vacias", "limit", "cursor" };

        public string Name => "auditar_capas";

        public string Description =>
            "Compara las capas del dibujo activo con el estándar LPS (lps.json) y clasifica cada una (estandar, " +
            "estandar_con_diferencias, cype, mcp, ajena_revit_aia, ajena_exportacion, generica, fuera_de_estandar) con " +
            "sus diferencias de color/tipo de línea/grosor y una capa sugerida. Solo propone: no renombra ni modifica nada. Paginada. " +
            "Desde 0.6 cada capa indica además patron_ajeno (revit_aia|exportacion|generica|null) y sugerencia_patron aunque la capa esté en el estándar.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""clasificacion"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 9,
      ""items"": { ""type"": ""string"", ""enum"": [""mcp"", ""estandar"", ""estandar_con_diferencias"", ""cype"", ""ajena_revit_aia"", ""ajena_exportacion"", ""generica"", ""fuera_de_estandar""] },
      ""description"": ""Devolver solo las capas con estas clasificaciones.""
    },
    ""incluir_vacias"": { ""type"": ""boolean"", ""default"": true, ""description"": ""false omite las capas sin entidades."" },
    ""limit"": " + ParamReader.SchemaLimit + @",
    ""cursor"": " + ParamReader.SchemaCursor + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""nombre"": { ""type"": ""string"" }, ""color"": { ""type"": ""integer"" }, ""tipo_linea"": { ""type"": ""string"" },
      ""grosor"": { ""type"": ""integer"" }, ""n_entidades"": { ""type"": ""integer"" }, ""vacia"": { ""type"": ""boolean"" },
      ""clasificacion"": { ""type"": ""string"" }, ""diferencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
      ""sugerencia"": { ""type"": [""string"", ""null""] },
      ""patron_ajeno"": { ""type"": [""string"", ""null""] }, ""sugerencia_patron"": { ""type"": [""string"", ""null""] } } } },
    ""total"": { ""type"": ""integer"" },
    ""cursor_siguiente"": { ""type"": [""string"", ""null""] },
    ""resumen"": { ""type"": ""object"" },
    ""estandar_disponible"": { ""type"": ""boolean"" },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""capas"", ""total"", ""cursor_siguiente"", ""resumen"", ""estandar_disponible"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Query
        {
            public HashSet<string>? Clasificacion;
            public bool IncluirVacias = true;
            public int Limit;
            public int Start;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Query Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var q = new Query
            {
                IncluirVacias = ParamReader.OptionalBool(p, "incluir_vacias") ?? true,
                Limit = ParamReader.Limit(p),
                Start = ParamReader.OptionalCursor(p),
            };

            List<string>? clasif = ParamReader.OptionalStringArray(p, "clasificacion", 1, 9, 1);
            if (clasif != null)
            {
                q.Clasificacion = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < clasif.Count; i++)
                {
                    if (LayerClassifier.Rank(clasif[i]) == 0)
                    {
                        throw ToolException.InvalidParams(
                            "clasificacion[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe ser uno de "
                            + string.Join(", ", LayerClassifier.All));
                    }

                    q.Clasificacion.Add(clasif[i]);
                }
            }

            return q;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Query q = Parse(parameters);
            Database db = ctx.RequireDocument().Database;
            Dictionary<string, object?> result = AuditSupport.Begin(ctx.Pid);
            var warnings = new List<string>();

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, null);
                LayerAuditResult analysis = new CapasCheck().Analyze(AuditSupport.CreateContext(db, tr, space));

                // Corrección F8 del gate de Fase 2 (§5C.1): el resumen (n_capas, n_vacias, n_capas_xref y
                // por_clasificacion) se calcula SIEMPRE sobre TODAS las capas (analysis.Capas), antes y con
                // independencia de los filtros 'clasificacion'/'incluir_vacias'. Antes se acumulaba dentro del bucle
                // de filtrado y, con un filtro, por_clasificacion sumaba solo las filas filtradas mientras n_capas
                // contaba todas.
                var porClasificacion = new Dictionary<string, object?>(StringComparer.Ordinal);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                int nVacias = 0;
                foreach (LayerAuditRow row in analysis.Capas)
                {
                    AuditSupport.Increment(counts, row.Clasificacion.Clasificacion);
                    if (row.NEntidades == 0)
                    {
                        nVacias++;
                    }
                }

                // Los filtros solo afectan a la lista 'capas' y a 'total'/'cursor_siguiente'.
                var filtered = new List<LayerAuditRow>();
                foreach (LayerAuditRow row in analysis.Capas)
                {
                    if (q.Clasificacion != null && !q.Clasificacion.Contains(row.Clasificacion.Clasificacion))
                    {
                        continue;
                    }

                    if (!q.IncluirVacias && row.NEntidades == 0)
                    {
                        continue;
                    }

                    filtered.Add(row);
                }

                foreach (string c in LayerClassifier.All)
                {
                    porClasificacion[c] = counts.TryGetValue(c, out int n) ? n : 0;
                }

                int total = filtered.Count;
                (int count, string? next) = ParamReader.Slice(total, q.Start, q.Limit);
                var page = new List<Dictionary<string, object?>>(count);
                for (int i = q.Start; i < q.Start + count; i++)
                {
                    page.Add(filtered[i].ToDictionary());
                }

                result["capas"] = page;
                result["total"] = total;
                result["cursor_siguiente"] = next;
                result["resumen"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["n_capas"] = analysis.Capas.Count,
                    ["n_vacias"] = nVacias,
                    ["n_capas_xref"] = analysis.NCapasXref,
                    ["por_clasificacion"] = porClasificacion,
                };
                result["estandar_disponible"] = analysis.EstandarDisponible;

                if (!analysis.EstandarDisponible)
                {
                    warnings.Add(AuditSupport.WarningNoStandard);
                }

                if (!analysis.Completo)
                {
                    warnings.Add(AuditSupport.WarningPartial);
                }

                tr.Commit();
            }

            AuditSupport.Finish(result, db, warnings);
            return result;
        }
    }
}
