using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// marcar_hallazgos (protocol.md §5C.9). Hilo principal; requiere documento; ESCRITURA.
    /// Por cada marca: nube de revisión (LWPOLYLINE cerrada con arcos) + MTEXT, todo en una Transaction dentro de
    /// <see cref="UndoGroup.Run"/> (un CTRL+Z; si falla una marca no se crea ninguna). La geometría la calcula
    /// GeometryCore (<see cref="RevisionCloud"/>, <see cref="MTextEscape"/>) antes de tocar la API.
    /// </summary>
    internal sealed class MarcarHallazgosHandler : IToolHandler
    {
        private const int MaxMarks = 100;
        private const int MaxTextLength = 500;
        private const double DefaultTextHeight = 0.2;
        private static readonly string[] Allowed = { "marcas", "capa", "espacio", "altura_texto", "forzar" };
        private static readonly string[] AllowedItem = { "bbox", "texto", "severidad" };
        private static readonly string[] Severities = { Severity.Alta, Severity.Media, Severity.Baja };

        public string Name => "marcar_hallazgos";

        public string Description =>
            "ESCRIBE en el dibujo: crea, por cada marca, una nube de revisión alrededor de la caja indicada y un texto " +
            "(MTEXT) junto a ella, todo en un solo paso de deshacer (un CTRL+Z quita todas las marcas de la llamada). " +
            "La capa debe ser MCP-* (por defecto MCP-OBSERVACIONES, que se crea si falta); 'altura_texto' va en unidades " +
            "del dibujo (en un plano en mm use p. ej. 200). Si alguna caja cae fuera de la extensión del espacio destino " +
            "(p. ej. coordenadas de modelo con un layout activo) no escribe nada y devuelve INVALID_PARAMS, salvo 'forzar': true. " +
            "Use auditar_dibujo para obtener las cajas.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""marcas"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""maxItems"": 100,
      ""description"": ""Marcas a dibujar (1 a 100), en orden."",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""bbox"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }, ""description"": ""Caja [[xmin, ymin], [xmax, ymax]] en WCS que rodea la nube (z opcional, se ignora). Una caja de tamaño cero se trata como punto."" },
          ""texto"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 500, ""description"": ""Texto de la observación (literal: no se interpretan códigos de formato MTEXT)."" },
          ""severidad"": { ""type"": ""string"", ""enum"": [""alta"", ""media"", ""baja""], ""description"": ""Si se da, el texto empieza por [ALTA], [MEDIA] o [BAJA]."" }
        },
        ""required"": [""bbox"", ""texto""],
        ""additionalProperties"": false
      }
    },
    ""capa"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""pattern"": ""^[Mm][Cc][Pp]-"", ""default"": ""MCP-OBSERVACIONES"", ""description"": ""Capa MCP-* de las marcas; si no existe se crea en el mismo paso de deshacer."" },
    ""espacio"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""'modelo' o nombre de un layout. Por defecto, el espacio actual. No cambia el espacio activo del usuario."" },
    ""altura_texto"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 0.2, ""description"": ""Altura del texto en unidades del dibujo; también fija el margen y el tamaño de los arcos de la nube."" },
    ""forzar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = escribir aunque alguna marca quede fuera de la extensión del espacio destino (por defecto se rechaza con INVALID_PARAMS sin escribir nada)."" }
  },
  ""required"": [""marcas""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""marcas"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""nube"": { ""type"": ""string"" },
      ""texto"": { ""type"": ""string"" },
      ""bbox_nube"": { ""type"": ""array"" },
      ""n_vertices"": { ""type"": ""integer"" } } } },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""n_marcas"": { ""type"": ""integer"" },
    ""capa"": { ""type"": ""string"" },
    ""capa_creada"": { ""type"": ""boolean"" },
    ""espacio"": { ""type"": ""string"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""marcas"", ""handles"", ""n_marcas"", ""capa"", ""capa_creada"", ""espacio"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Una marca ya validada.</summary>
        private sealed class Mark
        {
            public Mark(BBox2 box, string text, string? severity)
            {
                Box = box;
                Text = text;
                Severity = severity;
            }

            public BBox2 Box { get; }
            public string Text { get; }
            public string? Severity { get; }
        }

        private sealed class MarcarParams
        {
            public MarcarParams(List<Mark> marks, string layer, string? space, double textHeight, bool force)
            {
                Force = force;
                Marks = marks;
                Layer = layer;
                Space = space;
                TextHeight = textHeight;
            }

            public List<Mark> Marks { get; }
            public string Layer { get; }
            public string? Space { get; }
            public double TextHeight { get; }

            /// <summary>F9: escribir aunque haya marcas fuera de la extensión del espacio destino.</summary>
            public bool Force { get; }
        }

        /// <summary>Marca lista para escribir: geometría y contenido calculados fuera de la API.</summary>
        private sealed class PreparedMark
        {
            public PreparedMark(RevisionCloudShape shape, string contents)
            {
                Shape = shape;
                Contents = contents;
            }

            public RevisionCloudShape Shape { get; }
            public string Contents { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        /// <summary>Valida y convierte params. Sin AutoCAD: hilo del pipe y otra vez en el principal.</summary>
        private static MarcarParams Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            if (!p.TryGetProperty("marcas", out JsonElement arr))
            {
                throw ToolException.InvalidParams("marcas: obligatorio");
            }

            if (arr.ValueKind != JsonValueKind.Array)
            {
                throw ToolException.InvalidParams("marcas: debe ser un array de 1 a 100 objetos {bbox, texto, severidad}");
            }

            int n = arr.GetArrayLength();
            if (n < 1 || n > MaxMarks)
            {
                throw ToolException.InvalidParams("marcas: debe tener entre 1 y 100 elementos");
            }

            var marks = new List<Mark>(n);
            int i = 0;
            foreach (JsonElement item in arr.EnumerateArray())
            {
                string path = "marcas[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams(path + ": debe ser un objeto {bbox, texto, severidad}");
                }

                ParamReader.RejectUnknown(item, AllowedItem, path + ".");
                if (!item.TryGetProperty("bbox", out JsonElement bboxEl))
                {
                    throw ToolException.InvalidParams(path + ".bbox: obligatorio");
                }

                // Una caja de tamaño cero es válida (BBox2.FromCorners la admite): se trata como punto.
                BBox2 box = ParamReader.ReadBbox(bboxEl, path + ".bbox");

                string lengthMsg = path + ".texto: debe tener entre 1 y 500 caracteres";
                if (!item.TryGetProperty("texto", out JsonElement textEl))
                {
                    throw ToolException.InvalidParams(path + ".texto: obligatorio");
                }

                if (textEl.ValueKind != JsonValueKind.String)
                {
                    throw ToolException.InvalidParams(path + ".texto: debe ser una cadena");
                }

                string text = textEl.GetString() ?? "";
                int chars = CodePointCount(text);
                if (chars < 1 || chars > MaxTextLength)
                {
                    throw ToolException.InvalidParams(lengthMsg);
                }

                string? severity = null;
                if (item.TryGetProperty("severidad", out JsonElement sevEl))
                {
                    string sevMsg = path + ".severidad: debe ser uno de " + string.Join(", ", Severities);
                    if (sevEl.ValueKind != JsonValueKind.String || !Severity.IsValid(sevEl.GetString()))
                    {
                        throw ToolException.InvalidParams(sevMsg);
                    }

                    severity = sevEl.GetString();
                }

                marks.Add(new Mark(box, text, severity));
                i++;
            }

            string layer = ParamReader.OptionalString(p, "capa", 1, 255) ?? PluginInfo.ObservationsLayer;
            McpLayerFactory.RequireMcpLayerName(layer);
            string? space = ParamReader.OptionalString(p, "espacio", 1, 255);

            double height = DefaultTextHeight;
            if (p.TryGetProperty("altura_texto", out JsonElement hEl))
            {
                if (hEl.ValueKind != JsonValueKind.Number || !hEl.TryGetDouble(out height) || !double.IsFinite(height) || height <= 0)
                {
                    throw ToolException.InvalidParams(RevisionCloud.InvalidTextHeight);
                }
            }

            bool force = ParamReader.OptionalBool(p, "forzar") ?? false;
            return new MarcarParams(marks, layer, space, height, force);
        }

        /// <summary>Longitud en caracteres Unicode (como maxLength de JSON Schema), no en unidades UTF-16.</summary>
        private static int CodePointCount(string s)
        {
            int count = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                }

                count++;
            }

            return count;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            MarcarParams p = Parse(parameters);
            Document doc = ctx.RequireDocument();

            // Geometría y contenido FUERA de la API de AutoCAD (GeometryCore).
            var prepared = new List<PreparedMark>(p.Marks.Count);
            for (int i = 0; i < p.Marks.Count; i++)
            {
                Mark m = p.Marks[i];
                RevisionCloudShape shape;
                try
                {
                    shape = RevisionCloud.Build(m.Box, p.TextHeight);
                }
                catch (ArgumentException ex)
                {
                    string detail = ex.Message == RevisionCloud.InvalidTextHeight
                        ? ex.Message
                        : "marcas[" + i.ToString(CultureInfo.InvariantCulture) + "].bbox: " + ex.Message;
                    throw ToolException.InvalidParams(detail);
                }

                prepared.Add(new PreparedMark(shape, SeverityPrefix(m.Severity) + MTextEscape.Escape(m.Text)));
            }

            Database db = doc.Database;
            int ins = AcadUtil.InsUnits(db);
            List<string> warnings = AcadUtil.UnitWarnings(db);

            // F9 (0.3.2): ANTES de escribir (y fuera del UndoGroup), cada marca debe intersecar la extensión del espacio
            // destino ampliada un 10 %. Desde 0.4 (§5D.9) la comprobación es la común de R4 (PlacementCheck.Run, con el
            // tope de coste de PlacementBudget: si se corta con marcas aún fuera, no bloquea y advierte). Lectura con
            // StartOpenCloseTransaction; el NOT_FOUND del espacio sale aquí igual. Mensaje y data.marcas_fuera sin cambios.
            var boxes = new List<BBox2>(p.Marks.Count);
            foreach (Mark m in p.Marks)
            {
                boxes.Add(m.Box);
            }

            int warningsBefore = warnings.Count;
            PlacementCheck.Result placement = PlacementCheck.Run(db, p.Space, boxes, p.Force, "marcas", warnings);
            if (placement.Fuera > 0 && !placement.Parcial && placement.Extension != null && warnings.Count == warningsBefore + 1)
            {
                // §5C.9: con forzar, la advertencia de marcar_hallazgos conserva su texto de 0.3.2 ("se escribieron marcas
                // … (marcas i, …)"); PlacementCheck usa el texto genérico de R4, que se sustituye aquí.
                List<int> outside = MarkPlacement.OutsideIndices(boxes, placement.Extension);
                warnings[warningsBefore] = "forzar: true: se escribieron marcas fuera de la extensión del espacio destino '"
                    + placement.Espacio + "' " + MarkPlacement.Format(placement.Extension.Value)
                    + " (marcas " + string.Join(", ", outside) + ").";
            }

            string layer = p.Layer;
            string? space = p.Space;
            double height = p.TextHeight;
            return UndoGroup.Run(doc, d => Write(d, prepared, layer, space, height, ctx.Pid, ins, warnings));
        }

        private static string SeverityPrefix(string? severity) => severity switch
        {
            Severity.Alta => "[ALTA] ",
            Severity.Media => "[MEDIA] ",
            Severity.Baja => "[BAJA] ",
            _ => "",
        };

        /// <summary>
        /// Escritura dentro de UNA transacción. Cualquier excepción descarta la transacción (Dispose sin Commit) y no
        /// se crea nada: ni marcas ni capa. El espacio activo del usuario no se cambia (ni TILEMODE ni CurrentSpaceId).
        /// </summary>
        private static object Write(
            Document doc, List<PreparedMark> marks, string layerName, string? espacio, double height, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, espacio);
                ObjectId layerId = McpLayerFactory.EnsureLayer(db, tr, layerName, warnings, out bool layerCreated);
                string actualLayer = ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).Name;
                var target = (BlockTableRecord)tr.GetObject(resolved.Btr.ObjectId, OpenMode.ForWrite);

                var rows = new List<object>(marks.Count);
                var handles = new List<string>(marks.Count * 2);
                foreach (PreparedMark mark in marks)
                {
                    RevisionCloudShape shape = mark.Shape;

                    var cloud = new Polyline(shape.Vertices.Count);
                    cloud.SetDatabaseDefaults(db);
                    for (int i = 0; i < shape.Vertices.Count; i++)
                    {
                        (double x, double y) = shape.Vertices[i];
                        cloud.AddVertexAt(i, new Point2d(x, y), shape.Bulges[i], 0, 0);
                    }

                    cloud.Closed = true;
                    cloud.ConstantWidth = 0;
                    cloud.LayerId = layerId;
                    cloud.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    cloud.Linetype = "ByLayer";
                    cloud.LineWeight = LineWeight.ByLayer;
                    target.AppendEntity(cloud);
                    tr.AddNewlyCreatedDBObject(cloud, true);

                    var text = new MText();
                    text.SetDatabaseDefaults(db);
                    text.Location = new Point3d(shape.Bbox.Xmax + 0.5 * height, shape.Bbox.Ymax, 0);
                    text.Attachment = AttachmentPoint.TopLeft;
                    text.TextHeight = height;
                    text.Width = 30 * height;
                    text.Contents = mark.Contents;
                    text.LayerId = layerId;
                    text.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    text.Linetype = "ByLayer";
                    text.LineWeight = LineWeight.ByLayer;
                    target.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);

                    string cloudHandle = AcadUtil.HandleToString(cloud.Handle);
                    string textHandle = AcadUtil.HandleToString(text.Handle);
                    handles.Add(cloudHandle);
                    handles.Add(textHandle);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["nube"] = cloudHandle,
                        ["texto"] = textHandle,
                        ["bbox_nube"] = shape.Bbox.ToArray2D(),
                        ["n_vertices"] = shape.Vertices.Count,
                    });
                }

                tr.Commit();

                return new Dictionary<string, object?>
                {
                    ["pid"] = pid,
                    ["marcas"] = rows,
                    ["handles"] = handles,
                    ["n_marcas"] = rows.Count,
                    ["capa"] = actualLayer,
                    ["capa_creada"] = layerCreated,
                    ["espacio"] = resolved.Name,
                    ["insunits"] = ins,
                    ["unidades"] = UnitsTable.Name(ins),
                    ["advertencias"] = warnings,
                };
            }
        }
    }
}
