using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// texto (protocol.md §5D.4). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). TEXT (DBText) de una
    /// línea: los saltos de línea se sustituyen por espacios (advertencia) y los códigos %% se neutralizan
    /// (<see cref="TextEscape.EscapeDbText"/>, R7). Justificación izquierda = Position; centro/derecha/medio =
    /// HorizontalMode + AlignmentPoint + AdjustAlignment(db) tras añadir la entidad. El estilo inexistente se rechaza
    /// con NOT_FOUND ANTES del UndoGroup (lectura sin efectos) para no dejar un paso de deshacer vacío. R4: el punto.
    /// </summary>
    internal sealed class TextoHandler : IToolHandler
    {
        public const string NewlinesWarning =
            "texto: los saltos de línea se sustituyeron por espacios (TEXT es de una sola línea); use mtext.";

        private static readonly string[] Own = { "posicion", "texto", "altura", "rotacion", "justificacion", "estilo" };
        private static readonly string[] Allowed = PrimitiveSupport.Concat(PrimitiveSupport.Concat(Own, EntityColor.ParamNames), DrawWrite.CommonParams);
        private static readonly string[] Justifications = { "izquierda", "centro", "derecha", "medio" };

        public string Name => "texto";

        public string Description =>
            "ESCRIBE en el dibujo: crea un texto de una línea (TEXT) en la posición [x, y] (WCS) con altura en unidades del dibujo, " +
            "rotación en grados, justificación y estilo opcionales, en un solo paso de deshacer. El texto es literal (no se interpretan " +
            "códigos %%); para varias líneas use mtext. Capa por defecto MCP-DIBUJO (o el concepto 'textos').";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""posicion"": " + PrimitiveSupport.PointSchema + @",
    ""texto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 2000, ""description"": ""Texto literal de una línea (los saltos de línea se sustituyen por espacios)."" },
    ""altura"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Altura del texto en unidades del dibujo (> 0)."" },
    ""rotacion"": " + PrimitiveSupport.AngleSchemaDefault0 + @",
    ""justificacion"": { ""type"": ""string"", ""enum"": [""izquierda"", ""centro"", ""derecha"", ""medio""], ""default"": ""izquierda"", ""description"": ""izquierda: posicion es el punto de inserción; centro/derecha/medio: posicion es el punto de alineación."" },
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de texto existente; por defecto el estilo actual."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""color"": " + EntityColor.SchemaColor + @",
    ""color_rgb"": " + EntityColor.SchemaColorRgb + @"
  },
  ""required"": [""posicion"", ""texto"", ""altura""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = PrimitiveSupport.OutputSchema(@"{
      ""type"": ""object"",
      ""properties"": {
        ""handle"": { ""type"": ""string"" },
        ""posicion"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""altura"": { ""type"": ""number"" },
        ""rotacion"": { ""type"": ""number"" },
        ""justificacion"": { ""type"": ""string"" },
        ""estilo"": { ""type"": ""string"" },
        ""bbox"": { ""type"": [""array"", ""null""] }
      }
    }");

        private sealed class Params
        {
            public Params(Point3 pos, string contents, bool hadNewlines, double height, double rotation, string justification, string? style, DrawWrite.Common common, ColorSpec? color)
            {
                Pos = pos;
                Contents = contents;
                HadNewlines = hadNewlines;
                Height = height;
                Rotation = rotation;
                Justification = justification;
                Style = style;
                Common = common;
                Color = color;
            }

            /// <summary>Color pedido (null = PorCapa).</summary>
            public ColorSpec? Color { get; }

            public Point3 Pos { get; }

            /// <summary>TextString final (una línea, %% neutralizados).</summary>
            public string Contents { get; }

            public bool HadNewlines { get; }
            public double Height { get; }

            /// <summary>Grados normalizados a [0, 360).</summary>
            public double Rotation { get; }

            public string Justification { get; }
            public string? Style { get; }
            public DrawWrite.Common Common { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            Point3 pos = ParamReader.RequiredPoint(p, "posicion");
            string texto = ParamReader.RequiredText(p, "texto", 1, 2000);
            double height = ParamReader.RequiredNumber(p, "altura", 0, double.PositiveInfinity, exclusiveMin: true);
            double rotation = Angles.Normalize360(PrimitiveSupport.OptionalFinite(p, "rotacion") ?? 0.0);
            string justification = ParamReader.OptionalEnum(p, "justificacion", Justifications) ?? "izquierda";
            string? style = ParamReader.OptionalString(p, "estilo", 1, 255);
            DrawWrite.Common common = DrawWrite.ReadCommon(p, null);
            ColorSpec? color = EntityColor.Read(p);

            string single = TextEscape.CollapseNewlines(texto, out bool had);
            return new Params(pos, TextEscape.EscapeDbText(single), had, height, rotation, justification, style, common, color);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);
            PlacementCheck.Run(db, p.Common.Espacio, new List<BBox2> { PlacementCheck.PointBox(p.Pos) }, p.Common.Forzar, "entidades", warnings);
            PrimitiveSupport.CheckTextStyle(db, p.Style);

            if (p.HadNewlines)
            {
                warnings.Add(NewlinesWarning);
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings));
        }

        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Common.Espacio);
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Common.Capa, null, warnings);
                (ObjectId styleId, string styleName) = PrimitiveSupport.ResolveTextStyle(db, tr, p.Style);
                BlockTableRecord space = PrimitiveSupport.OpenForWrite(tr, resolved);

                var text = new DBText();
                DrawWrite.ApplyByLayer(text, db, layer.Id);
                EntityColor.Apply(text, p.Color);
                text.TextStyleId = styleId;   // antes que Height: el estilo no debe pisar la altura pedida
                text.Height = p.Height;
                text.Rotation = Angles.ToRadians(p.Rotation);
                text.TextString = p.Contents;
                var point = AcadUtil.ToPoint3d(p.Pos);
                text.Position = point;
                if (p.Justification != "izquierda")
                {
                    text.HorizontalMode = p.Justification switch
                    {
                        "centro" => TextHorizontalMode.TextCenter,
                        "derecha" => TextHorizontalMode.TextRight,
                        _ => TextHorizontalMode.TextMid,
                    };
                    text.VerticalMode = TextVerticalMode.TextBase;
                    text.AlignmentPoint = point;
                }
                else
                {
                    text.HorizontalMode = TextHorizontalMode.TextLeft;
                    text.VerticalMode = TextVerticalMode.TextBase;
                }

                string handle = DrawWrite.Append(space, tr, text);
                if (p.Justification != "izquierda")
                {
                    // Recalcula Position a partir de AlignmentPoint (la entidad ya es residente en db).
                    text.AdjustAlignment(db);
                }

                var entidad = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = handle,
                    ["posicion"] = AcadUtil.ToArray(text.Position),
                    ["altura"] = text.Height,
                    ["rotacion"] = AcadUtil.ToDegrees360(text.Rotation),
                    ["justificacion"] = p.Justification,
                    ["estilo"] = styleName,
                    ["bbox"] = PrimitiveSupport.Box2D(text),
                };

                tr.Commit();
                Dictionary<string, object?> r = DrawWrite.Response(pid, new List<string> { handle }, layer, resolved.Name, ins, warnings);
                r["entidad"] = entidad;
                return r;
            }
        }
    }
}
