using System;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Ventana 2D (WCS, XY) de una vista: centro, ancho y alto. Cálculo puro para vista_png (protocol.md §5B.7):
    /// a partir de un bbox pedido se obtiene la ventana ampliada al aspecto ancho:alto de la imagen, sin recortar
    /// nunca la caja pedida y manteniendo su centro.
    /// </summary>
    public readonly struct ViewWindow
    {
        /// <summary>Margen mínimo relativo para cajas degeneradas (ancho y alto cero).</summary>
        public const double DegenerateRelativeMargin = 1e-6;

        /// <summary>Mensaje exacto del contrato (protocol.md §2.5).</summary>
        public const string MinGreaterThanMaxMessage = "bbox: mínimo mayor que máximo";

        private ViewWindow(double centerX, double centerY, double width, double height, double xMin, double yMin, double xMax, double yMax)
        {
            CenterX = centerX;
            CenterY = centerY;
            Width = width;
            Height = height;
            XMin = xMin;
            YMin = yMin;
            XMax = xMax;
            YMax = yMax;
        }

        public double CenterX { get; }

        public double CenterY { get; }

        public double Width { get; }

        public double Height { get; }

        public double XMin { get; }

        public double YMin { get; }

        public double XMax { get; }

        public double YMax { get; }

        /// <summary>Caja capturada en el formato de salida de vista_png: [[xmin, ymin], [xmax, ymax]].</summary>
        public double[][] ToBbox2D() => new[] { new[] { XMin, YMin }, new[] { XMax, YMax } };

        /// <summary>
        /// Amplía la caja (nunca la recorta) al aspecto <paramref name="pixelWidth"/>:<paramref name="pixelHeight"/>
        /// manteniendo el centro. Caja degenerada (ancho y alto 0) → alto mínimo = 1e-6 × max(1, |coordenadas|).
        /// </summary>
        /// <exception cref="ArgumentException">Coordenadas no finitas, o mínimo mayor que máximo.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Tamaño de imagen no positivo.</exception>
        public static ViewWindow FromBbox(double xMin, double yMin, double xMax, double yMax, int pixelWidth, int pixelHeight)
        {
            if (!double.IsFinite(xMin) || !double.IsFinite(yMin) || !double.IsFinite(xMax) || !double.IsFinite(yMax))
            {
                throw new ArgumentException("bbox: coordenada no finita");
            }

            if (xMin > xMax || yMin > yMax)
            {
                throw new ArgumentException(MinGreaterThanMaxMessage);
            }

            if (pixelWidth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelWidth), "El ancho en píxeles debe ser positivo.");
            }

            if (pixelHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelHeight), "El alto en píxeles debe ser positivo.");
            }

            double aspect = (double)pixelWidth / pixelHeight;
            double cx = xMin + ((xMax - xMin) / 2.0);
            double cy = yMin + ((yMax - yMin) / 2.0);
            double w = xMax - xMin;
            double h = yMax - yMin;

            double scale = Math.Max(1.0, Math.Max(Math.Max(Math.Abs(xMin), Math.Abs(xMax)), Math.Max(Math.Abs(yMin), Math.Abs(yMax))));
            double minSize = DegenerateRelativeMargin * scale;
            if (w < minSize && h < minSize)
            {
                // Punto (o caja casi nula): se le da un tamaño mínimo para que la vista sea válida.
                h = minSize;
                w = Math.Max(w, 0.0);
            }

            // Ampliar la dimensión corta hasta el aspecto pedido (h > 0 o w > 0 garantizado aquí).
            if (h <= 0 || w / h > aspect)
            {
                h = w / aspect;
            }
            else
            {
                w = h * aspect;
            }

            // La ventana debe contener íntegra la caja pedida, también frente al redondeo.
            double outXMin = Math.Min(xMin, cx - (w / 2.0));
            double outXMax = Math.Max(xMax, cx + (w / 2.0));
            double outYMin = Math.Min(yMin, cy - (h / 2.0));
            double outYMax = Math.Max(yMax, cy + (h / 2.0));

            return new ViewWindow(cx, cy, w, h, outXMin, outYMin, outXMax, outYMax);
        }

        /// <summary>Ventana de la vista actual del usuario (CenterPoint / Width / Height de la vista), sin cambios.</summary>
        /// <exception cref="ArgumentException">Valores no finitos o tamaño negativo.</exception>
        public static ViewWindow FromView(double centerX, double centerY, double width, double height)
        {
            if (!double.IsFinite(centerX) || !double.IsFinite(centerY) || !double.IsFinite(width) || !double.IsFinite(height))
            {
                throw new ArgumentException("vista: valor no finito");
            }

            if (width < 0 || height < 0)
            {
                throw new ArgumentException("vista: tamaño negativo");
            }

            return new ViewWindow(
                centerX,
                centerY,
                width,
                height,
                centerX - (width / 2.0),
                centerY - (height / 2.0),
                centerX + (width / 2.0),
                centerY + (height / 2.0));
        }
    }
}
