using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Layouts
{
    /// <summary>
    /// Reglas puras de layouts y viewports (plugin 0.9.0, fase M): nombres de layout válidos y escalas de viewport.
    /// Convención de escala: "1:N" = 1 unidad de papel real representa N unidades reales del modelo. Con papel en mm y
    /// modelo en metros, 1:50 → CustomScale = 1000 / 50 = 20 (unidades de papel por unidad de modelo).
    /// </summary>
    public static class LayoutRules
    {
        /// <summary>Longitud máxima del nombre de un layout en AutoCAD.</summary>
        public const int MaxNameLength = 255;

        /// <summary>Tolerancia relativa para reconocer una escala estándar (0,5 %).</summary>
        public const double StandardTolerance = 0.005;

        private static readonly char[] InvalidNameChars = { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' };

        /// <summary>Denominadores de las escalas métricas habituales en planos (1:N), de menor a mayor.</summary>
        public static readonly IReadOnlyList<int> StandardDenominators = new[]
        {
            1, 2, 5, 10, 15, 20, 25, 30, 40, 50, 75, 100, 125, 150, 200, 250, 300, 400, 500, 750, 1000, 1250, 1500, 2000, 2500, 5000,
        };

        /// <summary>
        /// Motivo por el que <paramref name="name"/> no sirve como nombre de layout, o null si sirve: vacío, más de 255
        /// caracteres, espacios al inicio o al final, caracteres de control o &lt; &gt; / \ " : ; ? * | , = `, o "Model"
        /// (reservado para el espacio modelo, sin distinguir mayúsculas).
        /// </summary>
        public static string? NameProblem(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "el nombre está vacío";
            }

            if (name.Length > MaxNameLength)
            {
                return "el nombre tiene más de " + MaxNameLength.ToString(CultureInfo.InvariantCulture) + " caracteres";
            }

            if (name.Trim().Length != name.Length)
            {
                return "el nombre empieza o termina con espacios";
            }

            foreach (char c in name)
            {
                if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                {
                    return "el nombre contiene caracteres de control o invisibles";
                }
            }

            if (name.IndexOfAny(InvalidNameChars) >= 0)
            {
                return "el nombre contiene alguno de estos caracteres no admitidos: < > / \\ \" : ; ? * | , = `";
            }

            if (string.Equals(name, "Model", StringComparison.OrdinalIgnoreCase))
            {
                return "'Model' está reservado para el espacio modelo";
            }

            return null;
        }

        /// <summary>
        /// Lee una escala "1:N" o "1/N" (N &gt; 0 y ≤ 100000, admite decimales con punto). Devuelve false si no tiene esa
        /// forma. No se admiten ampliaciones (2:1): en planos de edificación no se usan.
        /// </summary>
        public static bool TryParseScale(string? text, out double denominator)
        {
            denominator = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string t = text.Trim();
            int sep = t.IndexOfAny(new[] { ':', '/' });
            if (sep <= 0 || sep == t.Length - 1)
            {
                return false;
            }

            string left = t.Substring(0, sep).Trim();
            string right = t.Substring(sep + 1).Trim();
            if (left != "1")
            {
                return false;
            }

            if (!double.TryParse(right, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n)
                || !double.IsFinite(n) || n <= 0 || n > 100000)
            {
                return false;
            }

            denominator = n;
            return true;
        }

        /// <summary>Texto "1:N" (N sin decimales si es entero, si no con hasta 2 decimales).</summary>
        public static string ScaleLabel(double denominator)
        {
            double r = Math.Round(denominator);
            string n = Math.Abs(denominator - r) < 1e-9
                ? r.ToString("0", CultureInfo.InvariantCulture)
                : denominator.ToString("0.##", CultureInfo.InvariantCulture);
            return "1:" + n;
        }

        /// <summary>
        /// Denominador N de la escala real de un viewport, o null si no se puede saber: CustomScale es unidades de papel por
        /// unidad de modelo; <paramref name="paperMmPerUnit"/> son los mm por unidad de papel (1 si el papel está en mm,
        /// 25,4 en pulgadas) y <paramref name="modelMetersPerUnit"/> los metros por unidad de modelo (null con INSUNITS = 0).
        /// N = (mm reales por unidad de modelo) / (mm de papel por unidad de modelo).
        /// </summary>
        public static double? Denominator(double customScale, double paperMmPerUnit, double? modelMetersPerUnit)
        {
            if (modelMetersPerUnit == null || !(customScale > 0) || !(paperMmPerUnit > 0) || !double.IsFinite(customScale))
            {
                return null;
            }

            double n = modelMetersPerUnit.Value * 1000.0 / (customScale * paperMmPerUnit);
            return double.IsFinite(n) && n > 0 ? n : null;
        }

        /// <summary>
        /// mm reales de papel por unidad de espacio papel, teniendo en cuenta la escala de impresión del layout
        /// (<paramref name="plotPaper"/> unidades de papel = <paramref name="plotDrawing"/> unidades de dibujo). null si
        /// el layout imprime "ajustado al papel" (la escala real depende de la extensión impresa) o si los datos no son
        /// válidos. Con impresión 1:1 es <paramref name="paperMmPerUnit"/>.
        /// </summary>
        public static double? PaperMmPerSpaceUnit(double? paperMmPerUnit, double plotPaper, double plotDrawing, bool scaledToFit)
        {
            if (scaledToFit || paperMmPerUnit == null || !(plotPaper > 0) || !(plotDrawing > 0)
                || !double.IsFinite(plotPaper) || !double.IsFinite(plotDrawing))
            {
                return null;
            }

            double v = paperMmPerUnit.Value * plotPaper / plotDrawing;
            return double.IsFinite(v) && v > 0 ? v : null;
        }

        /// <summary>CustomScale (unidades de papel por unidad de modelo) para la escala 1:<paramref name="denominator"/>.</summary>
        public static double CustomScale(double denominator, double paperMmPerUnit, double modelMetersPerUnit) =>
            modelMetersPerUnit * 1000.0 / (denominator * paperMmPerUnit);

        /// <summary>La escala estándar que coincide con <paramref name="denominator"/> dentro de <see cref="StandardTolerance"/>, o null.</summary>
        public static int? MatchingStandard(double denominator)
        {
            foreach (int s in StandardDenominators)
            {
                if (Math.Abs(denominator - s) <= s * StandardTolerance)
                {
                    return s;
                }
            }

            return null;
        }

        /// <summary>
        /// La escala estándar más grande (denominador más pequeño) con la que una ventana de modelo cabe en el viewport:
        /// el menor estándar ≥ <paramref name="requiredDenominator"/> (con la tolerancia). null si ninguno alcanza.
        /// </summary>
        public static int? FittingStandard(double requiredDenominator)
        {
            if (!(requiredDenominator > 0) || !double.IsFinite(requiredDenominator))
            {
                return null;
            }

            foreach (int s in StandardDenominators)
            {
                if (s >= requiredDenominator * (1 - StandardTolerance))
                {
                    return s;
                }
            }

            return null;
        }
    }
}
