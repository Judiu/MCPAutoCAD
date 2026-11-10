using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// vista_png mecanismo "plot" (protocol.md §5B.7): PublishEngine con "PublishToWeb PNG.pc3" a un PNG temporal.
    /// SOLO hilo principal. Es una LECTURA: no crea pasos de deshacer ni modifica objetos del dibujo (§3.9, defecto F7
    /// del gate de Fase 2). Medidas, todas exigidas por §5B.7 "Deshacer y variables de sistema":
    /// <list type="number">
    /// <item>(0.3.2) Todo el ciclo del trazado, incluidos el cambio y la restauración de BACKGROUNDPLOT, va dentro de
    /// <see cref="UndoRecordingSuspension"/> (Database.DisableUndoRecording, restaurando el estado previo de
    /// Database.UndoRecording en finally): el gate en vivo 0.3.1 demostró que el PROPIO PublishEngine modifica
    /// SORTENTSTABLE, VIEWPORT y VX_TABLE_RECORD y cambia PLQUIET, y esas modificaciones creaban un paso de deshacer.</item>
    /// <item>BACKGROUNDPLOT se cambia a 0 solo si no vale ya 0, y se restaura solo si se cambió (única sysvar que una
    /// lectura puede tocar; es de registro, no del dibujo).</item>
    /// <item>El Layout se abre solo ForRead y nunca se escribe: la PlotSettings sobre la que trabaja
    /// PlotSettingsValidator es una copia NO residente (new PlotSettings + CopyFrom(layout), copia layout → copia).</item>
    /// <item>Con 'espacio' se plotea el Layout de ese espacio (PlotInfo.Layout) sin LayoutManager.SetCurrentLayout ni
    /// asignar CurrentLayout: CTAB/TILEMODE no cambian.</item>
    /// <item>DocumentLock se mantiene (PublishEngine lo exige en contexto de aplicación; desde 0.4 se llama en contexto de
    /// comando, donde el bloqueo es anidado/reentrante) pero no se abre ninguna transacción de escritura.</item>
    /// <item>(0.4, gate en vivo de Fase 3) DisableUndoRecording NO impide que el trazado deje UN paso en la pila de
    /// deshacer de comandos (CTRL+Z mostraba un "_u" que no deshacía nada del usuario; PLQUIET y STYLESHEET cambiadas por
    /// el PublishEngine). Por eso el LLAMADOR (VistaPngHandler) ejecuta este método dentro de
    /// <see cref="UndoGroup.RunNeutral"/>: "_.UNDO _BE" → trazado → "_.UNDO _E" → "_.U". La suspensión de la grabación de
    /// deshacer se mantiene DENTRO del grupo (defensa en profundidad para los objetos que el PublishEngine modifica).</item>
    /// <item>Diagnóstico permanente: durante el trazado se registran en plugin.log los objetos abiertos para escritura
    /// o modificados (Database.ObjectOpenedForModify/ObjectModified) y las sysvars cambiadas
    /// (Application.SystemVariableChanged). Si el gate vuelve a ver un paso de deshacer, el log dice qué lo produjo.</item>
    /// </list>
    /// El mecanismo "captura" (<see cref="ViewCapture"/>) no cambia nada del dibujo ni sysvars: solo llama a
    /// Application.UpdateScreen (redibujo de pantalla, sin registro de deshacer) y lee la imagen.
    /// </summary>
    internal static class PlotCapture
    {
        /// <summary>Dispositivo de salida PNG que trae AutoCAD (§5B.7).</summary>
        public const string DeviceName = "PublishToWeb PNG.pc3";

        /// <summary>Máximo de líneas de diagnóstico por llamada (para que el log siga siendo barato).</summary>
        private const int MaxDiagnosticLines = 50;

        /// <summary>
        /// Plotea a PNG. <paramref name="layoutId"/> = Layout del espacio pedido (§5B.7 'espacio'), o null para el layout
        /// actual (solo lectura de LayoutManager). <paramref name="window"/> null → Extents centrado.
        /// </summary>
        /// <remarks>
        /// <paramref name="advertencias"/> (§5D.9): si al terminar el trazado no se pudo reactivar la grabación de deshacer,
        /// se añade la advertencia fija de <see cref="ReactivationWarning"/> en vez de fallar en silencio.
        /// </remarks>
        public static ViewCapture.Result Capture(Document doc, ObjectId? layoutId, ViewWindow? window, int width, int height, List<string> advertencias)
        {
            // §5B.7: si AutoCAD ya está trazando → BUSY (también se comprueba antes de abrir el grupo neutro).
            ThrowIfPlotting();
            PaperGeometry? paper = null;

            // §9: archivos temporales en %LOCALAPPDATA%\MCPAutoCAD\tmp\vista_<guid>.png.
            string tmpDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPAutoCAD", "tmp");
            Directory.CreateDirectory(tmpDir);
            string tmpFile = Path.Combine(tmpDir, "vista_" + Guid.NewGuid().ToString("N") + ".png");

            try
            {
                // DocumentLock: PlotSettingsValidator/PublishEngine lo exigen en contexto de aplicación. No se abre
                // ninguna transacción de escritura bajo este bloqueo (§3.9).
                using (doc.LockDocument())
                {
                    // F7 (0.3.2): es el PROPIO PublishEngine de AutoCAD quien modifica objetos del dibujo al trazar. Log del
                    // gate en vivo 0.3.1 durante UN plot con Layout1 activo: "sysvar cambiada PLQUIET", "objeto modificado
                    // 238AC (SORTENTSTABLE, DrawOrderTable)", "abierto para escritura/modificado 12D40 (VIEWPORT)", "B83B9
                    // (VX_TABLE_RECORD, ImpSymbolTableRecord)", "3B5B9 (VIEWPORT)" y "BACKGROUNDPLOT 2 → 0"; tras
                    // marcar_hallazgos → vista_png(bbox) → UN CTRL+Z las marcas seguían ahí (el CTRL+Z deshizo lo del plot).
                    // Nuestro código no abre nada ForWrite (copia no residente de PlotSettings), así que la única forma de
                    // que esas modificaciones internas no entren en el historial es suspender la grabación de deshacer de la
                    // base de datos durante TODO el ciclo (incluidos el cambio y la restauración de BACKGROUNDPLOT) y
                    // restaurar SIEMPRE el estado previo (Database.UndoRecording) al salir, también ante excepción.
                    // Aquí no hay UNDO ni comandos: el grupo neutro BE/E/U lo pone el llamador (UndoGroup.RunNeutral, 0.4).
                    UndoRecordingSuspension? suspension = null;
                    try
                    {
                    using (suspension = new UndoRecordingSuspension(doc.Database, "vista_png plot"))
                    {
                        // §5B.7 / §3.9: BACKGROUNDPLOT es la ÚNICA sysvar que una lectura puede cambiar. El
                        // PublishEngine honra BACKGROUNDPLOT (defecto 2 = publicación en segundo plano) y, en segundo
                        // plano, el PNG temporal no existe cuando se lee. Se pone a 0 SOLO si su valor actual no es 0 y
                        // se restaura SOLO si se cambió.
                        short previousValue = ToShort(AcApp.GetSystemVariable("BACKGROUNDPLOT"));
                        bool changedBackground = false;
                        if (previousValue != 0)
                        {
                            AcApp.SetSystemVariable("BACKGROUNDPLOT", (short)0);
                            changedBackground = true;
                            PluginLog.Info("vista_png plot: BACKGROUNDPLOT " + previousValue.ToString(CultureInfo.InvariantCulture)
                                + " → 0 durante el trazado (el PublishEngine en segundo plano no deja el PNG listo al leerlo); se restaura al terminar.");
                        }

                        try
                        {
                            using var diag = new ModificationDiagnostics(doc.Database, "vista_png plot");
                            paper = PlotToFile(doc, layoutId, window, width, height, tmpFile, advertencias);
                        }
                        finally
                        {
                            if (changedBackground)
                            {
                                try
                                {
                                    AcApp.SetSystemVariable("BACKGROUNDPLOT", previousValue);
                                }
                                catch (Exception ex)
                                {
                                    PluginLog.Warn("No se pudo restaurar BACKGROUNDPLOT (" + ex.GetType().Name + ": " + ex.Message + ").");
                                }
                            }
                        }
                    }
                    }
                    finally
                    {
                        // §5D.9: la reactivación del deshacer se evalúa tras el using (Dispose ya se ejecutó), también
                        // si el trazado lanzó.
                        if (suspension != null && suspension.ReactivationFailed)
                        {
                            advertencias.Add(ReactivationWarning(suspension.FailureDetail ?? "desconocido"));
                        }
                    }
                }

                // §5B.7: se lee el PNG y se informan sus dimensiones reales (pueden diferir de lo pedido).
                byte[] png = File.ReadAllBytes(tmpFile);
                (int realW, int realH) = ReadPngSize(png);
                var result = new ViewCapture.Result(png, realW, realH, "PublishToWeb PNG");
                // 0.9 (punto 4): zona real con el área imprimible de la hoja (márgenes leídos tras Validate).
                if (window.HasValue)
                {
                    // Sin márgenes leídos (ReadPaper devolvió null, ya avisado) la imagen se toma como hoja sin márgenes.
                    PaperGeometry pg = paper ?? new PaperGeometry(realW, realH, 0, 0, 0, 0);
                    result.Coverage = PlotCoverage.ComputeInPrintable(
                        window.Value, realW, realH, pg.Width, pg.Height, pg.Left, pg.Bottom, pg.Right, pg.Top);
                }

                return result;
            }
            finally
            {
                // §5B.7/§9: el temporal se borra siempre, también ante excepción.
                try
                {
                    if (File.Exists(tmpFile))
                    {
                        File.Delete(tmpFile);
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("No se pudo borrar el temporal '" + tmpFile + "' (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
            }
        }

        /// <summary>
        /// §5B.7: si AutoCAD ya está trazando → BUSY motivo "comando", comando "PLOT". VistaPngHandler lo llama ANTES de
        /// lanzar el grupo neutro, para que un BUSY no toque el historial de deshacer.
        /// </summary>
        public static void ThrowIfPlotting()
        {
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            {
                throw new ToolException(
                    ErrorCodes.Busy,
                    "AutoCAD está imprimiendo o trazando; reintente cuando termine.",
                    new Dictionary<string, object?> { ["motivo"] = "comando", ["comando"] = "PLOT" });
            }
        }

        /// <summary>Advertencia exacta de protocol.md §5D.9 (detalle = '&lt;Tipo&gt;: &lt;mensaje&gt;').</summary>
        public static string ReactivationWarning(string detalle) =>
            "No se pudo reactivar la grabación de deshacer tras el trazado (" + detalle
            + "): los cambios siguientes podrían no poder deshacerse; guarde y vuelva a abrir el dibujo.";

        private static short ToShort(object? value)
        {
            try
            {
                return value == null ? (short)2 : Convert.ToInt16(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 2;
            }
        }

        /// <summary>
        /// Tamaños de papel personalizados ("User N (… Pixels)") que el PC3 tiene en ese momento. Solo lectura: nunca se
        /// escribe en el .pmp del usuario desde aquí.
        /// </summary>
        private static List<string> CustomMediaNames(PlotSettingsValidator psv, PlotSettings ps)
        {
            var names = new List<string>();
            StringCollection canonical = psv.GetCanonicalMediaNameList(ps);
            for (int i = 0; i < canonical.Count; i++)
            {
                string? name = canonical[i];
                if (!string.IsNullOrEmpty(name) && name.IndexOf("User", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    names.Add(name);
                }
            }

            return names;
        }

        /// <summary>Hoja del trazado tras validar: tamaño y márgenes (unidades de la hoja, las mismas que la ventana).</summary>
        private readonly record struct PaperGeometry(double Width, double Height, double Left, double Bottom, double Right, double Top);

        private static PaperGeometry? PlotToFile(Document doc, ObjectId? requestedLayoutId, ViewWindow? window, int width, int height, string outFile, List<string> advertencias)
        {
            PaperGeometry? paperResult = null;
            Database db = doc.Database;

            // §3.9: transacción de solo lectura (open/close). Nada se abre ForWrite.
            using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();

            // §5B.7 (F5/F7): con 'espacio' se usa el Layout del espacio resuelto (btr.LayoutId) SIN cambiar el actual.
            // Sin 'espacio', el layout actual se obtiene en solo lectura (GetLayoutId no modifica nada; nunca se asigna
            // LayoutManager.CurrentLayout ni se llama a SetCurrentLayout).
            ObjectId layoutId;
            if (requestedLayoutId.HasValue && !requestedLayoutId.Value.IsNull)
            {
                layoutId = requestedLayoutId.Value;
            }
            else
            {
                LayoutManager lm = LayoutManager.Current;
                layoutId = lm.GetLayoutId(lm.CurrentLayout);
            }

            // §5B.7: el Layout del dibujo se abre solo ForRead y nunca se escribe en él.
            var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);

            // §5B.7: PlotSettings en memoria (no residente), copiada DEL layout (ps ← layout). Nunca se añade a la base
            // de datos ni a un diccionario; todas las llamadas de PlotSettingsValidator van sobre 'ps', jamás sobre 'layout'.
            using var ps = new PlotSettings(layout.ModelType);
            ps.CopyFrom(layout);

            PlotSettingsValidator psv = PlotSettingsValidator.Current;

            // §5B.7: dispositivo PublishToWeb PNG.pc3.
            psv.SetPlotConfigurationName(ps, DeviceName, null);
            psv.RefreshLists(ps);

            // §5B.7: tamaño PNG más pequeño que cubra ancho×alto (nombres leídos del PC3 en tiempo de ejecución).
            string? media = ChooseMedia(psv, ps, width, height);
            if (media == null)
            {
                // Sin medio que cubra la captura se fija el primer tamaño canónico; el validador (MatchEnabled) ajusta el resto.
                StringCollection canonical = psv.GetCanonicalMediaNameList(ps);
                for (int i = 0; i < canonical.Count && media == null; i++)
                {
                    if (!string.IsNullOrEmpty(canonical[i]))
                    {
                        media = canonical[i];
                    }
                }

                PluginLog.Warn("El PC3 '" + DeviceName + "' no dio un tamaño que cubra la captura; se fija " + (media ?? "(ninguno)") + ".");
            }

            if (media != null)
            {
                psv.SetCanonicalMediaName(ps, media);
            }

            // §5B.7: Window con bbox (ventana ya ampliada al aspecto), o Extents centrado sin bbox.
            if (window.HasValue)
            {
                ViewWindow w = window.Value;
                // En el plot del layout (plano, sin giro) DCS = WCS en XY.
                // SetPlotWindowArea debe ir ANTES de SetPlotType(Window).
                psv.SetPlotWindowArea(ps, new Extents2d(w.XMin, w.YMin, w.XMax, w.YMax));
                psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
            }
            else
            {
                psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
            }

            psv.SetPlotCentered(ps, true);

            // §5B.7: ajustar a la hoja, sin rotación, sin tabla de estilos.
            psv.SetUseStandardScale(ps, true);
            psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
            psv.SetPlotRotation(ps, PlotRotation.Degrees000);
            psv.SetCurrentStyleSheet(ps, "");

            // §5B.7: PlotInfo.Layout = layout del espacio pedido; OverrideSettings = la copia no residente.
            using var info = new PlotInfo
            {
                Layout = layoutId,
                OverrideSettings = ps,
            };

            // Fallo 23 (0.10.0, en vivo en AutoCAD 2026): con MatchingPolicy.MatchDisabled, Validate lanzó eNoMatchingMedia en
            // TODA captura por ventana (medios 'QHD_(2560.00_x_1440.00_Pixels)' y 'UserDefinedRaster…'), aunque el medio
            // canónico estaba fijado; 0.9.0 con MatchEnabled funcionaba el mismo día. Se vuelve a MatchEnabled (el trazado por
            // ventana con ajuste a la hoja necesita emparejar el medio). El crecimiento de tamaños "User N" en el .pmp queda
            // como diagnóstico (abajo y PmpDiagnostics), sin causa confirmada.
            using var validator = new PlotInfoValidator
            {
                MediaMatchingPolicy = MatchingPolicy.MatchEnabled,
            };

            // Instantánea de tamaños "User" DESPUÉS de RefreshLists y ANTES de Validate (0.9, punto 2).
            psv.RefreshLists(ps);
            List<string> customBefore = CustomMediaNames(psv, ps);
            PmpDiagnostics.Log("antes de Validate");
            validator.Validate(info);
            PmpDiagnostics.Log("tras Validate");

            // Con MatchEnabled el validador puede resolver otro medio: la hoja real es la de ValidatedSettings, no 'ps'.
            PlotSettings? validated = info.ValidatedSettings;
            paperResult = ReadPaper(validated ?? ps, advertencias);

            // §5B.7: PublishEngine a archivo (BACKGROUNDPLOT = 0 → síncrono).
            using (PlotEngine engine = PlotFactory.CreatePublishEngine())
            {
                engine.BeginPlot(null, null);
                engine.BeginDocument(info, doc.Name, null, 1, true, outFile);
                var pageInfo = new PlotPageInfo();
                engine.BeginPage(pageInfo, info, true, null);
                // AutoCAD 2026 no tiene PlotEngine.PlotGraphics(): el par equivalente es Begin/EndGenerateGraphics.
                engine.BeginGenerateGraphics(null);
                engine.EndGenerateGraphics(null);
                engine.EndPage(null);
                engine.EndDocument(null);
                engine.EndPlot(null);
            }

            PmpDiagnostics.Log("tras EndPlot");

            // Diagnóstico del bug "User N": si el trazado añadió tamaños personalizados, se avisa (no se borran ni se editan
            // los .pmp del usuario desde aquí; ver el informe de 0.9).
            psv.RefreshLists(ps);
            var added = new List<string>();
            foreach (string name in CustomMediaNames(psv, ps))
            {
                if (!customBefore.Contains(name))
                {
                    added.Add(name);
                }
            }

            if (added.Count > 0)
            {
                PluginLog.Warn("vista_png plot: el trazado añadió tamaño(s) personalizado(s) al PC3 '" + DeviceName + "': " + string.Join("; ", added));
                advertencias.Add(
                    "AutoCAD registró " + added.Count.ToString(CultureInfo.InvariantCulture)
                    + " tamaño(s) de papel personalizado(s) en el PC3 '" + DeviceName + "' (" + string.Join("; ", added) + ").");
            }

            // Commit sin cambios (no se abrió nada para escritura): equivale a Abort, pero Commit es más barato en
            // AutoCAD y es la convención de las lecturas del plugin (§3.9).
            tr.Commit();
            return paperResult;
        }

        /// <summary>
        /// Tamaño y márgenes de la hoja tras validar (0.9, punto 4). Supuesto: PlotPaperMargins.MinPoint = (izq, inf) y
        /// MaxPoint = (der, sup) como distancias. Si no se puede leer, null y se avisa (el cálculo cae a márgenes 0).
        /// </summary>
        private static PaperGeometry? ReadPaper(PlotSettings ps, List<string> advertencias)
        {
            try
            {
                var size = ps.PlotPaperSize;
                Extents2d m = ps.PlotPaperMargins;
                double left = m.MinPoint.X;
                double bottom = m.MinPoint.Y;
                double right = m.MaxPoint.X;
                double top = m.MaxPoint.Y;
                PluginLog.Info("vista_png plot: hoja " + size.X.ToString(CultureInfo.InvariantCulture) + "x"
                    + size.Y.ToString(CultureInfo.InvariantCulture) + " márgenes izq=" + left.ToString(CultureInfo.InvariantCulture)
                    + " inf=" + bottom.ToString(CultureInfo.InvariantCulture) + " der=" + right.ToString(CultureInfo.InvariantCulture)
                    + " sup=" + top.ToString(CultureInfo.InvariantCulture) + ".");
                if (Math.Abs(left) + Math.Abs(bottom) + Math.Abs(right) + Math.Abs(top) > 1e-6)
                {
                    advertencias.Add("La hoja del PC3 tiene márgenes no nulos: bbox_real calcula la zona sobre el área imprimible.");
                }

                return new PaperGeometry(size.X, size.Y, left, bottom, right, top);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("No se pudieron leer tamaño y márgenes de la hoja (" + ex.GetType().Name + ": " + ex.Message + ").");
                advertencias.Add("No se pudieron leer los márgenes de la hoja: bbox_real puede ser impreciso.");
                return null;
            }
        }

        /// <summary>Lee los medios canónicos del PC3 y elige con PlotMediaSelector (usa el nombre local si el canónico no trae números).</summary>
        private static string? ChooseMedia(PlotSettingsValidator psv, PlotSettings ps, int width, int height)
        {
            StringCollection canonical = psv.GetCanonicalMediaNameList(ps);
            var media = new List<(string name, double a, double b)>();
            for (int i = 0; i < canonical.Count; i++)
            {
                string? name = canonical[i];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                (double a, double b)? dims = PlotMediaSelector.ParseCanonical(name);
                if (dims == null)
                {
                    try
                    {
                        dims = PlotMediaSelector.ParseCanonical(psv.GetLocaleMediaName(ps, i));
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warn("GetLocaleMediaName(" + i.ToString(CultureInfo.InvariantCulture) + ") falló (" + ex.GetType().Name + ").");
                    }
                }

                if (dims != null)
                {
                    media.Add((name, dims.Value.a, dims.Value.b));
                }
            }

            string? chosen = PlotMediaSelector.Choose(media, width, height);
            PluginLog.Info("vista_png plot: " + media.Count.ToString(CultureInfo.InvariantCulture) + " medios PNG; elegido '" + (chosen ?? "(ninguno)") + "'.");
            return chosen;
        }

        /// <summary>Ancho y alto de un PNG desde la cabecera IHDR (bytes 16–23, big-endian).</summary>
        internal static (int width, int height) ReadPngSize(byte[] png)
        {
            if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
            {
                throw new InvalidDataException("El archivo generado por el plot no es un PNG válido.");
            }

            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return (w, h);
        }

        /// <summary>
        /// Diagnóstico de solo lectura (0.9, punto 2): fecha y tamaño de los .pmp de PublishToWeb PNG/JPG en cada etapa del
        /// trazado. NUNCA escribe en esos archivos. La ruta se busca una vez bajo la carpeta Autodesk de APPDATA.
        /// </summary>
        internal static class PmpDiagnostics
        {
            private static readonly Lazy<string[]> Files = new Lazy<string[]>(FindFiles);

            public static void Log(string etapa)
            {
                try
                {
                    string[] files = Files.Value;
                    if (files.Length == 0)
                    {
                        PluginLog.Info("pmp " + etapa + ": no se encontraron los .pmp de PublishToWeb.");
                        return;
                    }

                    foreach (string f in files)
                    {
                        var fi = new FileInfo(f);
                        string desc = fi.Exists
                            ? "mtimeUtc=" + fi.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture) + " len=" + fi.Length.ToString(CultureInfo.InvariantCulture)
                            : "no existe";
                        PluginLog.Info("pmp " + etapa + ": " + fi.Name + " " + desc);
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("pmp " + etapa + ": diagnóstico no disponible (" + ex.GetType().Name + ").");
                }
            }

            private static string[] FindFiles()
            {
                var found = new List<string>();
                try
                {
                    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk");
                    if (!Directory.Exists(root))
                    {
                        return Array.Empty<string>();
                    }

                    foreach (string dir in Directory.GetDirectories(root, "AutoCAD *"))
                    {
                        foreach (string name in new[] { "PublishToWeb PNG.pmp", "PublishToWeb JPG.pmp" })
                        {
                            foreach (string f in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
                            {
                                found.Add(f);
                                break;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Sin ruta legible: el diagnóstico se omite.
                }

                return found.ToArray();
            }
        }

        /// <summary>
        /// Diagnóstico de F7 (§5B.7): mientras existe, registra en plugin.log (INFO) cada objeto de la base de datos que
        /// se abre para escritura o se modifica, y cada variable de sistema que cambia. Una lectura correcta no produce
        /// ninguna línea. Se desuscribe en Dispose (siempre, vía using). Tope de <see cref="MaxDiagnosticLines"/> líneas.
        /// </summary>
        internal sealed class ModificationDiagnostics : IDisposable
        {
            private readonly Database _db;
            private readonly string _prefix;
            private int _lines;
            private int _events;

            public ModificationDiagnostics(Database db, string prefix)
            {
                _db = db;
                _prefix = prefix;
                _db.ObjectOpenedForModify += OnOpenedForModify;
                _db.ObjectModified += OnModified;
                AcApp.SystemVariableChanged += OnSysVarChanged;
            }

            public void Dispose()
            {
                try
                {
                    _db.ObjectOpenedForModify -= OnOpenedForModify;
                    _db.ObjectModified -= OnModified;
                    AcApp.SystemVariableChanged -= OnSysVarChanged;
                }
                catch (Exception ex)
                {
                    PluginLog.Warn(_prefix + ": no se pudo desuscribir el diagnóstico (" + ex.GetType().Name + ").");
                }

                if (_events > MaxDiagnosticLines)
                {
                    PluginLog.Info(_prefix + ": " + _events.ToString(CultureInfo.InvariantCulture)
                        + " eventos de modificación en total (solo se registraron los primeros "
                        + MaxDiagnosticLines.ToString(CultureInfo.InvariantCulture) + ").");
                }
            }

            private void OnOpenedForModify(object sender, ObjectEventArgs e) => LogObject("abierto para escritura", e);

            private void OnModified(object sender, ObjectEventArgs e) => LogObject("objeto modificado", e);

            private void LogObject(string what, ObjectEventArgs e)
            {
                _events++;
                if (_lines >= MaxDiagnosticLines)
                {
                    return;
                }

                _lines++;
                try
                {
                    DBObject o = e.DBObject;
                    string handle = AcadUtil.HandleToString(o.Handle);
                    string tipo = o.GetRXClass()?.DxfName ?? o.GetType().Name;
                    PluginLog.Info(_prefix + ": " + what + " " + handle + " (" + tipo + ", " + o.GetType().Name + ").");
                }
                catch (Exception ex)
                {
                    PluginLog.Info(_prefix + ": " + what + " (no legible: " + ex.GetType().Name + ").");
                }
            }

            private void OnSysVarChanged(object sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
            {
                _events++;
                if (_lines >= MaxDiagnosticLines)
                {
                    return;
                }

                _lines++;
                try
                {
                    PluginLog.Info(_prefix + ": sysvar cambiada " + e.Name + " (changed=" + e.Changed.ToString(CultureInfo.InvariantCulture) + ").");
                }
                catch (Exception ex)
                {
                    PluginLog.Info(_prefix + ": sysvar cambiada (no legible: " + ex.GetType().Name + ").");
                }
            }
        }
    }
}
