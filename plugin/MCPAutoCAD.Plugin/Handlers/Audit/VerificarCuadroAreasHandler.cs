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
    /// verificar_cuadro_areas (protocol.md §5C.5). Hilo principal; requiere documento; solo lectura. La detección y
    /// las reglas viven en GeometryCore (<see cref="AreaTable"/>) vía <see cref="CuadroAreasCheck.Analyze"/>; aquí se
    /// añaden los contornos, se ordena, se asignan ids y se acota a 200 hallazgos.
    /// </summary>
    internal sealed class VerificarCuadroAreasHandler : IToolHandler
    {
        public const int MaxFindings = 200;
        public const int MaxContours = 20;

        private static readonly string[] Allowed = { "espacio", "bbox", "contornos" };

        public string Name => "verificar_cuadro_areas";

        public string Description =>
            "Detecta los cuadros de áreas (textos LOTE, AREA PISO n, TOTAL CONSTRUIDO, LIBRE, OCUPACION, CONSTRUCCION…) de un " +
            "espacio del dibujo activo y comprueba su aritmética y las discrepancias entre cuadros. Con 'contornos' " +
            "(etiqueta → handle de una entidad cerrada) compara además el área geométrica con la declarada. En el espacio " +
            "modelo marca 'en_layout' según los viewports de los layouts (un cuadro fuera de ellos puede ser una versión " +
            "anterior). Cada cuadro declara 'reglas_evaluadas' y 'reglas_no_evaluables'. No modifica nada.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""espacio"": " + ParamReader.SchemaEspacio + @",
    ""bbox"": " + ParamReader.SchemaBbox + @",
    ""contornos"": {
      ""type"": ""object"", ""minProperties"": 1, ""maxProperties"": 20,
      ""propertyNames"": { ""pattern"": """ + AreaTable.ContourLabelPattern + @""" },
      ""additionalProperties"": " + ParamReader.SchemaHandle + @",
      ""description"": ""Etiqueta del cuadro (LOTE, LIBRE, TOTAL CONSTRUIDO, REMATE PUNTO FIJO o AREA PISO n) → handle de la entidad cerrada cuya área geométrica se compara con la declarada.""
    }
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""espacio"": { ""type"": ""string"" },
    ""cuadros"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""id"": { ""type"": ""string"" }, ""titulo"": { ""type"": [""string"", ""null""] }, ""bbox"": { ""type"": [""array"", ""null""] },
      ""valores"": { ""type"": ""object"" }, ""handles"": { ""type"": ""object"" },
      ""etiquetas_sin_valor"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
      ""en_layout"": { ""type"": ""boolean"" },
      ""reglas_evaluadas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
      ""reglas_no_evaluables"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
        ""regla"": { ""type"": ""string"" }, ""motivo"": { ""type"": ""string"" } }, ""required"": [""regla"", ""motivo""] } } },
      ""required"": [""id"", ""titulo"", ""bbox"", ""valores"", ""handles"", ""etiquetas_sin_valor"", ""en_layout"", ""reglas_evaluadas"", ""reglas_no_evaluables""] } },
    ""n_textos_leidos"": { ""type"": ""integer"" },
    ""hallazgos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""total"": { ""type"": ""integer"" },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""espacio"", ""cuadros"", ""n_textos_leidos"", ""hallazgos"", ""total"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Query
        {
            public string? Espacio;
            public BBox2? Bbox;
            public List<(string etiqueta, string handle)> Contornos = new List<(string, string)>();
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Query Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var q = new Query
            {
                Espacio = ParamReader.OptionalString(p, "espacio", 1, 255),
                Bbox = ParamReader.OptionalBbox(p, "bbox"),
            };

            if (p.TryGetProperty("contornos", out JsonElement c))
            {
                if (c.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams("contornos: debe ser un objeto etiqueta → handle");
                }

                foreach (JsonProperty prop in c.EnumerateObject())
                {
                    if (!AreaTable.IsContourLabel(prop.Name))
                    {
                        throw ToolException.InvalidParams("contornos: etiqueta no reconocida '" + prop.Name + "'");
                    }

                    string path = "contornos." + prop.Name;
                    if (prop.Value.ValueKind != JsonValueKind.String || !IsHandle(prop.Value.GetString()))
                    {
                        throw ToolException.InvalidParams(path + ": formato inválido");
                    }

                    q.Contornos.Add((prop.Name, prop.Value.GetString()!));
                }

                if (q.Contornos.Count < 1 || q.Contornos.Count > MaxContours)
                {
                    throw ToolException.InvalidParams("contornos: debe tener entre 1 y 20 entradas");
                }
            }

            return q;
        }

        private static bool IsHandle(string? s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 16)
            {
                return false;
            }

            foreach (char ch in s)
            {
                bool hex = (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F') || (ch >= 'a' && ch <= 'f');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
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

                // Los contornos se validan antes de leer los textos: NOT_FOUND / sin área cortan la llamada.
                var contours = new List<(string etiqueta, string handle, double area, BBox2? box)>();
                foreach ((string etiqueta, string handle) in q.Contornos)
                {
                    Entity? e = AcadUtil.TryGetEntityByHandle(db, handle, tr) ?? throw ToolException.EntityNotFound(handle);
                    if (!AuditSupport.TryArea(e, out double? area) || area == null)
                    {
                        throw ToolException.InvalidParams("contornos." + etiqueta + ": la entidad no tiene área");
                    }

                    contours.Add((etiqueta, AcadUtil.HandleToString(e.Handle), area.Value, AcadUtil.TryBBox2(e)));
                }

                AreaTableAnalysis analysis = new CuadroAreasCheck().Analyze(AuditSupport.CreateContext(db, tr, space), q.Bbox);
                AreaTableResult parsed = analysis.Result;
                var findings = new List<Finding>(parsed.Hallazgos);
                warnings.AddRange(parsed.Advertencias);

                foreach ((string etiqueta, string handle, double area, BBox2? box) in contours)
                {
                    bool declared = false;
                    foreach (AreaTableBox b in parsed.Cuadros)
                    {
                        if (b.Valores.ContainsKey(etiqueta))
                        {
                            declared = true;
                            break;
                        }
                    }

                    if (!declared)
                    {
                        warnings.Add("contornos." + etiqueta + ": ningún cuadro declara esa etiqueta.");
                        continue;
                    }

                    foreach (Finding f in AreaTable.CompareContour(parsed, etiqueta, handle, area, box))
                    {
                        f.Espacio = space.Name;
                        findings.Add(f);
                    }
                }

                string? outsideWarning = CuadroAreasCheck.OutsideLayoutWarning(parsed);
                if (outsideWarning != null)
                {
                    warnings.Add(outsideWarning);
                }

                if (parsed.Cuadros.Count == 0)
                {
                    warnings.Add("No se detectó ningún cuadro de áreas en el espacio " + space.Name + ".");
                }

                Finding.Sort(findings, AuditSupport.RuleOrder);
                Finding.AssignIds(findings);
                int total = findings.Count;
                if (total > MaxFindings)
                {
                    warnings.Add("Se devuelven los " + AuditSupport.Int(MaxFindings) + " primeros hallazgos de "
                        + AuditSupport.Int(total) + ".");
                }

                if (!analysis.Completo)
                {
                    warnings.Add(AuditSupport.WarningPartial);
                }

                result["espacio"] = space.Name;
                result["cuadros"] = FormatBoxes(parsed.Cuadros);
                result["n_textos_leidos"] = analysis.NTextos;
                result["hallazgos"] = AuditSupport.ToDictionaries(findings.GetRange(0, Math.Min(total, MaxFindings)));
                result["total"] = total;
                tr.Commit();
            }

            AuditSupport.Finish(result, db, warnings);
            return result;
        }

        private static List<Dictionary<string, object?>> FormatBoxes(List<AreaTableBox> boxes)
        {
            var list = new List<Dictionary<string, object?>>(boxes.Count);
            foreach (AreaTableBox b in boxes)
            {
                var valores = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, double> kv in b.Valores)
                {
                    valores[kv.Key] = AcadUtil.NumberOrNull(kv.Value);
                }

                var handles = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, (string etiqueta, string? valor)> kv in b.Handles)
                {
                    handles[kv.Key] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["etiqueta"] = kv.Value.etiqueta,
                        ["valor"] = kv.Value.valor,
                    };
                }

                var noEvaluables = new List<Dictionary<string, object?>>(b.ReglasNoEvaluables.Count);
                foreach ((string regla, string motivo) in b.ReglasNoEvaluables)
                {
                    noEvaluables.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["regla"] = regla,
                        ["motivo"] = motivo,
                    });
                }

                list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = b.Id,
                    ["titulo"] = b.Titulo,
                    ["bbox"] = AuditSupport.Box2D(b.Bbox),
                    ["valores"] = valores,
                    ["handles"] = handles,
                    ["etiquetas_sin_valor"] = b.EtiquetasSinValor,
                    ["en_layout"] = b.EnLayout,
                    ["reglas_evaluadas"] = new List<string>(b.ReglasEvaluadas),
                    ["reglas_no_evaluables"] = noEvaluables,
                });
            }

            return list;
        }
    }
}
