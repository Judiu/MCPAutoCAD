using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Reglas puras (sin AutoCAD) para validar huecos (islas interiores) dentro de un contorno exterior plano
    /// (polilinea y hatch, fallo 60).
    /// </summary>
    public static class PolygonHoleRules
    {
        public const int MaxHoles = 50;
        private const double AreaEps = 1e-12;
        private const double CoordEps = 1e-12;
        private const double DefaultTol = 1e-9;

        /// <summary>Área con signo (fórmula de Gauss): positiva en sentido antihorario.</summary>
        public static double SignedArea(IReadOnlyList<(double x, double y)> ring)
        {
            if (ring == null || ring.Count < 3)
            {
                return 0.0;
            }

            double sum = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                (double x1, double y1) = ring[i];
                (double x2, double y2) = ring[(i + 1) % ring.Count];
                sum += (x1 * y2) - (x2 * y1);
            }

            return 0.5 * sum;
        }

        /// <summary>Indica si el punto <paramref name="p"/> yace sobre el segmento [a, b] dentro de <paramref name="tol"/>.</summary>
        public static bool PointOnSegment((double x, double y) p, (double x, double y) a, (double x, double y) b, double tol = DefaultTol)
        {
            double dx = b.x - a.x;
            double dy = b.y - a.y;
            double len2 = (dx * dx) + (dy * dy);
            if (len2 <= tol * tol)
            {
                double ex = p.x - a.x;
                double ey = p.y - a.y;
                return Math.Sqrt((ex * ex) + (ey * ey)) <= tol;
            }

            double t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2;
            if (t < -tol || t > 1.0 + tol)
            {
                return false;
            }

            double clamped = Math.Clamp(t, 0.0, 1.0);
            double projX = a.x + (clamped * dx);
            double projY = a.y + (clamped * dy);
            double px = p.x - projX;
            double py = p.y - projY;
            return Math.Sqrt((px * px) + (py * py)) <= tol;
        }

        /// <summary>Indica si <paramref name="p"/> está dentro o sobre el borde del polígono cerrado.</summary>
        public static bool PointInOrOnPolygon((double x, double y) p, IReadOnlyList<(double x, double y)> poly, double tol = DefaultTol)
        {
            if (poly == null || poly.Count < 3)
            {
                return false;
            }

            for (int i = 0; i < poly.Count; i++)
            {
                if (PointOnSegment(p, poly[i], poly[(i + 1) % poly.Count], tol))
                {
                    return true;
                }
            }

            return RayInside(p, poly);
        }

        /// <summary>Indica si <paramref name="p"/> está estrictamente en el interior del polígono (fuera del borde).</summary>
        public static bool PointStrictlyInPolygon((double x, double y) p, IReadOnlyList<(double x, double y)> poly, double tol = DefaultTol)
        {
            if (poly == null || poly.Count < 3)
            {
                return false;
            }

            for (int i = 0; i < poly.Count; i++)
            {
                if (PointOnSegment(p, poly[i], poly[(i + 1) % poly.Count], tol))
                {
                    return false;
                }
            }

            return RayInside(p, poly);
        }

        private static bool RayInside((double x, double y) p, IReadOnlyList<(double x, double y)> poly)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                (double xi, double yi) = poly[i];
                (double xj, double yj) = poly[j];
                bool crossesY = (yi > p.y) != (yj > p.y);
                if (crossesY)
                {
                    double xIntersect = ((xj - xi) * (p.y - yi) / (yj - yi)) + xi;
                    if (p.x < xIntersect)
                    {
                        inside = !inside;
                    }
                }
            }

            return inside;
        }

        /// <summary>
        /// Busca un punto estrictamente interior al polígono simple <paramref name="ring"/> mediante barrido horizontal
        /// entre sus coordenadas Y distintas. Válido para anillos convexos y cóncavos (p. ej. en forma de U o L).
        /// </summary>
        public static (double x, double y)? FindInteriorPoint(IReadOnlyList<(double x, double y)> ring)
        {
            if (ring == null || ring.Count < 3)
            {
                return null;
            }

            var ys = new List<double>(ring.Count);
            for (int i = 0; i < ring.Count; i++)
            {
                ys.Add(ring[i].y);
            }

            ys.Sort();
            var distinct = new List<double>(ys.Count);
            foreach (double y in ys)
            {
                if (distinct.Count == 0 || (y - distinct[distinct.Count - 1]) > CoordEps)
                {
                    distinct.Add(y);
                }
            }

            for (int k = 0; k + 1 < distinct.Count; k++)
            {
                double y0 = distinct[k];
                double y1 = distinct[k + 1];
                if ((y1 - y0) <= CoordEps)
                {
                    continue;
                }

                double yMid = 0.5 * (y0 + y1);
                var xs = new List<double>();
                for (int i = 0; i < ring.Count; i++)
                {
                    (double x1, double yA) = ring[i];
                    (double x2, double yB) = ring[(i + 1) % ring.Count];
                    if ((yA <= yMid && yB > yMid) || (yB <= yMid && yA > yMid))
                    {
                        double xInt = x1 + ((yMid - yA) * (x2 - x1) / (yB - yA));
                        xs.Add(xInt);
                    }
                }

                xs.Sort();
                if (xs.Count >= 2 && (xs[1] - xs[0]) > CoordEps)
                {
                    return (0.5 * (xs[0] + xs[1]), yMid);
                }
            }

            return null;
        }

        /// <summary>
        /// Indica si los segmentos abiertos (a, b) y (c, d) se cruzan propiamente (excluye toques en extremos o colineales).
        /// </summary>
        public static bool SegmentsProperlyCross(
            (double x, double y) a, (double x, double y) b, (double x, double y) c, (double x, double y) d, double tol = DefaultTol)
        {
            double o1 = CrossSign(a, b, c, tol);
            double o2 = CrossSign(a, b, d, tol);
            double o3 = CrossSign(c, d, a, tol);
            double o4 = CrossSign(c, d, b, tol);
            return ((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0))
                && ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0));
        }

        private static double CrossSign((double x, double y) a, (double x, double y) b, (double x, double y) p, double tol)
        {
            double dx = b.x - a.x;
            double dy = b.y - a.y;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            double eps = Math.Max(tol * len, AreaEps);
            double cross = (dx * (p.y - a.y)) - (dy * (p.x - a.x));
            if (Math.Abs(cross) <= eps)
            {
                return 0.0;
            }

            return cross > 0 ? 1.0 : -1.0;
        }

        /// <summary>
        /// Valida una lista de anillos interiores frente al contorno <paramref name="outer"/> y entre sí. Devuelve el
        /// primer error legible con prefijo <paramref name="holesPrefix"/>, o <c>null</c> si todo es válido.
        /// </summary>
        public static string? ValidateHoles(
            IReadOnlyList<(double x, double y)> outer,
            IReadOnlyList<IReadOnlyList<(double x, double y)>> holes,
            string holesPrefix = "huecos")
        {
            if (holes == null || holes.Count == 0)
            {
                return null;
            }

            if (holes.Count > MaxHoles)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}: máximo {1} huecos", holesPrefix, MaxHoles);
            }

            var interiors = new List<(double x, double y)>(holes.Count);
            for (int i = 0; i < holes.Count; i++)
            {
                IReadOnlyList<(double x, double y)> hole = holes[i];
                string idxPrefix = holesPrefix + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                IReadOnlyList<string> polyErrors = PolylineValidator.ValidateDetailed(hole, cerrada: true, bulges: null);
                if (polyErrors.Count > 0)
                {
                    string err = polyErrors[0];
                    if (err.StartsWith("vertices", StringComparison.Ordinal))
                    {
                        return idxPrefix + err.Substring("vertices".Length);
                    }

                    return idxPrefix + ": " + err;
                }

                if (Math.Abs(SignedArea(hole)) <= AreaEps)
                {
                    return idxPrefix + ": el hueco tiene área nula";
                }

                for (int v = 0; v < hole.Count; v++)
                {
                    if (!PointInOrOnPolygon(hole[v], outer))
                    {
                        return idxPrefix + ": el hueco sobresale del contorno exterior";
                    }
                }

                for (int e1 = 0; e1 < hole.Count; e1++)
                {
                    (double x, double y) hA = hole[e1];
                    (double x, double y) hB = hole[(e1 + 1) % hole.Count];
                    for (int e2 = 0; e2 < outer.Count; e2++)
                    {
                        if (SegmentsProperlyCross(hA, hB, outer[e2], outer[(e2 + 1) % outer.Count]))
                        {
                            return idxPrefix + ": cruza el contorno exterior";
                        }
                    }
                }

                (double x, double y)? interior = FindInteriorPoint(hole);
                if (interior == null || !PointStrictlyInPolygon(interior.Value, outer))
                {
                    return idxPrefix + ": el hueco queda fuera del contorno exterior";
                }

                interiors.Add(interior.Value);
            }

            for (int i = 0; i < holes.Count; i++)
            {
                IReadOnlyList<(double x, double y)> hI = holes[i];
                for (int j = i + 1; j < holes.Count; j++)
                {
                    IReadOnlyList<(double x, double y)> hJ = holes[j];
                    string jPrefix = holesPrefix + "[" + j.ToString(CultureInfo.InvariantCulture) + "]";
                    string iPrefix = holesPrefix + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    for (int e1 = 0; e1 < hI.Count; e1++)
                    {
                        for (int e2 = 0; e2 < hJ.Count; e2++)
                        {
                            if (SegmentsProperlyCross(hI[e1], hI[(e1 + 1) % hI.Count], hJ[e2], hJ[(e2 + 1) % hJ.Count]))
                            {
                                return jPrefix + ": se solapa con " + iPrefix;
                            }
                        }
                    }

                    if (PointStrictlyInPolygon(interiors[j], hI) || PointStrictlyInPolygon(interiors[i], hJ))
                    {
                        return jPrefix + ": se solapa con " + iPrefix;
                    }
                }
            }

            return null;
        }
    }
}
