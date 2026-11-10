using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// mtext (protocol.md §5D.4). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). MTEXT con
    /// Contents = <see cref="MTextEscape.Escape"/>(texto) (R7: literal; saltos de línea → \P). Attachment según
    /// 'adjunto' (se fija ANTES que Location: el setter de Attachment conserva Location). Estilo inexistente →
    /// NOT_FOUND antes del UndoGroup. R4: el punto de posición. Forma simple o lote (mtexts, sinónimo parrafos: 1 a 100 objetos con
    /// las mismas propiedades de un mtext), nunca ambas.
    /// </summary>
    internal sealed class MtextHandler : IToolHandler
    {
        /// <summary>Máximo de párrafos por lote.</summary>
        public const int MaxItems = 100;

        private static readonly string[] Own = { "posicion", "texto", "altura", "ancho", "rotacion", "adjunto", "estilo" };
        private static readonly string[] Batch = { "mtexts", "parrafos" };
        private static readonly string[] ItemAllowed = { "posicion", "texto", "altura", "ancho", "rotacion", "adjunto", "estilo" };
        private static readonly string[] Allowed = PrimitiveSupport.Concat(
            PrimitiveSupport.Concat(Own, Batch),
            PrimitiveSupport.Concat(DrawWrite.CommonParams, EntityColor.ParamNames));

        private static readonly string[] Attachments =
        {
            "superior_izquierda", "superior_centro", "superior_derecha",
            "medio_izquierda", "medio_centro", "medio_derecha",
            "inferior_izquierda", "inferior_centro", "inferior_derecha",
        };

        private static readonly AttachmentPoint[] AttachmentValues =
        {
            AttachmentPoint.TopLeft, AttachmentPoint.TopCenter, AttachmentPoint.TopRight,
            AttachmentPoint.MiddleLeft, AttachmentPoint.MiddleCenter, AttachmentPoint.MiddleRight,
            AttachmentPoint.BottomLeft, AttachmentPoint.BottomCenter, AttachmentPoint.BottomRight,
        };

        public string Name => "mtext";

        public string Description =>
            "ESCRIBE en el dibujo: crea un texto de párrafo (MTEXT) en la posición [x, y] (WCS) con altura, ancho de ajuste (0 = sin ajuste), " +
            "rotación en grados, punto de adjunto, estilo y color opcionales; o varios (mtexts: 1 a 100; parrafos es sinónimo), en un solo paso de deshacer. " +
            "El texto es literal (los saltos de línea se conservan; no se interpretan códigos de formato). Capa por defecto MCP-DIBUJO (o el concepto 'textos').";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        private const string ItemSchema = @"{
        ""type"": ""object"",
        ""properties"": {
          ""posicion"": " + PrimitiveSupport.PointSchema + @",
          ""texto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 2000, ""description"": ""Texto literal; los saltos de línea se convierten en párrafos."" },
          ""altura"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Altura del texto en unidades del dibujo (> 0)."" },
          ""ancho"": { ""type"": ""number"", ""minimum"": 0, ""default"": 0, ""description"": ""Ancho de ajuste de línea en unidades del dibujo (0 = sin ajuste)."" },
          ""rotacion"": " + PrimitiveSupport.AngleSchemaDefault0 + @",
          ""adjunto"": { ""type"": ""string"", ""enum"": [""superior_izquierda"", ""superior_centro"", ""superior_derecha"", ""medio_izquierda"", ""medio_centro"", ""medio_derecha"", ""inferior_izquierda"", ""inferior_centro"", ""inferior_derecha""], ""default"": ""superior_izquierda"" },
          ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de texto existente; por defecto el estilo actual."" }
        },
        ""required"": [""posicion"", ""texto"", ""altura""],
        ""additionalProperties"": false
      }";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""posicion"": " + PrimitiveSupport.PointSchema + @",
    ""texto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 2000, ""description"": ""Texto literal; los saltos de línea se convierten en párrafos. Forma simple: no combinar con mtexts."" },
    ""altura"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Altura del texto en unidades del dibujo (> 0)."" },
    ""ancho"": { ""type"": ""number"", ""minimum"": 0, ""default"": 0, ""description"": ""Ancho de ajuste de línea en unidades del dibujo (0 = sin ajuste)."" },
    ""rotacion"": " + PrimitiveSupport.AngleSchemaDefault0 + @",
    ""adjunto"": { ""type"": ""string"", ""enum"": [""superior_izquierda"", ""superior_centro"", ""superior_derecha"", ""medio_izquierda"", ""medio_centro"", ""medio_derecha"", ""inferior_izquierda"", ""inferior_centro"", ""inferior_derecha""], ""default"": ""superior_izquierda"", ""description"": ""Qué punto del cuadro de texto coincide con posicion."" },
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de texto existente; por defecto el estilo actual."" },
    ""mtexts"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": " + ItemSchema + @", ""description"": ""Lote: 1 a 100 MTEXT con sus propias propiedades; comparten capa, espacio, color y forzar. No combinar con la forma simple. Un solo paso de deshacer; si uno falla, no se crea ninguno."" },
    ""parrafos"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": " + ItemSchema + @", ""description"": ""Sinónimo de mtexts (nombre anterior). No combinar con mtexts."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""color"": " + EntityColor.SchemaColor + @",
    ""color_rgb"": " + EntityColor.SchemaColorRgb + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = PrimitiveSupport.OutputSchema(@"{
      ""type"": ""object"",
      ""properties"": {
        ""handle"": { ""type"": ""string"" },
        ""posicion"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""altura"": { ""type"": ""number"" },
        ""ancho"": { ""type"": ""number"" },
        ""rotacion"": { ""type"": ""number"" },
        ""adjunto"": { ""type"": ""string"" },
        ""estilo"": { ""type"": ""string"" },
        ""bbox"": { ""type"": [""array"", ""null""] }
      }
    }");

        /// <summary>Un párrafo ya validado.</summary>
        private sealed class Item
        {
            public Item(Point3 pos, string contents, double height, double width, double rotation, int attachment, string? style)
            {
                Pos = pos;
                Contents = contents;
                Height = height;
                Width = width;
                Rotation = rotation;
                Attachment = attachment;
                Style = style;
            }

            public Point3 Pos { get; }
            public string Contents { get; }
            public double Height { get; }
            public double Width { get; }

            /// <summary>Grados normalizados a [0, 360).</summary>
            public double Rotation { get; }

            /// <summary>Índice en <see cref="Attachments"/>.</summary>
            public int Attachment { get; }

            public string? Style { get; }
        }

        private sealed class Params
        {
            public Params(List<Item> items, bool batch, DrawWrite.Common common, ColorSpec? color)
            {
                Items = items;
                Batch = batch;
                Common = common;
                Color = color;
            }

            public List<Item> Items { get; }
            public bool Batch { get; }
            public DrawWrite.Common Common { get; }
            public ColorSpec? Color { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            // Fallo 9 (verificación 2026-11-10): el lote se llama mtexts, como polilineas/circulos/arcos; parrafos sigue como sinónimo.
            bool hasMtexts = ParamReader.Has(p, "mtexts");
            bool hasParrafos = ParamReader.Has(p, "parrafos");
            if (hasMtexts && hasParrafos)
            {
                throw ToolException.InvalidParams("mtexts y parrafos son el mismo lote: use solo mtexts");
            }

            bool hasBatch = hasMtexts || hasParrafos;
            string batchKey = hasMtexts ? "mtexts" : "parrafos";
            bool hasSimple = ParamReader.Has(p, "posicion") || ParamReader.Has(p, "texto") || ParamReader.Has(p, "altura")
                || ParamReader.Has(p, "ancho") || ParamReader.Has(p, "rotacion") || ParamReader.Has(p, "adjunto") || ParamReader.Has(p, "estilo");
            if (hasBatch && hasSimple)
            {
                throw ToolException.InvalidParams(batchKey + ": no se puede combinar con posicion, texto, altura, ancho, rotacion, adjunto o estilo");
            }

            var items = new List<Item>();
            if (hasBatch)
            {
                JsonElement arr = p.GetProperty(batchKey);
                if (arr.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams(batchKey + ": debe ser un array de 1 a 100 objetos {posicion, texto, altura, ...}");
                }

                int n = arr.GetArrayLength();
                if (n < 1 || n > MaxItems)
                {
                    throw ToolException.InvalidParams(batchKey + ": debe tener entre 1 y 100 elementos");
                }

                int i = 0;
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string prefix = batchKey + "[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto {posicion, texto, altura, ...}");
                    }

                    ParamReader.RejectUnknown(el, ItemAllowed, prefix);
                    items.Add(ParseItem(el, prefix));
                    i++;
                }
            }
            else
            {
                items.Add(ParseItem(p, ""));
            }

            DrawWrite.Common common = DrawWrite.ReadCommon(p, null);
            ColorSpec? color = EntityColor.Read(p);
            return new Params(items, hasBatch, common, color);
        }

        private static Item ParseItem(JsonElement src, string prefix)
        {
            try
            {
                Point3 pos = ParamReader.RequiredPoint(src, "posicion");
                string texto = ParamReader.RequiredText(src, "texto", 1, 2000);
                double height = ParamReader.RequiredNumber(src, "altura", 0, double.PositiveInfinity, exclusiveMin: true);
                double width = PrimitiveSupport.OptionalNonNegative(src, "ancho") ?? 0.0;
                double rotation = Angles.Normalize360(PrimitiveSupport.OptionalFinite(src, "rotacion") ?? 0.0);
                string adjunto = ParamReader.OptionalEnum(src, "adjunto", Attachments) ?? Attachments[0];
                string? style = ParamReader.OptionalString(src, "estilo", 1, 255);
                return new Item(pos, MTextEscape.Escape(texto), height, width, rotation, Array.IndexOf(Attachments, adjunto), style);
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string detalle = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + detalle);
            }
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);

            var boxes = new List<BBox2>(p.Items.Count);
            foreach (Item item in p.Items)
            {
                boxes.Add(PlacementCheck.PointBox(item.Pos));
            }

            PlacementCheck.Run(db, p.Common.Espacio, boxes, p.Common.Forzar, p.Batch ? "mtexts" : "entidades", warnings);

            // Estilos: cada uno debe existir (NOT_FOUND antes del UndoGroup).
            // Un solo chequeo por estilo distinto (un lote suele repetir pocos estilos).
            var estilos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Item item in p.Items)
            {
                if (item.Style != null)
                {
                    estilos.Add(item.Style);
                }
            }

            foreach (string estilo in estilos)
            {
                PrimitiveSupport.CheckTextStyle(db, estilo);
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
                BlockTableRecord space = PrimitiveSupport.OpenForWrite(tr, resolved);

                var handles = new List<string>(p.Items.Count);
                var entidades = new List<object>(p.Items.Count);
                foreach (Item item in p.Items)
                {
                    (ObjectId styleId, string styleName) = PrimitiveSupport.ResolveTextStyle(db, tr, item.Style);

                    var mt = new MText();
                    DrawWrite.ApplyByLayer(mt, db, layer.Id);
                    EntityColor.Apply(mt, p.Color);
                    mt.TextStyleId = styleId;   // antes que TextHeight
                    mt.TextHeight = item.Height;
                    mt.Width = item.Width;
                    mt.Attachment = AttachmentValues[item.Attachment];
                    mt.Location = AcadUtil.ToPoint3d(item.Pos);
                    mt.Rotation = Angles.ToRadians(item.Rotation);
                    mt.Contents = item.Contents;
                    string handle = DrawWrite.Append(space, tr, mt);
                    handles.Add(handle);

                    entidades.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["handle"] = handle,
                        ["posicion"] = AcadUtil.ToArray(mt.Location),
                        ["altura"] = mt.TextHeight,
                        ["ancho"] = mt.Width,
                        ["rotacion"] = AcadUtil.ToDegrees360(mt.Rotation),
                        ["adjunto"] = Attachments[item.Attachment],
                        ["estilo"] = styleName,
                        ["bbox"] = PrimitiveSupport.Box2D(mt),
                    });
                }

                tr.Commit();
                Dictionary<string, object?> r = DrawWrite.Response(pid, handles, layer, resolved.Name, ins, warnings);
                if (p.Batch)
                {
                    r["entidades"] = entidades;
                }
                else
                {
                    r["entidad"] = entidades[0];
                }

                return r;
            }
        }
    }
}
