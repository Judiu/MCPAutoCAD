using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>Segmento de entrada (LINE, o LWPOLYLINE abierta de 2 vértices) con su identificador (handle).</summary>
    public readonly struct WallLine
    {
        public WallLine(string id, double x1, double y1, double x2, double y2)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
        }

        public string Id { get; }

        public double X1 { get; }

        public double Y1 { get; }

        public double X2 { get; }

        public double Y2 { get; }
    }

    /// <summary>
    /// Parámetros de emparejado, en unidades del dibujo. Con <paramref name="espesores"/> (lista discreta) el espesor de
    /// cada par debe caer a ±<paramref name="toleranciaEspesor"/> de un valor de la lista, y el rango [min, max] se ignora.
    /// </summary>
    public sealed class WallPairOptions
    {
        /// <summary>Tolerancia angular por defecto entre caras paralelas (grados).</summary>
        public const double DefaultAnguloGrados = 0.5;

        public WallPairOptions(
            double espesorMin,
            double espesorMax,
            double tolerancia,
            double anguloGrados = DefaultAnguloGrados,
            IReadOnlyList<double>? espesores = null,
            double toleranciaEspesor = 0.0)
        {
            if (!(espesorMin > 0) || !double.IsFinite(espesorMin))
            {
                throw new ArgumentException("espesor_min: debe ser un número > 0");
            }

            if (!(espesorMax >= espesorMin) || !double.IsFinite(espesorMax))
            {
                throw new ArgumentException("espesor_max: debe ser un número >= espesor_min");
            }

            if (!(tolerancia > 0) || !double.IsFinite(tolerancia))
            {
                throw new ArgumentException("tolerancia_union: debe ser un número > 0");
            }

            if (!(anguloGrados > 0 && anguloGrados <= 5))
            {
                throw new ArgumentException("tolerancia angular: debe estar en (0, 5] grados");
            }

            if (espesores != null)
            {
                if (espesores.Count == 0)
                {
                    throw new ArgumentException("espesores: debe tener al menos un valor");
                }

                foreach (double e in espesores)
                {
                    if (!(e > 0) || !double.IsFinite(e))
                    {
                        throw new ArgumentException("espesores: cada valor debe ser un número > 0");
                    }
                }

                if (!(toleranciaEspesor >= 0) || !double.IsFinite(toleranciaEspesor))
                {
                    throw new ArgumentException("tolerancia de espesor: debe ser un número >= 0");
                }
            }

            EspesorMin = espesorMin;
            EspesorMax = espesorMax;
            Tolerancia = tolerancia;
            CosTolerancia = Math.Cos(anguloGrados * Math.PI / 180.0);
            Espesores = espesores?.ToList();
            ToleranciaEspesor = toleranciaEspesor;
            MaxEspesor = espesores == null ? espesorMax : Math.Max(espesorMax, espesores.Max());
        }

        public double EspesorMin { get; }

        public double EspesorMax { get; }

        public double Tolerancia { get; }

        /// <summary>Coseno mínimo de |u_a · u_b| para considerar dos segmentos paralelos.</summary>
        public double CosTolerancia { get; }

        /// <summary>Lista discreta de espesores (o null para usar el rango).</summary>
        public IReadOnlyList<double>? Espesores { get; }

        public double ToleranciaEspesor { get; }

        /// <summary>Mayor espesor admisible (máximo del rango o de la lista).</summary>
        public double MaxEspesor { get; }
    }

    /// <summary>Cara de muro: cuadrilátero formado por dos segmentos paralelos y el solape común.</summary>
    public sealed class WallQuad
    {
        public WallQuad(string idA, string idB, double espesor, double solape, IReadOnlyList<(double x, double y)> vertices)
        {
            IdA = idA;
            IdB = idB;
            Espesor = espesor;
            Solape = solape;
            Vertices = vertices;
        }

        public string IdA { get; }

        public string IdB { get; }

        public double Espesor { get; }

        public double Solape { get; }

        /// <summary>Cuatro vértices (P0, P1 sobre la línea A; Q1, Q0 sobre la línea B).</summary>
        public IReadOnlyList<(double x, double y)> Vertices { get; }
    }

    /// <summary>Resultado del emparejado de segmentos.</summary>
    public sealed class WallPairingResult
    {
        public WallPairingResult(
            IReadOnlyList<WallQuad> rects,
            int pares,
            IReadOnlyList<string> ignoradas,
            IReadOnlyList<string> sinPareja,
            IReadOnlyList<string> usadas,
            IReadOnlyList<(string Id, double Libre)> residuales,
            IReadOnlyList<string> advertencias,
            IReadOnlyList<(double x, double y)> puntosLibres)
        {
            PuntosLibres = puntosLibres;
            Advertencias = advertencias;
            Rects = rects;
            Pares = pares;
            Ignoradas = ignoradas;
            SinPareja = sinPareja;
            Usadas = usadas;
            Residuales = residuales;
        }

        public IReadOnlyList<WallQuad> Rects { get; }

        /// <summary>Número de pares distintos de segmentos que aportan alguna cara.</summary>
        public int Pares { get; }

        /// <summary>Segmentos de longitud ≤ tolerancia_union (no se emparejan).</summary>
        public IReadOnlyList<string> Ignoradas { get; }

        /// <summary>Segmentos válidos sin ninguna pareja.</summary>
        public IReadOnlyList<string> SinPareja { get; }

        /// <summary>Segmentos que aportaron al menos una cara (completa o parcialmente).</summary>
        public IReadOnlyList<string> Usadas { get; }

        /// <summary>Para cada segmento usado: longitud de tramo libre que NO cae sobre ninguna cara (los tramos libres sobre un muro vecino no cuentan).</summary>
        public IReadOnlyList<(string Id, double Libre)> Residuales { get; }

        /// <summary>Avisos del emparejado (p. ej. pares sin cierre de tapa cuando no se pasó espesores).</summary>
        public IReadOnlyList<string> Advertencias { get; }

        /// <summary>Extremos de los tramos libres que no cubre ningún muro (vanos no modelados): los usa la clasificación de tapas.</summary>
        public IReadOnlyList<(double x, double y)> PuntosLibres { get; }
    }

    /// <summary>
    /// Emparejado de segmentos paralelos a una distancia de espesor. Cada par aporta el cuadrilátero sobre el solape común.
    /// Orden de prioridad (determinista): (1) pares "cerrados" — hay un segmento que tapa el muro en un extremo, es decir
    /// une los dos extremos del par (tapa de muro, o cara pasante que cruza el muro); (2) menor distancia; (3) mayor solape;
    /// (4) índice. La distancia se comprueba en AMBOS extremos (no solo en la media): con 10 m y 0,5° la deriva es ~8,7 cm.
    /// Cada par consume solo el tramo que solapa de los tramos libres de ambos segmentos. Después, un hueco libre de
    /// como mucho el espesor máximo entre dos tramos consumidos con la misma distancia y el mismo muro vecino (cara
    /// pasante partida, p. ej. juntas en T) se rellena con un cuadrilátero (puente).
    /// Limitación documentada (BAJA-2): un vano cuyo ancho es igual al espesor, con jambas que abarcan el muro, se toma como
    /// muro (el par de jambas tiene solape = espesor, lo mínimo admitido). Sin <c>espesores</c> no hay forma de distinguirlo.
    /// </summary>
    public static class WallPairing
    {
        /// <summary>Tolerancia angular para considerar perpendicular una tapa (sin(2°)).</summary>
        private static readonly double SinPerpendicular = Math.Sin(2.0 * Math.PI / 180.0);

        public static WallPairingResult Pair(IReadOnlyList<WallLine> lines, WallPairOptions opt)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            if (opt == null)
            {
                throw new ArgumentNullException(nameof(opt));
            }

            double tol = opt.Tolerancia;
            int n = lines.Count;
            var segs = new Seg?[n];
            var ignoradas = new List<string>();
            for (int i = 0; i < n; i++)
            {
                WallLine w = lines[i];
                double dx = w.X2 - w.X1;
                double dy = w.Y2 - w.Y1;
                double len = Math.Sqrt((dx * dx) + (dy * dy));
                if (!double.IsFinite(len) || len <= tol)
                {
                    segs[i] = null;
                    ignoradas.Add(w.Id);
                    continue;
                }

                segs[i] = new Seg(w.X1, w.Y1, w.X2, w.Y2, len);
            }

            var cands = new List<Candidate>();
            for (int i = 0; i < n; i++)
            {
                Seg? a = segs[i];
                if (a == null)
                {
                    continue;
                }

                for (int j = i + 1; j < n; j++)
                {
                    Seg? b = segs[j];
                    if (b == null)
                    {
                        continue;
                    }

                    if (Math.Abs((a.Ux * b.Ux) + (a.Uy * b.Uy)) < opt.CosTolerancia)
                    {
                        continue;
                    }

                    double d1 = Math.Abs(Cross(a.Ux, a.Uy, b.Ax - a.Ax, b.Ay - a.Ay));
                    double d2 = Math.Abs(Cross(a.Ux, a.Uy, b.Bx - a.Ax, b.By - a.Ay));
                    double? d = MatchThickness(opt, d1, d2);
                    if (d == null)
                    {
                        continue;
                    }

                    Project(a, b, out double s1, out double s2);
                    double overlap = Math.Min(a.L, Math.Max(s1, s2)) - Math.Max(0.0, Math.Min(s1, s2));
                    // Un muro nunca es más corto que grueso: jambas de un vano (solape = espesor) no forman muro.
                    if (overlap <= tol || overlap < d.Value - tol)
                    {
                        continue;
                    }

                    int closed = ClosedSides(segs, a, b, i, j, tol);
                    cands.Add(new Candidate(i, j, d.Value, overlap, closed));
                }
            }

            // Desempate independiente del orden de las líneas: primero el segmento con menos candidatos (muro de cara libre
            // antes que una cara compartida con el aire), luego la distancia.
            var nCand = new int[n];
            foreach (Candidate cc in cands)
            {
                nCand[cc.I]++;
                nCand[cc.J]++;
            }

            cands.Sort((x, y) =>
            {
                int c = y.Closed.CompareTo(x.Closed);
                if (c != 0)
                {
                    return c;
                }

                c = Math.Min(nCand[x.I], nCand[x.J]).CompareTo(Math.Min(nCand[y.I], nCand[y.J]));
                if (c != 0)
                {
                    return c;
                }

                c = x.D.CompareTo(y.D);
                if (c != 0)
                {
                    return c;
                }

                c = y.Overlap.CompareTo(x.Overlap);
                if (c != 0)
                {
                    return c;
                }

                c = x.I.CompareTo(y.I);
                return c != 0 ? c : x.J.CompareTo(y.J);
            });

            var free = new List<Iv>[n];
            var recs = new List<Rec>[n];
            for (int i = 0; i < n; i++)
            {
                free[i] = segs[i] == null ? new List<Iv>() : new List<Iv> { new Iv(0.0, segs[i]!.L) };
                recs[i] = new List<Rec>();
            }

            var rects = new List<WallQuad>();
            var used = new bool[n];
            var pares = new HashSet<(int, int)>();
            var emitted = new bool[cands.Count];
            for (int ci = 0; ci < cands.Count; ci++)
            {
                Candidate c = cands[ci];
                Seg a = segs[c.I]!;
                Seg b = segs[c.J]!;
                Project(a, b, out double s1, out double s2);
                double k = (s2 - s1) / b.L;

                var bS = new List<Iv>(free[c.J].Count);
                foreach (Iv iv in free[c.J])
                {
                    bS.Add(Iv.Sorted(s1 + (k * iv.A), s1 + (k * iv.B)));
                }

                bS.Sort((x, y) => x.A.CompareTo(y.A));   // con caras antiparalelas (k < 0) el orden se invierte

                List<Iv> pieces = Intersect(free[c.I], bS, tol);
                emitted[ci] = pieces.Count > 0;
                foreach (Iv o in pieces)
                {
                    free[c.I] = Subtract(free[c.I], o);
                    Iv back = Iv.Sorted((o.A - s1) / k, (o.B - s1) / k);
                    free[c.J] = Subtract(free[c.J], back);
                    rects.Add(MakeQuad(a, b, o, c.D, lines[c.I].Id, lines[c.J].Id));
                    recs[c.I].Add(new Rec(o.A, o.B, c.J, c.D));
                    recs[c.J].Add(new Rec(back.A, back.B, c.I, c.D));
                    used[c.I] = true;
                    used[c.J] = true;
                    pares.Add((c.I, c.J));
                }
            }

            // Puentes: hueco libre ≤ espesor máximo entre dos tramos consumidos con el mismo espesor y el mismo muro vecino.
            for (int x = 0; x < n; x++)
            {
                Seg? sx = segs[x];
                if (sx == null || recs[x].Count < 2)
                {
                    continue;
                }

                foreach (Iv g in free[x].ToList())
                {
                    double len = g.B - g.A;
                    if (len <= tol || len > opt.MaxEspesor + tol || g.A <= tol || g.B >= sx.L - tol)
                    {
                        continue;
                    }

                    Rec? left = recs[x].FirstOrDefault(r => Math.Abs(r.B - g.A) <= tol);
                    Rec? right = recs[x].FirstOrDefault(r => Math.Abs(r.A - g.B) <= tol);
                    if (left == null || right == null || Math.Abs(left.D - right.D) > tol)
                    {
                        continue;
                    }

                    // T real: el muro perpendicular pasa por el extremo del hueco sobre x o por su proyección sobre la cara
                    // vecina y (p1/p2 empiezan en la línea de y y se alejan). Una jamba contenida en el espesor no cuenta.
                    Seg y = segs[left.Partner]!;
                    Seg z = segs[right.Partner]!;
                    if (!EndCrossed(segs, used, x, sx, y, PointAt(sx, g.A), left.D, tol)
                        || !EndCrossed(segs, used, x, sx, y, PointAt(sx, g.B), left.D, tol))
                    {
                        continue;
                    }

                    double zmx = (z.Ax + z.Bx) / 2.0;
                    double zmy = (z.Ay + z.By) / 2.0;
                    if (Math.Abs(Cross(y.Ux, y.Uy, zmx - y.Ax, zmy - y.Ay)) > tol)
                    {
                        continue;   // los dos muros vecinos no están sobre la misma línea
                    }

                    rects.Add(MakeQuad(sx, y, g, left.D, lines[x].Id, lines[left.Partner].Id));
                    free[x] = Subtract(free[x], g);
                    recs[x].Add(new Rec(g.A, g.B, left.Partner, left.D));
                    used[x] = true;
                    pares.Add((Math.Min(x, left.Partner), Math.Max(x, left.Partner)));
                }
            }

            // Esquinas a inglete: un tramo libre corto en el EXTREMO de una cara, junto a un par, cuyo extremo lejano coincide
            // con el extremo de un muro perpendicular usado, es el cuadrado de esquina. Una vez por esquina.
            var corners = new HashSet<(long, long)>();
            for (int x = 0; x < n; x++)
            {
                Seg? sx = segs[x];
                if (sx == null || recs[x].Count == 0)
                {
                    continue;
                }

                foreach (Iv g in free[x].ToList())
                {
                    double len = g.B - g.A;
                    if (len <= tol || len > opt.MaxEspesor + tol)
                    {
                        continue;
                    }

                    bool atStart = g.A <= tol;
                    bool atEnd = g.B >= sx.L - tol;
                    if (atStart == atEnd)
                    {
                        continue;
                    }

                    Rec? rec = atStart
                        ? recs[x].FirstOrDefault(r => Math.Abs(r.A - g.B) <= tol)
                        : recs[x].FirstOrDefault(r => Math.Abs(r.B - g.A) <= tol);
                    if (rec == null)
                    {
                        continue;
                    }

                    (double fx, double fy) far = atStart ? (sx.Ax, sx.Ay) : (sx.Bx, sx.By);
                    int perp = PerpendicularUsedEndAt(segs, used, x, sx, far, tol);
                    if (perp < 0)
                    {
                        continue;
                    }

                    // El cuadrado de esquina mide el espesor del muro perpendicular; más largo es un vano (puerta < 0,62).
                    double espPerp = recs[perp].Count > 0 ? recs[perp][0].D : rec.D;
                    if (len > espPerp + tol)
                    {
                        continue;
                    }

                    (double mx, double my) = PointAt(sx, (g.A + g.B) / 2.0);
                    if (WallPairing.Covered(rects, mx, my, tol))
                    {
                        continue;   // el tramo ya lo cubre un muro: no es esquina
                    }

                    if (!corners.Add(((long)Math.Round(far.fx / tol), (long)Math.Round(far.fy / tol))))
                    {
                        continue;
                    }

                    rects.Add(MakeQuad(sx, segs[rec.Partner]!, g, rec.D, lines[x].Id, lines[rec.Partner].Id));
                    free[x] = Subtract(free[x], g);
                    recs[x].Add(new Rec(g.A, g.B, rec.Partner, rec.D));
                }
            }

            // Avisos: sin 'espesores' el cierre por tapas es la única defensa contra huecos de aire entre muros.
            var advertencias = new List<string>();
            {
                var segBajo = new bool[n];
                int sinCierre = 0;
                for (int ci = 0; ci < cands.Count; ci++)
                {
                    if (emitted[ci] && cands[ci].Closed < 10)
                    {
                        sinCierre++;
                        segBajo[cands[ci].I] = true;
                        segBajo[cands[ci].J] = true;
                    }
                }

                int desplazados = 0;
                for (int ci = 0; ci < cands.Count; ci++)
                {
                    if (!emitted[ci] && cands[ci].Closed >= 10 && (segBajo[cands[ci].I] || segBajo[cands[ci].J]))
                    {
                        desplazados++;
                    }
                }

                if (sinCierre + desplazados > 0)
                {
                    string modo = opt.Espesores == null ? "sin 'espesores' " : "";
                    advertencias.Add(
                        "muros_desde_lineas: " + modo + sinCierre.ToString(CultureInfo.InvariantCulture)
                        + " par(es) se emparejaron sin cierre de tapa y " + desplazados.ToString(CultureInfo.InvariantCulture)
                        + " par(es) cerrado(s) quedaron desplazados: pueden ser huecos de aire o jambas"
                        + (opt.Espesores == null ? ". Pase 'espesores'." : "; revise la previsualización."));
                }
            }

            var sinPareja = new List<string>();
            var usadas = new List<string>();
            var residuales = new List<(string Id, double Libre)>();
            var puntosLibres = new List<(double x, double y)>();
            for (int i = 0; i < n; i++)
            {
                if (segs[i] == null)
                {
                    continue;
                }

                if (used[i])
                {
                    usadas.Add(lines[i].Id);
                    // Solo cuenta el tramo libre que no cae sobre una cara (p. ej. la cara exterior que termina en la cara de otro muro).
                    // Longitud exacta no cubierta (recorte contra cada cara), no una muestra de un punto.
                    double sinCubrir = 0.0;
                    foreach (Iv iv in free[i])
                    {
                        double no = UncoveredLength(rects, segs[i]!, iv, tol);
                        sinCubrir += no;
                        if (no > tol)
                        {
                            puntosLibres.Add(PointAt(segs[i]!, iv.A));
                            puntosLibres.Add(PointAt(segs[i]!, iv.B));
                        }
                    }

                    residuales.Add((lines[i].Id, sinCubrir));
                }
                else
                {
                    sinPareja.Add(lines[i].Id);
                }
            }

            return new WallPairingResult(rects, pares.Count, ignoradas, sinPareja, usadas, residuales, advertencias, puntosLibres);
        }

        /// <summary>
        /// true si (x, y) está dentro de alguna cara o a ≤ <paramref name="tolerancia"/> de su borde. Lo usa
        /// <see cref="WallFromLines.Build"/> para decidir si un segmento queda cubierto por los muros.
        /// </summary>
        public static bool Covered(IReadOnlyList<WallQuad> rects, double x, double y, double tolerancia)
        {
            foreach (WallQuad r in rects)
            {
                if (PolygonMetrics.Contains(r.Vertices, x, y) || PolygonMetrics.DistanceToBoundary(r.Vertices, x, y) <= tolerancia)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Primera cara que cubre (x, y) (dentro o a ≤ tolerancia de su borde), o null.</summary>
        internal static WallQuad? CoveringQuad(IReadOnlyList<WallQuad> rects, double x, double y, double tolerancia)
        {
            foreach (WallQuad r in rects)
            {
                if (PolygonMetrics.Contains(r.Vertices, x, y) || PolygonMetrics.DistanceToBoundary(r.Vertices, x, y) <= tolerancia)
                {
                    return r;
                }
            }

            return null;
        }

        /// <summary>Tapa perpendicular a un muro usado: se usa en <see cref="WallFromLines.Build"/>.</summary>
        internal static bool IsPerpendicular(double ux1, double uy1, double ux2, double uy2) =>
            Math.Abs((ux1 * ux2) + (uy1 * uy2)) <= SinPerpendicular;

        // ------------------------------------------------------------------ auxiliares

        /// <summary>Espesor del par si ambos extremos cumplen el rango (o la lista); null si no.</summary>
        private static double? MatchThickness(WallPairOptions o, double d1, double d2)
        {
            if (o.Espesores != null)
            {
                double? best = null;
                double bestErr = double.PositiveInfinity;
                foreach (double e in o.Espesores)
                {
                    if (Math.Abs(d1 - e) <= o.ToleranciaEspesor && Math.Abs(d2 - e) <= o.ToleranciaEspesor)
                    {
                        double err = Math.Abs(((d1 + d2) / 2.0) - e);
                        if (err < bestErr)
                        {
                            bestErr = err;
                            best = (d1 + d2) / 2.0;
                        }
                    }
                }

                return best;
            }

            if (d1 < o.EspesorMin || d1 > o.EspesorMax || d2 < o.EspesorMin || d2 > o.EspesorMax)
            {
                return null;
            }

            return (d1 + d2) / 2.0;
        }

        /// <summary>
        /// Puntuación de cierre del par (a, b): por cada extremo de a (con el extremo más cercano de b), 10 si una TAPA
        /// une exactamente los dos puntos (un segmento cuyos extremos coinciden con ellos), 1 si una cara PASANTE los cruza
        /// (un segmento que pasa por ambos y se extiende), 0 si ninguna. Un hueco de aire entre dos muros casi nunca tiene tapa.
        /// </summary>
        private static int ClosedSides(Seg?[] segs, Seg a, Seg b, int i, int j, double tol)
        {
            int total = 0;
            foreach ((double x, double y) pa in new[] { (a.Ax, a.Ay), (a.Bx, a.By) })
            {
                (double x, double y) pb = Dist2(pa, (b.Ax, b.Ay)) <= Dist2(pa, (b.Bx, b.By)) ? (b.Ax, b.Ay) : (b.Bx, b.By);
                int best = 0;
                for (int k = 0; k < segs.Length && best < 2; k++)
                {
                    Seg? s = segs[k];
                    if (k == i || k == j || s == null)
                    {
                        continue;
                    }

                    if (pa.x < Math.Min(s.Ax, s.Bx) - tol || pa.x > Math.Max(s.Ax, s.Bx) + tol
                        || pa.y < Math.Min(s.Ay, s.By) - tol || pa.y > Math.Max(s.Ay, s.By) + tol)
                    {
                        continue;
                    }

                    bool tapa = (Dist2(pa, (s.Ax, s.Ay)) <= tol * tol && Dist2(pb, (s.Bx, s.By)) <= tol * tol)
                        || (Dist2(pa, (s.Bx, s.By)) <= tol * tol && Dist2(pb, (s.Ax, s.Ay)) <= tol * tol);
                    if (tapa)
                    {
                        best = 10;
                    }
                    else if (PlanarGeom.NearestOnSegment(pa, (s.Ax, s.Ay), (s.Bx, s.By), out _) <= tol
                        && PlanarGeom.NearestOnSegment(pb, (s.Ax, s.Ay), (s.Bx, s.By), out _) <= tol)
                    {
                        best = 1;
                    }
                }

                total += best;
            }

            return total;
        }

        private static (double x, double y) PointAt(Seg s, double t) => (s.Ax + (s.Ux * t), s.Ay + (s.Uy * t));

        /// <summary>
        /// true si en el punto <paramref name="p"/> (sobre la cara x) hay otro segmento que lo cruza y se aleja de la línea de x
        /// más de <paramref name="d"/> (+ tolerancia): un muro perpendicular, no una jamba contenida en el espesor.
        /// </summary>
        private static bool CrossAt(Seg?[] segs, bool[] used, int x, Seg sx, (double x, double y) p, double d, double tol)
        {
            for (int k = 0; k < segs.Length; k++)
            {
                Seg? s = segs[k];
                if (k == x || s == null || !used[k] || PlanarGeom.NearestOnSegment(p, (s.Ax, s.Ay), (s.Bx, s.By), out _) > tol)
                {
                    continue;
                }

                double farA = Math.Abs(Cross(sx.Ux, sx.Uy, s.Ax - sx.Ax, s.Ay - sx.Ay));
                double farB = Math.Abs(Cross(sx.Ux, sx.Uy, s.Bx - sx.Ax, s.By - sx.Ay));
                if (Math.Max(farA, farB) > d + tol)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Hay un muro usado perpendicular a x con un extremo a ≤ tol de <paramref name="p"/> (esquina a inglete).</summary>
        private static int PerpendicularUsedEndAt(Seg?[] segs, bool[] used, int x, Seg sx, (double x, double y) p, double tol)
        {
            for (int k = 0; k < segs.Length; k++)
            {
                Seg? s = segs[k];
                if (k == x || s == null || !used[k] || !IsPerpendicular(sx.Ux, sx.Uy, s.Ux, s.Uy))
                {
                    continue;
                }

                if (Dist2(p, (s.Ax, s.Ay)) <= tol * tol || Dist2(p, (s.Bx, s.By)) <= tol * tol)
                {
                    return k;
                }
            }

            return -1;
        }

        /// <summary>
        /// Extremo del hueco cruzado por un muro perpendicular que llega a la cara VECINA y: se mide sobre la proyección del
        /// extremo sobre y. Un muro que llega por la cara continua (x) no basta: sería una ventana alineada con el muro.
        /// </summary>
        private static bool EndCrossed(Seg?[] segs, bool[] used, int x, Seg sx, Seg y, (double x, double y) p, double d, double tol) =>
            CrossAt(segs, used, x, sx, ProjectOnLine(y, p), d, tol);

        /// <summary>
        /// Longitud del tramo [iv] de s que NO cae dentro (ni a ≤ tol del borde) de ninguna cara: recorte exacto contra cada
        /// cuadrilátero convexo (Cyrus–Beck), no una muestra de un punto.
        /// </summary>
        private static double UncoveredLength(IReadOnlyList<WallQuad> rects, Seg s, Iv iv, double tol)
        {
            var rest = new List<Iv> { iv };
            foreach (WallQuad q in rects)
            {
                IReadOnlyList<(double x, double y)> v = q.Vertices;
                double orient = PolygonMetrics.SignedArea(v) >= 0 ? 1.0 : -1.0;
                double lo = double.NegativeInfinity;
                double hi = double.PositiveInfinity;
                for (int e = 0; e < v.Count && lo <= hi; e++)
                {
                    (double x, double y) p = v[e];
                    (double x, double y) r = v[(e + 1) % v.Count];
                    double ex = r.x - p.x;
                    double ey = r.y - p.y;
                    double el = Math.Sqrt((ex * ex) + (ey * ey));
                    if (el <= 1e-12)
                    {
                        continue;
                    }

                    // f(t) = distancia con signo (hacia dentro) de s(t) al borde e; se admite f(t) >= -tol.
                    double f0 = orient * Cross(ex, ey, s.Ax - p.x, s.Ay - p.y) / el;
                    double df = orient * Cross(ex, ey, s.Ux, s.Uy) / el;
                    if (Math.Abs(df) <= 1e-12)
                    {
                        if (f0 < -tol)
                        {
                            lo = double.PositiveInfinity;
                        }
                    }
                    else
                    {
                        double t = (-tol - f0) / df;
                        if (df > 0)
                        {
                            lo = Math.Max(lo, t);
                        }
                        else
                        {
                            hi = Math.Min(hi, t);
                        }
                    }
                }

                if (lo < hi)
                {
                    var next = new List<Iv>();
                    foreach (Iv part in rest)
                    {
                        next.AddRange(Subtract(new List<Iv> { part }, new Iv(lo, hi)));
                    }

                    rest = next;
                }
            }

            double total = 0.0;
            foreach (Iv part in rest)
            {
                total += part.B - part.A;
            }

            return total;
        }

        private static double Dist2((double x, double y) p, (double x, double y) q)
        {
            double dx = p.x - q.x;
            double dy = p.y - q.y;
            return (dx * dx) + (dy * dy);
        }

        private static double Cross(double ux, double uy, double vx, double vy) => (ux * vy) - (uy * vx);

        /// <summary>Proyecciones de los extremos de b sobre el eje de a (a en s = 0 ... L_a).</summary>
        private static void Project(Seg a, Seg b, out double s1, out double s2)
        {
            s1 = ((b.Ax - a.Ax) * a.Ux) + ((b.Ay - a.Ay) * a.Uy);
            s2 = ((b.Bx - a.Ax) * a.Ux) + ((b.By - a.Ay) * a.Uy);
        }

        private static List<Iv> Intersect(List<Iv> xs, List<Iv> ys, double tol)
        {
            var res = new List<Iv>();
            int p = 0;
            int q = 0;
            while (p < xs.Count && q < ys.Count)
            {
                double lo = Math.Max(xs[p].A, ys[q].A);
                double hi = Math.Min(xs[p].B, ys[q].B);
                if (hi - lo > tol)
                {
                    res.Add(new Iv(lo, hi));
                }

                if (xs[p].B < ys[q].B)
                {
                    p++;
                }
                else
                {
                    q++;
                }
            }

            return res;
        }

        private static List<Iv> Subtract(List<Iv> xs, Iv o)
        {
            var res = new List<Iv>(xs.Count + 1);
            foreach (Iv iv in xs)
            {
                if (iv.B <= o.A || iv.A >= o.B)
                {
                    res.Add(iv);
                    continue;
                }

                if (iv.A < o.A)
                {
                    res.Add(new Iv(iv.A, o.A));
                }

                if (iv.B > o.B)
                {
                    res.Add(new Iv(o.B, iv.B));
                }
            }

            res.RemoveAll(iv => iv.B - iv.A <= 1e-12);
            return res;
        }

        /// <summary>Cuadrilátero sobre el tramo o de la línea a (coordenada s) con su proyección sobre la línea b.</summary>
        private static WallQuad MakeQuad(Seg a, Seg b, Iv o, double espesor, string idA, string idB)
        {
            (double x, double y) p0 = (a.Ax + (a.Ux * o.A), a.Ay + (a.Uy * o.A));
            (double x, double y) p1 = (a.Ax + (a.Ux * o.B), a.Ay + (a.Uy * o.B));
            (double x, double y) q1 = ProjectOnLine(b, p1);
            (double x, double y) q0 = ProjectOnLine(b, p0);
            return new WallQuad(idA, idB, espesor, o.B - o.A, new[] { p0, p1, q1, q0 });
        }

        private static (double x, double y) ProjectOnLine(Seg b, (double x, double y) p)
        {
            double t = ((p.x - b.Ax) * b.Ux) + ((p.y - b.Ay) * b.Uy);
            return (b.Ax + (t * b.Ux), b.Ay + (t * b.Uy));
        }

        private sealed class Seg
        {
            public Seg(double ax, double ay, double bx, double by, double len)
            {
                Ax = ax;
                Ay = ay;
                Bx = bx;
                By = by;
                L = len;
                Ux = (bx - ax) / len;
                Uy = (by - ay) / len;
            }

            public double Ax { get; }

            public double Ay { get; }

            public double Bx { get; }

            public double By { get; }

            public double L { get; }

            public double Ux { get; }

            public double Uy { get; }
        }

        /// <summary>Tramo consumido de un segmento (coordenada t sobre él) con el vecino y el espesor del par.</summary>
        private sealed class Rec
        {
            public Rec(double a, double b, int partner, double d)
            {
                A = a;
                B = b;
                Partner = partner;
                D = d;
            }

            public double A { get; }

            public double B { get; }

            public int Partner { get; }

            public double D { get; }
        }

        private readonly struct Iv
        {
            public Iv(double a, double b)
            {
                A = a;
                B = b;
            }

            public double A { get; }

            public double B { get; }

            public static Iv Sorted(double a, double b) => a <= b ? new Iv(a, b) : new Iv(b, a);
        }

        private readonly struct Candidate
        {
            public Candidate(int i, int j, double d, double overlap, int closed)
            {
                I = i;
                J = j;
                D = d;
                Overlap = overlap;
                Closed = closed;
            }

            public int I { get; }

            public int J { get; }

            public double D { get; }

            public double Overlap { get; }

            public int Closed { get; }
        }
    }

    /// <summary>Resultado completo de <see cref="WallFromLines.Build"/>.</summary>
    public sealed class WallBuildResult
    {
        public WallBuildResult(
            WallPairingResult pairing,
            WallUnionResult union,
            IReadOnlyList<string> borrables,
            IReadOnlyList<string> parciales,
            IReadOnlyList<string> tapas)
        {
            Pairing = pairing;
            Union = union;
            Borrables = borrables;
            Parciales = parciales;
            Tapas = tapas;
        }

        public WallPairingResult Pairing { get; }

        public WallUnionResult Union { get; }

        /// <summary>Segmentos usados cuyo recorrido queda cubierto por los muros y sin tramo libre: pueden borrarse si se pide.</summary>
        public IReadOnlyList<string> Borrables { get; }

        /// <summary>Segmentos usados con algún tramo libre (no cubierto por muro): no se borran.</summary>
        public IReadOnlyList<string> Parciales { get; }

        /// <summary>Segmentos sin pareja, de longitud ≈ espesor, perpendiculares a un muro y cubiertos: tapas de muro.</summary>
        public IReadOnlyList<string> Tapas { get; }

        public IReadOnlyList<WallRegion> Regions => Union.Regions;
    }

    /// <summary>Entrada completa: emparejado + unión + selección de segmentos borrables y tapas. Límites de seguridad aquí.</summary>
    public static class WallFromLines
    {
        /// <summary>Máximo de segmentos de entrada.</summary>
        public const int MaxLineas = 2000;

        /// <summary>Máximo de caras emparejadas (la unión es O(E²)).</summary>
        public const int MaxCaras = 1000;

        /// <summary>Máximo de muros (regiones) resultantes.</summary>
        public const int MaxMuros = 500;

        /// <summary>Espesor mínimo por defecto, en metros (7,5 cm).</summary>
        public const double DefaultEspesorMinM = 0.075;

        /// <summary>Espesor máximo por defecto, en metros (62 cm).</summary>
        public const double DefaultEspesorMaxM = 0.62;

        /// <summary>Tolerancia de unión por defecto, en metros (1,2 cm).</summary>
        public const double DefaultToleranciaM = 0.012;

        /// <summary>Tolerancia por defecto de un valor de la lista <c>espesores</c>, en metros (±1 cm).</summary>
        public const double DefaultToleranciaEspesorM = 0.01;

        public static WallBuildResult Build(IReadOnlyList<WallLine> lines, WallPairOptions opt)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            if (lines.Count > MaxLineas)
            {
                throw new ArgumentException(
                    "demasiadas líneas de entrada (" + lines.Count.ToString(CultureInfo.InvariantCulture)
                    + "; límite " + MaxLineas.ToString(CultureInfo.InvariantCulture) + "): reduzca la bbox o capas_origen");
            }

            WallPairingResult pr = WallPairing.Pair(lines, opt);
            if (pr.Rects.Count > MaxCaras)
            {
                throw new ArgumentException(
                    "demasiadas caras emparejadas (" + pr.Rects.Count.ToString(CultureInfo.InvariantCulture)
                    + "; límite " + MaxCaras.ToString(CultureInfo.InvariantCulture) + "): reduzca la bbox o capas_origen");
            }

            WallUnionResult un = WallRegionUnion.Union(pr.Rects.Select(r => r.Vertices).ToList(), opt.Tolerancia);
            if (un.Regions.Count > MaxMuros)
            {
                throw new ArgumentException(
                    "demasiados muros resultantes (" + un.Regions.Count.ToString(CultureInfo.InvariantCulture)
                    + "; límite " + MaxMuros.ToString(CultureInfo.InvariantCulture) + "): reduzca la bbox");
            }

            double tol = opt.Tolerancia;
            var usadas = new HashSet<string>(pr.Usadas, StringComparer.Ordinal);
            var libre = pr.Residuales.ToDictionary(r => r.Id, r => r.Libre, StringComparer.Ordinal);

            var borrables = new List<string>();
            var parciales = new List<string>();
            var tapas = new List<string>();
            foreach (WallLine w in lines)
            {
                bool cubierto = Covered(pr.Rects, w.X1, w.Y1, tol)
                    && Covered(pr.Rects, w.X2, w.Y2, tol)
                    && Covered(pr.Rects, (w.X1 + w.X2) / 2.0, (w.Y1 + w.Y2) / 2.0, tol);
                if (usadas.Contains(w.Id))
                {
                    (cubierto && libre[w.Id] <= tol ? borrables : parciales).Add(w.Id);
                    continue;
                }

                double len = Math.Sqrt(((w.X2 - w.X1) * (w.X2 - w.X1)) + ((w.Y2 - w.Y1) * (w.Y2 - w.Y1)));
                if (!cubierto || len <= tol || len > opt.MaxEspesor + tol)
                {
                    continue;
                }

                double ux = (w.X2 - w.X1) / len;
                double uy = (w.Y2 - w.Y1) / len;
                // Perpendicular al muro QUE CUBRE la línea (no a cualquier muro del dibujo).
                WallQuad? muro = WallPairing.CoveringQuad(pr.Rects, (w.X1 + w.X2) / 2.0, (w.Y1 + w.Y2) / 2.0, tol);
                // Una jamba junto al extremo de un tramo libre de una cara usada es un vano no modelado: no es tapa de muro.
                bool junto = pr.PuntosLibres.Any(p => NearPoint(p, (w.X1, w.Y1), tol) || NearPoint(p, (w.X2, w.Y2), tol));
                if (muro != null && !junto && AbarcaEspesor(muro, (w.X1, w.Y1), (w.X2, w.Y2), tol))
                {
                    double mx = muro.Vertices[1].x - muro.Vertices[0].x;
                    double my = muro.Vertices[1].y - muro.Vertices[0].y;
                    double ml = Math.Sqrt((mx * mx) + (my * my));
                    if (ml > 0 && WallPairing.IsPerpendicular(ux, uy, mx / ml, my / ml))
                    {
                        tapas.Add(w.Id);
                    }
                }
            }

            return new WallBuildResult(pr, un, borrables, parciales, tapas);
        }

        private static bool Covered(IReadOnlyList<WallQuad> rects, double x, double y, double tol) =>
            WallPairing.Covered(rects, x, y, tol);

        /// <summary>
        /// La tapa une las dos caras del muro: un extremo a ≤ tol de un borde largo del cuadrilátero y el otro a ≤ tol del
        /// opuesto. Un tramo corto sobre una sola cara (sello, mobiliario, fragmento) no la cumple.
        /// </summary>
        private static bool AbarcaEspesor(WallQuad muro, (double x, double y) p1, (double x, double y) p2, double tol)
        {
            IReadOnlyList<(double x, double y)> v = muro.Vertices;
            bool ab = NearSegment(p1, v[0], v[1], tol) && NearSegment(p2, v[2], v[3], tol);
            bool ba = NearSegment(p1, v[2], v[3], tol) && NearSegment(p2, v[0], v[1], tol);
            return ab || ba;
        }

        private static bool NearSegment((double x, double y) p, (double x, double y) a, (double x, double y) b, double tol) =>
            PlanarGeom.NearestOnSegment(p, a, b, out _) <= tol;

        private static bool NearPoint((double x, double y) p, (double x, double y) q, double tol)
        {
            double dx = p.x - q.x;
            double dy = p.y - q.y;
            return (dx * dx) + (dy * dy) <= tol * tol;
        }
    }
}
