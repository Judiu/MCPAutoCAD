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

    /// <summary>Recorte de <see cref="PlotCoverage.CropToWindow"/>: píxeles (origen arriba a la izquierda) y zona WCS.</summary>
    public sealed class CropRect
    {
        public CropRect(int x, int y, int width, int height, ImageCoverage coverage)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Coverage = coverage;
        }

        public int X { get; }

        public int Y { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Zona WCS que cubre la imagen recortada (bbox_real).</summary>
        public ImageCoverage Coverage { get; }
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

        /// <summary>Margen mínimo (px por lado) para que <see cref="CropToWindow"/> recorte; por debajo no compensa.</summary>
        public const int MinCropMarginPx = 3;

        /// <summary>Diferencia relativa máxima admitida entre la escala declarada y la de cada eje para recortar.</summary>
        public const double MaxScaleMismatch = 0.005;

        /// <summary>
        /// Fallo 46: rectángulo de píxeles (origen arriba a la izquierda) de una imagen de <paramref name="pixelWidth"/>×
        /// <paramref name="pixelHeight"/> px que cubre <paramref name="coverage"/>, recortado a la ventana de trazado
        /// <paramref name="window"/> (redondeo hacia fuera: la ventana nunca se recorta). Devuelve null si la ventana ya
        /// ocupa la imagen (márgenes menores que <see cref="MinCropMarginPx"/> en ambos ejes) o los datos no son válidos.
        /// La zona del recorte se devuelve en <see cref="CropRect.Coverage"/> (misma escala).
        /// </summary>
        public static CropRect? CropToWindow(ImageCoverage coverage, ViewWindow window, int pixelWidth, int pixelHeight)
        {
            if (coverage == null || pixelWidth <= 0 || pixelHeight <= 0)
            {
                return null;
            }

            double upp = coverage.UnitsPerPixel;
            if (!(upp > 0) || double.IsInfinity(upp) || !(window.Width > 0) || !(window.Height > 0))
            {
                return null;
            }

            // Auditoría M3: la zona debe tener la misma escala en X y en Y que el PNG; si no (márgenes u orientación del
            // PC3 mal leídos), recortar cortaría contenido real: se devuelve la hoja completa.
            double uppX = (coverage.XMax - coverage.XMin) / pixelWidth;
            double uppY = (coverage.YMax - coverage.YMin) / pixelHeight;
            if (!(uppX > 0) || !(uppY > 0)
                || Math.Abs(uppX - upp) > MaxScaleMismatch * upp || Math.Abs(uppY - upp) > MaxScaleMismatch * upp)
            {
                return null;
            }

            // Tolerancia de redondeo: una fracción de píxel no añade una fila/columna.
            const double eps = 1e-6;
            int x0 = Clamp((int)Math.Floor((window.XMin - coverage.XMin) / upp + eps), 0, pixelWidth);
            int x1 = Clamp((int)Math.Ceiling((window.XMax - coverage.XMin) / upp - eps), 0, pixelWidth);
            int y0 = Clamp((int)Math.Floor((coverage.YMax - window.YMax) / upp + eps), 0, pixelHeight);
            int y1 = Clamp((int)Math.Ceiling((coverage.YMax - window.YMin) / upp - eps), 0, pixelHeight);
            if (x1 - x0 < 1 || y1 - y0 < 1)
            {
                return null;
            }

            int margenX = pixelWidth - (x1 - x0);
            int margenY = pixelHeight - (y1 - y0);
            if (margenX < 2 * MinCropMarginPx && margenY < 2 * MinCropMarginPx)
            {
                return null;
            }

            var zona = new ImageCoverage(
                coverage.XMin + x0 * upp,
                coverage.YMax - y1 * upp,
                coverage.XMin + x1 * upp,
                coverage.YMax - y0 * upp,
                upp);
            return new CropRect(x0, y0, x1 - x0, y1 - y0, zona);
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

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
