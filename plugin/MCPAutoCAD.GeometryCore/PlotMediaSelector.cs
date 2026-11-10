using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Elección del tamaño de "papel" del dispositivo PublishToWeb PNG.pc3 para vista_png mecanismo "plot"
    /// (protocol.md §5B.7). Los nombres reales se leen del PC3 en tiempo de ejecución; aquí solo se elige.
    /// </summary>
    public static class PlotMediaSelector
    {
        // "XGA (768.00 x 1024.00 Pixels)", "VGA_(480.00_x_640.00_Pixels)", "1024x768", "800.00 × 600.00".
        private static readonly Regex DimensionPair = new Regex(
            @"(\d+(?:[.,]\d+)?)[\s_]*[xX×][\s_]*(\d+(?:[.,]\d+)?)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Devuelve el nombre del medio más pequeño (por área) con la MISMA forma (horizontal/vertical) que cubre
        /// <paramref name="width"/>×<paramref name="height"/> (a = ancho de la imagen, b = alto); si ninguno, el más
        /// pequeño que los cubre girado;
        /// si ninguno cubre, el de mayor área. Empates de área: el primero de la lista. Lista vacía o sin dimensiones
        /// válidas → null.
        /// </summary>
        public static string? Choose(IEnumerable<(string name, double a, double b)> media, int width, int height)
        {
            if (media == null)
            {
                throw new ArgumentNullException(nameof(media));
            }

            string? sameOrientation = null;
            double sameOrientationArea = double.PositiveInfinity;
            string? rotated = null;
            double rotatedArea = double.PositiveInfinity;
            string? largest = null;
            double largestArea = double.NegativeInfinity;

            foreach ((string name, double a, double b) in media)
            {
                if (name == null || !double.IsFinite(a) || !double.IsFinite(b) || a <= 0 || b <= 0)
                {
                    continue;
                }

                double area = a * b;
                // Misma forma que la petición (horizontal/vertical; un cuadrado casa con todo) y la cubre sin girar.
                bool sameShape = width == height || a == b || (a > b) == (width > height);
                if (sameShape && a >= width && b >= height && area < sameOrientationArea)
                {
                    sameOrientation = name;
                    sameOrientationArea = area;
                }

                if (b >= width && a >= height && area < rotatedArea)
                {
                    rotated = name;
                    rotatedArea = area;
                }

                if (area > largestArea)
                {
                    largest = name;
                    largestArea = area;
                }
            }

            return sameOrientation ?? rotated ?? largest;
        }

        /// <summary>
        /// Extrae los dos números de un nombre de medio como "XGA (768.00 x 1024.00 Pixels)" → (768, 1024).
        /// null si el nombre no contiene un par "N x M".
        /// </summary>
        public static (double a, double b)? ParseCanonical(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            Match m = DimensionPair.Match(name);
            if (!m.Success)
            {
                return null;
            }

            if (!TryParse(m.Groups[1].Value, out double a) || !TryParse(m.Groups[2].Value, out double b) || a <= 0 || b <= 0)
            {
                return null;
            }

            return (a, b);
        }

        private static bool TryParse(string text, out double value) =>
            double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
}
