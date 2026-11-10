using System;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Zona WCS que cubre realmente una imagen de vista_png (protocol.md §5B.7, <c>bbox_real</c>) y su escala. Inmutable.
    /// </summary>
    public sealed class ImageCoverage
    {
        public ImageCoverage(double xMin, double yMin, double xMax, double yMax, double unitsPerPixel)
        {
            XMin = xMin;
            YMin = yMin;
            XMax = xMax;
            YMax = yMax;
            UnitsPerPixel = unitsPerPixel;
        }

        public double XMin { get; }

        public double YMin { get; }

        public double XMax { get; }

        public double YMax { get; }

        /// <summary>Unidades de dibujo (INSUNITS del dibujo) por píxel de la imagen; uniforme en X e Y.</summary>
        public double UnitsPerPixel { get; }

        /// <summary>Salida [[xmin, ymin], [xmax, ymax]].</summary>
        public double[][] ToBbox2D() => new[] { new[] { XMin, YMin }, new[] { XMax, YMax } };
    }

    /// <summary>
    /// Cálculo puro de vista_png (protocol.md §5B.7) para el mecanismo "plot" con PublishToWeb PNG, cuyo papel tiene
    /// tamaños fijos del PC3. El trazado ajusta la ventana a la hoja (ScaleToFit) y centra: la escala es
    /// s = min(píxeles_x / ancho_ventana, píxeles_y / alto_ventana) y la imagen cubre píxeles / s unidades en cada eje,
    /// centrada en la ventana. Así se obtiene la zona real de la imagen aunque la hoja tenga márgenes.
    /// </summary>
    public static class PlotCoverage
    {
        /// <summary>
        /// Zona real de una imagen de <paramref name="pixelWidth"/>×<paramref name="pixelHeight"/> px trazada con la ventana
        /// <paramref name="window"/> (ScaleToFit y centrado). Exacta si el PNG es la hoja completa; el redondeo del trazado
        /// puede desviar ±1 px.
        /// </summary>
        public static ImageCoverage Compute(ViewWindow window, int pixelWidth, int pixelHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelWidth), "La imagen debe tener píxeles positivos.");
            }

            if (!(window.Width > 0) || !(window.Height > 0))
            {
                throw new ArgumentException("La ventana no tiene tamaño.");
            }

            double scale = Math.Min(pixelWidth / window.Width, pixelHeight / window.Height);
            double halfW = pixelWidth / (2.0 * scale);
            double halfH = pixelHeight / (2.0 * scale);
            return new ImageCoverage(
                window.CenterX - halfW,
                window.CenterY - halfH,
                window.CenterX + halfW,
                window.CenterY + halfH,
                1.0 / scale);
        }

        /// <summary>
        /// Zona real de la imagen cuando la hoja tiene márgenes (0.9, punto 4). Papel de <paramref name="paperWidth"/>×
        /// <paramref name="paperHeight"/> (en las mismas unidades que la ventana de trazado) con márgenes
        /// izquierdo/inferior/derecho/superior. ScaleToFit ajusta la ventana al área imprimible y la centra en ella; el centro
        /// de la hoja queda desplazado de la ventana si los márgenes son asimétricos. Con márgenes 0 equivale a
        /// <see cref="Compute"/>.
        /// </summary>
        public static ImageCoverage ComputeInPrintable(
            ViewWindow window,
            int pixelWidth,
            int pixelHeight,
            double paperWidth,
            double paperHeight,
            double marginLeft,
            double marginBottom,
            double marginRight,
            double marginTop)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelWidth), "La imagen debe tener píxeles positivos.");
            }

            if (!(window.Width > 0) || !(window.Height > 0))
            {
                throw new ArgumentException("La ventana no tiene tamaño.");
            }

            double printW = paperWidth - marginLeft - marginRight;
            double printH = paperHeight - marginBottom - marginTop;
            if (!(paperWidth > 0) || !(paperHeight > 0) || !(printW > 0) || !(printH > 0))
            {
                throw new ArgumentException("El área imprimible de la hoja no es válida.");
            }

            double scale = Math.Min(printW / window.Width, printH / window.Height);
            // Centro de la hoja respecto al centro de la ventana (en unidades de hoja, pasadas a WCS).
            double offsetX = ((paperWidth / 2.0) - (marginLeft + (printW / 2.0))) / scale;
            double offsetY = ((paperHeight / 2.0) - (marginBottom + (printH / 2.0))) / scale;
            double centerX = window.CenterX + offsetX;
            double centerY = window.CenterY + offsetY;
            double halfW = paperWidth / (2.0 * scale);
            double halfH = paperHeight / (2.0 * scale);
            return new ImageCoverage(
                centerX - halfW,
                centerY - halfH,
                centerX + halfW,
                centerY + halfH,
                (paperWidth / pixelWidth) / scale);
        }

        /// <summary>
        /// Tamaño de hoja (píxeles) que se pide al PC3 para un bbox pedido. ajustar_aspecto = true: el de la petición (la
        /// ventana se amplía a ese aspecto). false: el bbox ajustado a la caja ancho×alto máxima pedida (la imagen tiene el
        /// aspecto del bbox, sin ampliarlo).
        /// </summary>
        public static (int width, int height) MediaRequest(double xMin, double yMin, double xMax, double yMax, int ancho, int alto, bool ajustarAspecto)
        {
            if (ajustarAspecto)
            {
                return (ancho, alto);
            }

            double bw = xMax - xMin;
            double bh = yMax - yMin;
            if (!(bw > 0) || !(bh > 0))
            {
                return (ancho, alto);
            }

            if (bw / bh >= (double)ancho / alto)
            {
                return (ancho, Math.Max(1, (int)Math.Round(ancho * bh / bw)));
            }

            return (Math.Max(1, (int)Math.Round(alto * bw / bh)), alto);
        }

        /// <summary>
        /// Ventana de trazado para un bbox pedido. ajustar_aspecto = true: la caja se amplía al aspecto ancho:alto (nunca se
        /// recorta; ViewWindow.FromBbox). false: la ventana es exactamente el bbox; si la caja es degenerada (ancho o alto
        /// cero) se usa la ampliación normal para no dejar una ventana sin tamaño.
        /// </summary>
        public static ViewWindow Window(double xMin, double yMin, double xMax, double yMax, int ancho, int alto, bool ajustarAspecto)
        {
            if (ajustarAspecto || !(xMax - xMin > 0) || !(yMax - yMin > 0))
            {
                return ViewWindow.FromBbox(xMin, yMin, xMax, yMax, ancho, alto);
            }

            return ViewWindow.FromView(xMin + ((xMax - xMin) / 2.0), yMin + ((yMax - yMin) / 2.0), xMax - xMin, yMax - yMin);
        }
    }
}
