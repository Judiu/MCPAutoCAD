using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Layouts;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// ajustar_viewport (plugin 0.9.0, fase M2). Cambia la escala, la zona de modelo, la posición en papel o el bloqueo de
    /// un viewport existente. Reglas:
    /// <list type="bullet">
    /// <item>ventana_modelo → el viewport pasa a mostrar exactamente esa zona (tamaño en papel = ventana × escala, con la
    /// escala pedida o la actual).</item>
    /// <item>escala sin ventana_modelo → conserva el tamaño en papel y el centro de modelo (la zona visible crece o encoge).</item>
    /// <item>centro_papel → mueve el viewport en el papel.</item>
    /// <item>bloquear → fija el bloqueo de escala.</item>
    /// </list>
    /// Cambiar la vista exige un viewport en planta sin giro. Sin confirmar: true solo previsualiza.
    /// </summary>
    internal sealed class AjustarViewportHandler : IToolHandler
    {
        private static readonly string[] Allowed =
        {
            "handle", "escala", "ventana_modelo", "centro_papel", "bloquear", "capa", "crear_escala_anotacion", "unidades_modelo", "confirmar", "preview",
        };

        public string Name => "ajustar_viewport";

        public string Description =>
            "Ajusta un viewport existente (handle de listar_layouts): 'escala' 1:N (conserva su tamaño en papel y su centro de " +
            "modelo), 'ventana_modelo' (pasa a mostrar exactamente esa zona; el tamaño en papel se recalcula), 'centro_papel' " +
            "(lo mueve en el papel), 'bloquear' y/o 'capa'. Cambiar la vista exige un viewport en planta, sin giro y sin recorte " +
            "poligonal; al cambiar la escala asigna la escala de anotación equivalente si el dibujo la tiene. Sin confirmar: true " +
            "solo previsualiza; con confirmar: true escribe en un paso de deshacer.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handle"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Handle del viewport (campo handle de listar_layouts)."" },
    ""escala"": " + LayoutWrite.SchemaEscala + @",
    ""ventana_modelo"": " + LayoutWrite.SchemaVentanaModelo + @",
    ""centro_papel"": " + LayoutWrite.SchemaCentroPapel + @",
    ""bloquear"": { ""type"": ""boolean"", ""description"": ""true/false = bloquear/desbloquear la escala. Si se omite, se conserva el bloqueo actual."" },
    ""capa"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Mover el viewport a esta capa (existente, concepto de lps.json o capa del estándar que se crea si falta)."" },
    ""crear_escala_anotacion"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Si el dibujo no tiene una escala de anotación equivalente, crearla ('LPS 1:N') en la lista de escalas. false = no tocar la lista; solo se avisa."" },
    ""unidades_modelo"": " + LayoutWrite.SchemaUnidadesModelo + @",
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""handle""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""handle"": { ""type"": ""string"" },
    ""layout"": { ""type"": ""string"" },
    ""antes"": { ""type"": ""object"", ""description"": ""centro_papel, ancho_papel, alto_papel, ventana_modelo (null si no está en planta o está girado), factor_escala, escala (null sin unidades métricas o si el layout imprime ajustado al papel), bloqueado, capa."" },
    ""despues"": { ""type"": ""object"", ""description"": ""Los mismos campos tras el ajuste (mismas reglas de null)."" },
    ""escala_anotacion"": { ""type"": [""string"", ""null""], ""description"": ""Escala de anotación asignada si cambia la vista; null si no cambia o si el dibujo no tiene una equivalente."" },
    ""solapa_con"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""handle"", ""layout"", ""antes"", ""despues"", ""escala_anotacion"", ""solapa_con""]
}");

        private sealed class Params
        {
            public string Handle = "";
            public double? Denominador;
            public BBox2? Ventana;
            public double? PaperX;
            public double? PaperY;
            public bool? Bloquear;
            public string? Capa;
            public bool CrearEscala = true;
            public string? UnidadesModelo;
            public CleanWrite.Common Common = new CleanWrite.Common(false, null);

            public bool ChangesView => Denominador != null || Ventana != null;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var r = new Params
            {
                Handle = ParamReader.OptionalHandle(p, "handle") ?? throw ToolException.InvalidParams("handle: obligatorio"),
                Denominador = LayoutWrite.ReadScale(p),
                Ventana = ParamReader.OptionalBbox(p, "ventana_modelo"),
                Bloquear = ParamReader.OptionalBool(p, "bloquear"),
                Capa = ParamReader.OptionalString(p, "capa", 1, 255),
                CrearEscala = ParamReader.OptionalBool(p, "crear_escala_anotacion") ?? true,
                UnidadesModelo = LayoutWrite.ReadModelUnits(p),
                Common = CleanWrite.ReadCommon(p),
            };
            Point3? c = ParamReader.OptionalPoint(p, "centro_papel");
            if (c != null)
            {
                r.PaperX = c.Value.X;
                r.PaperY = c.Value.Y;
            }

            if (r.Ventana is BBox2 v && (!(v.Width > 0) || !(v.Height > 0)))
            {
                throw ToolException.InvalidParams("ventana_modelo: debe tener ancho y alto mayores que 0");
            }

            if (!r.ChangesView && c == null && r.Bloquear == null && r.Capa == null)
            {
                throw ToolException.InvalidParams("indique al menos uno de escala, ventana_modelo, centro_papel, bloquear o capa");
            }

            if (r.Capa != null)
            {
                Draw.DrawLayerFactory.Plan(r.Capa, null);
            }

            return r;
        }

        /// <summary>Viewport de papel (no el viewport general del layout) con ese handle, y su layout; si no, error.</summary>
        private static (Viewport vp, Layout layout) Resolve(Database db, Transaction tr, string handle)
        {
            Entity? e = AcadUtil.TryGetEntityByHandle(db, handle, tr);
            if (e == null)
            {
                throw ToolException.EntityNotFound(handle);
            }

            if (!(e is Viewport vp))
            {
                throw ToolException.InvalidParams("handle: la entidad " + handle + " no es un viewport (" + AcadUtil.DxfName(e) + ")");
            }

            if (!(tr.GetObject(vp.OwnerId, OpenMode.ForRead) is BlockTableRecord owner) || !owner.IsLayout || owner.LayoutId.IsNull
                || !(tr.GetObject(owner.LayoutId, OpenMode.ForRead) is Layout layout) || layout.ModelType)
            {
                throw ToolException.InvalidParams("handle: el viewport " + handle + " no está en un layout de papel");
            }

            if (Audit.LayoutViewports.PaperViewportId(tr, owner) == vp.ObjectId)
            {
                throw ToolException.InvalidParams("handle: " + handle + " es el viewport general del papel del layout, no un viewport de dibujo");
            }

            if (tr.GetObject(vp.LayerId, OpenMode.ForRead) is LayerTableRecord ltr && ltr.IsLocked)
            {
                throw ToolException.InvalidParams("handle: el viewport está en la capa bloqueada '" + ltr.Name + "'");
            }

            return (vp, layout);
        }

        /// <summary>true si el viewport está en planta (dirección +Z) y sin giro.</summary>
        private static bool PlanNoTwist(Viewport vp)
        {
            Vector3d dir = vp.ViewDirection;
            double len = dir.Length;
            bool plan = len > 0 && dir.Z > 0 && Math.Abs(dir.X / len) <= 1e-6 && Math.Abs(dir.Y / len) <= 1e-6;
            double twist = vp.TwistAngle % (2 * Math.PI);
            return plan && (Math.Abs(twist) <= 1e-9 || Math.Abs(Math.Abs(twist) - 2 * Math.PI) <= 1e-9);
        }

        private sealed class Planned
        {
            public string Layout = "";
            public string Handle = "";
            public string? Capa;
            public ViewportGeometry? Geo;
            public double PaperX;
            public double PaperY;
            public bool Locked;
            public Dictionary<string, object?> Antes = new Dictionary<string, object?>();
            public Dictionary<string, object?> Despues = new Dictionary<string, object?>();
            public List<string> Solapa = new List<string>();

            /// <summary>Denominador 1:N de la vista nueva (pedido o deducido de las unidades), o null si no se puede saber.</summary>
            public double? Den;
        }

        private static Planned Plan(Database db, Transaction tr, Params p, List<string> warnings)
        {
            (Viewport vp, Layout layout) = Resolve(db, tr, p.Handle);
            double? modelMeters = null;
            double? paperMm = null;
            if (p.Denominador != null)
            {
                modelMeters = LayoutWrite.ModelMeters(db, p.UnidadesModelo);
                paperMm = LayoutWrite.PaperMm(layout);
            }
            else
            {
                // Solo para informar la escala 1:N antes/después; sin unidades o sin escala de impresión fija → null.
                (paperMm, string _) = ListarLayoutsHandler.PlotScale(layout);
                int ins = AcadUtil.InsUnits(db);
                double? fromIns = ins == 0 ? null : GeometryCore.Audit.UnitsScale.MetersPerUnit(ins);
                modelMeters = ViewportPlanner.ResolveModelMeters(fromIns, p.UnidadesModelo, out string? _);
            }

            bool plan = PlanNoTwist(vp);
            if (p.ChangesView && !plan)
            {
                throw ToolException.InvalidParams("handle: el viewport " + p.Handle + " no está en planta o está girado; solo se admiten centro_papel, bloquear y capa");
            }

            if ((p.ChangesView || p.PaperX != null) && vp.NonRectClipOn)
            {
                // El contorno de recorte (NonRectClipEntityId) no se mueve ni se escala con el rectángulo del viewport.
                throw ToolException.InvalidParams("handle: el viewport " + p.Handle + " tiene recorte poligonal; solo se admiten bloquear y capa");
            }

            string capaActual = vp.Layer;
            string? capaNueva = p.Capa == null ? null : Draw.DrawLayerFactory.Check(db, tr, p.Capa, null, out bool _, warnings).Nombre;

            var result = new Planned
            {
                Layout = layout.LayoutName,
                Handle = AcadUtil.HandleToString(vp.Handle),
                Capa = capaNueva,
                PaperX = p.PaperX ?? vp.CenterPoint.X,
                PaperY = p.PaperY ?? vp.CenterPoint.Y,
                Locked = p.Bloquear ?? vp.Locked,
            };

            BBox2? window = null;
            if (plan)
            {
                window = LayoutWrite.Checked(() => ViewportWindow.FromViewportTarget(
                    vp.ViewTarget.X, vp.ViewTarget.Y, vp.ViewCenter.X, vp.ViewCenter.Y, vp.ViewHeight, vp.Width, vp.Height, vp.TwistAngle));
            }

            result.Antes = Describe(vp.CenterPoint.X, vp.CenterPoint.Y, vp.Width, vp.Height, window, vp.CustomScale, vp.Locked, capaActual, paperMm, modelMeters);

            if (p.ChangesView)
            {
                double cs = p.Denominador != null
                    ? LayoutWrite.Checked(() => ViewportPlanner.CheckedScale(p.Denominador.Value, paperMm!.Value, modelMeters!.Value))
                    : vp.CustomScale;
                if (p.Ventana is BBox2 v)
                {
                    result.Geo = LayoutWrite.Checked(() => ViewportPlanner.FromWindowWithScale(v, cs, result.PaperX, result.PaperY));
                }
                else
                {
                    BBox2 w = window!.Value;
                    result.Geo = LayoutWrite.Checked(() => ViewportPlanner.Rescale(
                        vp.Width, vp.Height, result.PaperX, result.PaperY, (w.Xmin + w.Xmax) / 2, (w.Ymin + w.Ymax) / 2, cs));
                }

                ViewportGeometry g = result.Geo;
                result.Den = p.Denominador ?? (paperMm == null ? null : LayoutRules.Denominator(g.CustomScale, paperMm.Value, modelMeters));
                result.Despues = Describe(g.PaperX, g.PaperY, g.Width, g.Height, g.ModelWindow, g.CustomScale, result.Locked, capaNueva ?? capaActual, paperMm, modelMeters);
            }
            else
            {
                // Mover el viewport en el papel no cambia la zona de modelo que muestra.
                result.Despues = Describe(result.PaperX, result.PaperY, vp.Width, vp.Height, window,vp.CustomScale, result.Locked, capaNueva ?? capaActual, paperMm, modelMeters);
            }

            BBox2 paperBox = LayoutWrite.Checked(() => BBox2.FromCorners(
                result.PaperX - (result.Geo?.Width ?? vp.Width) / 2, result.PaperY - (result.Geo?.Height ?? vp.Height) / 2,
                result.PaperX + (result.Geo?.Width ?? vp.Width) / 2, result.PaperY + (result.Geo?.Height ?? vp.Height) / 2));
            result.Solapa = ViewportPaper.Overlapping(tr, layout, paperBox, vp.ObjectId);
            ViewportPaper.CheckOnPaper(layout, paperBox, warnings);
            if (result.Solapa.Count > 0)
            {
                warnings.Add("El viewport se cruza en el papel con " + result.Solapa.Count + " viewport(s): " + string.Join(", ", result.Solapa) + ".");
            }

            return result;
        }

        private static Dictionary<string, object?> Describe(
            double px, double py, double w, double h, BBox2? window, double cs, bool locked, string capa, double? paperMm, double? modelMeters)
        {
            double? den = paperMm == null ? null : LayoutRules.Denominator(cs, paperMm.Value, modelMeters);
            int? std = den == null ? null : LayoutRules.MatchingStandard(den.Value);
            return new Dictionary<string, object?>
            {
                ["centro_papel"] = new[] { AcadUtil.NumberOrNull(px), AcadUtil.NumberOrNull(py) },
                ["ancho_papel"] = AcadUtil.NumberOrNull(w),
                ["alto_papel"] = AcadUtil.NumberOrNull(h),
                ["ventana_modelo"] = window?.ToArray2D(),
                ["factor_escala"] = AcadUtil.NumberOrNull(cs),
                ["escala"] = den == null ? null : LayoutRules.ScaleLabel(std ?? Math.Round(den.Value, 2)),
                ["bloqueado"] = locked,
                ["capa"] = capa,
            };
        }

        /// <summary>Denominador 1:N para nombrar la escala de anotación (estándar si coincide), o null si no se conoce.</summary>
        private static double? Denominator(Planned plan)
        {
            if (plan.Den is double d && d > 0)
            {
                int? std = LayoutRules.MatchingStandard(d);
                return std ?? Math.Round(d, 2);
            }

            return null;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            var baseWarnings = new List<string>(AcadUtil.UnitWarnings(db));

            Dictionary<string, object?> Build(CleanWrite.Mode m, List<string> warnings, Planned plan, string? anotacion)
            {
                Dictionary<string, object?> r = CleanWrite.Response(ctx.Pid, m, ins, warnings);
                r["handle"] = plan.Handle;
                r["layout"] = plan.Layout;
                r["antes"] = plan.Antes;
                r["despues"] = plan.Despues;
                r["escala_anotacion"] = anotacion;
                r["solapa_con"] = plan.Solapa;
                return r;
            }

            var preWarnings = new List<string>(baseWarnings);
            Planned pre;
            string? anotacionPre = null;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                pre = Plan(db, tr, p, preWarnings);
                if (pre.Geo != null)
                {
                    double? den = Denominator(pre);
                    (anotacionPre, bool crear) = ViewportPaper.PlanAnnotationScale(db, pre.Geo.CustomScale, den, p.CrearEscala);
                    if (anotacionPre == null && !p.CrearEscala)
                    {
                        preWarnings.Add("El dibujo no tiene una escala de anotación equivalente y crear_escala_anotacion es false: el viewport conservará su escala de anotación.");
                    }
                    else if (crear && anotacionPre == null)
                    {
                        preWarnings.Add("No se podrá asignar escala de anotación: la lista de escalas del dibujo no está disponible o no hay nombre libre para crearla.");
                    }
                    else if (crear)
                    {
                        preWarnings.Add("El dibujo no tiene una escala de anotación equivalente; al confirmar, si sigue sin existir, se creará '" + anotacionPre + "' y se asignará al viewport.");
                    }
                }

                if (pre.Capa != null && Draw.DrawLayerFactory.IsMcpName(pre.Capa))
                {
                    preWarnings.Add(CrearViewportHandler.McpLayerWarning(pre.Capa));
                }

                tr.Commit();
            }

            if (mode == CleanWrite.Mode.Preview)
            {
                return Build(mode, preWarnings, pre, anotacionPre);
            }

            return UndoGroup.Run(doc, d =>
            {
                var warnings = new List<string>(baseWarnings);
                using (Transaction tr = d.Database.TransactionManager.StartTransaction())
                {
                    Planned plan = Plan(d.Database, tr, p, warnings);
                    (Viewport vpRead, Layout _) = Resolve(d.Database, tr, p.Handle);
                    var vp = (Viewport)tr.GetObject(vpRead.ObjectId, OpenMode.ForWrite);
                    string? anotacion = null;

                    if (p.Capa != null)
                    {
                        // Plan ya añadió las advertencias de la capa (Check); se descartan las repetidas de Ensure.
                        Draw.DrawLayerFactory.Result layer = Draw.DrawLayerFactory.Ensure(d.Database, tr, p.Capa, null, new List<string>());
                        vp.LayerId = layer.Id;
                        if (Draw.DrawLayerFactory.IsMcpName(layer.Nombre))
                        {
                            warnings.Add(CrearViewportHandler.McpLayerWarning(layer.Nombre));
                        }
                    }

                    // El bloqueo de escala solo impide el zoom interactivo; se quita mientras se ajusta y se fija al final.
                    vp.Locked = false;
                    if (plan.Geo != null)
                    {
                        ViewportPaper.Apply(vp, plan.Geo, vp.ViewTarget.Z, vp.CenterPoint.Z);
                        anotacion = ViewportPaper.EnsureAnnotationScale(
                            vp, d.Database, plan.Geo.CustomScale, Denominator(plan), warnings, p.CrearEscala);
                    }
                    else if (p.PaperX != null)
                    {
                        vp.CenterPoint = new Point3d(plan.PaperX, plan.PaperY, vp.CenterPoint.Z);
                    }

                    vp.Locked = plan.Locked;
                    tr.Commit();
                    return Build(CleanWrite.Mode.Write, warnings, plan, anotacion);
                }
            });
        }
    }
}
