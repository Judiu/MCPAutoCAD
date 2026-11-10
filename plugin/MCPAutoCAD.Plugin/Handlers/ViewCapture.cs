using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.GraphicsSystem;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// vista_png mecanismo "captura" (protocol.md §5B.7): API gráfica nativa. SOLO hilo principal.
    /// 1) DocumentExtension.CapturePreviewImage(Document, uint, uint) → Bitmap (acmgd.dll, AutoCAD 2026).
    /// 2) Si devuelve null: Manager.GetCurrentAcGsView(CVPORT) + View.GetSnapshot(Rectangle) (área visible en
    ///    pantalla), recortado al aspecto pedido y escalado a ancho×alto.
    /// </summary>
    internal static class ViewCapture
    {
        /// <summary>Resultado de una captura: PNG y tamaño real en píxeles.</summary>
        internal sealed class Result
        {
            public Result(byte[] png, int width, int height, string api)
            {
                Png = png;
                Width = width;
                Height = height;
                Api = api;
            }

            public byte[] Png { get; }

            public int Width { get; }

            public int Height { get; }

            /// <summary>API que produjo la imagen ("CapturePreviewImage" o "GetSnapshot"), para el log.</summary>
            public string Api { get; }

            /// <summary>Zona WCS real de la imagen (0.9, con márgenes de hoja). Null si el mecanismo no la calcula.</summary>
            public ImageCoverage? Coverage { get; set; }
        }

        public static Result Capture(Document doc, int width, int height)
        {
            // F7 (0.3.2): este mecanismo no abre ni modifica ningún objeto de la base de datos ni cambia sysvars desde
            // nuestro código (UpdateScreen es un redibujo; CapturePreviewImage/GetSnapshot leen la imagen), y el gate en
            // vivo 0.3.1 solo registró modificaciones durante el PLOT (PublishEngine). Por eso aquí NO se suspende la
            // grabación de deshacer (haría falta DocumentLock, que la captura validada no usa); en su lugar se registra
            // el mismo diagnóstico que en el plot: si AutoCAD modificara algo por dentro, plugin.log lo diría
            // ("vista_png captura: objeto modificado …") y se aplicaría UndoRecordingSuspension también aquí.
            using var diag = new PlotCapture.ModificationDiagnostics(doc.Database, "vista_png captura");

            // Que la pantalla refleje la vista recién fijada antes de capturar (§5B.7: zoom al bbox → captura).
            try
            {
                AcApp.UpdateScreen();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("UpdateScreen falló antes de capturar (" + ex.GetType().Name + ": " + ex.Message + ").");
            }

            Bitmap? bitmap = doc.CapturePreviewImage((uint)width, (uint)height);
            string api = "CapturePreviewImage";
            if (bitmap == null)
            {
                PluginLog.Warn("CapturePreviewImage devolvió null; se intenta GraphicsManager/GetSnapshot.");
                bitmap = SnapshotCurrentView(doc, width, height);
                api = "GetSnapshot";
            }

            if (bitmap == null)
            {
                throw new InvalidOperationException("CapturePreviewImage devolvió null");
            }

            using (bitmap)
            {
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                return new Result(ms.ToArray(), bitmap.Width, bitmap.Height, api);
            }
        }

        /// <summary>
        /// Alternativa: instantánea de la vista GS del viewport actual (solo el área visible en pantalla), recortada
        /// al centro con el aspecto ancho:alto y escalada a ese tamaño. null si no hay vista GS.
        /// </summary>
        private static Bitmap? SnapshotCurrentView(Document doc, int width, int height)
        {
            Manager? gsm = doc.GraphicsManager;
            if (gsm == null)
            {
                return null;
            }

            int cvport = AcadUtil.GetIntSysVar("CVPORT");
            View? view = gsm.GetCurrentAcGsView(cvport);
            if (view == null)
            {
                return null;
            }

            // La vista GS pertenece a AutoCAD: no se destruye. Se suprime el finalizador del envoltorio por si
            // intentara liberar el objeto nativo desde el hilo del finalizador.
            GC.SuppressFinalize(view);

            // SCREENSIZE = tamaño en píxeles del viewport actual (Point2d).
            object? screen = AcApp.GetSystemVariable("SCREENSIZE");
            int screenW;
            int screenH;
            if (screen is Autodesk.AutoCAD.Geometry.Point2d size)
            {
                screenW = (int)Math.Round(size.X);
                screenH = (int)Math.Round(size.Y);
            }
            else
            {
                screenW = width;
                screenH = height;
            }

            if (screenW <= 0 || screenH <= 0)
            {
                return null;
            }

            using Bitmap? raw = view.GetSnapshot(new Rectangle(0, 0, screenW, screenH));
            if (raw == null)
            {
                return null;
            }

            // Recorte centrado al aspecto pedido (sin deformar) y escalado.
            double aspect = (double)width / height;
            int cropW = raw.Width;
            int cropH = (int)Math.Round(cropW / aspect);
            if (cropH > raw.Height)
            {
                cropH = raw.Height;
                cropW = (int)Math.Round(cropH * aspect);
            }

            cropW = Math.Max(1, Math.Min(cropW, raw.Width));
            cropH = Math.Max(1, Math.Min(cropH, raw.Height));
            var src = new Rectangle((raw.Width - cropW) / 2, (raw.Height - cropH) / 2, cropW, cropH);

            var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            try
            {
                using Graphics g = Graphics.FromImage(result);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(raw, new Rectangle(0, 0, width, height), src, GraphicsUnit.Pixel);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
    }
}
