using System;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Cálculo puro de <c>zoom_a</c> (solo vista): la ventana que encuadra una caja con un margen y con el aspecto de la vista
    /// actual. El margen es una fracción del lado MAYOR de la caja, añadida a cada lado (margen 0.1 = 10 %). Si la caja es
    /// un punto (ambos lados 0) se usa un lado de 1 unidad de dibujo antes del margen. Solo se amplía, nunca se recorta: la
    /// dimensión que sobra se agranda hasta el aspecto <paramref name="aspect"/> (ancho / alto).
    /// </summary>
    public static class ZoomFit
    {
        /// <summary>Margen máximo admitido (fracción del lado mayor).</summary>
        public const double MaxMargin = 10.0;

        /// <summary>Lado de la caja cuando es un punto (unidades de dibujo).</summary>
        public const double PointSide = 1.0;

        /// <summary>
        /// Ventana de vista para la caja [<paramref name="xmin"/>, <paramref name="xmax"/>] × [<paramref name="ymin"/>, <paramref name="ymax"/>].
        /// <exception cref="ArgumentException">Coordenadas no finitas, mínimo mayor que máximo, margen fuera de [0, 10] o aspecto no positivo.</exception>
        /// </summary>
        public static ViewWindow Fit(double xmin, double ymin, double xmax, double ymax, double margin, double aspect)
        {
            if (!double.IsFinite(xmin) || !double.IsFinite(ymin) || !double.IsFinite(xmax) || !double.IsFinite(ymax))
            {
                throw new ArgumentException("ventana: coordenada no finita");
            }

            if (xmin > xmax || ymin > ymax)
            {
                throw new ArgumentException(BBox2.MinGreaterThanMax);
            }

            if (!double.IsFinite(margin) || margin < 0 || margin > MaxMargin)
            {
                throw new ArgumentException("margen: debe estar entre 0 y 10");
            }

            if (!double.IsFinite(aspect) || aspect <= 0)
            {
                throw new ArgumentException("vista: aspecto no válido");
            }

            double w = xmax - xmin;
            double h = ymax - ymin;
            double major = Math.Max(w, h);
            double cw;
            double ch;
            if (major <= 0)
            {
                // Punto: lado de referencia PointSide; el margen se aplica a ese lado (nunca una ventana de tamaño 0).
                cw = PointSide * (1.0 + (2.0 * margin));
                ch = cw;
            }
            else
            {
                double pad = margin * major;
                cw = w + (2 * pad);
                ch = h + (2 * pad);
            }

            // Aspecto: se agranda la dimensión que falta, sin recortar nada.
            if (cw / ch > aspect)
            {
                ch = cw / aspect;
            }
            else
            {
                cw = ch * aspect;
            }

            double cx = xmin + ((xmax - xmin) / 2.0);
            double cy = ymin + ((ymax - ymin) / 2.0);
            return ViewWindow.FromView(cx, cy, cw, ch);
        }
    }
}
