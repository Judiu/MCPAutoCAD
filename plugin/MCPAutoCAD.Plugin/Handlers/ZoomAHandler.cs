using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// zoom_a (0.8.1). Hilo principal; requiere documento. CAMBIA SOLO LA VISTA del usuario: no escribe en el dibujo, así que
    /// NO abre <see cref="UndoGroup"/> (ni BE/E/U) y por eso <c>escritura: false</c>. La vista se cambia con
    /// <c>Editor.SetCurrentView</c> sobre el ViewTableRecord de la vista actual (solo CenterPoint, Width y Height; el aspecto
    /// de la vista se conserva). Caja = unión de las cajas de los handles, o la ventana indicada; encuadre con
    /// <see cref="ZoomFit"/>.
    /// </summary>
    internal sealed class ZoomAHandler : IToolHandler
    {
        private const int MaxHandles = 500;
        private const double DefaultMargin = 0.1;

        private static readonly string[] Allowed = { "handles", "ventana", "margen" };

        public string Name => "zoom_a";

        public string Description =>
            "CAMBIA SOLO LA VISTA del usuario (no el dibujo, sin paso de deshacer): encuadra la vista activa en los handles indicados o en una ventana [[x0, y0], [x1, y1]] " +
            "con un margen (fracción del lado mayor, 0.1 por defecto) y conserva el aspecto de la vista. Indique handles o ventana, no ambos.";

        public bool RequiresDocument => true;

        /// <summary>false: no modifica el dibujo (solo cambia la vista).</summary>
        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Entidades cuya extensión conjunta encuadrar. Sin extensión calculable → se omiten con advertencia."" },
    ""ventana"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } }, ""description"": ""Caja [[xmin, ymin], [xmax, ymax]] en WCS que encuadrar (z opcional, se ignora)."" },
    ""margen"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 10, ""default"": 0.1, ""description"": ""Margen alrededor de la caja, como fracción de su lado mayor (0.1 = 10 %)."" }
  },
  ""oneOf"": [{ ""required"": [""handles""] }, { ""required"": [""ventana""] }],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""origen"": { ""type"": ""string"", ""enum"": [""handles"", ""ventana""] },
    ""caja"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } } },
    ""centro"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""ancho"": { ""type"": ""number"" },
    ""alto"": { ""type"": ""number"" },
    ""margen"": { ""type"": ""number"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""origen"", ""caja"", ""centro"", ""ancho"", ""alto"", ""margen"", ""handles"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Params
        {
            public Params(List<string>? handles, BBox2? ventana, double margen)
            {
                Handles = handles;
                Ventana = ventana;
                Margen = margen;
            }

            /// <summary>Handles (null si se pidió ventana).</summary>
            public List<string>? Handles { get; }

            /// <summary>Ventana (null si se pidieron handles).</summary>
            public BBox2? Ventana { get; }

            public double Margen { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasHandles = ParamReader.Has(p, "handles");
            bool hasVentana = ParamReader.Has(p, "ventana");
            if (hasHandles == hasVentana)
            {
                throw ToolException.InvalidParams("debe indicar handles o ventana (uno de los dos)");
            }

            List<string>? handles = hasHandles ? ParamReader.RequiredHandles(p, "handles", 1, MaxHandles) : null;
            BBox2? ventana = hasVentana ? ParamReader.OptionalBbox(p, "ventana") : null;
            double margen = ParamReader.OptionalNumber(p, "margen", 0, ZoomFit.MaxMargin) ?? DefaultMargin;
            return new Params(handles, ventana, margen);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            Editor ed = doc.Editor;
            var warnings = new List<string>();
            int ins = AcadUtil.InsUnits(db);

            BBox2 caja;
            if (p.Handles != null)
            {
                caja = UnionOf(db, p.Handles, warnings);
            }
            else
            {
                caja = p.Ventana!.Value;
            }

            ViewWindow win;
            using (ViewTableRecord vista = ed.GetCurrentView())
            {
                double aspect = vista.Width > 0 && vista.Height > 0 ? vista.Width / vista.Height : 1.0;
                try
                {
                    win = ZoomFit.Fit(caja.Xmin, caja.Ymin, caja.Xmax, caja.Ymax, p.Margen, aspect);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }

                vista.CenterPoint = new Point2d(win.CenterX, win.CenterY);
                vista.Width = win.Width;
                vista.Height = win.Height;
                ed.SetCurrentView(vista);
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = ctx.Pid,
                ["origen"] = p.Handles != null ? "handles" : "ventana",
                ["caja"] = win.ToBbox2D(),
                ["centro"] = new[] { win.CenterX, win.CenterY },
                ["ancho"] = win.Width,
                ["alto"] = win.Height,
                ["margen"] = p.Margen,
                ["handles"] = p.Handles ?? new List<string>(),
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = warnings,
            };
        }

        /// <summary>Unión de las cajas 2D de las entidades; las que no tienen extensión se omiten con advertencia.</summary>
        private static BBox2 UnionOf(Database db, List<string> handles, List<string> warnings)
        {
            BBox2? total = null;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (string h in handles)
                {
                    Entity e = AcadUtil.TryGetEntityByHandle(db, h, tr) ?? throw ToolException.EntityNotFound(h);
                    BBox2? b = AcadUtil.TryBBox2(e);
                    if (b == null)
                    {
                        warnings.Add("handle " + h + " sin extensión calculable: omitido del encuadre.");
                        continue;
                    }

                    total = total == null ? b : total.Value.Union(b.Value);
                }

                tr.Commit();
            }

            if (total == null)
            {
                throw ToolException.InvalidParams("handles: ninguna entidad tiene extensión calculable");
            }

            return total.Value;
        }
    }
}
