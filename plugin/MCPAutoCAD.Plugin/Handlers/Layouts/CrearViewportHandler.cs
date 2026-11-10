using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Layouts;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// crear_viewport (plugin 0.9.0, fase M2). Crea en un layout de papel un viewport rectangular en planta, sin giro, que
    /// muestra exactamente 'ventana_modelo' a la escala 1:N pedida, centrado en 'centro_papel'. Sin confirmar: true solo
    /// previsualiza (tamaño en papel y comprobaciones). Con confirmar: true lo crea en un paso de deshacer. No toca el
    /// espacio modelo ni cambia la pestaña activa.
    /// </summary>
    internal sealed class CrearViewportHandler : IToolHandler
    {
        private static readonly string[] Allowed =
        {
            "layout", "ventana_modelo", "escala", "centro_papel", "capa", "bloquear", "crear_escala_anotacion", "unidades_modelo", "confirmar", "preview",
        };

        public string Name => "crear_viewport";

        public string Description =>
            "Crea en un layout de papel un viewport en planta que muestra exactamente la zona de modelo 'ventana_modelo' a escala " +
            "'1:N', centrado en 'centro_papel' (unidades del espacio papel, normalmente mm). El tamaño en papel sale de la ventana y la escala. Por defecto va en la " +
            "capa del concepto 'viewports' de lps.json (o MCP-DIBUJO si no existe) y queda bloqueado. Sin confirmar: true solo previsualiza; con confirmar: true lo crea " +
            "en un paso de deshacer, con la escala de anotación equivalente (la crea como 'LPS 1:N' si el dibujo no la tiene). El layout debe haberse activado alguna vez. Con INSUNITS = 0 indique unidades_modelo.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""layout"": " + LayoutWrite.SchemaLayout + @",
    ""ventana_modelo"": " + LayoutWrite.SchemaVentanaModelo + @",
    ""escala"": " + LayoutWrite.SchemaEscala + @",
    ""centro_papel"": " + LayoutWrite.SchemaCentroPapel + @",
    ""capa"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Capa del viewport: una capa existente, un concepto de lps.json o una capa del estándar (se crea si falta). Por defecto el concepto 'viewports' de lps.json si existe; si no, MCP-DIBUJO (con advertencia)."" },
    ""bloquear"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Bloquear la escala del viewport (evita cambiarla con zoom dentro de él)."" },
    ""crear_escala_anotacion"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Si el dibujo no tiene una escala de anotación equivalente, crearla ('LPS 1:N') en la lista de escalas. false = no tocar la lista; solo se avisa."" },
    ""unidades_modelo"": " + LayoutWrite.SchemaUnidadesModelo + @",
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""layout"", ""ventana_modelo"", ""escala"", ""centro_papel""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""layout"": { ""type"": ""string"" },
    ""handle"": { ""type"": [""string"", ""null""], ""description"": ""Handle del viewport creado (null en previsualización)."" },
    ""capa"": { ""type"": ""string"" },
    ""centro_papel"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""ancho_papel"": { ""type"": ""number"" },
    ""alto_papel"": { ""type"": ""number"" },
    ""caja_papel"": { ""type"": ""array"" },
    ""ventana_modelo"": { ""type"": ""array"" },
    ""escala"": { ""type"": ""string"" },
    ""factor_escala"": { ""type"": ""number"" },
    ""escala_anotacion"": { ""type"": [""string"", ""null""], ""description"": ""Escala de anotación asignada al viewport (en previsualización, la que se asignará; si el dibujo no tiene una equivalente se crea 'LPS 1:N'). null si no se pudo asignar."" },
    ""bloqueado"": { ""type"": ""boolean"" },
    ""solapa_con"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Handles de viewports existentes del layout cuyo rectángulo se cruza con el nuevo."" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""layout"", ""handle"", ""capa"", ""centro_papel"", ""ancho_papel"", ""alto_papel"", ""caja_papel"", ""ventana_modelo"", ""escala"", ""factor_escala"", ""escala_anotacion"", ""bloqueado"", ""solapa_con""]
}");

        private sealed class Params
        {
            public string Layout = "";
            public BBox2 Ventana;
            public double Denominador;
            public double PaperX;
            public double PaperY;
            public string? Capa;
            public bool Bloquear;
            public bool CrearEscala = true;
            public string? UnidadesModelo;
            public CleanWrite.Common Common = new CleanWrite.Common(false, null);
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var r = new Params
            {
                Layout = LayoutWrite.ReadLayoutName(p, "layout"),
                Ventana = ParamReader.OptionalBbox(p, "ventana_modelo") ?? throw ToolException.InvalidParams("ventana_modelo: obligatorio"),
                Denominador = LayoutWrite.ReadScale(p) ?? throw ToolException.InvalidParams("escala: obligatorio"),
                Capa = ParamReader.OptionalString(p, "capa", 1, 255),
                Bloquear = ParamReader.OptionalBool(p, "bloquear") ?? true,
                CrearEscala = ParamReader.OptionalBool(p, "crear_escala_anotacion") ?? true,
                UnidadesModelo = LayoutWrite.ReadModelUnits(p),
                Common = CleanWrite.ReadCommon(p),
            };
            Point3 c = ParamReader.RequiredPoint(p, "centro_papel");
            r.PaperX = c.X;
            r.PaperY = c.Y;
            if (!(r.Ventana.Width > 0) || !(r.Ventana.Height > 0))
            {
                throw ToolException.InvalidParams("ventana_modelo: debe tener ancho y alto mayores que 0");
            }

            DrawLayerFactory.Plan(r.Capa, LayoutWrite.ViewportConcept);
            return r;
        }

        /// <summary>
        /// Plan completo (lectura): geometría, viewports solapados y advertencias de papel. Rechaza un layout sin viewport
        /// general de papel (nunca activado): el viewport nuevo se confundiría con él (PaperViewportId toma el primero del
        /// registro) y al activarlo AutoCAD puede añadir otro por LAYOUTCREATEVIEWPORT.
        /// </summary>
        private static (string layout, ViewportGeometry geo, List<string> solapa) Plan(Database db, Transaction tr, Params p, List<string> warnings)
        {
            LayoutWrite.Found found = LayoutWrite.FindLayout(db, tr, p.Layout);
            var btr = (BlockTableRecord)tr.GetObject(found.Layout.BlockTableRecordId, OpenMode.ForRead);
            if (Audit.LayoutViewports.PaperViewportId(tr, btr).IsNull)
            {
                throw ToolException.InvalidParams(
                    "layout: '" + found.Name + "' no se ha activado nunca (no tiene viewport de papel); haga clic una vez en su pestaña y vuelva a llamar");
            }

            double modelMeters = LayoutWrite.ModelMeters(db, p.UnidadesModelo);
            double paperMm = LayoutWrite.PaperMm(found.Layout);
            ViewportGeometry geo = LayoutWrite.Checked(() =>
                ViewportPlanner.FromWindow(p.Ventana, p.Denominador, paperMm, modelMeters, p.PaperX, p.PaperY));
            List<string> solapa = ViewportPaper.Overlapping(tr, found.Layout, geo.PaperBox, null);
            ViewportPaper.CheckOnPaper(found.Layout, geo.PaperBox, warnings);
            if (solapa.Count > 0)
            {
                warnings.Add("El viewport se cruza en el papel con " + solapa.Count + " viewport(s) existente(s): " + string.Join(", ", solapa) + ".");
            }

            if (!LayoutWrite.IsCurrent(found.Name, LayoutWrite.CurrentTab()))
            {
                warnings.Add("'" + found.Name + "' no es el layout activo: al activarlo compruebe que el viewport está encendido (MVIEW > ON si no se ve).");
            }

            return (found.Name, geo, solapa);
        }

        internal static string McpLayerWarning(string capa) =>
            "El viewport va en la capa del puente '" + capa + "' (lps.json no define el concepto 'viewports'): las capas MCP-* se " +
            "tratan como marcas del puente. Indique 'capa' con una capa del plano o añada 'viewports' al estándar.";

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            var baseWarnings = new List<string>(AcadUtil.UnitWarnings(db));
            string? capaPedida = p.Capa;

            Dictionary<string, object?> Build(
                CleanWrite.Mode m, List<string> warnings, string layout, ViewportGeometry geo, List<string> solapa, string? handle, string capa, string? anotacion)
            {
                Dictionary<string, object?> r = CleanWrite.Response(ctx.Pid, m, ins, warnings);
                r["layout"] = layout;
                r["handle"] = handle;
                r["capa"] = capa;
                r["centro_papel"] = new[] { geo.PaperX, geo.PaperY };
                r["ancho_papel"] = geo.Width;
                r["alto_papel"] = geo.Height;
                r["caja_papel"] = geo.PaperBox.ToArray2D();
                r["ventana_modelo"] = geo.ModelWindow.ToArray2D();
                r["escala"] = LayoutRules.ScaleLabel(p.Denominador);
                r["factor_escala"] = geo.CustomScale;
                r["escala_anotacion"] = anotacion;
                r["bloqueado"] = p.Bloquear;
                r["solapa_con"] = solapa;
                return r;
            }

            // Lectura previa (también en modo escritura: un rechazo no deja un paso de deshacer vacío).
            var preWarnings = new List<string>(baseWarnings);
            (string layout, ViewportGeometry geo, List<string> solapa) pre;
            string capaPlan;
            string? anotacionPlan;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                pre = Plan(db, tr, p, preWarnings);
                capaPlan = DrawLayerFactory.Check(db, tr, capaPedida, LayoutWrite.ViewportConcept, out bool _, preWarnings).Nombre;
                (anotacionPlan, bool crearAnotacion) = ViewportPaper.PlanAnnotationScale(db, pre.geo.CustomScale, p.Denominador, p.CrearEscala);
                if (anotacionPlan == null && !p.CrearEscala)
                {
                    preWarnings.Add("El dibujo no tiene una escala de anotación equivalente a " + LayoutRules.ScaleLabel(p.Denominador)
                        + " y crear_escala_anotacion es false: el viewport conservará la escala de anotación por defecto.");
                }
                else if (crearAnotacion && anotacionPlan == null)
                {
                    preWarnings.Add("No se podrá asignar escala de anotación: la lista de escalas del dibujo no está disponible o no hay nombre libre para crearla.");
                }
                else if (crearAnotacion)
                {
                    preWarnings.Add("El dibujo no tiene una escala de anotación equivalente a " + LayoutRules.ScaleLabel(p.Denominador)
                        + " con estas unidades; al confirmar, si sigue sin existir, se creará '" + anotacionPlan + "' y se asignará al viewport.");
                }
                tr.Commit();
            }

            if (mode == CleanWrite.Mode.Preview)
            {
                if (DrawLayerFactory.IsMcpName(capaPlan))
                {
                    preWarnings.Add(McpLayerWarning(capaPlan));
                }

                return Build(mode, preWarnings, pre.layout, pre.geo, pre.solapa, null, capaPlan, anotacionPlan);
            }

            return UndoGroup.Run(doc, d =>
            {
                Database wdb = d.Database;
                var warnings = new List<string>(baseWarnings);
                using (Transaction tr = wdb.TransactionManager.StartTransaction())
                {
                    // Se recalcula todo dentro del paso de deshacer (el dibujo pudo cambiar desde la lectura previa).
                    (string layout, ViewportGeometry geo, List<string> solapa) plan = Plan(wdb, tr, p, warnings);
                    LayoutWrite.Found found = LayoutWrite.FindLayout(wdb, tr, plan.layout);
                    DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(wdb, tr, capaPedida, LayoutWrite.ViewportConcept, warnings);
                    var btr = (BlockTableRecord)tr.GetObject(found.Layout.BlockTableRecordId, OpenMode.ForWrite);
                    var vp = new Viewport();
                    DrawWrite.ApplyByLayer(vp, wdb, layer.Id);
                    string handle = DrawWrite.Append(btr, tr, vp);
                    ViewportPaper.Apply(vp, plan.geo, 0, 0);
                    string? anotacion = ViewportPaper.EnsureAnnotationScale(vp, wdb, plan.geo.CustomScale, p.Denominador, warnings, p.CrearEscala);

                    vp.Locked = p.Bloquear;
                    try
                    {
                        vp.On = true;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        warnings.Add("No se pudo encender el viewport desde fuera de su layout; enciéndalo con MVIEW > ON al activar '" + found.Name + "'.");
                    }

                    if (DrawLayerFactory.IsMcpName(layer.Nombre))
                    {
                        warnings.Add(McpLayerWarning(layer.Nombre));
                    }

                    tr.Commit();
                    return Build(CleanWrite.Mode.Write, warnings, found.Name, plan.geo, plan.solapa, handle, layer.Nombre, anotacion);
                }
            });
        }
    }

    /// <summary>Utilidades de papel compartidas por crear_viewport y ajustar_viewport (hilo principal).</summary>
    internal static class ViewportPaper
    {
        /// <summary>Handles de viewports (sin el de papel ni <paramref name="except"/>) cuyo rectángulo cruza <paramref name="box"/>.</summary>
        public static List<string> Overlapping(Transaction tr, Layout layout, BBox2 box, ObjectId? except)
        {
            var list = new List<string>();
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            ObjectId paper = Audit.LayoutViewports.PaperViewportId(tr, btr);
            foreach (ObjectId id in btr)
            {
                if (id.IsErased || id == paper || (except != null && id == except.Value) || id.ObjectClass.DxfName != "VIEWPORT")
                {
                    continue;
                }

                if (tr.GetObject(id, OpenMode.ForRead) is Viewport other)
                {
                    try
                    {
                        BBox2 ob = BBox2.FromCorners(
                            other.CenterPoint.X - other.Width / 2, other.CenterPoint.Y - other.Height / 2,
                            other.CenterPoint.X + other.Width / 2, other.CenterPoint.Y + other.Height / 2);
                        if (ViewportPlanner.Overlaps(ob, box))
                        {
                            list.Add(AcadUtil.HandleToString(other.Handle));
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Viewport con medidas no finitas: no se compara.
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// Advertencia si la caja se sale de los límites del papel del layout (Layout.Limits, LIMMIN/LIMMAX del espacio
        /// papel). [Probable] AutoCAD los ajusta a la hoja al aplicar la configuración de página; por eso solo se avisa.
        /// </summary>
        public static void CheckOnPaper(Layout layout, BBox2 box, List<string> warnings)
        {
            try
            {
                Extents2d lim = layout.Limits;
                if (lim.MaxPoint.X - lim.MinPoint.X <= 0 || lim.MaxPoint.Y - lim.MinPoint.Y <= 0)
                {
                    return;
                }

                const double tol = 1e-6;
                if (box.Xmin < lim.MinPoint.X - tol || box.Ymin < lim.MinPoint.Y - tol || box.Xmax > lim.MaxPoint.X + tol || box.Ymax > lim.MaxPoint.Y + tol)
                {
                    warnings.Add(
                        "El viewport " + Fmt(box) + " se sale de los límites del papel del layout '" + layout.LayoutName + "' "
                        + Fmt(lim.MinPoint.X, lim.MinPoint.Y, lim.MaxPoint.X, lim.MaxPoint.Y) + ": revise centro_papel, la escala o el tamaño de papel.");
                }
            }
            catch (Exception)
            {
                // Límites ilegibles: sin comprobación.
            }
        }

        /// <summary>
        /// Aplica la geometría: planta (dirección Z), sin giro, tamaño, centro y escala. El objetivo pasa a (0, 0,
        /// <paramref name="targetZ"/>) y ViewCenter es el centro de modelo en WCS (con objetivo en el origen XY y sin giro,
        /// DCS = WCS desplazado); se conservan la Z del objetivo y la del centro en papel.
        /// </summary>
        public static void Apply(Viewport vp, ViewportGeometry geo, double targetZ, double centerZ)
        {
            vp.ViewDirection = Vector3d.ZAxis;
            vp.ViewTarget = new Point3d(0, 0, targetZ);
            vp.TwistAngle = 0;
            vp.CenterPoint = new Point3d(geo.PaperX, geo.PaperY, centerZ);
            vp.Width = geo.Width;
            vp.Height = geo.Height;
            vp.ViewCenter = new Point2d(geo.ModelX, geo.ModelY);
            vp.CustomScale = geo.CustomScale;
        }

        /// <summary>
        /// Escala de anotación del dibujo (ACDB_ANNOTATIONSCALES) equivalente a <paramref name="customScale"/> (unidades de
        /// papel por unidad de dibujo = PaperUnits / DrawingUnits, tolerancia relativa 1e-6), o null. Los contextos que no
        /// coinciden se liberan; el devuelto lo libera quien llama (using).
        /// </summary>
        public static AnnotationScale? FindAnnotationScale(Database db, double customScale)
        {
            AnnotationScale? found = null;
            try
            {
                ObjectContextCollection? scales = db.ObjectContextManager?.GetContextCollection("ACDB_ANNOTATIONSCALES");
                if (scales == null)
                {
                    return null;
                }

                foreach (ObjectContext oc in scales)
                {
                    if (found == null && oc is AnnotationScale sc && sc.DrawingUnits > 0 && sc.PaperUnits > 0
                        && Math.Abs(sc.PaperUnits / sc.DrawingUnits - customScale) <= customScale * 1e-6)
                    {
                        found = sc;
                        continue;
                    }

                    oc.Dispose();
                }
            }
            catch (Exception)
            {
                // Lista de escalas ilegible: sin escala de anotación.
                found?.Dispose();
                return null;
            }

            return found;
        }

        /// <summary>Nombre de la escala existente equivalente, o null.</summary>
        private static string? ExistingName(Database db, double customScale)
        {
            using (AnnotationScale? sc = FindAnnotationScale(db, customScale))
            {
                return sc?.Name;
            }
        }

        /// <summary>
        /// Nombre libre para la escala nueva: "LPS 1:N" si se conoce el denominador, "LPS factor F" si no; con sufijo
        /// " (i)" si ya existe ese nombre con otra razón. null si la lista de escalas no está disponible.
        /// </summary>
        private static string? FreeName(Database db, double? denominator, double customScale)
        {
            ObjectContextCollection? scales = db.ObjectContextManager?.GetContextCollection("ACDB_ANNOTATIONSCALES");
            if (scales == null)
            {
                return null;
            }

            string baseName = denominator is double n && n > 0 ? "LPS " + LayoutRules.ScaleLabel(n) : "LPS factor " + Num(customScale);
            string name = baseName;
            for (int i = 2; scales.HasContext(name); i++)
            {
                if (i > 99)
                {
                    return null;
                }

                name = baseName + " (" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            }

            return name;
        }

        /// <summary>
        /// Previsualización: (nombre, crear). Nombre de la escala existente que se asignará, o el que tendrá la que se cree
        /// (el mismo cálculo que la escritura). null si la lista de escalas no está disponible.
        /// </summary>
        public static (string? nombre, bool crear) PlanAnnotationScale(Database db, double customScale, double? denominator, bool permitirCrear = true)
        {
            try
            {
                string? existing = ExistingName(db, customScale);
                return existing != null ? (existing, false) : permitirCrear ? (FreeName(db, denominator, customScale), true) : (null, false);
            }
            catch (Exception)
            {
                return (null, false);
            }
        }

        /// <summary>
        /// Asigna al viewport la escala de anotación equivalente a <paramref name="customScale"/>; si el dibujo no tiene
        /// ninguna, la crea (razón legible: F:1 si el factor es ≥ 1, 1:1/F si no) dentro de la escritura en curso (el mismo
        /// paso de deshacer). Devuelve el nombre asignado o null, y en ese caso añade a <paramref name="warnings"/> el motivo
        /// real (no se pudo crear o no se pudo asignar).
        /// </summary>
        public static string? EnsureAnnotationScale(Viewport vp, Database db, double customScale, double? denominator, List<string> warnings, bool permitirCrear = true)
        {
            bool created = false;
            if (ExistingName(db, customScale) == null)
            {
                if (!permitirCrear)
                {
                    warnings.Add(AnnotationWarning(denominator, customScale, "el dibujo no tiene una equivalente y crear_escala_anotacion es false"));
                    return null;
                }

                try
                {
                    ObjectContextCollection? scales = db.ObjectContextManager?.GetContextCollection("ACDB_ANNOTATIONSCALES");
                    string? name = FreeName(db, denominator, customScale);
                    if (scales == null || name == null)
                    {
                        warnings.Add(AnnotationWarning(denominator, customScale, "la lista de escalas de anotación del dibujo no está disponible o no hay nombre libre"));
                        return null;
                    }

                    using (var nuevo = new AnnotationScale())
                    {
                        nuevo.Name = name;
                        nuevo.PaperUnits = customScale >= 1 ? customScale : 1.0;
                        nuevo.DrawingUnits = customScale >= 1 ? 1.0 : 1.0 / customScale;
                        scales.AddContext(nuevo);
                    }

                    created = true;
                }
                catch (Exception ex)
                {
                    warnings.Add(AnnotationWarning(denominator, customScale, "no se pudo crear la escala (" + ex.Message + ")"));
                    return null;
                }
            }

            using (AnnotationScale? sc = FindAnnotationScale(db, customScale))
            {
                if (sc == null)
                {
                    warnings.Add(AnnotationWarning(denominator, customScale, created ? "AutoCAD no registró la escala creada" : "la lista de escalas no se pudo releer"));
                    return null;
                }

                try
                {
                    vp.AnnotationScale = sc;
                }
                catch (Exception ex)
                {
                    warnings.Add(AnnotationWarning(denominator, customScale, "la escala '" + sc.Name + "' existe pero no se pudo asignar al viewport (" + ex.Message + ")"));
                    return null;
                }

                if (created)
                {
                    warnings.Add("Se creó la escala de anotación '" + sc.Name + "' (" + Num(sc.PaperUnits) + " unidad(es) de papel = "
                        + Num(sc.DrawingUnits) + " unidad(es) de dibujo) y se asignó al viewport.");
                }

                return sc.Name;
            }
        }

        private static string AnnotationWarning(double? denominator, double customScale, string motivo) =>
            "Escala de anotación no asignada" + (denominator is double n && n > 0 ? " para " + LayoutRules.ScaleLabel(n) : "") + " (factor " + Num(customScale)
            + " unidades de papel por unidad de dibujo): " + motivo + ". Los textos o cotas anotativos pueden verse a otro tamaño.";

        private static string Num(double v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        private static string Fmt(BBox2 b) => Fmt(b.Xmin, b.Ymin, b.Xmax, b.Ymax);

        private static string Fmt(double a, double b, double c, double d) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "[[{0:0.##}, {1:0.##}], [{2:0.##}, {3:0.##}]]", a, b, c, d);
    }
}
