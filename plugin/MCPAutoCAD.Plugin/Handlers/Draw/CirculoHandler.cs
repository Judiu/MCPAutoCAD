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
    /// circulo (protocol.md §5D.3). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z).
    /// Circle(centro, normal Z, radio). Forma simple (centro, radio) o lote (circulos: 1 a 100 objetos {centro, radio}),
    /// nunca ambas. R4: caja centro ± radio por círculo (etiqueta 'entidades' o 'circulos' en el lote).
    /// </summary>
    internal sealed class CirculoHandler : IToolHandler
    {
        /// <summary>Máximo de círculos por lote.</summary>
        public const int MaxItems = 100;

        private static readonly string[] Own = { "centro", "radio" };
        private static readonly string[] Batch = { "circulos" };
        private static readonly string[] ItemAllowed = { "centro", "radio" };
        private static readonly string[] Allowed = PrimitiveSupport.Concat(
            PrimitiveSupport.Concat(Own, Batch),
            PrimitiveSupport.Concat(DrawWrite.CommonParams, EntityColor.ParamNames));

        public string Name => "circulo";

        public string Description =>
            "ESCRIBE en el dibujo: crea un círculo con centro [x, y] (WCS) y radio en unidades del dibujo, o varios (circulos: 1 a 100 " +
            "objetos {centro, radio}), con color opcional, en un solo paso de deshacer. Capa por defecto MCP-DIBUJO (o un concepto de lps.json como 'columnas').";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""centro"": " + PrimitiveSupport.PointSchema + @",
    ""radio"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Radio en unidades del dibujo (> 0)."" },
    ""circulos"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100,
      ""description"": ""Lote: 1 a 100 círculos {centro, radio}; comparten capa, espacio, color y forzar. No combinar con centro/radio. Un solo paso de deshacer; si uno falla, no se crea ninguno."",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""centro"": " + PrimitiveSupport.PointSchema + @",
          ""radio"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Radio en unidades del dibujo (> 0)."" }
        },
        ""required"": [""centro"", ""radio""],
        ""additionalProperties"": false
      }
    },
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
        ""centro"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""radio"": { ""type"": ""number"" },
        ""longitud"": { ""type"": [""number"", ""null""] },
        ""area"": { ""type"": [""number"", ""null""] }
      }
    }");

        private sealed class Item
        {
            public Item(Point3 centro, double radio)
            {
                Centro = centro;
                Radio = radio;
            }

            public Point3 Centro { get; }
            public double Radio { get; }
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
            bool hasBatch = ParamReader.Has(p, "circulos");
            bool hasSimple = ParamReader.Has(p, "centro") || ParamReader.Has(p, "radio");
            if (hasBatch && hasSimple)
            {
                throw ToolException.InvalidParams("circulos: no se puede combinar con centro o radio");
            }

            var items = new List<Item>();
            if (hasBatch)
            {
                JsonElement arr = p.GetProperty("circulos");
                if (arr.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams("circulos: debe ser un array de 1 a 100 objetos {centro, radio}");
                }

                int n = arr.GetArrayLength();
                if (n < 1 || n > MaxItems)
                {
                    throw ToolException.InvalidParams("circulos: debe tener entre 1 y 100 elementos");
                }

                int i = 0;
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string prefix = "circulos[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto {centro, radio}");
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
                Point3 centro = ParamReader.RequiredPoint(src, "centro");
                double radio = ParamReader.RequiredNumber(src, "radio", 0, double.PositiveInfinity, exclusiveMin: true);
                return new Item(centro, radio);
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
                boxes.Add(PrimitiveSupport.CircleBox(item.Centro, item.Radio));
            }

            PlacementCheck.Run(db, p.Common.Espacio, boxes, p.Common.Forzar, p.Batch ? "circulos" : "entidades", warnings);

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
                    var circle = new Circle(AcadUtil.ToPoint3d(item.Centro), Vector3d.ZAxis, item.Radio);
                    DrawWrite.ApplyByLayer(circle, db, layer.Id);
                    EntityColor.Apply(circle, p.Color);
                    string handle = DrawWrite.Append(space, tr, circle);
                    handles.Add(handle);

                    entidades.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["handle"] = handle,
                        ["centro"] = AcadUtil.ToArray(circle.Center),
                        ["radio"] = circle.Radius,
                        ["longitud"] = PrimitiveSupport.TryNumber(() => circle.Circumference),
                        ["area"] = PrimitiveSupport.TryNumber(() => circle.Area),
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
