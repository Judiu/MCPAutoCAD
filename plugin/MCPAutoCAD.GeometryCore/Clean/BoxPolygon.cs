using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Clean
{
    /// <summary>Relación de la caja de una entidad con el contorno (protocol.md §5F.1).</summary>
    public enum BoxRelation
    {
        /// <summary>Las cuatro esquinas dentro (borde incluido) y ninguna arista del contorno cruza el interior de la caja.</summary>
        Dentro,

        /// <summary>Las cuatro esquinas fuera, ninguna arista del contorno toca la caja y la caja no contiene al contorno.</summary>
        Fuera,

        /// <summary>Cualquier otro caso (la caja toca o atraviesa el borde, o contiene al contorno).</summary>
        Cruza,
    }

    /// <summary>Criterio del parámetro <c>criterio</c> de borrar_fuera_de (§5F.1).</summary>
    public enum OutsideCriterion
    {
        /// <summary>"caja_cruza" (defecto): se conserva lo que está dentro o cruza; solo se borra lo totalmente fuera.</summary>
        CajaCruza,

        /// <summary>"caja_dentro": solo se conserva lo totalmente dentro; lo que cruza también se borra.</summary>
        CajaDentro,
    }

    /// <summary>
    /// Clasificación pura de una caja 2D frente a un polígono cerrado (borrar_fuera_de, protocol.md §5F.1). Sin
    /// AutoCAD. El polígono llega como lista de vértices (sin repetir el primero al final); un círculo se aproxima con
    /// <see cref="CirclePolygon"/>. El borde cuenta como dentro. El resultado no depende de la orientación del polígono.
    /// </summary>
    public static class BoxPolygon
    {
        public const string CriterioCajaCruza = "caja_cruza";
        public const string CriterioCajaDentro = "caja_dentro";

        /// <summary>Lados con que se aproxima un círculo (§5F.1).</summary>
        public const int CircleSides = 64;

        /// <summary>Mensaje de polígono con menos de 3 vértices (§5F.1).</summary>
        public const string MinVertices = "contorno: se necesitan al menos 3 vértices";

        /// <summary>Mensaje de criterio desconocido (§5F.1).</summary>
        public const string InvalidCriterion = "criterio: debe ser caja_cruza o caja_dentro";

        /// <summary>
        /// Relación de <paramref name="box"/> con <paramref name="polygon"/> (≥ 3 vértices, cerrado implícitamente,
        /// simple). Exacto también para polígonos no convexos: Dentro exige las cuatro esquinas dentro (ray casting,
        /// borde = dentro, tolerancia <paramref name="tol"/>) y que ninguna arista del polígono corte el interior de
        /// la caja; Fuera exige las cuatro esquinas fuera, ninguna arista que toque la caja (ni en el borde) y que la
        /// caja no contenga ningún vértice del polígono; el resto es Cruza. Una caja degenerada (punto o segmento)
        /// se clasifica igual. Polígono con &lt; 3 vértices → ArgumentException "contorno: se necesitan al menos 3 vértices".
        /// </summary>
        public static BoxRelation Classify(IReadOnlyList<(double x, double y)> polygon, BBox2 box, double tol = 1e-9)
        {
            if (polygon == null)
            {
                throw new ArgumentNullException(nameof(polygon));
            }

            if (polygon.Count < 3)
            {
                throw new ArgumentException(MinVertices);
            }

            if (!double.IsFinite(tol) || tol < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tol), "La tolerancia debe ser un número finito no negativo.");
            }

            int inside = 0;
            inside += PointInPolygon(polygon, box.Xmin, box.Ymin, tol) ? 1 : 0;
            inside += PointInPolygon(polygon, box.Xmax, box.Ymin, tol) ? 1 : 0;
            inside += PointInPolygon(polygon, box.Xmax, box.Ymax, tol) ? 1 : 0;
            inside += PointInPolygon(polygon, box.Xmin, box.Ymax, tol) ? 1 : 0;

            if (inside == 4)
            {
                return BoxInside(polygon, box, tol) ? BoxRelation.Dentro : BoxRelation.Cruza;
            }

            if (inside == 0)
            {
                // Caja cerrada ampliada en tol: una arista que la toca (incluido un vértice del polígono dentro de la
                // caja, o el contorno entero dentro de ella) la convierte en Cruza.
                double xmin = box.Xmin - tol, ymin = box.Ymin - tol, xmax = box.Xmax + tol, ymax = box.Ymax + tol;
                int n = polygon.Count;
                for (int i = 0; i < n; i++)
                {
                    if (SegmentHitsRect(polygon[i], polygon[(i + 1) % n], xmin, ymin, xmax, ymax))
                    {
                        return BoxRelation.Cruza;
                    }
                }

                return BoxRelation.Fuera;
            }

            return BoxRelation.Cruza;
        }

        /// <summary>true si una entidad con la relación <paramref name="rel"/> se BORRA con el criterio dado.</summary>
        public static bool ShouldDelete(BoxRelation rel, OutsideCriterion criterio)
        {
            if (criterio != OutsideCriterion.CajaCruza && criterio != OutsideCriterion.CajaDentro)
            {
                throw new ArgumentOutOfRangeException(nameof(criterio));
            }

            return rel switch
            {
                BoxRelation.Fuera => true,
                BoxRelation.Cruza => criterio == OutsideCriterion.CajaDentro,
                BoxRelation.Dentro => false,
                _ => throw new ArgumentOutOfRangeException(nameof(rel)),
            };
        }

        /// <summary>"caja_cruza" → CajaCruza, "caja_dentro" → CajaDentro (sin distinguir mayúsculas); null → CajaCruza; otro → ArgumentException "criterio: debe ser caja_cruza o caja_dentro".</summary>
        public static OutsideCriterion ParseCriterion(string? criterio)
        {
            if (criterio == null)
            {
                return OutsideCriterion.CajaCruza;
            }

            if (string.Equals(criterio, CriterioCajaCruza, StringComparison.OrdinalIgnoreCase))
            {
                return OutsideCriterion.CajaCruza;
            }

            if (string.Equals(criterio, CriterioCajaDentro, StringComparison.OrdinalIgnoreCase))
            {
                return OutsideCriterion.CajaDentro;
            }

            throw new ArgumentException(InvalidCriterion);
        }

        /// <summary>Polígono regular de <paramref name="sides"/> lados inscrito en el círculo (antihorario, empieza en +X).</summary>
        public static List<(double x, double y)> CirclePolygon(double cx, double cy, double radius, int sides = CircleSides)
        {
            if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(radius) || radius <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(radius), "círculo: centro finito y radio positivo");
            }

            if (sides < 3)
            {
                throw new ArgumentOutOfRangeException(nameof(sides), "círculo: se necesitan al menos 3 lados");
            }

            var result = new List<(double x, double y)>(sides);
            for (int i = 0; i < sides; i++)
            {
                double a = 2.0 * Math.PI * i / sides;
                result.Add((cx + (radius * Math.Cos(a)), cy + (radius * Math.Sin(a))));
            }

            return result;
        }

        /// <summary>
        /// Ray casting con el borde como dentro (distancia al borde ≤ <paramref name="tol"/>), el mismo criterio que
        /// <c>Gen.PolygonMetrics.Contains</c> pero con la tolerancia del llamador.
        /// </summary>
        public static bool PointInPolygon(IReadOnlyList<(double x, double y)> v, double x, double y, double tol = 1e-9)
        {
            int n = v.Count;
            for (int i = 0; i < n; i++)
            {
                if (DistanceToSegment(v[i], v[(i + 1) % n], x, y) <= tol)
                {
                    return true;
                }
            }

            bool inside = false;
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
        /// Con las cuatro esquinas ya dentro: true si toda la caja está dentro del polígono cerrado. Caja con área:
        /// ninguna arista toca el interior (caja reducida en tol) y el centro está dentro (descarta el caso patológico
        /// de un polígono que solo toca las cuatro esquinas desde fuera). Caja degenerada (segmento): se parte el
        /// segmento en los parámetros donde lo cortan las aristas y el punto medio de cada tramo debe estar dentro.
        /// </summary>
        private static bool BoxInside(IReadOnlyList<(double x, double y)> v, BBox2 box, double tol)
        {
            bool flatX = box.Width <= 2 * tol;
            bool flatY = box.Height <= 2 * tol;
            if (flatX && flatY)
            {
                return true; // punto: sus esquinas ya están dentro
            }

            double cx = (box.Xmin + box.Xmax) / 2.0;
            double cy = (box.Ymin + box.Ymax) / 2.0;
            if (flatX)
            {
                return SegmentInside(v, (cx, box.Ymin), (cx, box.Ymax), tol);
            }

            if (flatY)
            {
                return SegmentInside(v, (box.Xmin, cy), (box.Xmax, cy), tol);
            }

            double xmin = box.Xmin + tol, ymin = box.Ymin + tol, xmax = box.Xmax - tol, ymax = box.Ymax - tol;
            int n = v.Count;
            for (int i = 0; i < n; i++)
            {
                if (SegmentHitsRect(v[i], v[(i + 1) % n], xmin, ymin, xmax, ymax))
                {
                    return false;
                }
            }

            return PointInPolygon(v, cx, cy, tol);
        }

        /// <summary>true si el segmento a→b (extremos ya dentro) está entero dentro del polígono cerrado.</summary>
        private static bool SegmentInside(IReadOnlyList<(double x, double y)> v, (double x, double y) a, (double x, double y) b, double tol)
        {
            double dx = b.x - a.x, dy = b.y - a.y;
            double len2 = (dx * dx) + (dy * dy);
            var ts = new List<double> { 0.0, 1.0 };
            int n = v.Count;
            for (int i = 0; i < n; i++)
            {
                (double x, double y) c = v[i];
                (double x, double y) d = v[(i + 1) % n];
                double ex = d.x - c.x, ey = d.y - c.y;
                double denom = (dx * ey) - (dy * ex);
                double acx = c.x - a.x, acy = c.y - a.y;
                if (Math.Abs(denom) > 1e-15 * Math.Max(1.0, len2 + (ex * ex) + (ey * ey)))
                {
                    double t = ((acx * ey) - (acy * ex)) / denom;
                    double u = ((acx * dy) - (acy * dx)) / denom;
                    if (t >= 0 && t <= 1 && u >= -1e-12 && u <= 1 + 1e-12)
                    {
                        ts.Add(t);
                    }
                }

                // Proyección de los extremos de la arista (paralelas, colineales o vértices sobre el segmento): añadir
                // parámetros de más solo añade puntos de prueba, nunca cambia un resultado correcto.
                AddProjection(ts, a, dx, dy, len2, c);
                AddProjection(ts, a, dx, dy, len2, d);
            }

            ts.Sort();
            for (int k = 0; k + 1 < ts.Count; k++)
            {
                if (ts[k + 1] - ts[k] <= 1e-12)
                {
                    continue;
                }

                double tm = (ts[k] + ts[k + 1]) / 2.0;
                if (!PointInPolygon(v, a.x + (tm * dx), a.y + (tm * dy), tol))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddProjection(List<double> ts, (double x, double y) a, double dx, double dy, double len2, (double x, double y) p)
        {
            if (len2 <= 0)
            {
                return;
            }

            double t = (((p.x - a.x) * dx) + ((p.y - a.y) * dy)) / len2;
            if (t > 0 && t < 1)
            {
                ts.Add(t);
            }
        }

        /// <summary>Liang–Barsky: true si el segmento p→q toca el rectángulo cerrado [xmin, xmax] × [ymin, ymax].</summary>
        private static bool SegmentHitsRect((double x, double y) p, (double x, double y) q, double xmin, double ymin, double xmax, double ymax)
        {
            if (xmin > xmax || ymin > ymax)
            {
                return false;
            }

            double t0 = 0.0, t1 = 1.0;
            double dx = q.x - p.x, dy = q.y - p.y;
            return Clip(-dx, p.x - xmin, ref t0, ref t1)
                && Clip(dx, xmax - p.x, ref t0, ref t1)
                && Clip(-dy, p.y - ymin, ref t0, ref t1)
                && Clip(dy, ymax - p.y, ref t0, ref t1);
        }

        private static bool Clip(double pp, double qq, ref double t0, ref double t1)
        {
            if (pp == 0)
            {
                return qq >= 0;
            }

            double r = qq / pp;
            if (pp < 0)
            {
                if (r > t1)
                {
                    return false;
                }

                if (r > t0)
                {
                    t0 = r;
                }
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }

                if (r < t1)
                {
                    t1 = r;
                }
            }

            return true;
        }

        private static double DistanceToSegment((double x, double y) a, (double x, double y) b, double x, double y)
        {
            double dx = b.x - a.x, dy = b.y - a.y;
            double len2 = (dx * dx) + (dy * dy);
            double t = len2 <= 0 ? 0 : (((x - a.x) * dx) + ((y - a.y) * dy)) / len2;
            t = Math.Max(0, Math.Min(1, t));
            double px = a.x + (t * dx) - x, py = a.y + (t * dy) - y;
            return Math.Sqrt((px * px) + (py * py));
        }
    }
}
