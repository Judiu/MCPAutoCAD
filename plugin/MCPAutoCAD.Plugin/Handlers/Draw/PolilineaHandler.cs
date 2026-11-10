using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// polilinea (protocol.md §5D.2). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z).
    /// LWPOLYLINE (Polyline) en el plano XY del WCS, elevación 0: la z de los vértices se ignora (advertencia si ≠ 0).
    /// Forma simple (vertices, cerrada, bulges, ancho) o lote (polilineas: 1 a 100 objetos con esas mismas propiedades),
    /// nunca ambas. Validación pura en <see cref="PolylineValidator"/> (con el punto del cruce en la autointersección).
    /// R4: UNA caja por polilínea (la de sus vértices), etiqueta 'vertices' (forma simple) o 'polilineas' (lote).
    /// </summary>
    internal sealed class PolilineaHandler : IToolHandler
    {
        /// <summary>Máximo de polilíneas por lote.</summary>
        public const int MaxItems = 100;

        private static readonly string[] Own = { "vertices", "cerrada", "bulges", "ancho" };
        private static readonly string[] Batch = { "polilineas" };
        private static readonly string[] ItemAllowed = { "vertices", "cerrada", "bulges", "ancho" };
        private static readonly string[] Allowed = PrimitiveSupport.Concat(
            PrimitiveSupport.Concat(Own, Batch),
            PrimitiveSupport.Concat(DrawWrite.CommonParams, EntityColor.ParamNames));

        public string Name => "polilinea";

        public string Description =>
            "ESCRIBE en el dibujo: crea una polilínea 2D (LWPOLYLINE) con 2 a 1000 vértices [x, y] en WCS y unidades del dibujo, " +
            "opcionalmente cerrada, con arcos (bulges), ancho constante y color; o varias (polilineas: 1 a 100) en un solo paso de deshacer. " +
            "Capa por defecto MCP-DIBUJO (o un concepto de lps.json como 'muros'); rechaza vértices duplicados y cerradas que se autointersecan (cita tramos y punto del cruce).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        private const string ItemSchema = @"{
        ""type"": ""object"",
        ""properties"": {
          ""vertices"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 1000, ""items"": " + PrimitiveSupport.PointSchema + @", ""description"": ""Vértices [x, y] en WCS (la z se ignora)."" },
          ""cerrada"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = polilínea cerrada (no repita el primer vértice al final)."" },
          ""bulges"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""Bulge de cada tramo (misma longitud que vertices)."" },
          ""ancho"": { ""type"": ""number"", ""minimum"": 0, ""default"": 0, ""description"": ""Ancho constante en unidades del dibujo (0 = línea fina)."" }
        },
        ""required"": [""vertices""],
        ""additionalProperties"": false
      }";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""vertices"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 1000, ""items"": " + PrimitiveSupport.PointSchema + @", ""description"": ""Vértices [x, y] en WCS (si traen z se ignora: la polilínea queda en el plano XY). Forma simple: no combinar con polilineas."" },
    ""cerrada"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = polilínea cerrada (no repita el primer vértice al final)."" },
    ""bulges"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""Bulge de cada tramo (misma longitud que vertices): bulges[i] = tramo del vértice i al i+1 (el último, al cierre). 0 = recto; 1 = semicírculo antihorario."" },
    ""ancho"": { ""type"": ""number"", ""minimum"": 0, ""default"": 0, ""description"": ""Ancho constante en unidades del dibujo (0 = línea fina)."" },
    ""polilineas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": " + ItemSchema + @", ""description"": ""Lote: 1 a 100 polilíneas con sus propias vertices/cerrada/bulges/ancho; todas comparten capa, espacio, color y forzar. No combinar con la forma simple. Un solo paso de deshacer; si una falla, no se crea ninguna."" },
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
        ""n_vertices"": { ""type"": ""integer"" },
        ""cerrada"": { ""type"": ""boolean"" },
        ""longitud"": { ""type"": [""number"", ""null""] },
        ""area"": { ""type"": [""number"", ""null""] },
        ""bbox"": { ""type"": [""array"", ""null""] }
      }
    }");

        /// <summary>Una polilínea ya validada.</summary>
        private sealed class Item
        {
            public Item(List<(double x, double y)> vertices, bool closed, List<double>? bulges, double width, bool zIgnored)
            {
                Vertices = vertices;
                Closed = closed;
                Bulges = bulges;
                Width = width;
                ZIgnored = zIgnored;
            }

            public List<(double x, double y)> Vertices { get; }
            public bool Closed { get; }
            public List<double>? Bulges { get; }
            public double Width { get; }
            public bool ZIgnored { get; }
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

            /// <summary>true si vino en la forma de lote (polilineas).</summary>
            public bool Batch { get; }

            public DrawWrite.Common Common { get; }
            public ColorSpec? Color { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasBatch = ParamReader.Has(p, "polilineas");
            bool hasSimple = ParamReader.Has(p, "vertices") || ParamReader.Has(p, "cerrada")
                || ParamReader.Has(p, "bulges") || ParamReader.Has(p, "ancho");
            if (hasBatch && hasSimple)
            {
                throw ToolException.InvalidParams("polilineas: no se puede combinar con vertices, cerrada, bulges o ancho");
            }

            var items = new List<Item>();
            if (hasBatch)
            {
                JsonElement arr = p.GetProperty("polilineas");
                if (arr.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams("polilineas: debe ser un array de 1 a 100 objetos {vertices, ...}");
                }

                int n = arr.GetArrayLength();
                if (n < 1 || n > MaxItems)
                {
                    throw ToolException.InvalidParams("polilineas: debe tener entre 1 y 100 elementos");
                }

                int i = 0;
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string prefix = "polilineas[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto {vertices, ...}");
                    }

                    ParamReader.RejectUnknown(el, ItemAllowed, prefix);
                    items.Add(ParseItem(el, prefix));
                    i++;
                }
            }
            else
            {
                if (!ParamReader.Has(p, "vertices"))
                {
                    throw ToolException.InvalidParams("vertices: obligatorio (o polilineas para varias)");
                }

                items.Add(ParseItem(p, ""));
            }

            DrawWrite.Common common = DrawWrite.ReadCommon(p, null);
            ColorSpec? color = EntityColor.Read(p);
            return new Params(items, hasBatch, common, color);
        }

        /// <summary>
        /// Una polilínea (forma simple: <paramref name="src"/> = la raíz; lote: el objeto del elemento). En lote, cada
        /// rechazo se cita con el prefijo "polilineas[i]." (validación completa antes de escribir).
        /// </summary>
        private static Item ParseItem(JsonElement src, string prefix)
        {
            try
            {
                List<Point3> points = ParamReader.RequiredPoints(src, "vertices", 2, 1000);
                var vertices = new List<(double x, double y)>(points.Count);
                bool zIgnored = false;
                foreach (Point3 pt in points)
                {
                    vertices.Add((pt.X, pt.Y));
                    zIgnored |= pt.Z != 0;
                }

                bool closed = ParamReader.OptionalBool(src, "cerrada") ?? false;
                List<double>? bulges = PrimitiveSupport.OptionalNumberArray(src, "bulges");
                double width = PrimitiveSupport.OptionalNonNegative(src, "ancho") ?? 0.0;

                IReadOnlyList<string> errors = PolylineValidator.ValidateDetailed(vertices, closed, bulges);
                if (errors.Count > 0)
                {
                    throw ToolException.InvalidParams(errors[0]);
                }

                return new Item(vertices, closed, bulges, width, zIgnored);
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
                boxes.Add(PrimitiveSupport.BoxOf(item.Vertices));
            }

            PlacementCheck.Run(db, p.Common.Espacio, boxes, p.Common.Forzar, p.Batch ? "polilineas" : "vertices", warnings);

            if (p.Items.Exists(it => it.ZIgnored))
            {
                warnings.Add(PolylineValidator.ZIgnoredWarning);
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
                    var pl = new Polyline(item.Vertices.Count);
                    DrawWrite.ApplyByLayer(pl, db, layer.Id);
                    EntityColor.Apply(pl, p.Color);
                    for (int i = 0; i < item.Vertices.Count; i++)
                    {
                        double bulge = item.Bulges == null ? 0.0 : item.Bulges[i];
                        pl.AddVertexAt(i, new Point2d(item.Vertices[i].x, item.Vertices[i].y), bulge, 0, 0);
                    }

                    pl.Closed = item.Closed;
                    pl.Elevation = 0;
                    pl.Normal = Vector3d.ZAxis;
                    pl.ConstantWidth = item.Width;
                    string handle = DrawWrite.Append(space, tr, pl);
                    handles.Add(handle);

                    entidades.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["handle"] = handle,
                        ["n_vertices"] = pl.NumberOfVertices,
                        ["cerrada"] = pl.Closed,
                        ["longitud"] = PrimitiveSupport.TryNumber(() => pl.Length),
                        ["area"] = item.Closed ? PrimitiveSupport.TryNumber(() => pl.Area) : null,
                        ["bbox"] = PrimitiveSupport.Box2D(pl),
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

    /// <summary>
    /// Utilidades compartidas por los handlers de primitivas de Fase 3 (polilinea, circulo, arco, texto, mtext, hatch).
    /// Sin estado. Las piezas sin AutoCAD (lectura de parámetros) se pueden usar en ValidateParams.
    /// </summary>
    internal static class PrimitiveSupport
    {
        public const string PointSchema =
            @"{ ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Punto [x, y] o [x, y, z] en WCS (z = 0 si se omite)."" }";

        public const string AngleSchemaDefault0 =
            @"{ ""type"": ""number"", ""default"": 0, ""description"": ""Grados, antihorarios desde +X (cualquier valor finito; se normaliza a [0, 360))."" }";

        /// <summary>Concatena dos listas de nombres de parámetros.</summary>
        public static string[] Concat(string[] a, string[] b)
        {
            var r = new string[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }

        /// <summary>
        /// OutputSchema R6 común + 'entidad' (forma simple) o 'entidades' (lote) con el schema dado. Ninguna de las dos es
        /// obligatoria: la forma de lote no trae 'entidad' y la simple no trae 'entidades'.
        /// </summary>
        public static JsonElement OutputSchema(string entidadSchema) => Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capa"": { ""type"": ""string"" },
    ""capa_creada"": { ""type"": ""boolean"" },
    ""espacio"": { ""type"": ""string"", ""description"": ""'modelo' o nombre del layout."" },
    ""entidad"": " + entidadSchema + @",
    ""entidades"": { ""type"": ""array"", ""items"": " + entidadSchema + @", ""description"": ""Solo en la forma de lote, en el orden de la entrada."" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""handles"", ""capa"", ""capa_creada"", ""espacio"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Número finito opcional (cualquier valor): "&lt;name&gt;: debe ser un número finito".</summary>
        public static double? OptionalFinite(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double v) || !double.IsFinite(v))
            {
                throw ToolException.InvalidParams(name + ": debe ser un número finito");
            }

            return v;
        }

        /// <summary>Número finito obligatorio (cualquier valor).</summary>
        public static double RequiredFinite(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out _))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            return OptionalFinite(obj, name)!.Value;
        }

        /// <summary>Número ≥ 0 opcional: "&lt;name&gt;: debe ser un número mayor o igual que 0".</summary>
        public static double? OptionalNonNegative(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double v) || !double.IsFinite(v) || v < 0)
            {
                throw ToolException.InvalidParams(name + ": debe ser un número mayor o igual que 0");
            }

            return v;
        }

        /// <summary>
        /// Array opcional de números (sin límite de longitud: la comprueba el validador). Elemento no numérico →
        /// "&lt;name&gt;[i]: debe ser un número finito".
        /// </summary>
        public static List<double>? OptionalNumberArray(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind != JsonValueKind.Array)
            {
                throw ToolException.InvalidParams(name + ": debe ser un array de números");
            }

            var list = new List<double>(el.GetArrayLength());
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double v) || !double.IsFinite(v))
                {
                    throw ToolException.InvalidParams(name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe ser un número finito");
                }

                list.Add(v);
                i++;
            }

            return list;
        }

        /// <summary>Caja XY de una lista de vértices 2D (no vacía).</summary>
        public static BBox2 BoxOf(IReadOnlyList<(double x, double y)> vertices)
        {
            double xmin = double.PositiveInfinity, ymin = double.PositiveInfinity;
            double xmax = double.NegativeInfinity, ymax = double.NegativeInfinity;
            foreach ((double x, double y) in vertices)
            {
                xmin = Math.Min(xmin, x);
                ymin = Math.Min(ymin, y);
                xmax = Math.Max(xmax, x);
                ymax = Math.Max(ymax, y);
            }

            return BBox2.FromCorners(xmin, ymin, xmax, ymax);
        }

        /// <summary>Caja de un círculo/arco completo: centro ± radio.</summary>
        public static BBox2 CircleBox(Point3 c, double r) => BBox2.FromCorners(c.X - r, c.Y - r, c.X + r, c.Y + r);

        /// <summary>Espacio destino abierto para escritura dentro de la transacción de escritura.</summary>
        public static BlockTableRecord OpenForWrite(Transaction tr, SpaceResolver.ResolvedSpace resolved) =>
            (BlockTableRecord)tr.GetObject(resolved.Btr.ObjectId, OpenMode.ForWrite);

        /// <summary>Caja 2D [[xmin, ymin], [xmax, ymax]] de la entidad (GeometricExtents) o null.</summary>
        public static double[][]? Box2D(Entity e) => AcadUtil.TryBBox2(e)?.ToArray2D();

        /// <summary>Valor numérico leído de AutoCAD, o null si lanza o no es finito.</summary>
        public static object? TryNumber(Func<double> read)
        {
            try
            {
                double v = read();
                return double.IsFinite(v) ? v : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Estilo de texto: <paramref name="name"/> null → el actual (Database.Textstyle); si no, un TextStyleTableRecord
        /// existente y no borrado (sin mayúsculas). Inexistente → NOT_FOUND "El estilo de texto '&lt;x&gt;' no existe en el
        /// dibujo." (data.estilo). Devuelve el id y el nombre real.
        /// </summary>
        public static (ObjectId id, string name) ResolveTextStyle(Database db, Transaction tr, string? name)
        {
            ObjectId id = ObjectId.Null;
            if (name == null)
            {
                id = db.Textstyle;
            }
            else
            {
                var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                bool has;
                try
                {
                    has = table.Has(name);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    has = false;
                }

                if (has)
                {
                    id = table[name];
                }

                if (!has || id.IsNull || id.IsErased)
                {
                    // TextStyleTable.Has no distingue mayúsculas; se recorre además por si el indexador no lo resuelve.
                    id = ObjectId.Null;
                    foreach (ObjectId candidate in table)
                    {
                        if (candidate.IsErased)
                        {
                            continue;
                        }

                        var rec = (TextStyleTableRecord)tr.GetObject(candidate, OpenMode.ForRead);
                        if (string.Equals(rec.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            id = candidate;
                            break;
                        }
                    }
                }

                if (id.IsNull)
                {
                    throw new ToolException(
                        ErrorCodes.NotFound,
                        "El estilo de texto '" + name + "' no existe en el dibujo.",
                        new Dictionary<string, object?> { ["estilo"] = name });
                }
            }

            var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            return (id, style.Name);
        }

        /// <summary>Comprobación previa del estilo (lectura sin efectos, fuera del UndoGroup): NOT_FOUND sin paso de deshacer.</summary>
        public static void CheckTextStyle(Database db, string? name)
        {
            if (name == null)
            {
                return;
            }

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                ResolveTextStyle(db, tr, name);
                tr.Commit();
            }
        }
    }
}
