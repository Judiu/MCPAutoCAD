using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Layouts
{
    /// <summary>Geometría de un viewport en planta sin giro: tamaño y centro en papel, centro en modelo y CustomScale.</summary>
    public sealed class ViewportGeometry
    {
        public ViewportGeometry(double customScale, double width, double height, double paperX, double paperY, double modelX, double modelY)
        {
            CustomScale = customScale;
            Width = width;
            Height = height;
            PaperX = paperX;
            PaperY = paperY;
            ModelX = modelX;
            ModelY = modelY;
        }

        /// <summary>Unidades de papel por unidad de modelo.</summary>
        public double CustomScale { get; }

        public double Width { get; }

        public double Height { get; }

        public double PaperX { get; }

        public double PaperY { get; }

        public double ModelX { get; }

        public double ModelY { get; }

        /// <summary>Caja del viewport en el papel.</summary>
        public BBox2 PaperBox => BBox2.FromCorners(PaperX - Width / 2, PaperY - Height / 2, PaperX + Width / 2, PaperY + Height / 2);

        /// <summary>Ventana de modelo que muestra (Width/CustomScale × Height/CustomScale alrededor del centro de modelo).</summary>
        public BBox2 ModelWindow
        {
            get
            {
                double hw = Width / CustomScale / 2, hh = Height / CustomScale / 2;
                return BBox2.FromCorners(ModelX - hw, ModelY - hh, ModelX + hw, ModelY + hh);
            }
        }
    }

    /// <summary>
    /// Cálculos puros de crear_viewport / ajustar_viewport y ordenar_layouts (plugin 0.9.0, fase M2).
    /// </summary>
    public static class ViewportPlanner
    {
        /// <summary>Tamaño máximo de un viewport en unidades de papel (10 m de papel en mm).</summary>
        public const double MaxPaperSize = 10000;

        /// <summary>Tamaño mínimo de un viewport en unidades de papel.</summary>
        public const double MinPaperSize = 1e-3;

        /// <summary>Valores admitidos de unidades_modelo.</summary>
        public static readonly string[] ModelUnitNames = { "m", "cm", "mm" };

        /// <summary>Metros por unidad para "m", "cm" o "mm"; null si no es uno de ellos.</summary>
        public static double? MetersPerModelUnit(string? name) => name switch
        {
            "m" => 1.0,
            "cm" => 0.01,
            "mm" => 0.001,
            _ => null,
        };

        /// <summary>
        /// Metros por unidad de modelo a usar: los de INSUNITS (<paramref name="insunitsMeters"/>, null con INSUNITS = 0 o
        /// sin equivalencia métrica) o los de <paramref name="unidadesModelo"/>. Si ambos existen y no coinciden → problema
        /// (no se adivina cuál es el correcto). Si no hay ninguno → problema. Devuelve null y el motivo en
        /// <paramref name="problem"/>.
        /// </summary>
        public static double? ResolveModelMeters(double? insunitsMeters, string? unidadesModelo, out string? problem)
        {
            problem = null;
            double? given = unidadesModelo == null ? null : MetersPerModelUnit(unidadesModelo);
            if (unidadesModelo != null && given == null)
            {
                problem = "unidades_modelo: debe ser m, cm o mm";
                return null;
            }

            if (insunitsMeters is double ins && ins > 0 && double.IsFinite(ins))
            {
                if (given != null && Math.Abs(given.Value - ins) > ins * 1e-9)
                {
                    problem = "unidades_modelo: '" + unidadesModelo + "' no coincide con las unidades que declara el dibujo (INSUNITS); omita unidades_modelo o corrija INSUNITS";
                    return null;
                }

                return ins;
            }

            if (given == null)
            {
                problem = "unidades_modelo: el dibujo no declara unidades métricas (INSUNITS); indique 'm', 'cm' o 'mm' para calcular la escala";
                return null;
            }

            return given;
        }

        /// <summary>
        /// Viewport que muestra <paramref name="window"/> a escala 1:<paramref name="denominator"/>, centrado en el papel en
        /// (<paramref name="paperX"/>, <paramref name="paperY"/>). Lanza ArgumentException si la ventana tiene ancho o alto 0
        /// o el viewport resultante queda fuera de [<see cref="MinPaperSize"/>, <see cref="MaxPaperSize"/>].
        /// </summary>
        public static ViewportGeometry FromWindow(
            BBox2 window, double denominator, double paperMmPerSpaceUnit, double modelMetersPerUnit, double paperX, double paperY)
        {
            double cs = CheckedScale(denominator, paperMmPerSpaceUnit, modelMetersPerUnit);
            return FromWindowWithScale(window, cs, paperX, paperY);
        }

        /// <summary>Como <see cref="FromWindow"/> con el CustomScale ya conocido.</summary>
        public static ViewportGeometry FromWindowWithScale(BBox2 window, double customScale, double paperX, double paperY)
        {
            if (!(customScale > 0) || !double.IsFinite(customScale))
            {
                throw new ArgumentException("escala: factor de escala no válido");
            }

            if (!(window.Width > 0) || !(window.Height > 0))
            {
                throw new ArgumentException("ventana_modelo: debe tener ancho y alto mayores que 0");
            }

            CheckPaperPoint(paperX, paperY);
            double w = window.Width * customScale;
            double h = window.Height * customScale;
            CheckSize(w, h);
            return new ViewportGeometry(customScale, w, h, paperX, paperY, (window.Xmin + window.Xmax) / 2, (window.Ymin + window.Ymax) / 2);
        }

        /// <summary>
        /// Nueva escala para un viewport existente que conserva su tamaño en papel y su centro de modelo (la ventana de
        /// modelo crece o encoge alrededor de ese centro).
        /// </summary>
        public static ViewportGeometry Rescale(
            double width, double height, double paperX, double paperY, double modelX, double modelY, double customScale)
        {
            if (!(customScale > 0) || !double.IsFinite(customScale))
            {
                throw new ArgumentException("escala: factor de escala no válido");
            }

            CheckPaperPoint(paperX, paperY);
            CheckSize(width, height);
            if (!double.IsFinite(modelX) || !double.IsFinite(modelY))
            {
                throw new ArgumentException("el centro de modelo del viewport no es finito");
            }

            return new ViewportGeometry(customScale, width, height, paperX, paperY, modelX, modelY);
        }

        /// <summary>CustomScale de 1:N con comprobaciones (ArgumentException si no es finito o no es positivo).</summary>
        public static double CheckedScale(double denominator, double paperMmPerSpaceUnit, double modelMetersPerUnit)
        {
            if (!(denominator > 0) || !(paperMmPerSpaceUnit > 0) || !(modelMetersPerUnit > 0))
            {
                throw new ArgumentException("escala: no se puede calcular el factor de escala");
            }

            double cs = LayoutRules.CustomScale(denominator, paperMmPerSpaceUnit, modelMetersPerUnit);
            if (!(cs > 0) || !double.IsFinite(cs))
            {
                throw new ArgumentException("escala: no se puede calcular el factor de escala");
            }

            return cs;
        }

        private static void CheckPaperPoint(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e7 || Math.Abs(y) > 1e7)
            {
                throw new ArgumentException("centro_papel: debe ser un punto finito con |x|, |y| ≤ 1e7");
            }
        }

        private static void CheckSize(double w, double h)
        {
            if (!double.IsFinite(w) || !double.IsFinite(h) || w > MaxPaperSize || h > MaxPaperSize)
            {
                throw new ArgumentException(
                    "el viewport resultante mide " + Fmt(w) + " × " + Fmt(h) + " unidades de papel (máximo "
                    + Fmt(MaxPaperSize) + "): use una escala menor (1:N con N mayor) o una ventana más pequeña");
            }

            if (w < MinPaperSize || h < MinPaperSize)
            {
                throw new ArgumentException(
                    "el viewport resultante mide " + Fmt(w) + " × " + Fmt(h) + " unidades de papel: use una escala mayor (1:N con N menor) o una ventana más grande");
            }
        }

        /// <summary>
        /// Nuevo orden de pestañas: los nombres de <paramref name="requested"/> primero, en ese orden, y después los demás
        /// layouts de <paramref name="current"/> en su orden actual. Comparación sin mayúsculas; devuelve los nombres tal
        /// como están en <paramref name="current"/>. ArgumentException si hay nombres repetidos, desconocidos o la lista
        /// está vacía.
        /// </summary>
        public static List<string> Reorder(IReadOnlyList<string> current, IReadOnlyList<string> requested)
        {
            if (requested == null || requested.Count == 0)
            {
                throw new ArgumentException("orden: debe tener al menos un layout");
            }

            var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string c in current)
            {
                byKey[c] = c;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            var result = new List<string>(current.Count);
            for (int i = 0; i < requested.Count; i++)
            {
                string r = requested[i];
                if (!seen.Add(r))
                {
                    throw new ArgumentException("orden[" + i.ToString(CultureInfo.InvariantCulture) + "]: layout repetido '" + r + "'");
                }

                if (byKey.TryGetValue(r, out string? real))
                {
                    result.Add(real);
                }
                else
                {
                    missing.Add(r);
                }
            }

            if (missing.Count > 0)
            {
                throw new ArgumentException(
                    "orden: layouts inexistentes: " + string.Join(", ", missing) + " (layouts del dibujo: " + string.Join(", ", current) + ")");
            }

            foreach (string c in current)
            {
                if (!seen.Contains(c))
                {
                    result.Add(c);
                }
            }

            return result;
        }

        /// <summary>true si las cajas se solapan con área (tocarse en un borde no cuenta).</summary>
        public static bool Overlaps(BBox2 a, BBox2 b) =>
            a.Xmin < b.Xmax && b.Xmin < a.Xmax && a.Ymin < b.Ymax && b.Ymin < a.Ymax;

        private static string Fmt(double v) => double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "∞";
    }
}
