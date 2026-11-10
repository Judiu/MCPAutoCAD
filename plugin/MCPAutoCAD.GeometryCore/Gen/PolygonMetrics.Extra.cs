using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Métricas de polígono de Fase 4A (protocol.md §5E.3.3 y §5E.9): centroide, pertenencia, distancia al borde, punto
    /// interior para la etiqueta de recinto y formato de área "12,35 m²". Puro. Polígono = lazo cerrado implícito (el
    /// último vértice se une al primero; no se repite).
    /// </summary>
    public static partial class PolygonMetrics
    {
        /// <summary>Tolerancia de borde de <see cref="Contains"/>.</summary>
        public const double BoundaryTolerance = 1e-9;

        /// <summary>Celdas por lado de la rejilla del polo de inaccesibilidad (≥ 32).</summary>
        public const int InteriorGridCells = 32;

        /// <summary>Tolerancia (distancia euclídea absoluta, la de WallOutline/PolylineValidator) de vértices duplicados.</summary>
        public const double DuplicateTolerance = 1e-9;

        /// <summary>
        /// Colapsa vértices CONSECUTIVOS duplicados (distancia euclídea ≤ <paramref name="tol"/>, como WallOutline y
        /// PolylineValidator): se conserva el primero de cada grupo. Con <paramref name="lazoCerrado"/> (lazo cerrado
        /// implícito) se quitan además los últimos que coincidan con el primero; en una polilínea abierta, no.
        /// <paramref name="eliminados"/> = vértices quitados. No valida nada más (≥ 3 vértices, autointersección y área los
        /// comprueba el llamador después). Lo usan recinto (contorno por handle, §5E.3.3) y muro (eje por handle, §5E.3.1).
        /// </summary>
        public static List<(double x, double y)> CollapseConsecutiveDuplicates(
            IReadOnlyList<(double x, double y)> v, out int eliminados, double tol = DuplicateTolerance, bool lazoCerrado = true)
        {
            if (v == null)
            {
                throw new ArgumentNullException(nameof(v));
            }

            var r = new List<(double x, double y)>(v.Count);
            foreach ((double x, double y) p in v)
            {
                if (r.Count > 0 && SameVertex(r[r.Count - 1], p, tol))
                {
                    continue;
                }

                r.Add(p);
            }

            while (lazoCerrado && r.Count > 1 && SameVertex(r[r.Count - 1], r[0], tol))
            {
                r.RemoveAt(r.Count - 1);
            }

            eliminados = v.Count - r.Count;
            return r;
        }

        /// <summary>Distancia EUCLÍDEA ≤ tol: el mismo criterio que WallOutline y PolylineValidator (vértice duplicado).</summary>
        private static bool SameVertex((double x, double y) a, (double x, double y) b, double tol) => Dist(a, b) <= tol;

        /// <summary>Advertencia de recinto cuando se colapsaron vértices duplicados de la polilínea <paramref name="handle"/>.</summary>
        public static string CollapsedDuplicatesWarning(string handle, int n) =>
            "contorno: la polilínea " + handle + " tiene " + n.ToString(CultureInfo.InvariantCulture)
            + " vértice(s) consecutivo(s) duplicado(s); se ignoraron para el área, el centroide y la etiqueta.";

        /// <summary>Advertencia de muro cuando se colapsaron vértices duplicados del eje dado por handle (§5E.3.1).</summary>
        public static string CollapsedAxisDuplicatesWarning(string handle, int n) =>
            "eje: la polilínea " + handle + " tiene " + n.ToString(CultureInfo.InvariantCulture)
            + " vértice(s) consecutivo(s) duplicado(s); se ignoraron.";

        /// <summary>Centroide del polígono (fórmula del cordón); área 0 (≤ 1e-12) → centro de la caja.</summary>
        public static (double x, double y) Centroid(IReadOnlyList<(double x, double y)> v)
        {
            RequireVertices(v);
            (double ox, double oy) = v[0]; // origen local para no perder precisión con coordenadas grandes
            double a2 = 0;
            double cx = 0;
            double cy = 0;
            for (int i = 0; i < v.Count; i++)
            {
                double x1 = v[i].x - ox;
                double y1 = v[i].y - oy;
                double x2 = v[(i + 1) % v.Count].x - ox;
                double y2 = v[(i + 1) % v.Count].y - oy;
                double cross = (x1 * y2) - (x2 * y1);
                a2 += cross;
                cx += (x1 + x2) * cross;
                cy += (y1 + y2) * cross;
            }

            double area = a2 / 2.0;
            if (Math.Abs(area) <= 1e-12)
            {
                (double xmin, double ymin, double xmax, double ymax) = Box(v);
                return ((xmin + xmax) / 2.0, (ymin + ymax) / 2.0);
            }

            return ((cx / (6.0 * area)) + ox, (cy / (6.0 * area)) + oy);
        }

        /// <summary>Ray casting; un punto sobre el borde (distancia ≤ 1e-9) cuenta como dentro.</summary>
        public static bool Contains(IReadOnlyList<(double x, double y)> v, double x, double y)
        {
            RequireVertices(v);
            if (DistanceToBoundary(v, x, y) <= BoundaryTolerance)
            {
                return true;
            }

            bool inside = false;
            int n = v.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                (double xi, double yi) = v[i];
                (double xj, double yj) = v[j];
                if ((yi > y) != (yj > y))
                {
                    double xCross = xi + ((y - yi) * (xj - xi) / (yj - yi));
                    if (x < xCross)
                    {
                        inside = !inside;
                    }
                }
            }

            return inside;
        }

        /// <summary>
        /// Centroide si cae dentro (<paramref name="esCentroide"/> = true); si no, polo de inaccesibilidad aproximado:
        /// rejilla de 32×32 celdas sobre la caja, la celda interior con mayor distancia al borde, refinada 2 veces
        /// alrededor de la mejor con paso/4 (<paramref name="esCentroide"/> = false).
        /// </summary>
        public static (double x, double y) InteriorPoint(IReadOnlyList<(double x, double y)> v, out bool esCentroide)
        {
            RequireVertices(v);
            (double x, double y) c = Centroid(v);
            if (Contains(v, c.x, c.y))
            {
                esCentroide = true;
                return c;
            }

            esCentroide = false;
            (double xmin, double ymin, double xmax, double ymax) = Box(v);
            for (int cells = InteriorGridCells; cells <= InteriorGridCells * 8; cells *= 2)
            {
                double sx = (xmax - xmin) / cells;
                double sy = (ymax - ymin) / cells;
                (double x, double y)? best = null;
                double bestD = double.NegativeInfinity;
                for (int i = 0; i < cells; i++)
                {
                    for (int j = 0; j < cells; j++)
                    {
                        double px = xmin + ((i + 0.5) * sx);
                        double py = ymin + ((j + 0.5) * sy);
                        Consider(v, px, py, ref best, ref bestD);
                    }
                }

                if (best == null)
                {
                    continue; // polígono demasiado fino para esta rejilla: se densifica
                }

                for (int r = 0; r < 2; r++)
                {
                    // best no puede volver a null (Consider solo lo sustituye por otro punto); GetValueOrDefault
                    // evita el falso positivo CS8629 del análisis de flujo a través de 'ref'.
                    double ox = best.GetValueOrDefault().x;
                    double oy = best.GetValueOrDefault().y;
                    sx /= 4.0;
                    sy /= 4.0;
                    for (int i = -4; i <= 4; i++)
                    {
                        for (int j = -4; j <= 4; j++)
                        {
                            Consider(v, ox + (i * sx), oy + (j * sy), ref best, ref bestD);
                        }
                    }
                }

                return best.GetValueOrDefault();
            }

            // Ninguna celda cae dentro (polígono degenerado): punto medio del primer tramo (sobre el borde).
            return ((v[0].x + v[1 % v.Count].x) / 2.0, (v[0].y + v[1 % v.Count].y) / 2.0);
        }

        /// <summary>Distancia mínima del punto a los tramos del contorno cerrado.</summary>
        public static double DistanceToBoundary(IReadOnlyList<(double x, double y)> v, double x, double y)
        {
            RequireVertices(v);
            int n = v.Count;
            if (n == 1)
            {
                return Dist(v[0], (x, y));
            }

            double best = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                best = Math.Min(best, SegmentDistance(v[i], v[(i + 1) % n], x, y));
            }

            return best;
        }

        /// <summary>
        /// "12,35 m²": coma decimal, <paramref name="decimales"/> fijos (0–3), sin separador de miles, redondeo
        /// MidpointRounding.AwayFromZero (en decimal, para que 12,345 → 12,35).
        /// </summary>
        public static string FormatArea(double area, int decimales)
        {
            if (decimales < 0 || decimales > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(decimales), "decimales: debe ser un entero entre 0 y 3");
            }

            if (!double.IsFinite(area))
            {
                throw new ArgumentException("area: debe ser un número finito", nameof(area));
            }

            string fmt = "F" + decimales.ToString(CultureInfo.InvariantCulture);
            string s;
            if (Math.Abs(area) < 7.9e27)
            {
                decimal d = Math.Round((decimal)area, decimales, MidpointRounding.AwayFromZero);
                if (d == 0m)
                {
                    d = 0m; // sin "-0"
                }

                s = d.ToString(fmt, CultureInfo.InvariantCulture);
            }
            else
            {
                s = Math.Round(area, decimales, MidpointRounding.AwayFromZero).ToString(fmt, CultureInfo.InvariantCulture);
            }

            if (s.StartsWith("-", StringComparison.Ordinal) && s.TrimStart('-').Trim('0', '.').Length == 0)
            {
                s = s.Substring(1);
            }

            return s.Replace('.', ',') + " m²";
        }

        private static void Consider(
            IReadOnlyList<(double x, double y)> v, double px, double py, ref (double x, double y)? best, ref double bestD)
        {
            if (!Contains(v, px, py))
            {
                return;
            }

            double d = DistanceToBoundary(v, px, py);
            if (d > bestD)
            {
                bestD = d;
                best = (px, py);
            }
        }

        private static double SegmentDistance((double x, double y) a, (double x, double y) b, double x, double y)
        {
            double dx = b.x - a.x;
            double dy = b.y - a.y;
            double len2 = (dx * dx) + (dy * dy);
            double t = len2 <= 0 ? 0 : (((x - a.x) * dx) + ((y - a.y) * dy)) / len2;
            t = Math.Clamp(t, 0.0, 1.0);
            return Dist((a.x + (t * dx), a.y + (t * dy)), (x, y));
        }

        private static (double xmin, double ymin, double xmax, double ymax) Box(IReadOnlyList<(double x, double y)> v)
        {
            double xmin = double.PositiveInfinity, ymin = double.PositiveInfinity, xmax = double.NegativeInfinity, ymax = double.NegativeInfinity;
            foreach ((double x, double y) in v)
            {
                xmin = Math.Min(xmin, x);
                ymin = Math.Min(ymin, y);
                xmax = Math.Max(xmax, x);
                ymax = Math.Max(ymax, y);
            }

            return (xmin, ymin, xmax, ymax);
        }

        private static void RequireVertices(IReadOnlyList<(double x, double y)> v)
        {
            if (v == null)
            {
                throw new ArgumentNullException(nameof(v));
            }

            if (v.Count == 0)
            {
                throw new ArgumentException("el polígono no tiene vértices", nameof(v));
            }
        }
    }
}
