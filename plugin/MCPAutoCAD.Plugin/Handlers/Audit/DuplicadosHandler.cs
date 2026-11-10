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
    /// duplicados (protocol.md §5C.6). Hilo principal; requiere documento; solo lectura. La agrupación vive en
    /// <see cref="DuplicadosCheck.Analyze"/> (hash de geometría redondeada de GeometryCore, O(n)).
    /// </summary>
    internal sealed class DuplicadosHandler : IToolHandler
    {
        public const string ToleranceError = "tolerancia: debe ser un número entre 1e-12 y 1";

        private static readonly string[] Allowed = { "espacio", "capas", "tipos", "tolerancia", "limit", "cursor" };

        public string Name => "duplicados";

        public string Description =>
            "Busca entidades duplicadas (misma geometría con una tolerancia) en un espacio del dibujo activo: LINE, " +
            "LWPOLYLINE, CIRCLE, ARC, INSERT, TEXT y MTEXT. Agrupa por hash de coordenadas redondeadas (la capa no cuenta) " +
            "y devuelve los grupos paginados con sus handles. No borra nada.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""espacio"": " + ParamReader.SchemaEspacio + @",
    ""capas"": " + ParamReader.SchemaCapas + @",
    ""tipos"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 7,
      ""items"": { ""type"": ""string"", ""enum"": [""LINE"", ""LWPOLYLINE"", ""CIRCLE"", ""ARC"", ""INSERT"", ""TEXT"", ""MTEXT""] },
      ""description"": ""Tipos a revisar (por defecto todos).""
    },
    ""tolerancia"": { ""type"": ""number"", ""minimum"": 1e-12, ""maximum"": 1, ""default"": 1e-6, ""description"": ""Tolerancia en unidades de dibujo para considerar iguales dos coordenadas."" },
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
    ""tolerancia"": { ""type"": ""number"" },
    ""resumen"": { ""type"": ""object"", ""properties"": {
      ""n_grupos"": { ""type"": ""integer"" }, ""n_entidades_sobrantes"": { ""type"": ""integer"" }, ""por_tipo"": { ""type"": ""object"" } } },
    ""grupos"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""tipo"": { ""type"": ""string"" }, ""n"": { ""type"": ""integer"" }, ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
      ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""bbox"": { ""type"": [""array"", ""null""] } } } },
    ""total"": { ""type"": ""integer"" },
    ""cursor_siguiente"": { ""type"": [""string"", ""null""] },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""espacio"", ""tolerancia"", ""resumen"", ""grupos"", ""total"", ""cursor_siguiente"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Query
        {
            public string? Espacio;
            public List<string>? Capas;
            public HashSet<string>? Tipos;
            public double Tolerancia = DuplicateHasher.DefaultTolerance;
            public int Limit;
            public int Start;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Query Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var q = new Query
            {
                Espacio = ParamReader.OptionalString(p, "espacio", 1, 255),
                Capas = ParamReader.OptionalStringArray(p, "capas", 1, 100, 1),
                Limit = ParamReader.Limit(p),
                Start = ParamReader.OptionalCursor(p),
            };

            List<string>? tipos = ParamReader.OptionalStringArray(p, "tipos", 1, 7, 1);
            if (tipos != null)
            {
                q.Tipos = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < tipos.Count; i++)
                {
                    bool known = false;
                    foreach (string t in DuplicateHasher.Types)
                    {
                        known |= string.Equals(t, tipos[i], StringComparison.Ordinal);
                    }

                    if (!known)
                    {
                        throw ToolException.InvalidParams(
                            "tipos[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe ser uno de " + string.Join(", ", DuplicateHasher.Types));
                    }

                    q.Tipos.Add(tipos[i]);
                }
            }

            if (p.TryGetProperty("tolerancia", out JsonElement tol))
            {
                if (tol.ValueKind != JsonValueKind.Number || !tol.TryGetDouble(out double t) || !double.IsFinite(t)
                    || t < DuplicateHasher.MinTolerance || t > DuplicateHasher.MaxTolerance)
                {
                    throw ToolException.InvalidParams(ToleranceError);
                }

                q.Tolerancia = t;
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
                SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, q.Espacio);
                DuplicateAnalysis analysis = new DuplicadosCheck().Analyze(
                    AuditSupport.CreateContext(db, tr, space), q.Capas, q.Tipos, q.Tolerancia);

                int sobrantes = 0;
                var porTipo = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (DuplicateGroup g in analysis.Grupos)
                {
                    sobrantes += g.N - 1;
                    AuditSupport.Increment(porTipo, g.Tipo);
                }

                int total = analysis.Grupos.Count;
                (int count, string? next) = ParamReader.Slice(total, q.Start, q.Limit);
                var page = new List<Dictionary<string, object?>>(count);
                for (int i = q.Start; i < q.Start + count; i++)
                {
                    page.Add(FormatGroup(analysis.Grupos[i]));
                }

                result["espacio"] = space.Name;
                result["tolerancia"] = q.Tolerancia;
                result["resumen"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["n_grupos"] = total,
                    ["n_entidades_sobrantes"] = sobrantes,
                    ["por_tipo"] = AuditSupport.SortedCounts(porTipo),
                };
                result["grupos"] = page;
                result["total"] = total;
                result["cursor_siguiente"] = next;
                if (!analysis.Completo)
                {
                    warnings.Add(AuditSupport.WarningPartial);
                }

                tr.Commit();
            }

            AuditSupport.Finish(result, db, warnings);
            return result;
        }

        /// <summary>
        /// Grupo del contrato. Los handles se acotan a 50 (como en §5C.0); si hay más, se añade handles_truncados: true
        /// y n sigue indicando el total.
        /// </summary>
        private static Dictionary<string, object?> FormatGroup(DuplicateGroup g)
        {
            var handles = new List<string>(Math.Min(g.N, Finding.MaxHandles));
            for (int i = 0; i < g.N && i < Finding.MaxHandles; i++)
            {
                handles.Add(AuditSupport.Handle(g.Ids[i]));
            }

            var d = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tipo"] = g.Tipo,
                ["n"] = g.N,
                ["handles"] = handles,
                ["capas"] = g.Capas,
                ["bbox"] = AuditSupport.Box2D(g.Bbox),
            };

            if (g.N > Finding.MaxHandles)
            {
                d["handles_truncados"] = true;
            }

            return d;
        }
    }
}
