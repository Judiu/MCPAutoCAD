using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// vista_png (protocol.md §5B.7, 0.3.2). Hilo principal; requiere documento; solo lectura: la vista del usuario no
    /// se toca, CTAB/TILEMODE/CLAYER no cambian y no se crea ningún paso de deshacer (§3.9, F7). Solo captura el espacio
    /// ACTIVO: un 'espacio' distinto → INVALID_PARAMS antes de plotear (F5 de 0.3.2: AutoCAD solo plotea el layout
    /// actual, eLayoutNotCurrent, y no se cambia de pestaña a espaldas del usuario). Con bbox siempre plot.
    /// Gate en vivo de Fase 3: el trazado dejaba UN paso "_u" pese a Database.DisableUndoRecording; desde 0.4 TODO
    /// trazado (bbox, mecanismo 'plot' y respaldo de 'auto') va en <see cref="UndoGroup.RunNeutral"/> (contexto de
    /// comando, BE → plot → E → U) y la respuesta es diferida (Task&lt;object&gt;). La captura nativa sigue síncrona.
    /// Desde el gate en vivo de Fase 4A (2026-10-27): dentro del grupo, la capa MCP-PREVIEW (no imprimible) se hace
    /// imprimible solo durante el trazado para que la previsualización se vea (<see cref="PreviewPlotVisibility"/>).
    /// </summary>
    internal sealed class VistaPngHandler : IToolHandler
    {
        /// <summary>
        /// Mecanismo que "auto" prueba primero sin bbox y en el espacio activo (§5B.7); el respaldo es "plot". Desde 0.4 el
        /// orden captura → plot es fijo (el plot va en el grupo neutro, asíncrono); se conserva como constante informativa.
        /// </summary>
        public static readonly string DefaultMechanism = Captura;

        private const int DefaultWidth = 1024;
        private const int DefaultHeight = 768;
        private const int MinSize = 64;
        private const int MaxSize = 2048;
        private const string Captura = "captura";
        private const string Plot = "plot";
        private const string Auto = "auto";

        /// <summary>Nombre del layout del espacio modelo (§5B.7 campo 'layout').</summary>
        private const string ModelLayoutName = "Model";

        /// <summary>Texto exacto de §5B.7 (F5, 0.3.2) cuando 'espacio' no es el activo.</summary>
        internal static string NotActiveSpaceMessage(string activo, string pedido) =>
            "vista_png solo puede capturar el espacio activo (" + activo + "); el usuario debe cambiar a la pestaña '"
            + pedido + "' o se debe omitir 'espacio'.";

        private static readonly string[] Allowed = { "bbox", "ancho", "alto", "mecanismo", "espacio", "ajustar_aspecto", "recortar" };

        public string Name => "vista_png";

        // Texto exacto de §5B.7 "Descripciones del manifest" (0.3.2), ampliado con ajustar_aspecto y bbox_real (0.9).
        public string Description =>
            "Devuelve una imagen PNG del dibujo activo sin alterar la vista del usuario ni el historial de deshacer. Sin " +
            "bbox captura la vista actual en pantalla; con bbox [[xmin,ymin],[xmax,ymax]] (WCS) plotea esa zona (con " +
            "ajustar_aspecto: true, ampliada al aspecto ancho:alto; false, el bbox exacto). Solo captura el espacio activo " +
            "(la pestaña abierta por el usuario) y nunca cambia de pestaña: si 'espacio' indica otro, devuelve " +
            "INVALID_PARAMS. Con plot, la hoja del PC3 se recorta a la ventana trazada (recortar: true por defecto), así una " +
            "zona alargada no trae márgenes blancos. La respuesta incluye bbox_real (zona WCS que cubre la imagen) y unidades_por_pixel, para " +
            "pasar de píxel a coordenada. Úsela para revisar visualmente; los números se verifican con las demás herramientas.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        public string TimeoutHint(JsonElement parameters) => "Pida una vista de una zona más pequeña (bbox) o con menos píxeles (ancho y alto).";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""bbox"": {
      ""type"": ""array"",
      ""minItems"": 2,
      ""maxItems"": 2,
      ""items"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
      ""description"": ""Ventana a capturar en WCS: [[xmin, ymin], [xmax, ymax]] (z opcional e ignorado). Sin bbox se captura la vista actual.""
    },
    ""ancho"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 2048, ""default"": 1024, ""description"": ""Ancho de la imagen en píxeles."" },
    ""alto"": { ""type"": ""integer"", ""minimum"": 64, ""maximum"": 2048, ""default"": 768, ""description"": ""Alto de la imagen en píxeles."" },
    ""mecanismo"": { ""type"": ""string"", ""enum"": [""auto"", ""captura"", ""plot""], ""default"": ""auto"", ""description"": ""auto (defecto): sin bbox, captura nativa de la pantalla (si falla, plot); con bbox, siempre plot. captura: solo la API gráfica (sin bbox). plot: PublishToWeb PNG, tamaños fijos del PC3 (ancho/alto reales pueden diferir). Siempre sobre el espacio activo."" },
    ""espacio"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""'modelo' o nombre de un layout; debe ser el espacio activo (AutoCAD solo plotea el layout actual y la herramienta no cambia de pestaña). Si es otro, INVALID_PARAMS: pida al usuario que cambie a esa pestaña u omita 'espacio'. Defecto: espacio activo."" },
    ""ajustar_aspecto"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Solo con bbox. true (defecto): la ventana se amplía al aspecto ancho:alto pedido; la caja pedida nunca se recorta y puede haber márgenes. false: la ventana es exactamente el bbox y la hoja pedida tiene el aspecto del bbox (ancho y alto fijan solo el tamaño máximo). En ambos casos la zona real está en bbox_real."" },
    ""recortar"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Solo con plot. true (defecto): la hoja del PC3 (tamaños fijos) se recorta a la ventana trazada y se quitan los márgenes blancos; ancho y alto reales pueden ser menores que los pedidos. false: la hoja completa, con márgenes."" }
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""mime"": { ""type"": ""string"" },
    ""png_base64"": { ""type"": ""string"" },
    ""ancho"": { ""type"": ""integer"" },
    ""alto"": { ""type"": ""integer"" },
    ""bbox"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }, ""description"": ""Ventana de trazado (WCS) pedida o ampliada; la zona que realmente cubre la imagen está en bbox_real."" },
    ""bbox_real"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }, ""description"": ""Zona WCS exacta [[xmin, ymin], [xmax, ymax]] que cubre la imagen devuelta (con recortar: false incluye los márgenes de la hoja). Con mecanismo captura es aproximada (advertencia)."" },
    ""recortada"": { ""type"": ""boolean"", ""description"": ""true si la hoja del PC3 se recortó a la ventana trazada."" },
    ""unidades_por_pixel"": { ""type"": [""number"", ""null""], ""description"": ""Unidades de dibujo (ver 'unidades') por píxel de la imagen, uniforme en X e Y: x_wcs = xmin + px × unidades_por_pixel; y_wcs = ymax − py × unidades_por_pixel."" },
    ""mecanismo"": { ""type"": ""string"", ""enum"": [""captura"", ""plot""] },
    ""espacio"": { ""type"": ""string"", ""enum"": [""modelo"", ""papel""] },
    ""layout"": { ""type"": ""string"" },
    ""vista_restaurada"": { ""type"": ""boolean"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""mime"", ""png_base64"", ""ancho"", ""alto"", ""bbox"", ""bbox_real"", ""unidades_por_pixel"", ""mecanismo"", ""espacio"", ""layout"", ""vista_restaurada"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters)
        {
            ParamReader.RequireObject(parameters, Allowed);
            ReadBbox(parameters);
            ParamReader.OptionalInt(parameters, "ancho", MinSize, MaxSize);
            ParamReader.OptionalInt(parameters, "alto", MinSize, MaxSize);
            ReadMechanism(parameters);
            ParamReader.OptionalString(parameters, "espacio", 1, 255);
            ParamReader.OptionalBool(parameters, "ajustar_aspecto");
            ParamReader.OptionalBool(parameters, "recortar");
        }

        /// <summary>Texto exacto de §5B.7 para una captura de espacio papel (sustituye al de 0.3.0).</summary>
        private static string PaperSpaceWarning(string layout) =>
            "La captura corresponde al espacio papel (layout " + layout + "), no al modelo.";

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            double[]? bbox = ReadBbox(parameters);
            int width = ParamReader.OptionalInt(parameters, "ancho", MinSize, MaxSize) ?? DefaultWidth;
            int height = ParamReader.OptionalInt(parameters, "alto", MinSize, MaxSize) ?? DefaultHeight;
            string mechanism = ReadMechanism(parameters);
            string? espacio = ParamReader.OptionalString(parameters, "espacio", 1, 255);
            bool ajustar = ParamReader.OptionalBool(parameters, "ajustar_aspecto") ?? true;
            bool recortar = ParamReader.OptionalBool(parameters, "recortar") ?? true;

            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            Editor ed = doc.Editor;
            var sw = Stopwatch.StartNew();

            var warnings = AcadUtil.UnitWarnings(db);

            // §5B.7: espacio activo = "modelo" si TILEMODE = 1; si no, el layout de CTAB (solo lectura de sysvars).
            bool activeIsModel = AcadUtil.GetIntSysVar("TILEMODE") == 1;
            string ctab = AcadUtil.GetStringSysVar("CTAB");
            string activeLabel = activeIsModel ? SpaceResolver.ModelName : ctab;

            // Espacio capturado: SIEMPRE el activo (layoutId null → PlotCapture usa el layout actual en solo lectura).
            ObjectId? layoutId = null;
            bool capturedPaper = !activeIsModel;
            string layoutName = activeIsModel ? ModelLayoutName : ctab;

            ViewCapture.Result? image = null;
            string usedMechanism = "";
            ViewWindow? window = null;

            // Respuesta común a todas las rutas (síncrona con captura; dentro del grupo neutro con plot).
            // Tamaño de referencia para el aviso de tamaño: el de la hoja pedida al PC3 (los bbox con ajustar_aspecto false lo cambian).
            int warnWidth = width;
            int warnHeight = height;
            object Finish(ViewCapture.Result img, string used, ViewWindow? win) =>
                BuildResult(ctx.Pid, db, img, used, win, capturedPaper, layoutName, warnings, sw, warnWidth, warnHeight, recortar);

            // Errores del trazado directo (bbox o mecanismo 'plot'): mismo mapeo que antes de 0.4 (F5 / FromAcad).
            Exception MapDirectPlotError(Exception ex) => MapPlotError(ex, layoutName, activeLabel, espacio);

            try
            {
                if (espacio != null)
                {
                    // §5B.7 (F5, 0.3.2): el espacio pedido se resuelve como §5B.1 (inexistente → NOT_FOUND data.espacio)
                    // y DEBE ser el activo. Gate en vivo 0.3.1: plotear otro layout lanzaba
                    // Autodesk.AutoCAD.Runtime.Exception eLayoutNotCurrent (AutoCAD solo plotea el layout actual) y el
                    // usuario recibía INTERNAL. Decisión: no cambiar CTAB/TILEMODE a espaldas del usuario; si no es el
                    // activo → INVALID_PARAMS antes de intentar plotear. Transacción de solo lectura.
                    using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
                    SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, espacio);
                    bool resolvedIsModel = string.Equals(resolved.Name, SpaceResolver.ModelName, StringComparison.Ordinal);
                    bool isActive = activeIsModel
                        ? resolvedIsModel
                        : !resolvedIsModel && ctab.Length > 0 && string.Equals(resolved.Name, ctab, StringComparison.OrdinalIgnoreCase);
                    tr.Commit();
                    if (!isActive)
                    {
                        throw ToolException.InvalidParams(NotActiveSpaceMessage(activeLabel, resolved.Name));
                    }
                }

                if (bbox == null)
                {
                    // Sin bbox y en el espacio activo: la vista no se toca; bbox de respuesta = ventana de la vista actual.
                    using ViewTableRecord original = ed.GetCurrentView();
                    // GetCurrentView() describe la vista en DCS. Solo es WCS en planta (VIEWTWIST 0, VIEWDIR (0,0,1)) y sin
                    // viewport flotante activo; si no, no se pasa ventana: Extents y bbox_real = null (0.10, revisión 2).
                    // Con Target XY ≠ 0 (tras 3DORBIT/DVIEW y PLAN) el DCS es el WCS desplazado: tampoco vale como WCS.
                    bool planView = IsPlanWcsView(activeIsModel)
                        && Math.Abs(original.Target.X) < 1e-9 && Math.Abs(original.Target.Y) < 1e-9;
                    window = planView
                        ? ViewWindow.FromView(original.CenterPoint.X, original.CenterPoint.Y, original.Width, original.Height)
                        : null;
                    if (!planView)
                    {
                        warnings.Add("La vista actual no está en planta WCS (viewport flotante, SCP girado, VIEWTWIST o VIEWDIR): se traza Extents y bbox_real = null.");
                    }

                    ViewWindow? viewWindow = window;

                    // 0.9 (punto 1): sin bbox el trazado usa la ventana de la vista, así imagen y bbox_real coinciden.
                    int viewMediaW = width;
                    int viewMediaH = height;
                    if (viewWindow.HasValue)
                    {
                        ViewWindow vw = viewWindow.Value;
                        (viewMediaW, viewMediaH) = PlotCoverage.MediaRequest(vw.XMin, vw.YMin, vw.XMax, vw.YMax, width, height, false);
                    }

                    if (mechanism == Plot)
                    {
                        // Trazado pedido explícitamente: grupo neutro (Task<object>; el despachador espera su resultado).
                        warnWidth = viewMediaW;
                        warnHeight = viewMediaH;
                        return RunPlotNeutral(doc, layoutId, viewWindow, viewMediaW, viewMediaH, warnings,
                            img => Finish(img, Plot, viewWindow), MapDirectPlotError);
                    }

                    if (mechanism == Captura)
                    {
                        usedMechanism = Captura;
                        image = RunCapture(doc, width, height);
                    }
                    else
                    {
                        // auto (§5B.7): captura nativa primero (síncrona, sin grupo); si lanza, respaldo plot dentro del
                        // grupo neutro. El orden captura → plot es fijo (DefaultMechanism = "captura").
                        Exception? capturaError = null;
                        try
                        {
                            usedMechanism = Captura;
                            image = RunCapture(doc, width, height);
                        }
                        catch (Exception ex)
                        {
                            capturaError = ex;
                            PluginLog.Warn("vista_png: mecanismo " + Captura + " falló (" + ex.GetType().FullName + ": " + ex.Message + "); se intenta " + Plot + ".");
                        }

                        if (capturaError != null)
                        {
                            Exception firstError = capturaError;
                            warnWidth = viewMediaW;
                            warnHeight = viewMediaH;
                            return RunPlotNeutral(doc, layoutId, viewWindow, viewMediaW, viewMediaH, warnings,
                                img =>
                                {
                                    warnings.Add("Mecanismo " + Captura + " falló (" + firstError.GetType().Name + "); se usó " + Plot + ".");
                                    return Finish(img, Plot, viewWindow);
                                },
                                plotError => MapFallbackError(firstError, plotError));
                        }
                    }
                }
                else
                {
                    ViewWindow w;
                    (int mediaWidth, int mediaHeight) = (width, height);
                    try
                    {
                        // ajustar_aspecto (0.9): true → ventana ampliada al aspecto pedido; false → bbox exacto y hoja con
                        // el aspecto del bbox. La hoja que se pide al PC3 sigue la misma decisión (PlotCoverage).
                        w = PlotCoverage.Window(bbox[0], bbox[1], bbox[2], bbox[3], width, height, ajustar);
                        (mediaWidth, mediaHeight) = PlotCoverage.MediaRequest(bbox[0], bbox[1], bbox[2], bbox[3], width, height, ajustar);
                        warnWidth = mediaWidth;
                        warnHeight = mediaHeight;
                    }
                    catch (ArgumentException ex)
                    {
                        throw ToolException.InvalidParams(ex.Message);
                    }

                    // Gate Fase 1 (2026-10-25): CapturePreviewImage ignora la vista temporal (devolvía la vista del
                    // usuario declarando el bbox pedido) y SetCurrentView marca DBMOD. Con bbox se usa siempre el plot,
                    // que recibe la ventana directamente: la vista del usuario no se toca.
                    if (mechanism == Captura)
                    {
                        throw ToolException.InvalidParams(
                            "mecanismo 'captura' no admite bbox: la captura nativa no respeta la ventana pedida; use 'plot' o 'auto'");
                    }

                    // AutoCAD interpreta la ventana de trazado en el DCS de la vista: fuera de planta WCS la imagen puede
                    // cubrir otra zona.
                    if (!IsPlanWcsView(activeIsModel))
                    {
                        warnings.Add("La vista actual no está en planta WCS (viewport flotante, SCP girado, VIEWTWIST o VIEWDIR): la ventana bbox se interpreta en coordenadas de la vista y la imagen puede no corresponder al bbox pedido; ponga la vista en planta (PLAN, SCP universal) y repita.");
                    }

                    // §5B.7 / §3.9 (gate en vivo de Fase 3): el trazado va SIEMPRE en el grupo neutro BE/E/U.
                    ViewWindow plotWindow = w;
                    return RunPlotNeutral(doc, layoutId, plotWindow, mediaWidth, mediaHeight, warnings,
                        img => Finish(img, Plot, plotWindow), MapDirectPlotError);
                }
            }
            catch (AcRuntimeException ex)
            {
                // Rutas síncronas (lecturas previas y captura nativa explícita). El trazado mapea sus errores dentro del
                // grupo neutro con MapPlotError.
                throw MapPlotError(ex, layoutName, activeLabel, espacio);
            }

            if (image == null)
            {
                throw new ToolException(
                    ErrorCodes.Internal,
                    "Error interno del plugin de AutoCAD. Causa: ningún mecanismo de captura devolvió imagen.",
                    new Dictionary<string, object?> { ["detalle"] = "vista_png: ningún mecanismo devolvió imagen" });
            }

            return Finish(image, usedMechanism, window);
        }

        /// <summary>
        /// true si la vista actual es de planta WCS: sin viewport flotante activo (en papel, CVPORT 1 es el propio papel),
        /// VIEWTWIST = 0 y VIEWDIR = (0,0,1). Ante lecturas inesperadas devuelve false (conservador).
        /// </summary>
        private static bool IsPlanWcsView(bool activeIsModel)
        {
            if (!activeIsModel && AcadUtil.GetIntSysVar("CVPORT") != 1)
            {
                return false;
            }

            try
            {
                // VIEWDIR se expresa en el SCP actual: con un SCP girado (WORLDUCS = 0) la vista puede no estar en planta WCS.
                if (AcadUtil.GetIntSysVar("WORLDUCS") != 1)
                {
                    return false;
                }

                double twist = Convert.ToDouble(AcApp.GetSystemVariable("VIEWTWIST"), CultureInfo.InvariantCulture);
                if (Math.Abs(twist) > 1e-9)
                {
                    return false;
                }

                object? dir = AcApp.GetSystemVariable("VIEWDIR");
                if (dir is Point3d d)
                {
                    return Math.Abs(d.X) < 1e-9 && Math.Abs(d.Y) < 1e-9 && Math.Abs(d.Z - 1.0) < 1e-9;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Lista de sysvars que el grupo neutro registra antes de BE y después de U (diagnóstico del gate).</summary>
        private static readonly string[] WatchedSysVars = { "DBMOD", "CTAB", "TILEMODE", "PLQUIET", "STYLESHEET", "BACKGROUNDPLOT" };

        /// <summary>Respuesta de §5B.7 (común a captura y plot). Añade la advertencia de espacio papel.</summary>
        private static object BuildResult(
            int pid, Database db, ViewCapture.Result image, string usedMechanism, ViewWindow? window, bool capturedPaper,
            string layoutName, List<string> warnings, Stopwatch sw, int requestedWidth, int requestedHeight, bool recortar)
        {
            const bool viewRestored = true;

            // Fallo 46: la hoja del PC3 tiene tamaño fijo; con plot se recorta a la ventana trazada (sin márgenes blancos).
            bool recortada = false;
            if (recortar && usedMechanism == Plot && window.HasValue && image.Coverage != null)
            {
                CropRect? rect = PlotCoverage.CropToWindow(image.Coverage, window.Value, image.Width, image.Height);
                if (rect == null && HayMargen(image.Coverage, window.Value))
                {
                    // Re-auditoría R2: había márgenes pero la escala de la zona no cuadra con el PNG; no se recorta a ciegas.
                    PluginLog.Info("vista_png: recorte omitido (escala X/Y de la zona no coincide con el PNG " + image.Width.ToString(CultureInfo.InvariantCulture)
                        + "x" + image.Height.ToString(CultureInfo.InvariantCulture) + ").");
                    warnings.Add("No se recortaron los márgenes: la escala de la hoja no coincide con la imagen; se devuelve la hoja completa (bbox_real la describe).");
                }

                if (rect != null)
                {
                    ViewCapture.Result? cortada = Recortar(image, rect, warnings);
                    if (cortada != null)
                    {
                        image = cortada;
                        recortada = true;
                    }
                }
            }

            if (capturedPaper)
            {
                warnings.Add(PaperSpaceWarning(layoutName));
            }

            double[][] bboxOut = window?.ToBbox2D() ?? new[] { new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 } };

            // bbox_real (0.9): zona WCS que cubre la imagen REAL (tamaño del PNG), no la ventana pedida. Con captura nativa
            // la imagen es la vista en pantalla: se calcula igual pero se avisa de que es aproximada.
            ImageCoverage? coverage = image.Coverage;
            if (coverage == null && usedMechanism == Captura && window.HasValue && image.Width > 0 && image.Height > 0)
            {
                coverage = PlotCoverage.Compute(window.Value, image.Width, image.Height);
                warnings.Add("bbox_real de la captura nativa es aproximado (la imagen es la vista en pantalla).");
            }

            if (coverage == null)
            {
                warnings.Add("bbox_real no disponible: no se pudo calcular la zona cubierta por la imagen (bbox_real = null).");
            }

            if (recortada)
            {
                if (requestedWidth != image.Width || requestedHeight != image.Height)
                {
                    warnings.Add(
                        "La imagen mide " + image.Width.ToString(CultureInfo.InvariantCulture) + "×" + image.Height.ToString(CultureInfo.InvariantCulture)
                        + " px (hoja pedida " + requestedWidth.ToString(CultureInfo.InvariantCulture) + "×" + requestedHeight.ToString(CultureInfo.InvariantCulture)
                        + "): la hoja del PC3 se recortó a la ventana trazada. bbox_real indica la zona que cubre.");
                }
            }
            else if (requestedWidth != image.Width || requestedHeight != image.Height)
            {
                warnings.Add(
                    "La imagen mide " + image.Width.ToString(CultureInfo.InvariantCulture) + "×" + image.Height.ToString(CultureInfo.InvariantCulture)
                    + " px (pedido " + requestedWidth.ToString(CultureInfo.InvariantCulture) + "×" + requestedHeight.ToString(CultureInfo.InvariantCulture)
                    + "): los tamaños de hoja del PC3 son fijos. bbox_real indica la zona que cubre.");
            }

            sw.Stop();
            PluginLog.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"vista_png mecanismo={usedMechanism} layout={layoutName} api={image.Api} {image.Width}x{image.Height} bytes={image.Png.Length} ms={sw.ElapsedMilliseconds} vista_restaurada={viewRestored}"));

            int ins = AcadUtil.InsUnits(db);
            return new
            {
                pid,
                mime = "image/png",
                png_base64 = Convert.ToBase64String(image.Png),
                ancho = image.Width,
                alto = image.Height,
                bbox = bboxOut,
                bbox_real = coverage?.ToBbox2D(),
                unidades_por_pixel = coverage?.UnitsPerPixel,
                mecanismo = usedMechanism,
                espacio = capturedPaper ? "papel" : "modelo",
                layout = layoutName,
                vista_restaurada = viewRestored,
                recortada,
                insunits = ins,
                unidades = UnitsTable.Name(ins),
                advertencias = warnings,
            };
        }

        /// <summary>true si la zona de la hoja excede la ventana en más del margen mínimo de recorte en algún eje.</summary>
        private static bool HayMargen(ImageCoverage c, ViewWindow w)
        {
            double upp = c.UnitsPerPixel;
            if (!(upp > 0))
            {
                return false;
            }

            double extraX = ((c.XMax - c.XMin) - w.Width) / upp;
            double extraY = ((c.YMax - c.YMin) - w.Height) / upp;
            // +2: CropToWindow redondea hacia fuera (hasta 1 px por lado); por debajo, el null es por margen pequeño, no por escala.
            return extraX >= 2 * PlotCoverage.MinCropMarginPx + 2 || extraY >= 2 * PlotCoverage.MinCropMarginPx + 2;
        }

        /// <summary>
        /// Fallo 46: recorta el PNG a <paramref name="rect"/> (píxeles, origen arriba a la izquierda) y devuelve la imagen
        /// nueva con su zona WCS. Si el PNG no tiene el tamaño declarado o falla la decodificación, devuelve null (se
        /// devuelve la hoja completa) y lo advierte.
        /// </summary>
        private static ViewCapture.Result? Recortar(ViewCapture.Result image, CropRect rect, List<string> warnings)
        {
            try
            {
                using var entrada = new MemoryStream(image.Png);
                using var hoja = new System.Drawing.Bitmap(entrada);
                if (hoja.Width != image.Width || hoja.Height != image.Height
                    || rect.X + rect.Width > hoja.Width || rect.Y + rect.Height > hoja.Height)
                {
                    warnings.Add("No se recortó la imagen: el PNG no tiene el tamaño esperado; se devuelve la hoja completa.");
                    return null;
                }

                using System.Drawing.Bitmap recorte = hoja.Clone(
                    new System.Drawing.Rectangle(rect.X, rect.Y, rect.Width, rect.Height), hoja.PixelFormat);
                using var salida = new MemoryStream();
                recorte.Save(salida, System.Drawing.Imaging.ImageFormat.Png);
                return new ViewCapture.Result(salida.ToArray(), rect.Width, rect.Height, image.Api) { Coverage = rect.Coverage };
            }
            catch (Exception ex) when (ex is ArgumentException || ex is OutOfMemoryException || ex is System.Runtime.InteropServices.ExternalException)
            {
                PluginLog.Warn("vista_png: recorte fallido (" + ex.GetType().Name + ": " + ex.Message + "); se devuelve la hoja completa.");
                warnings.Add("No se pudo recortar la imagen (" + ex.GetType().Name + "); se devuelve la hoja completa.");
                return null;
            }
        }

        /// <summary>
        /// Trazado (mecanismo "plot") dentro de <see cref="UndoGroup.RunNeutral"/> (protocol.md §5B.7, gate en vivo de
        /// Fase 3): "_.UNDO _BE" → PlotCapture → "_.UNDO _E" → "_.U", efecto neto cero pasos de deshacer. Devuelve un
        /// Task&lt;object&gt; que el despachador espera (RequestDispatcher acepta resultados diferidos). Si AutoCAD ya está
        /// trazando, BUSY ANTES de lanzar el contexto de comando (no se toca el historial). <paramref name="finish"/> arma
        /// la respuesta dentro del grupo; <paramref name="mapError"/> traduce los errores del trazado.
        /// </summary>
        private static Task<object> RunPlotNeutral(
            Document doc, ObjectId? layoutId, ViewWindow? window, int width, int height, List<string> warnings,
            Func<ViewCapture.Result, object> finish, Func<Exception, Exception> mapError)
        {
            PlotCapture.ThrowIfPlotting();
            return UndoGroup.RunNeutral(
                doc,
                "vista_png",
                d =>
                {
                    // Gate en vivo de Fase 4A (2026-10-27, bloqueante): MCP-PREVIEW es no imprimible y el trazado la omitía.
                    // Se hace imprimible SOLO durante el trazado, dentro de este grupo neutro, y se restaura explícitamente
                    // en finally; el "_.U" del grupo lo deshace además (PreviewPlotVisibility, protocol.md §5B.7).
                    ObjectId? previewLayer = PreviewPlotVisibility.Enable(d, warnings);
                    ViewCapture.Result image;
                    try
                    {
                        image = PlotCapture.Capture(d, layoutId, window, width, height, warnings);
                    }
                    catch (Exception ex)
                    {
                        Exception mapped = mapError(ex);
                        if (ReferenceEquals(mapped, ex))
                        {
                            throw;
                        }

                        throw mapped;
                    }
                    finally
                    {
                        if (previewLayer.HasValue)
                        {
                            PreviewPlotVisibility.Restore(d, previewLayer.Value, warnings);
                        }
                    }

                    return finish(image);
                },
                warnings,
                WatchedSysVars);
        }

        /// <summary>
        /// Captura nativa, SIN grupo de deshacer (decisión documentada en protocol.md §5B.7): ViewCapture no abre objetos
        /// ni cambia sysvars (solo UpdateScreen + lectura de la imagen) y el gate de Fase 3 no mostró paso alguno con ella.
        /// Envolverla en BE/E/U añadiría tres comandos al historial de la consola en cada captura sin beneficio probado.
        /// </summary>
        private static ViewCapture.Result RunCapture(Document doc, int width, int height) => ViewCapture.Capture(doc, width, height);

        /// <summary>
        /// Errores del trazado directo: eLayoutNotCurrent → INVALID_PARAMS (F5); otra excepción de AutoCAD → INTERNAL con
        /// detalle; el resto (ToolException, p. ej. BUSY) se devuelve tal cual.
        /// </summary>
        private static Exception MapPlotError(Exception ex, string layoutName, string activeLabel, string? espacio)
        {
            if (ex is AcRuntimeException acad)
            {
                if (acad.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.LayoutNotCurrent)
                {
                    // Defensa (F5): si AutoCAD aún dijera eLayoutNotCurrent (p. ej. la pestaña cambió durante la llamada),
                    // se informa como el caso previsto, no como INTERNAL.
                    PluginLog.Warn("vista_png: eLayoutNotCurrent al plotear (" + layoutName + ").");
                    return ToolException.InvalidParams(NotActiveSpaceMessage(activeLabel, espacio ?? activeLabel));
                }

                return FromAcad(acad);
            }

            return ex;
        }

        /// <summary>
        /// auto (§5B.7): la captura falló y el respaldo plot también. BUSY se conserva (reintentable); cualquier otro fallo
        /// → INTERNAL "No se pudo capturar la vista con ningún mecanismo." con el detalle de ambos.
        /// </summary>
        private static Exception MapFallbackError(Exception capturaError, Exception plotError)
        {
            if (plotError is ToolException busy && busy.Code == ErrorCodes.Busy)
            {
                return busy;
            }

            return new ToolException(
                ErrorCodes.Internal,
                "No se pudo capturar la vista con ningún mecanismo.",
                new Dictionary<string, object?> { ["detalle"] = "captura: " + Describe(capturaError) + "; plot: " + Describe(plotError) });
        }

        /// <summary>bbox §2.5: [[x,y],[x,y]] o con z (ignorado). Devuelve {xmin, ymin, xmax, ymax} o null si no viene.</summary>
        private static double[]? ReadBbox(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("bbox", out JsonElement el))
            {
                return null;
            }

            const string shape = "bbox: debe ser [[xmin, ymin], [xmax, ymax]] (2 puntos de 2 o 3 números)";
            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 2)
            {
                throw ToolException.InvalidParams(shape);
            }

            var xy = new double[4];
            int p = 0;
            foreach (JsonElement point in el.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams(shape);
                }

                int n = point.GetArrayLength();
                if (n != 2 && n != 3)
                {
                    throw ToolException.InvalidParams(shape);
                }

                int i = 0;
                foreach (JsonElement c in point.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Number)
                    {
                        throw ToolException.InvalidParams(shape);
                    }

                    if (!c.TryGetDouble(out double v) || !double.IsFinite(v))
                    {
                        throw ToolException.InvalidParams("bbox: coordenada no finita");
                    }

                    if (i < 2)
                    {
                        xy[(p * 2) + i] = v;
                    }

                    i++;
                }

                p++;
            }

            if (xy[0] > xy[2] || xy[1] > xy[3])
            {
                throw ToolException.InvalidParams(ViewWindow.MinGreaterThanMaxMessage);
            }

            return xy;
        }

        private static string ReadMechanism(JsonElement parameters)
        {
            string? value = ParamReader.OptionalString(parameters, "mecanismo", 0);
            if (value == null)
            {
                return Auto;
            }

            if (value != Auto && value != Captura && value != Plot)
            {
                throw ToolException.InvalidParams("mecanismo: debe ser \"auto\", \"captura\" o \"plot\"");
            }

            return value;
        }

        private static string Describe(Exception ex) =>
            ex is AcRuntimeException acad
                ? ex.GetType().FullName + " (" + acad.ErrorStatus + "): " + ex.Message
                : ex.GetType().FullName + ": " + ex.Message;

        private static ToolException FromAcad(AcRuntimeException ex)
        {
            PluginLog.Error("vista_png: excepción de AutoCAD (" + ex.ErrorStatus + ")", ex);
            return new ToolException(
                ErrorCodes.Internal,
                // Fallo 23: el servidor solo muestra el mensaje (no data.detalle), así que la causa de AutoCAD va en el texto.
                "Error interno del plugin de AutoCAD. Al trazar la vista AutoCAD devolvió e" + ex.ErrorStatus
                + " (detalle en plugin.log).",
                new Dictionary<string, object?> { ["detalle"] = Describe(ex) });
        }
    }
}
