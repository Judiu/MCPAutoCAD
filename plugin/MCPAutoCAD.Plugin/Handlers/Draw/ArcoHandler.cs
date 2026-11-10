using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// arco (protocol.md §5D.3). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z).
    /// Arc(centro, radio, inicioRad, finRad): antihorario de inicio a fin (normal Z). Ángulos en grados normalizados a
    /// [0, 360) con <see cref="Angles.Normalize360"/>; iguales → INVALID_PARAMS. Forma simple o lote (arcos: 1 a 100
    /// objetos {centro, radio, angulo_inicio, angulo_fin}), nunca ambas. R4: caja centro ± radio (la del círculo completo;
    /// conservadora a efectos de "interseca la extensión").
    /// </summary>
    internal sealed class ArcoHandler : IToolHandler
    {
        /// <summary>Máximo de arcos por lote.</summary>
        public const int MaxItems = 100;

        private static readonly string[] Own = { "centro", "radio", "angulo_inicio", "angulo_fin" };
        private static readonly string[] Batch = { "arcos" };
        private static readonly string[] ItemAllowed = { "centro", "radio", "angulo_inicio", "angulo_fin" };
        private static readonly string[] Allowed = PrimitiveSupport.Concat(
            PrimitiveSupport.Concat(Own, Batch),
            PrimitiveSupport.Concat(PrimitiveSupport.Concat(DrawWrite.CommonParams, EntityColor.ParamNames), PrimitiveSupport.RespuestaParam));

        public string Name => "arco";

        public string Description =>
            "ESCRIBE en el dibujo: crea un arco de circunferencia con centro [x, y] (WCS), radio y ángulos de inicio y fin en grados " +
            "(antihorario desde +X, de inicio a fin), o varios (arcos: 1 a 100), con color opcional, en un solo paso de deshacer. " +
            "Capa por defecto MCP-DIBUJO (o un concepto de lps.json).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""centro"": " + PrimitiveSupport.PointSchema + @",
    ""radio"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Radio en unidades del dibujo (> 0)."" },
    ""angulo_inicio"": { ""type"": ""number"", ""description"": ""Ángulo inicial en grados, antihorario desde +X (cualquier valor finito; se normaliza a [0, 360))."" },
    ""angulo_fin"": { ""type"": ""number"", ""description"": ""Ángulo final en grados; el arco va antihorario de angulo_inicio a angulo_fin. Debe diferir de angulo_inicio (módulo 360)."" },
    ""arcos"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100,
      ""description"": ""Lote: 1 a 100 arcos {centro, radio, angulo_inicio, angulo_fin}; comparten capa, espacio, color y forzar. No combinar con la forma simple. Un solo paso de deshacer; si uno falla, no se crea ninguno."",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""centro"": " + PrimitiveSupport.PointSchema + @",
          ""radio"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Radio en unidades del dibujo (> 0)."" },
          ""angulo_inicio"": { ""type"": ""number"", ""description"": ""Ángulo inicial en grados (antihorario desde +X)."" },
          ""angulo_fin"": { ""type"": ""number"", ""description"": ""Ángulo final en grados; debe diferir de angulo_inicio (módulo 360)."" }
        },
        ""required"": [""centro"", ""radio"", ""angulo_inicio"", ""angulo_fin""],
        ""additionalProperties"": false
      }
    },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""color"": " + EntityColor.SchemaColor + @",
    ""color_rgb"": " + EntityColor.SchemaColorRgb + @",
    ""respuesta"": " + PrimitiveSupport.SchemaRespuesta + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = PrimitiveSupport.OutputSchema(@"{
      ""type"": ""object"",
      ""properties"": {
        ""handle"": { ""type"": ""string"" },
        ""centro"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""radio"": { ""type"": ""number"" },
        ""angulo_inicio"": { ""type"": ""number"" },
        ""angulo_fin"": { ""type"": ""number"" },
        ""longitud"": { ""type"": [""number"", ""null""] },
        ""bbox"": { ""type"": [""array"", ""null""] }
      }
    }");

        private sealed class Item
        {
            public Item(Point3 centro, double radio, double inicio, double fin)
            {
                Centro = centro;
                Radio = radio;
                Inicio = inicio;
                Fin = fin;
            }

            public Point3 Centro { get; }
            public double Radio { get; }

            /// <summary>Grados normalizados a [0, 360).</summary>
            public double Inicio { get; }

            public double Fin { get; }
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

            /// <summary>Fallo 34: completa | resumen | handles.</summary>
            public string Respuesta { get; init; } = PrimitiveSupport.RespuestaCompleta;
            public DrawWrite.Common Common { get; }
            public ColorSpec? Color { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasBatch = ParamReader.Has(p, "arcos");
            bool hasSimple = ParamReader.Has(p, "centro") || ParamReader.Has(p, "radio")
                || ParamReader.Has(p, "angulo_inicio") || ParamReader.Has(p, "angulo_fin");
            if (hasBatch && hasSimple)
            {
                throw ToolException.InvalidParams("arcos: no se puede combinar con centro, radio, angulo_inicio o angulo_fin");
            }

            var items = new List<Item>();
            if (hasBatch)
            {
                JsonElement arr = p.GetProperty("arcos");
                if (arr.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams("arcos: debe ser un array de 1 a 100 objetos {centro, radio, angulo_inicio, angulo_fin}");
                }

                int n = arr.GetArrayLength();
                if (n < 1 || n > MaxItems)
                {
                    throw ToolException.InvalidParams("arcos: debe tener entre 1 y 100 elementos");
                }

                int i = 0;
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    string prefix = "arcos[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un objeto {centro, radio, angulo_inicio, angulo_fin}");
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
            return new Params(items, hasBatch, common, color) { Respuesta = PrimitiveSupport.ReadRespuesta(p) };
        }

        private static Item ParseItem(JsonElement src, string prefix)
        {
            try
            {
                Point3 centro = ParamReader.RequiredPoint(src, "centro");
                double radio = ParamReader.RequiredNumber(src, "radio", 0, double.PositiveInfinity, exclusiveMin: true);
                double inicio = Angles.Normalize360(PrimitiveSupport.RequiredFinite(src, "angulo_inicio"));
                double fin = Angles.Normalize360(PrimitiveSupport.RequiredFinite(src, "angulo_fin"));
                if (Angles.SameDirection(inicio, fin))
                {
                    throw ToolException.InvalidParams(Angles.SameStartEnd);
                }

                return new Item(centro, radio, inicio, fin);
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

            PlacementCheck.Run(db, p.Common.Espacio, boxes, p.Common.Forzar, p.Batch ? "arcos" : "entidades", warnings);

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
                    var arc = new Arc(AcadUtil.ToPoint3d(item.Centro), item.Radio, Angles.ToRadians(item.Inicio), Angles.ToRadians(item.Fin));
                    DrawWrite.ApplyByLayer(arc, db, layer.Id);
                    EntityColor.Apply(arc, p.Color);
                    string handle = DrawWrite.Append(space, tr, arc);
                    handles.Add(handle);

                    entidades.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["handle"] = handle,
                        ["centro"] = AcadUtil.ToArray(arc.Center),
                        ["radio"] = arc.Radius,
                        ["angulo_inicio"] = AcadUtil.ToDegrees360(arc.StartAngle),
                        ["angulo_fin"] = AcadUtil.ToDegrees360(arc.EndAngle),
                        ["longitud"] = PrimitiveSupport.TryNumber(() => arc.Length),
                        ["bbox"] = PrimitiveSupport.Box2D(arc),
                    });
                }

                tr.Commit();
                Dictionary<string, object?> r = DrawWrite.Response(pid, handles, layer, resolved.Name, ins, warnings);
                PrimitiveSupport.PutEntities(r, entidades, p.Batch, p.Respuesta);

                return r;
            }
        }
    }
}
