using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// entidades_en_capa_0 (protocol.md §5C.2). Hilo principal; requiere documento; solo lectura. Por defecto recorre
    /// todos los espacios (a diferencia de §5B.1); con <c>espacio</c>, solo ese. La lógica vive en
    /// <see cref="Capa0Check.Analyze"/>.
    /// </summary>
    internal sealed class EntidadesEnCapa0Handler : IToolHandler
    {
        private static readonly string[] Allowed = { "espacio", "limit", "cursor" };

        public string Name => "entidades_en_capa_0";

        public string Description =>
            "Cuenta y lista las entidades dibujadas en la capa 0 del dibujo activo (no dentro de definiciones de bloque, " +
            "donde la capa 0 es correcta), con totales por tipo y por espacio. Por defecto revisa el modelo y todos los " +
            "layouts; 'espacio' limita a uno. Lista paginada con limit/cursor.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""espacio"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""'modelo' o nombre de un layout. Por defecto, todos los espacios (modelo y layouts)."" },
    ""limit"": " + ParamReader.SchemaLimit + @",
    ""cursor"": " + ParamReader.SchemaCursor + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""total"": { ""type"": ""integer"" },
    ""por_tipo"": { ""type"": ""object"" },
    ""por_espacio"": { ""type"": ""object"" },
    ""entidades"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""handle"": { ""type"": ""string"" }, ""tipo"": { ""type"": ""string"" }, ""espacio"": { ""type"": ""string"" },
      ""bbox"": { ""type"": [""array"", ""null""] } } } },
    ""cursor_siguiente"": { ""type"": [""string"", ""null""] },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""total"", ""por_tipo"", ""por_espacio"", ""entidades"", ""cursor_siguiente"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (string? espacio, int limit, int start) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            return (ParamReader.OptionalString(p, "espacio", 1, 255), ParamReader.Limit(p), ParamReader.OptionalCursor(p));
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (string? espacio, int limit, int start) = Parse(parameters);
            Database db = ctx.RequireDocument().Database;
            Dictionary<string, object?> result = AuditSupport.Begin(ctx.Pid);
            var warnings = new List<string>();

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.ResolvedSpace ctxSpace = SpaceResolver.Resolve(db, tr, espacio);
                List<SpaceResolver.ResolvedSpace> spaces = espacio == null
                    ? AuditSupport.AllSpaces(db, tr)
                    : new List<SpaceResolver.ResolvedSpace> { ctxSpace };

                Layer0Result analysis = new Capa0Check().Analyze(AuditSupport.CreateContext(db, tr, ctxSpace), spaces);

                var all = new List<(ObjectId id, string tipo, string espacio)>();
                var porTipo = new Dictionary<string, int>(StringComparer.Ordinal);
                var porEspacio = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (Layer0Space s in analysis.Espacios)
                {
                    if (s.Entidades.Count == 0)
                    {
                        continue;
                    }

                    porEspacio[s.Name] = s.Entidades.Count;
                    foreach ((ObjectId id, string tipo) in s.Entidades)
                    {
                        all.Add((id, tipo, s.Name));
                        AuditSupport.Increment(porTipo, tipo);
                    }
                }

                int total = all.Count;
                (int count, string? next) = ParamReader.Slice(total, start, limit);
                var page = new List<Dictionary<string, object?>>(count);
                for (int i = start; i < start + count; i++)
                {
                    (ObjectId id, string tipo, string sp) = all[i];
                    Entity? e = AuditSupport.Open(tr, id);
                    page.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["handle"] = AuditSupport.Handle(id),
                        ["tipo"] = tipo,
                        ["espacio"] = sp,
                        ["bbox"] = e == null ? null : AcadUtil.ExtentsOrNull(e),
                    });
                }

                result["total"] = total;
                result["por_tipo"] = AuditSupport.SortedCounts(porTipo);
                result["por_espacio"] = porEspacio;
                result["entidades"] = page;
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
    }
}
