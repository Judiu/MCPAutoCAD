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
    /// auditar_bloques (protocol.md §5C.3). Hilo principal; requiere documento; solo lectura. La lógica vive en
    /// <see cref="BloquesCheck.Analyze"/>; aquí se filtra por sección, se ordena, se asignan ids y se pagina.
    /// </summary>
    internal sealed class AuditarBloquesHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "seccion", "limit", "cursor" };

        public string Name => "auditar_bloques";

        public string Description =>
            "Audita las definiciones de bloque del dibujo activo: bloques definidos en unidades distintas a las del dibujo " +
            "(con el factor de escala implícito; indica si cada inserción ya lleva aplicado el factor de escala), nombres que " +
            "parecen arbitrarios y bloques sin inserciones. Devuelve un " +
            "resumen y hallazgos priorizados (paginados); no modifica nada.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""seccion"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 3,
      ""items"": { ""type"": ""string"", ""enum"": [""unidades_distintas"", ""nombres_sospechosos"", ""sin_uso""] },
      ""description"": ""Secciones cuyos hallazgos se devuelven (por defecto las tres).""
    },
    ""limit"": " + ParamReader.SchemaLimit + @",
    ""cursor"": " + ParamReader.SchemaCursor + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""resumen"": { ""type"": ""object"", ""properties"": {
      ""n_definiciones"": { ""type"": ""integer"" }, ""n_anonimos"": { ""type"": ""integer"" }, ""n_xrefs"": { ""type"": ""integer"" },
      ""unidades_distintas"": { ""type"": ""integer"" }, ""nombres_sospechosos"": { ""type"": ""integer"" }, ""sin_uso"": { ""type"": ""integer"" } } },
    ""hallazgos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""total"": { ""type"": ""integer"" },
    ""cursor_siguiente"": { ""type"": [""string"", ""null""] },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""resumen"", ""hallazgos"", ""total"", ""cursor_siguiente"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (HashSet<string> secciones, int limit, int start) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var secciones = new HashSet<string>(BloquesCheck.Secciones, StringComparer.Ordinal);
            List<string>? requested = ParamReader.OptionalStringArray(p, "seccion", 1, 3, 1);
            if (requested != null)
            {
                secciones.Clear();
                for (int i = 0; i < requested.Count; i++)
                {
                    if (Array.IndexOf(BloquesCheck.Secciones, requested[i]) < 0)
                    {
                        throw ToolException.InvalidParams(
                            "seccion[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe ser uno de "
                            + string.Join(", ", BloquesCheck.Secciones));
                    }

                    secciones.Add(requested[i]);
                }
            }

            return (secciones, ParamReader.Limit(p), ParamReader.OptionalCursor(p));
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (HashSet<string> secciones, int limit, int start) = Parse(parameters);
            Database db = ctx.RequireDocument().Database;
            Dictionary<string, object?> result = AuditSupport.Begin(ctx.Pid);
            var warnings = new List<string>();

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, null);
                BlockAuditResult analysis = new BloquesCheck().Analyze(AuditSupport.CreateContext(db, tr, space));

                var findings = new List<Finding>();
                foreach (BlockFinding bf in analysis.Hallazgos)
                {
                    if (secciones.Contains(bf.Seccion))
                    {
                        findings.Add(bf.Finding);
                    }
                }

                Finding.Sort(findings, AuditSupport.RuleOrder);
                Finding.AssignIds(findings);
                int total = findings.Count;
                (int count, string? next) = ParamReader.Slice(total, start, limit);

                result["resumen"] = analysis.Resumen();
                result["hallazgos"] = AuditSupport.ToDictionaries(findings.GetRange(start, count));
                result["total"] = total;
                result["cursor_siguiente"] = next;
                warnings.AddRange(analysis.Advertencias);
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
