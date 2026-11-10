using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// auditar_dibujo (protocol.md §5C.8). Hilo principal; requiere documento; SOLO LECTURA (una
    /// StartOpenCloseTransaction, sin DocumentLock ni UndoGroup). Ejecuta las reglas <see cref="IAuditCheck"/>
    /// con <see cref="SideAudit.Run"/> (el mismo código que auditar_archivo, §5H.6) y un presupuesto de 8 s
    /// (§3.10): nunca devuelve INTERNAL timeout por diseño; lo que no cabe va a reglas_omitidas o sale parcial.
    /// </summary>
    internal sealed class AuditarDibujoHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "espacio", "reglas", "limit", "cursor" };

        public string Name => "auditar_dibujo";

        public string Description =>
            "Audita el dibujo activo (solo lectura) con las reglas del estándar LPS: capas, entidades en capa 0, bloques, " +
            "cuadro de áreas, duplicados, textos fuera de escala, xrefs no resueltas y unidades. Devuelve un resumen por " +
            "regla y la lista paginada de hallazgos priorizados (alta, media, baja) con handles y caja; 'reglas' limita " +
            "las que se ejecutan. Presupuesto de 8 s: si no alcanza, el resultado es parcial y se indica.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""espacio"": " + ParamReader.SchemaEspacio + @",
    ""reglas"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""maxItems"": 8,
      ""items"": { ""type"": ""string"", ""enum"": [""auditar_capas"", ""entidades_en_capa_0"", ""auditar_bloques"", ""verificar_cuadro_areas"", ""duplicados"", ""textos_fuera_de_escala"", ""xrefs_no_resueltas"", ""unidades""] },
      ""description"": ""Reglas a ejecutar (por defecto todas); siempre se ejecutan en el orden fijo del contrato. 'espacio' aplica a verificar_cuadro_areas, duplicados y textos_fuera_de_escala.""
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
    ""espacio"": { ""type"": ""string"" },
    ""resumen"": { ""type"": ""object"", ""properties"": {
      ""total_hallazgos"": { ""type"": ""integer"" },
      ""por_severidad"": { ""type"": ""object"" },
      ""por_regla"": { ""type"": ""object"" },
      ""n_cuadros_fuera_de_layout"": { ""type"": ""integer"", ""minimum"": 0 } } },
    ""reglas_omitidas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""reglas_con_error"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""regla"": { ""type"": ""string"" }, ""excepcion"": { ""type"": ""string"" } } } },
    ""duracion_ms"": { ""type"": ""integer"" },
    ""hallazgos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""total"": { ""type"": ""integer"" },
    ""cursor_siguiente"": { ""type"": [""string"", ""null""] },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""espacio"", ""resumen"", ""reglas_omitidas"", ""reglas_con_error"", ""duracion_ms"", ""hallazgos"", ""total"", ""cursor_siguiente"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class AuditParams
        {
            public AuditParams(string? space, List<string> rules, int limit, int start)
            {
                Space = space;
                Rules = rules;
                Limit = limit;
                Start = start;
            }

            public string? Space { get; }

            /// <summary>Reglas pedidas, sin duplicados y en el orden del contrato.</summary>
            public List<string> Rules { get; }

            public int Limit { get; }
            public int Start { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static AuditParams Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string? space = ParamReader.OptionalString(p, "espacio", 1, 255);
            List<string>? requested = ParamReader.OptionalStringArray(p, "reglas", 1, 8, 1);
            var rules = new List<string>();
            if (requested == null)
            {
                rules.AddRange(AuditCheckRegistry.ContractRules);
            }
            else
            {
                for (int i = 0; i < requested.Count; i++)
                {
                    if (!AuditCheckRegistry.IsContractRule(requested[i]))
                    {
                        throw ToolException.InvalidParams(
                            "reglas[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe ser uno de "
                            + string.Join(", ", AuditCheckRegistry.ContractRules));
                    }
                }

                foreach (string rule in AuditCheckRegistry.ContractRules)
                {
                    if (requested.Contains(rule))
                    {
                        rules.Add(rule);
                    }
                }
            }

            int limit = ParamReader.Limit(p);
            int start = ParamReader.OptionalCursor(p);
            return new AuditParams(space, rules, limit, start);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Stopwatch watch = Stopwatch.StartNew();
            AuditParams p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            SideAuditResult audit;
            string spaceName;

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, p.Space);
                spaceName = space.Name;
                audit = SideAudit.Run(db, tr, space, p.Rules, DateTime.UtcNow.AddMilliseconds(PluginInfo.AuditBudgetMs));
                tr.Commit();
            }

            (List<Dictionary<string, object?>> page, string? next) = audit.Page(p.Start, p.Limit);

            List<string> warnings = AcadUtil.UnitWarnings(db);
            warnings.AddRange(audit.OwnWarnings);
            int ins = AcadUtil.InsUnits(db);

            return new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["espacio"] = spaceName,
                ["resumen"] = audit.BuildResumen(p.Rules),
                ["reglas_omitidas"] = audit.Omitted,
                ["reglas_con_error"] = audit.Errors,
                ["duracion_ms"] = watch.ElapsedMilliseconds,
                ["hallazgos"] = page,
                ["total"] = audit.All.Count,
                ["cursor_siguiente"] = next,
                ["origen_texto"] = "contenido_del_plano",
                ["insunits"] = ins,
                ["unidades"] = MCPAutoCAD.GeometryCore.UnitsTable.Name(ins),
                ["advertencias"] = warnings,
            };
        }
    }
}
