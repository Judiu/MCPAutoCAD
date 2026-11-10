using System;
using System.Collections.Generic;
using System.Linq;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Región de muro resultante de la unión: contorno exterior (antihorario) y agujeros (horario), en unidades del dibujo.
    /// </summary>
    public sealed class WallRegion
    {
        public WallRegion(IReadOnlyList<(double x, double y)> outer, IReadOnlyList<IReadOnlyList<(double x, double y)>> holes)
        {
            Outer = outer ?? throw new ArgumentNullException(nameof(outer));
            Holes = holes ?? throw new ArgumentNullException(nameof(holes));
            double area = Math.Abs(PolygonMetrics.SignedArea(outer));
            foreach (IReadOnlyList<(double x, double y)> h in holes)
            {
                area -= Math.Abs(PolygonMetrics.SignedArea(h));
            }

            Area = area;
            MinX = outer.Min(p => p.x);
            MinY = outer.Min(p => p.y);
            MaxX = outer.Max(p => p.x);
            MaxY = outer.Max(p => p.y);
        }

        public IReadOnlyList<(double x, double y)> Outer { get; }

        public IReadOnlyList<IReadOnlyList<(double x, double y)>> Holes { get; }

        /// <summary>Área neta: contorno exterior menos agujeros.</summary>
        public double Area { get; }

        public double MinX { get; }

        public double MinY { get; }

        public double MaxX { get; }

        public double MaxY { get; }
    }

    /// <summary>Resultado de <see cref="WallRegionUnion.Union"/>.</summary>
    public sealed class WallUnionResult
    {
        public WallUnionResult(IReadOnlyList<WallRegion> regions, int contornosAbiertos, int buclesDescartados)
        {
            Regions = regions;
            ContornosAbiertos = contornosAbiertos;
            BuclesDescartados = buclesDescartados;
        }

        /// <summary>Regiones unidas, ordenadas por (MinX, MinY).</summary>
        public IReadOnlyList<WallRegion> Regions { get; }

        /// <summary>Cadenas de bordes que no cerraron (degeneraciones numéricas); se descartan y se informan.</summary>
        public int ContornosAbiertos { get; }

        /// <summary>Bucles de área despreciable (≤ tolerancia²) descartados.</summary>
        public int BuclesDescartados { get; }
    }

    /// <summary>
    /// Unión de caras de muro (cuadriláteros) en regiones con contorno exterior y agujeros. Método de aristas:
    /// <list type="number">
    /// <item>Pegado de vértices: un vértice a menos de <c>tolerancia</c> del borde de otra cara (y fuera de ella) se mueve
    /// sobre ese borde. Así las juntas en L, T o a tope con hueco ≤ tolerancia quedan coincidentes.</item>
    /// <item>Se parten todas las aristas en sus puntos de corte y en los vértices que caen sobre ellas (solapes colineales).</item>
    /// <item>Un tramo es frontera si, a un lado queda dentro de la unión y al otro fuera (sonda a distancia ε). Se orienta
    /// con el interior a la izquierda y los tramos coincidentes se deduplican.</item>
    /// <item>Se encadenan los tramos en bucles; los de área positiva son contornos exteriores y los negativos agujeros, que se
    /// asignan al exterior más pequeño que los contiene.</item>
    /// </list>
    /// Puro (sin AutoCAD). Complejidad O(E²) en aristas: pensado para ≤ ~1000 caras.
    /// </summary>
    public static class WallRegionUnion
    {
        public static WallUnionResult Union(IReadOnlyList<IReadOnlyList<(double x, double y)>> quads, double tolerancia)
        {
            if (quads == null)
            {
                throw new ArgumentNullException(nameof(quads));
            }

            if (!(tolerancia >= 0) || !double.IsFinite(tolerancia))
            {
                throw new ArgumentException("tolerancia_union: debe ser un número >= 0");
            }

            double eps = Math.Max(tolerancia * 1e-3, 1e-7);
            double minArea = Math.Max(tolerancia * tolerancia, 1e-12);

            var polys = new List<Poly>(quads.Count);
            foreach (IReadOnlyList<(double x, double y)> q in quads)
            {
                if (q == null || q.Count < 3)
                {
                    throw new ArgumentException("cada cara debe tener al menos 3 vértices");
                }

                var p = new Poly(q.ToArray());
                if (Math.Abs(PolygonMetrics.SignedArea(p.V)) > minArea)
                {
                    polys.Add(p);
                }
            }

            if (tolerancia > 0)
            {
                Snap(polys, tolerancia, eps);
            }

            polys.RemoveAll(p => Math.Abs(PolygonMetrics.SignedArea(p.V)) <= minArea);
            if (polys.Count == 0)
            {
                return new WallUnionResult(new List<WallRegion>(), 0, 0);
            }

            // 1) Aristas y puntos de corte.
            var edges = new List<Edge>();
            foreach (Poly p in polys)
            {
                int n = p.V.Length;
                for (int k = 0; k < n; k++)
                {
                    edges.Add(new Edge(p.V[k], p.V[(k + 1) % n]));
                }
            }

            int ne = edges.Count;
            var splits = new List<double>[ne];
            for (int i = 0; i < ne; i++)
            {
                splits[i] = new List<double>();
            }

            for (int a = 0; a < ne; a++)
            {
                Edge ea = edges[a];
                if (ea.Len <= eps)
                {
                    continue;
                }

                double epsT = eps / ea.Len;
                for (int b = 0; b < ne; b++)
                {
                    if (b == a)
                    {
                        continue;
                    }

                    Edge eb = edges[b];
                    AddPointOn(ea, eb.A, eps, epsT, splits[a]);
                    AddPointOn(ea, eb.B, eps, epsT, splits[a]);
                    if (eb.Len <= eps)
                    {
                        continue;
                    }

                    double denom = (ea.Dx * eb.Dy) - (ea.Dy * eb.Dx);
                    if (Math.Abs(denom) <= 1e-14 * ea.Len * eb.Len)
                    {
                        continue;
                    }

                    double ex = eb.A.x - ea.A.x;
                    double ey = eb.A.y - ea.A.y;
                    double t = ((ex * eb.Dy) - (ey * eb.Dx)) / denom;
                    double u = ((ex * ea.Dy) - (ey * ea.Dx)) / denom;
                    double epsU = eps / eb.Len;
                    if (t > epsT && t < 1 - epsT && u >= -epsU && u <= 1 + epsU)
                    {
                        splits[a].Add(t);
                    }
                }
            }

            // 2) Tramos partidos, clasificados por sus dos lados.
            var clusterer = new PointClusterer(eps);
            var directed = new List<(int from, int to)>();
            var seen = new HashSet<(int, int)>();
            for (int a = 0; a < ne; a++)
            {
                Edge ea = edges[a];
                if (ea.Len <= eps)
                {
                    continue;
                }

                List<double> ts = splits[a];
                ts.Add(0.0);
                ts.Add(1.0);
                ts.Sort();
                for (int k = 0; k + 1 < ts.Count; k++)
                {
                    double t0 = ts[k];
                    double t1 = ts[k + 1];
                    if ((t1 - t0) * ea.Len <= eps)
                    {
                        continue;
                    }

                    (double x, double y) pStart = (ea.A.x + (t0 * ea.Dx), ea.A.y + (t0 * ea.Dy));
                    (double x, double y) pEnd = (ea.A.x + (t1 * ea.Dx), ea.A.y + (t1 * ea.Dy));
                    double mx = (pStart.x + pEnd.x) / 2.0;
                    double my = (pStart.y + pEnd.y) / 2.0;
                    double nx = -ea.Dy / ea.Len;
                    double ny = ea.Dx / ea.Len;
                    bool inLeft = CoveredByAny(polys, mx + (nx * eps), my + (ny * eps));
                    bool inRight = CoveredByAny(polys, mx - (nx * eps), my - (ny * eps));
                    if (inLeft == inRight)
                    {
                        continue;
                    }

                    int iStart = clusterer.Id(pStart);
                    int iEnd = clusterer.Id(pEnd);
                    if (iStart == iEnd)
                    {
                        continue;
                    }

                    (int from, int to) d = inLeft ? (iStart, iEnd) : (iEnd, iStart);
                    if (seen.Add(d))
                    {
                        directed.Add(d);
                    }
                }
            }

            // 3) Encadenado en bucles (interior a la izquierda).
            int nv = clusterer.Reps.Count;
            var outgoing = new List<int>[nv];
            for (int e = 0; e < directed.Count; e++)
            {
                int f = directed[e].from;
                outgoing[f] ??= new List<int>();
                outgoing[f].Add(e);
            }

            var used = new bool[directed.Count];
            var loops = new List<(double x, double y)[]>();
            int abiertos = 0;
            for (int e0 = 0; e0 < directed.Count; e0++)
            {
                if (used[e0])
                {
                    continue;
                }

                used[e0] = true;
                var chain = new List<int> { e0 };
                int start = directed[e0].from;
                int cur = e0;
                bool cerrado = false;
                while (true)
                {
                    int v = directed[cur].to;
                    if (v == start)
                    {
                        cerrado = true;
                        break;
                    }

                    List<int>? cands = outgoing[v];
                    if (cands == null)
                    {
                        break;
                    }

                    int best = -1;
                    double bestAngle = double.NegativeInfinity;
                    foreach (int c in cands)
                    {
                        if (used[c])
                        {
                            continue;
                        }

                        double ang = TurnAngle(clusterer, directed[cur], directed[c]);
                        if (ang > bestAngle)
                        {
                            bestAngle = ang;
                            best = c;
                        }
                    }

                    if (best < 0)
                    {
                        break;
                    }

                    used[best] = true;
                    chain.Add(best);
                    cur = best;
                }

                if (!cerrado)
                {
                    abiertos++;
                    continue;
                }

                var pts = chain.Select(e => clusterer.Reps[directed[e].from]).ToList();
                pts = Simplify(pts, Math.Max(eps, tolerancia * 0.1));
                if (pts.Count >= 3)
                {
                    loops.Add(pts.ToArray());
                }
            }

            // 4) Bucles → exteriores y agujeros.
            var outers = new List<(double x, double y)[]>();
            var holes = new List<(double x, double y)[]>();
            int descartados = 0;
            foreach ((double x, double y)[] loop in loops)
            {
                double area = PolygonMetrics.SignedArea(loop);
                if (Math.Abs(area) <= minArea)
                {
                    descartados++;
                }
                else if (area > 0)
                {
                    outers.Add(loop);
                }
                else
                {
                    holes.Add(loop);
                }
            }

            var holesOf = new List<List<IReadOnlyList<(double x, double y)>>>(outers.Count);
            for (int i = 0; i < outers.Count; i++)
            {
                holesOf.Add(new List<IReadOnlyList<(double x, double y)>>());
            }

            foreach ((double x, double y)[] h in holes)
            {
                // Un agujero pertenece al exterior más pequeño que lo contiene: se prueba un punto del interior del propio
                // agujero (no del borde) y el exterior debe ser mayor que el agujero.
                (double x, double y) ip = PolygonMetrics.InteriorPoint(h, out _);
                double ha = Math.Abs(PolygonMetrics.SignedArea(h));
                int best = -1;
                double bestArea = double.PositiveInfinity;
                for (int i = 0; i < outers.Count; i++)
                {
                    double oa = Math.Abs(PolygonMetrics.SignedArea(outers[i]));
                    if (oa > ha && oa < bestArea && PolygonMetrics.Contains(outers[i], ip.x, ip.y))
                    {
                        bestArea = oa;
                        best = i;
                    }
                }

                if (best >= 0)
                {
                    holesOf[best].Add(h);
                }
                else
                {
                    descartados++;
                }
            }

            var regions = new List<WallRegion>(outers.Count);
            for (int i = 0; i < outers.Count; i++)
            {
                regions.Add(new WallRegion(outers[i], holesOf[i]));
            }

            regions.Sort((p, q) =>
            {
                int c = p.MinX.CompareTo(q.MinX);
                return c != 0 ? c : p.MinY.CompareTo(q.MinY);
            });
            return new WallUnionResult(regions, abiertos, descartados);
        }

        // ------------------------------------------------------------------ auxiliares

        /// <summary>Pega los vértices que quedan a ≤ tolerancia del borde de otra cara (y fuera de todas) sobre ese borde.</summary>
        /// <remarks>
        /// Regla de junta (desplaza geometría ≤ tol): un vértice se pega al borde de otra cara solo si (a) no está ya sobre
        /// la recta de ese borde (un vértice colineal se queda donde está) y (b) una de sus aristas incidentes es casi
        /// perpendicular a ese borde (≤ 5°): es un extremo de muro que llega a la cara de otro muro (junta a tope o en T).
        /// </remarks>
        private static void Snap(List<Poly> polys, double tol, double eps)
        {
            double sinTol = Math.Sin(5.0 * Math.PI / 180.0);
            for (int i = 0; i < polys.Count; i++)
            {
                Poly p = polys[i];
                int n = p.V.Length;
                for (int k = 0; k < n; k++)
                {
                    (double x, double y) v = p.V[k];
                    (double x, double y) prev = p.V[(k - 1 + n) % n];
                    (double x, double y) next = p.V[(k + 1) % n];
                    bool inside = false;
                    double best = double.PositiveInfinity;
                    (double x, double y) bestPt = v;
                    for (int j = 0; j < polys.Count; j++)
                    {
                        if (j == i)
                        {
                            continue;
                        }

                        Poly q = polys[j];
                        if (v.x < q.MinX - tol || v.x > q.MaxX + tol || v.y < q.MinY - tol || v.y > q.MaxY + tol)
                        {
                            continue;
                        }

                        if (PolygonMetrics.Contains(q.V, v.x, v.y))
                        {
                            inside = true;
                            break;
                        }

                        int nq = q.V.Length;
                        for (int e = 0; e < nq; e++)
                        {
                            (double x, double y) a = q.V[e];
                            (double x, double y) b = q.V[(e + 1) % nq];
                            if (!Unit(b.x - a.x, b.y - a.y, out double wx, out double wy))
                            {
                                continue;
                            }

                            if (Math.Abs((wx * (v.y - a.y)) - (wy * (v.x - a.x))) <= eps)
                            {
                                continue;   // ya está sobre la recta del borde: colineal, no se mueve
                            }

                            double perp = Math.Min(IncidentDot(v, prev, wx, wy), IncidentDot(v, next, wx, wy));
                            if (!(perp <= sinTol))
                            {
                                continue;   // ninguna arista incidente es perpendicular a este borde
                            }

                            double d = PlanarGeom.NearestOnSegment(v, a, b, out (double x, double y) np);
                            if (d < best)
                            {
                                best = d;
                                bestPt = np;
                            }
                        }
                    }

                    if (!inside && best <= tol)
                    {
                        p.V[k] = bestPt;
                    }
                }

                p.RecomputeBox();
            }
        }

        private static bool Unit(double dx, double dy, out double ux, out double uy)
        {
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            if (!(len > 0))
            {
                ux = 0;
                uy = 0;
                return false;
            }

            ux = dx / len;
            uy = dy / len;
            return true;
        }

        /// <summary>|cos| entre la arista v→w y la dirección unitaria (wx, wy); 1 si la arista es degenerada.</summary>
        private static double IncidentDot((double x, double y) v, (double x, double y) w, double wx, double wy)
        {
            if (!Unit(w.x - v.x, w.y - v.y, out double ux, out double uy))
            {
                return 1.0;
            }

            return Math.Abs((ux * wx) + (uy * wy));
        }

        /// <summary>Añade a <paramref name="into"/> el parámetro de <paramref name="p"/> sobre la arista <paramref name="a"/> si p está sobre ella.</summary>
        private static void AddPointOn(Edge a, (double x, double y) p, double eps, double epsT, List<double> into)
        {
            double t = (((p.x - a.A.x) * a.Dx) + ((p.y - a.A.y) * a.Dy)) / (a.Len * a.Len);
            if (!(t > epsT && t < 1 - epsT))
            {
                return;
            }

            double qx = a.A.x + (t * a.Dx);
            double qy = a.A.y + (t * a.Dy);
            if (Math.Sqrt(((p.x - qx) * (p.x - qx)) + ((p.y - qy) * (p.y - qy))) <= eps)
            {
                into.Add(t);
            }
        }

        private static bool CoveredByAny(List<Poly> polys, double x, double y)
        {
            foreach (Poly p in polys)
            {
                if (x < p.MinX || x > p.MaxX || y < p.MinY || y > p.MaxY)
                {
                    continue;
                }

                if (PolygonMetrics.Contains(p.V, x, y))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Ángulo de giro de <paramref name="next"/> respecto a <paramref name="cur"/> en (-π, π]. Se elige el giro más a la izquierda (mantiene cada contorno separado en esquinas que se tocan).</summary>
        private static double TurnAngle(PointClusterer c, (int from, int to) cur, (int from, int to) next)
        {
            (double x, double y) p0 = c.Reps[cur.from];
            (double x, double y) p1 = c.Reps[cur.to];
            (double x, double y) p2 = c.Reps[next.to];
            double ix = p1.x - p0.x;
            double iy = p1.y - p0.y;
            double ox = p2.x - p1.x;
            double oy = p2.y - p1.y;
            return Math.Atan2((ix * oy) - (iy * ox), (ix * ox) + (iy * oy));
        }

        /// <summary>Quita vértices casi alineados (distancia al segmento vecino ≤ eps) hasta estabilizar.</summary>
        private static List<(double x, double y)> Simplify(List<(double x, double y)> pts, double eps)
        {
            var list = new List<(double x, double y)>(pts);
            bool changed = true;
            while (changed && list.Count > 3)
            {
                changed = false;
                for (int i = 0; i < list.Count && list.Count > 3; i++)
                {
                    int n = list.Count;
                    (double x, double y) prev = list[(i - 1 + n) % n];
                    (double x, double y) next = list[(i + 1) % n];
                    double d = PlanarGeom.NearestOnSegment(list[i], prev, next, out _);
                    if (d <= eps)
                    {
                        list.RemoveAt(i);
                        changed = true;
                        i--;
                    }
                }
            }

            return list;
        }

        /// <summary>Polígono de entrada (cara) con su caja envolvente.</summary>
        private sealed class Poly
        {
            public Poly((double x, double y)[] v)
            {
                V = v;
                RecomputeBox();
            }

            public (double x, double y)[] V { get; }

            public double MinX { get; private set; }

            public double MinY { get; private set; }

            public double MaxX { get; private set; }

            public double MaxY { get; private set; }

            public void RecomputeBox()
            {
                MinX = V.Min(p => p.x);
                MinY = V.Min(p => p.y);
                MaxX = V.Max(p => p.x);
                MaxY = V.Max(p => p.y);
            }
        }

        /// <summary>Arista dirigida A→B con su vector y longitud.</summary>
        private sealed class Edge
        {
            public Edge((double x, double y) a, (double x, double y) b)
            {
                A = a;
                B = b;
                Dx = b.x - a.x;
                Dy = b.y - a.y;
                Len = Math.Sqrt((Dx * Dx) + (Dy * Dy));
            }

            public (double x, double y) A { get; }

            public (double x, double y) B { get; }

            public double Dx { get; }

            public double Dy { get; }

            public double Len { get; }
        }

        /// <summary>Agrupa puntos a ≤ eps en un mismo identificador (rejilla de celdas de tamaño eps).</summary>
        private sealed class PointClusterer
        {
            private readonly double _eps;
            private readonly Dictionary<(long, long), List<int>> _cells = new Dictionary<(long, long), List<int>>();

            public PointClusterer(double eps)
            {
                _eps = eps;
            }

            public List<(double x, double y)> Reps { get; } = new List<(double x, double y)>();

            public int Id((double x, double y) p)
            {
                long cx = (long)Math.Floor(p.x / _eps);
                long cy = (long)Math.Floor(p.y / _eps);
                for (long dx = -1; dx <= 1; dx++)
                {
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        if (!_cells.TryGetValue((cx + dx, cy + dy), out List<int>? ids))
                        {
                            continue;
                        }

                        foreach (int id in ids)
                        {
                            (double x, double y) r = Reps[id];
                            double ddx = r.x - p.x;
                            double ddy = r.y - p.y;
                            if (Math.Sqrt((ddx * ddx) + (ddy * ddy)) <= _eps)
                            {
                                return id;
                            }
                        }
                    }
                }

                int nid = Reps.Count;
                Reps.Add(p);
                if (!_cells.TryGetValue((cx, cy), out List<int>? list))
                {
                    list = new List<int>();
                    _cells[(cx, cy)] = list;
                }

                list.Add(nid);
                return nid;
            }
        }
    }

    /// <summary>Geometría plana auxiliar (interna).</summary>
    internal static class PlanarGeom
    {
        /// <summary>Distancia de <paramref name="p"/> al segmento AB; <paramref name="near"/> = punto más cercano.</summary>
        public static double NearestOnSegment((double x, double y) p, (double x, double y) a, (double x, double y) b, out (double x, double y) near)
        {
            double dx = b.x - a.x;
            double dy = b.y - a.y;
            double len2 = (dx * dx) + (dy * dy);
            double t = len2 > 0 ? (((p.x - a.x) * dx) + ((p.y - a.y) * dy)) / len2 : 0.0;
            t = Math.Min(1.0, Math.Max(0.0, t));
            near = (a.x + (t * dx), a.y + (t * dy));
            double ex = p.x - near.x;
            double ey = p.y - near.y;
            return Math.Sqrt((ex * ex) + (ey * ey));
        }
    }
}
