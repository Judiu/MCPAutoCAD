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
using MCPAutoCAD.Plugin.Handlers.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// listar_layouts (plugin 0.9.0, fase M1). Solo lectura: por layout de papel, su orden, configuración de impresión
    /// y viewports (handle, centro y tamaño en papel, ventana de modelo, escala, bloqueo, capas congeladas en el viewport).
    /// La escala "1:N" solo se informa si INSUNITS del dibujo tiene equivalencia métrica (con INSUNITS = 0 → null).
    /// </summary>
    internal sealed class ListarLayoutsHandler : IToolHandler
    {
        private const int MaxFrozenListed = 50;
        private static readonly string[] Allowed = { "incluir_capas_congeladas" };

        public string Name => "listar_layouts";

        public string Description =>
            "Lista los layouts (pestañas de papel) del dibujo activo en su orden: impresora, papel, tamaño en mm, estilo de " +
            "impresión, rotación y sus viewports (handle, centro y tamaño en papel, ventana de modelo [[xmin,ymin],[xmax,ymax]], " +
            "escala 1:N si las unidades del dibujo y la escala de impresión del layout lo permiten, bloqueo y capas congeladas en " +
            "el viewport; 'encendido' solo se informa en el layout actual, en los demás es null). Solo lectura.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""incluir_capas_congeladas"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Incluir las capas congeladas en cada viewport (máx. 50 por viewport)."" }
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""layout_actual"": { ""type"": [""string"", ""null""] },
    ""layouts"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""description"": ""nombre, orden, handle, impresora, papel, tamano_papel_mm, unidades_papel, rotacion, estilo_impresion, escala_impresion, entidades_papel, pagina (configuración completa: la de los campos sueltos más centrado, area_impresion, desfase_mm y margenes_mm), viewports (encendido es null fuera del layout actual)."" } },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""layout_actual"", ""layouts"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters)
        {
            ParamReader.RequireObject(parameters, Allowed);
            ParamReader.OptionalBool(parameters, "incluir_capas_congeladas");
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            bool includeFrozen = ParamReader.OptionalBool(parameters, "incluir_capas_congeladas") ?? true;
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = AcadUtil.InsUnits(db);
            double? modelMeters = ins == 0 ? null : UnitsScale.MetersPerUnit(ins);
            var warnings = new List<string>(AcadUtil.UnitWarnings(db));
            if (modelMeters == null)
            {
                warnings.Add("El dibujo no declara unidades con equivalencia métrica (INSUNITS = " + ins + "): la escala 1:N de los viewports no se puede calcular; se informa solo factor_escala (unidades de papel por unidad de modelo).");
            }

            string? current = null;
            try
            {
                if (AcadUtil.GetIntSysVar("TILEMODE") == 0)
                {
                    current = AcadUtil.GetStringSysVar("CTAB");
                }
            }
            catch (Exception)
            {
                current = null;
            }

            var rows = new List<(int order, Dictionary<string, object?> row)>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout) || layout.ModelType)
                    {
                        continue;
                    }

                    rows.Add((layout.TabOrder, Describe(layout, tr, modelMeters, includeFrozen, current, warnings)));
                }

                tr.Commit();
            }

            rows.Sort((a, b) => a.order.CompareTo(b.order));
            var layouts = new List<Dictionary<string, object?>>();
            foreach (var r in rows)
            {
                layouts.Add(r.row);
            }

            return new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["layout_actual"] = current,
                ["layouts"] = layouts,
                ["origen_texto"] = "contenido_del_plano",
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = warnings,
            };
        }

        /// <summary>mm por unidad de papel del layout (1 en mm, 25,4 en pulgadas; píxeles → null).</summary>
        internal static double? PaperMmPerUnit(PlotSettings layout) => layout.PlotPaperUnits switch
        {
            PlotPaperUnit.Millimeters => 1.0,
            PlotPaperUnit.Inches => 25.4,
            _ => null,
        };

        /// <summary>
        /// mm reales de papel por unidad de espacio papel según la escala de impresión del layout, y su descripción
        /// ("1:1", "1:2", "ajustada al papel"). null si imprime ajustada al papel o en píxeles. [Probable] StdScale es la
        /// razón papel/dibujo de la escala estándar; se verifica en vivo.
        /// </summary>
        internal static (double? mm, string descripcion) PlotScale(PlotSettings layout)
        {
            double? unit = PaperMmPerUnit(layout);
            try
            {
                if (layout.UseStandardScale)
                {
                    if (layout.StdScaleType == StdScaleType.ScaleToFit)
                    {
                        return (null, "ajustada al papel");
                    }

                    double r = layout.StdScale;
                    return (LayoutRules.PaperMmPerSpaceUnit(unit, r, 1, false), DescribeRatio(r, 1));
                }

                CustomScale cs = layout.CustomPrintScale;
                return (LayoutRules.PaperMmPerSpaceUnit(unit, cs.Numerator, cs.Denominator, false), DescribeRatio(cs.Numerator, cs.Denominator));
            }
            catch (Exception)
            {
                return (null, "desconocida");
            }
        }

        private static string DescribeRatio(double paper, double drawing)
        {
            if (!(paper > 0) || !(drawing > 0) || !double.IsFinite(paper) || !double.IsFinite(drawing))
            {
                return "desconocida";
            }

            double r = drawing / paper;
            if (Math.Abs(r - 0.001) < 1e-12)
            {
                return "metros (1 mm = 0,001 unidades)";
            }

            return Math.Abs(r - Math.Round(r)) < 1e-9
                ? "1:" + Math.Round(r).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : paper.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ":"
                    + drawing.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, object?> Describe(
            Layout layout, Transaction tr, double? modelMeters, bool includeFrozen, string? current, List<string> warnings)
        {
            bool isCurrent = current != null && string.Equals(layout.LayoutName, current, StringComparison.OrdinalIgnoreCase);
            (double? paperMm, string escalaImpresion) = PlotScale(layout);
            if (layout.PlotPaperUnits == PlotPaperUnit.Pixels)
            {
                warnings.Add("Layout '" + layout.LayoutName + "': unidades de papel en píxeles; la escala 1:N de sus viewports no se puede calcular.");
            }
            else if (paperMm == null)
            {
                warnings.Add("Layout '" + layout.LayoutName + "': escala de impresión " + escalaImpresion + "; la escala 1:N de sus viewports no se puede calcular ni sale exacta al imprimir (configurar_layout: area_impresion 'presentacion' y escala_impresion '1:1' o 'metros').");
            }

            if (layout.PlotType == PlotType.Window)
            {
                warnings.Add("Layout '" + layout.LayoutName + "': imprime una ventana, no la hoja del layout; la posición y la escala dependen de esa ventana.");
            }

            var vps = new List<Dictionary<string, object?>>();
            int entities = 0;
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            ObjectId paperId = LayoutViewports.PaperViewportId(tr, btr);
            foreach (ObjectId id in btr)
            {
                if (id.IsErased)
                {
                    continue;
                }

                entities++;
                if (id == paperId || !(tr.GetObject(id, OpenMode.ForRead) is Viewport vp))
                {
                    continue;
                }

                vps.Add(DescribeViewport(vp, tr, paperMm, modelMeters, includeFrozen, isCurrent));
            }

            Point2d size = layout.PlotPaperSize; // siempre en mm
            bool rotated = layout.PlotRotation == PlotRotation.Degrees090 || layout.PlotRotation == PlotRotation.Degrees270;
            return new Dictionary<string, object?>
            {
                ["nombre"] = layout.LayoutName,
                ["orden"] = layout.TabOrder,
                ["handle"] = AcadUtil.HandleToString(layout.Handle),
                ["impresora"] = layout.PlotConfigurationName,
                ["papel"] = layout.CanonicalMediaName,
                ["tamano_papel_mm"] = new[] { AcadUtil.NumberOrNull(rotated ? size.Y : size.X), AcadUtil.NumberOrNull(rotated ? size.X : size.Y) },
                ["unidades_papel"] = layout.PlotPaperUnits switch
                {
                    PlotPaperUnit.Millimeters => "mm",
                    PlotPaperUnit.Inches => "pulgadas",
                    _ => "pixeles",
                },
                ["rotacion"] = layout.PlotRotation switch
                {
                    PlotRotation.Degrees090 => 90,
                    PlotRotation.Degrees180 => 180,
                    PlotRotation.Degrees270 => 270,
                    _ => 0,
                },
                ["estilo_impresion"] = layout.CurrentStyleSheet,
                ["escala_impresion"] = escalaImpresion,
                ["pagina"] = LayoutWrite.DescribePage(layout),
                ["entidades_papel"] = entities,
                ["viewports"] = vps,
            };
        }

        private static Dictionary<string, object?> DescribeViewport(
            Viewport vp, Transaction tr, double? paperMm, double? modelMeters, bool includeFrozen, bool isCurrentLayout)
        {
            double[][]? window = null;
            Vector3d dir = vp.ViewDirection;
            double len = dir.Length;
            bool plan = len > 0 && dir.Z > 0 && Math.Abs(dir.X / len) <= 1e-6 && Math.Abs(dir.Y / len) <= 1e-6;
            if (plan)
            {
                try
                {
                    BBox2 w = ViewportWindow.FromViewportTarget(
                        vp.ViewTarget.X, vp.ViewTarget.Y, vp.ViewCenter.X, vp.ViewCenter.Y, vp.ViewHeight, vp.Width, vp.Height, vp.TwistAngle);
                    window = w.ToArray2D();
                }
                catch (Exception)
                {
                    window = null;
                }
            }

            double custom = vp.CustomScale;
            double? den = paperMm == null ? null : LayoutRules.Denominator(custom, paperMm.Value, modelMeters);
            int? std = den == null ? null : LayoutRules.MatchingStandard(den.Value);
            var frozen = new List<string>();
            int frozenTotal = 0;
            if (includeFrozen)
            {
                ObjectIdCollection? ids;
                try
                {
                    ids = vp.GetFrozenLayers();
                }
                catch (Exception)
                {
                    ids = null; // lista no disponible: se informa vacía
                }

                if (ids != null)
                {
                    foreach (ObjectId lid in ids)
                    {
                        if (lid.IsNull || lid.IsErased)
                        {
                            continue;
                        }

                        frozenTotal++;
                        if (frozen.Count >= MaxFrozenListed)
                        {
                            continue;
                        }

                        try
                        {
                            if (tr.GetObject(lid, OpenMode.ForRead) is LayerTableRecord ltr)
                            {
                                frozen.Add(ltr.Name);
                            }
                        }
                        catch (Exception)
                        {
                            // Capa ilegible: cuenta en el total pero no se nombra.
                        }
                    }
                }
            }

            return new Dictionary<string, object?>
            {
                ["handle"] = AcadUtil.HandleToString(vp.Handle),
                ["capa"] = vp.Layer,
                ["centro_papel"] = new[] { AcadUtil.NumberOrNull(vp.CenterPoint.X), AcadUtil.NumberOrNull(vp.CenterPoint.Y) },
                ["ancho_papel"] = AcadUtil.NumberOrNull(vp.Width),
                ["alto_papel"] = AcadUtil.NumberOrNull(vp.Height),
                ["vista_en_planta"] = plan,
                ["ventana_modelo"] = window,
                ["factor_escala"] = AcadUtil.NumberOrNull(custom),
                ["escala"] = den == null ? null : LayoutRules.ScaleLabel(std ?? Math.Round(den.Value, 2)),
                ["escala_estandar"] = std != null,
                ["bloqueado"] = vp.Locked,
                ["encendido"] = isCurrentLayout ? vp.On : null, // fuera del layout actual AutoCAD informa On = false
                ["capas_congeladas"] = frozen,
                ["capas_congeladas_total"] = frozenTotal,
            };
        }
    }
}
