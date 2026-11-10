using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// linea (protocol.md §5.4). Hilo principal; requiere documento; ESCRITURA.
    /// Todas las líneas de una llamada se crean en una sola transacción (un paso de deshacer, atómico).
    /// Desde 0.4 sigue las reglas comunes de §5D.0: R2 (<see cref="DrawWrite.CheckUnits"/>: INSUNITS=0 →
    /// UNITS_UNKNOWN salvo aceptar_sin_unidades), R3 (<see cref="DrawLayerFactory.Ensure"/>: concepto, capa existente
    /// o del estándar / MCP-*; congelada → INVALID_PARAMS), R4 (<see cref="PlacementCheck.Run"/> con una caja por
    /// línea y etiqueta 'lineas', antes del UndoGroup), R5 (espacio) y ByLayer (<see cref="DrawWrite.ApplyByLayer"/>).
    /// 'exigir_unidades' se admite y no tiene efecto (obsoleto: R2 ya rechaza INSUNITS=0 por defecto).
    /// La forma de la respuesta se conserva (entidades[], handles, capa, capa_creada, espacio, insunits, unidades,
    /// advertencias); 'espacio' es ahora "modelo" o el nombre del layout.
    /// </summary>
    internal sealed class LineaHandler : IToolHandler
    {
        private const int MaxLines = 100;

        private static readonly string[] Allowed =
        {
            "lineas", "inicio", "fin", "capa", "ucs", "exigir_unidades", "aceptar_sin_unidades", "espacio", "forzar",
            "color", "color_rgb",
        };

        private static readonly string[] AllowedItem = { "inicio", "fin" };

        public string Name => "linea";

        public string Description =>
            "ESCRIBE en el dibujo: dibuja una o varias líneas rectas en un solo paso de deshacer. " +
            "Use 'lineas' (1 a 100 objetos {inicio, fin}) o, para una sola línea, 'inicio' y 'fin' a nivel raíz; nunca ambos. " +
            "Puntos [x, y] o [x, y, z] en WCS y en unidades del dibujo (ucs: true para el UCS actual); capa por defecto MCP-DIBUJO " +
            "(o un concepto de lps.json como 'muros'); color opcional (ACI, 'porcapa'/'porbloque' o color_rgb; por defecto PorCapa); rechaza dibujos sin unidades (salvo aceptar_sin_unidades) y líneas fuera de la extensión del espacio (salvo forzar).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        private const string PointSchema =
            @"{ ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Punto [x, y] o [x, y, z] (z = 0 si se omite)."" }";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""lineas"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""maxItems"": 100,
      ""description"": ""Líneas a crear, en orden. No combinar con inicio/fin."",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""inicio"": " + PointSchema + @",
          ""fin"": " + PointSchema + @"
        },
        ""required"": [""inicio"", ""fin""],
        ""additionalProperties"": false
      }
    },
    ""inicio"": " + PointSchema + @",
    ""fin"": " + PointSchema + @",
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""ucs"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true: los puntos están en el UCS actual y se transforman a WCS."" },
    ""exigir_unidades"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Obsoleto desde 0.4: se admite y no tiene efecto (un dibujo sin unidades ya se rechaza salvo aceptar_sin_unidades)."" },
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""color"": " + EntityColor.SchemaColor + @",
    ""color_rgb"": " + EntityColor.SchemaColorRgb + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""entidades"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""handle"": { ""type"": ""string"" },
          ""inicio"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 },
          ""fin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 3, ""maxItems"": 3 },
          ""longitud"": { ""type"": ""number"" }
        }
      }
    },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capa"": { ""type"": ""string"" },
    ""capa_creada"": { ""type"": ""boolean"" },
    ""espacio"": { ""type"": ""string"", ""description"": ""'modelo' o nombre del layout."" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""entidades"", ""handles"", ""capa"", ""capa_creada"", ""espacio"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Parámetros ya validados.</summary>
        private sealed class LineaParams
        {
            public LineaParams(List<(Point3 start, Point3 end)> lines, bool useUcs, DrawWrite.Common common, ColorSpec? color)
            {
                Lines = lines;
                UseUcs = useUcs;
                Common = common;
                Color = color;
            }

            /// <summary>Color pedido (null = PorCapa).</summary>
            public ColorSpec? Color { get; }

            public List<(Point3 start, Point3 end)> Lines { get; }

            public bool UseUcs { get; }

            /// <summary>capa, espacio, forzar, aceptar_sin_unidades (§5D.0).</summary>
            public DrawWrite.Common Common { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        /// <summary>Valida y convierte params. Sin AutoCAD: se usa en el hilo del pipe y otra vez en el principal.</summary>
        private static LineaParams Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasLines = ParamReader.Has(p, "lineas");
            bool hasStart = ParamReader.Has(p, "inicio");
            bool hasEnd = ParamReader.Has(p, "fin");

            if (hasLines && (hasStart || hasEnd))
            {
                throw ToolException.InvalidParams("lineas: no se puede combinar con inicio/fin");
            }

            var lines = new List<(Point3 start, Point3 end)>();
            if (hasLines)
            {
                JsonElement arr = p.GetProperty("lineas");
                if (arr.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams("lineas: debe ser un array de 1 a 100 objetos {inicio, fin}");
                }

                int n = arr.GetArrayLength();
                if (n < 1 || n > MaxLines)
                {
                    throw ToolException.InvalidParams("lineas: debe tener entre 1 y 100 elementos");
                }

                int i = 0;
                foreach (JsonElement item in arr.EnumerateArray())
                {
                    string path = "lineas[" + i + "]";
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        throw ToolException.InvalidParams(path + ": debe ser un objeto {inicio, fin}");
                    }

                    ParamReader.RejectUnknown(item, AllowedItem, path + ".");
                    if (!item.TryGetProperty("inicio", out JsonElement s))
                    {
                        throw ToolException.InvalidParams(path + ".inicio: obligatorio");
                    }

                    if (!item.TryGetProperty("fin", out JsonElement e))
                    {
                        throw ToolException.InvalidParams(path + ".fin: obligatorio");
                    }

                    lines.Add((ParamReader.ReadPoint(s, path + ".inicio"), ParamReader.ReadPoint(e, path + ".fin")));
                    i++;
                }
            }
            else if (hasStart && hasEnd)
            {
                lines.Add((ParamReader.ReadPoint(p.GetProperty("inicio"), "inicio"), ParamReader.ReadPoint(p.GetProperty("fin"), "fin")));
            }
            else if (hasStart || hasEnd)
            {
                throw ToolException.InvalidParams(hasStart ? "fin: obligatorio junto con inicio" : "inicio: obligatorio junto con fin");
            }
            else
            {
                throw ToolException.InvalidParams("lineas: obligatorio (o inicio y fin)");
            }

            bool useUcs = ParamReader.OptionalBool(p, "ucs") ?? false;

            // Obsoleto desde 0.4: se valida el tipo (booleano) y se ignora.
            ParamReader.OptionalBool(p, "exigir_unidades");

            // Longitud cero / no finitos: lo decide GeometryCore (texto exacto del contrato).
            IReadOnlyList<string> errors = LineValidator.ValidateLines(lines);
            if (errors.Count > 0)
            {
                throw ToolException.InvalidParams(errors[0]);
            }

            DrawWrite.Common common = DrawWrite.ReadCommon(p, null);
            ColorSpec? color = EntityColor.Read(p);
            return new LineaParams(lines, useUcs, common, color);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            LineaParams p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);   // R2

            // Puntos a WCS (la matriz del UCS se lee ahora, en el documento activo al ejecutar).
            var wcs = new List<(Point3d start, Point3d end)>(p.Lines.Count);
            var boxes = new List<BBox2>(p.Lines.Count);
            Matrix3d ucsToWcs = p.UseUcs ? doc.Editor.CurrentUserCoordinateSystem : Matrix3d.Identity;
            foreach ((Point3 start, Point3 end) in p.Lines)
            {
                Point3d s = AcadUtil.ToPoint3d(start);
                Point3d e = AcadUtil.ToPoint3d(end);
                if (p.UseUcs)
                {
                    s = s.TransformBy(ucsToWcs);
                    e = e.TransformBy(ucsToWcs);
                }

                wcs.Add((s, e));
                boxes.Add(BBox2.FromCorners(Math.Min(s.X, e.X), Math.Min(s.Y, e.Y), Math.Max(s.X, e.X), Math.Max(s.Y, e.Y)));
            }

            // R4/R5: lectura previa fuera del UndoGroup (NOT_FOUND del espacio y extensión del destino).
            PlacementCheck.Run(db, p.Common.Espacio, boxes, p.Common.Forzar, "lineas", warnings);

            DrawWrite.Common common = p.Common;
            ColorSpec? color = p.Color;
            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, wcs, common, color, pid, ins, warnings));
        }

        /// <summary>
        /// Escritura dentro de UNA transacción. Si algo falla, la transacción se descarta (Dispose sin Commit = Abort)
        /// y no se crea ninguna entidad (ni la capa). El bloqueo del documento lo gestiona <see cref="UndoGroup"/>.
        /// </summary>
        private static object Write(
            Document doc, List<(Point3d start, Point3d end)> lines, DrawWrite.Common common, ColorSpec? color, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, common.Espacio);
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, common.Capa, null, warnings);   // R3
                var space = (BlockTableRecord)tr.GetObject(resolved.Btr.ObjectId, OpenMode.ForWrite);

                var entities = new List<object>(lines.Count);
                var handles = new List<string>(lines.Count);
                foreach ((Point3d start, Point3d end) in lines)
                {
                    var line = new Line(start, end);
                    DrawWrite.ApplyByLayer(line, db, layer.Id);
                    EntityColor.Apply(line, color);
                    string handle = DrawWrite.Append(space, tr, line);
                    handles.Add(handle);
                    entities.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["handle"] = handle,
                        ["inicio"] = AcadUtil.ToArray(line.StartPoint),
                        ["fin"] = AcadUtil.ToArray(line.EndPoint),
                        ["longitud"] = line.Length,
                    });
                }

                tr.Commit();

                // Misma forma que hasta 0.3: pid, entidades, handles, capa, capa_creada, espacio, insunits, unidades, advertencias.
                Dictionary<string, object?> r6 = DrawWrite.Response(pid, handles, layer, resolved.Name, ins, warnings);
                var result = new Dictionary<string, object?>(StringComparer.Ordinal) { ["pid"] = pid, ["entidades"] = entities };
                foreach (KeyValuePair<string, object?> kv in r6)
                {
                    if (kv.Key != "pid")
                    {
                        result[kv.Key] = kv.Value;
                    }
                }

                return result;
            }
        }
    }
}
