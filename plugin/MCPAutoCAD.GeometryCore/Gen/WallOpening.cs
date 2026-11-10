using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MCPAutoCAD.GeometryCore.Draw;
using Pt = (double x, double y);

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Hueco (vano) en el contorno cerrado de un muro (protocol.md §5G.1). Puro.
    /// Representación fijada con la evidencia de casa6x12_limpio.dwg (§5G.0): el contorno del muro (LWPOLYLINE cerrada
    /// de 4 vértices de la oficina, o el contorno a inglete de <c>muro</c> de 4A) se PARTE en dos contornos cerrados, uno
    /// a cada lado del hueco; el lado corto de cada uno junto al hueco es la "tapa de jamba" (la oficina dibuja ese tramo
    /// como cierre de la polilínea abierta del muro: 95187 y 950CB del plano). El hueco queda vacío: las jambas las trae el
    /// bloque de puerta/ventana.
    /// </summary>
    public static class WallOpening
    {
        /// <summary>Tolerancia geométrica por defecto (la de WallOutline / PolylineValidator).</summary>
        public const double DefaultTol = 1e-9;

        /// <summary>Margen mínimo entre el hueco y el extremo del tramo: una pieza más corta sería casi degenerada (§5G.1).</summary>
        public const double MinMargin = 0.01;

        /// <summary>Componente x bajo la cual una dirección se considera vertical (el inicio pasa a ser el de menor y).</summary>
        private const double AxisSnap = 1e-9;

        /// <summary>
        /// Resultado de <see cref="Cut"/>: los dos contornos resultantes (cerrados, sin repetir el primer vértice, en el
        /// sentido del original), las cuatro esquinas del hueco (A1 y A2 en el lado del inicio del eje, B1 y B2 en el del
        /// fin; A1/B1 en la cara izquierda respecto a <see cref="Direccion"/>, A2/B2 en la derecha), el centro del hueco
        /// sobre la línea media, la dirección unitaria del tramo, el espesor medido y la longitud del tramo.
        /// </summary>
        public sealed record OpeningResult(
            IReadOnlyList<(double x, double y)> LadoInicio,
            IReadOnlyList<(double x, double y)> LadoFin,
            (double x, double y) A1,
            (double x, double y) A2,
            (double x, double y) B1,
            (double x, double y) B2,
            (double x, double y) Centro,
            (double x, double y) Direccion,
            double Espesor,
            double LongitudTramo,
            double DistanciaDesdeInicio);

        /// <summary>
        /// Parte el polígono cerrado <paramref name="contorno"/> por un hueco de ancho <paramref name="ancho"/> centrado en
        /// la proyección de <paramref name="centro"/> sobre la línea media del tramo recto más cercano a ese punto.
        /// Tramo = la arista del contorno más cercana a <paramref name="centro"/> (cara 1) y su arista paralela opuesta
        /// (cara 2); espesor = distancia entre ambas. Las dos líneas de corte son perpendiculares al tramo. Lanza
        /// <see cref="WallGeometryException"/> con <c>EsParametro = false</c> (→ INVALID_GEOMETRY) y estos textos exactos:
        /// <list type="bullet">
        /// <item>"muro: el contorno debe ser un polígono cerrado simple de al menos 4 vértices"</item>
        /// <item>"muro: no se encontró un tramo recto (dos caras paralelas) cerca del punto indicado"</item>
        /// <item>"ancho: el hueco (&lt;w&gt;) no cabe en el tramo (longitud &lt;L&gt;): se sale del tramo o cruza una esquina"</item>
        /// <item>"ancho: el hueco cruza un vértice del contorno (esquina u otro vano)"</item>
        /// <item>"ancho: debe ser menor que la longitud del tramo"</item>
        /// </list>
        /// y con <c>EsParametro = true</c> (→ INVALID_PARAMS): "ancho: debe ser un número mayor que 0". Números con
        /// <c>0.###</c> invariante. Cada contorno resultante debe pasar <c>PolylineValidator</c> (cerrado, simple, área &gt; 0).
        /// </summary>
        public static OpeningResult Cut(
            IReadOnlyList<(double x, double y)> contorno,
            (double x, double y) centro,
            double ancho,
            double tol = DefaultTol)
        {
            if (!double.IsFinite(ancho) || ancho <= 0)
            {
                throw new WallGeometryException("ancho: debe ser un número mayor que 0", true);
            }

            List<Pt> loop = NormalizeCcw(contorno, tol, out bool invertido);
            WallSpan s = FindSpan(loop, centro, tol);

            if (ancho >= s.Longitud - tol)
            {
                throw new WallGeometryException("ancho: debe ser menor que la longitud del tramo", false);
            }

            double t = Dot(Sub(centro, s.Inicio), s.Direccion);
            double t1 = t - (ancho / 2.0);
            double t2 = t + (ancho / 2.0);
            if (t1 < MinMargin || t2 > s.Longitud - MinMargin)
            {
                throw new WallGeometryException(
                    "ancho: el hueco (" + Num(ancho) + ") no cabe en el tramo (longitud " + Num(s.Longitud)
                    + "): se sale del tramo o cruza una esquina",
                    false);
            }

            RejectVertexInStrip(loop, s, t1, t2, tol);

            int n = loop.Count;
            (Pt e1a, Pt e1b) = (loop[s.I], loop[(s.I + 1) % n]);
            (Pt e2a, Pt e2b) = (loop[s.J], loop[(s.J + 1) % n]);
            Pt corte1 = Along(s, t1);
            Pt corte2 = Along(s, t2);
            Pt a1Cara = OnEdge(e1a, e1b, corte1, s.Direccion);
            Pt b1Cara = OnEdge(e1a, e1b, corte2, s.Direccion);
            Pt a2Cara = OnEdge(e2a, e2b, corte1, s.Direccion);
            Pt b2Cara = OnEdge(e2a, e2b, corte2, s.Direccion);

            // Trozo que contiene v_i (sigue el lazo desde v_{j+1}) y trozo que contiene v_{i+1} (hasta v_j). Si el eje va en el
            // sentido de e1, el primero es el del inicio; si va al revés, el del fin. El interior queda a la izquierda de e1
            // (lazo CCW), así que e1 es la cara DERECHA respecto al eje cuando van en el mismo sentido.
            bool mismoSentido = Dot(s.Direccion, Sub(e1b, e1a)) > 0;
            List<Pt> ladoV = LoopPiece(loop, (s.J + 1) % n, s.I);
            List<Pt> ladoW = LoopPiece(loop, (s.I + 1) % n, s.J);
            if (mismoSentido)
            {
                ladoV.Insert(0, a2Cara);
                ladoV.Insert(0, a1Cara);
                ladoW.Insert(0, b1Cara);
                ladoW.Add(b2Cara);
            }
            else
            {
                ladoV.Insert(0, b2Cara);
                ladoV.Insert(0, b1Cara);
                ladoW.Insert(0, a1Cara);
                ladoW.Add(a2Cara);
            }

            List<Pt> ladoInicio = mismoSentido ? ladoV : ladoW;
            List<Pt> ladoFin = mismoSentido ? ladoW : ladoV;
            CheckPiece(ladoInicio, tol);
            CheckPiece(ladoFin, tol);
            if (invertido)
            {
                ladoInicio.Reverse();
                ladoFin.Reverse();
            }

            return new OpeningResult(
                ladoInicio,
                ladoFin,
                mismoSentido ? a2Cara : a1Cara,
                mismoSentido ? a1Cara : a2Cara,
                mismoSentido ? b2Cara : b1Cara,
                mismoSentido ? b1Cara : b2Cara,
                Along(s, t),
                s.Direccion,
                s.Espesor,
                s.Longitud,
                t);
        }

        /// <summary>
        /// Centro del hueco a partir de una distancia desde un extremo del eje de un contorno de 4 vértices (rectángulo o
        /// paralelogramo): el eje es la línea media entre las dos caras largas; <paramref name="desdeFin"/> = false mide
        /// desde el extremo de menor x (a igual x, menor y). Lanza <see cref="WallGeometryException"/> (EsParametro = false)
        /// "muro: posicion por distancia solo se admite en contornos de 4 vértices" y (EsParametro = true)
        /// "posicion: la distancia &lt;d&gt; se sale del tramo (longitud &lt;L&gt;)".
        /// En un paralelogramo el eje útil es el mismo que usa <see cref="Cut"/>: el solape de las dos caras proyectado sobre
        /// la dirección, para que distancia y <see cref="OpeningResult.DistanciaDesdeInicio"/> sean la misma medida.
        /// </summary>
        public static (double x, double y) CenterFromDistance(
            IReadOnlyList<(double x, double y)> contorno,
            double distancia,
            bool desdeFin,
            double tol = DefaultTol)
        {
            if (contorno == null || PolygonMetrics.CollapseConsecutiveDuplicates(contorno, out _, tol).Count != 4)
            {
                throw new WallGeometryException("muro: posicion por distancia solo se admite en contornos de 4 vértices", false);
            }

            List<Pt> loop = NormalizeCcw(contorno, tol, out _);
            WallSpan s = LongestPair(loop, tol);
            if (!double.IsFinite(distancia) || distancia < -tol || distancia > s.Longitud + tol)
            {
                throw new WallGeometryException(
                    "posicion: la distancia " + Num(distancia) + " se sale del tramo (longitud " + Num(s.Longitud) + ")", true);
            }

            return Along(s, desdeFin ? s.Longitud - distancia : distancia);
        }

        /// <summary>Advertencia de §5G.1: el hueco cae sobre entidades de la capa de columnas. Cita hasta 5 handles.</summary>
        public static string ColumnWarning(string capa, IReadOnlyList<string> handles)
        {
            const int MaxCitados = 5;
            int resto = handles.Count - MaxCitados;
            return "vano: el hueco cae sobre " + Int(handles.Count) + " entidad(es) de la capa '" + capa + "' ("
                + string.Join(", ", handles.Take(MaxCitados)) + (resto > 0 ? " y " + Int(resto) + " más" : string.Empty)
                + "): revise la estructura antes de abrirlo.";
        }

        /// <summary>Advertencia de §5G.1: sombreados bajo el hueco que no se rehacen. Cita hasta 5 handles.</summary>
        public static string HatchesCrossingWarning(IReadOnlyList<string> handles)
        {
            const int MaxCitados = 5;
            int resto = handles.Count - MaxCitados;
            return "vano: " + Int(handles.Count) + " sombreado(s) cruzan el hueco y no se rehacen ("
                + string.Join(", ", handles.Take(MaxCitados)) + (resto > 0 ? " y " + Int(resto) + " más" : string.Empty) + "): revíselos.";
        }

        /// <summary>
        /// true si el rectángulo del hueco (A1, A2, B2, B1) toca o cruza <paramref name="caja"/>: ejes separadores de la caja y
        /// del hueco. Sirve para avisar de un hueco abierto sobre una columna (§5G.1).
        /// </summary>
        public static bool HoleTouches(OpeningResult hueco, BBox2 caja)
        {
            Pt[] q = { hueco.A1, hueco.A2, hueco.B2, hueco.B1 };
            if (q.Max(p => p.x) < caja.Xmin || q.Min(p => p.x) > caja.Xmax || q.Max(p => p.y) < caja.Ymin || q.Min(p => p.y) > caja.Ymax)
            {
                return false;
            }

            Pt[] esquinas = { (caja.Xmin, caja.Ymin), (caja.Xmax, caja.Ymin), (caja.Xmax, caja.Ymax), (caja.Xmin, caja.Ymax) };
            foreach (Pt eje in new[] { hueco.Direccion, (-hueco.Direccion.y, hueco.Direccion.x) })
            {
                double maxHueco = q.Max(p => Dot(p, eje));
                double minHueco = q.Min(p => Dot(p, eje));
                if (esquinas.Max(p => Dot(p, eje)) < minHueco || esquinas.Min(p => Dot(p, eje)) > maxHueco)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Tramo recto: caras e1 = v_i→v_{i+1} y e2 = v_j→v_{j+1} del lazo CCW, dirección e inicio de la línea media.
        /// </summary>
        private sealed record WallSpan(int I, int J, Pt Inicio, Pt Direccion, double Longitud, double Espesor, Pt NormalInterior);

        /// <summary>Quita duplicados, valida (polígono simple de ≥ 4 vértices con área) y devuelve el lazo en sentido antihorario.</summary>
        private static List<Pt> NormalizeCcw(IReadOnlyList<Pt> contorno, double tol, out bool invertido)
        {
            invertido = false;
            if (contorno == null)
            {
                throw InvalidContour();
            }

            List<Pt> loop = PolygonMetrics.CollapseConsecutiveDuplicates(contorno, out _, tol);
            if (loop.Count < 4 || PolylineValidator.Validate(loop, true, null, tol).Count > 0)
            {
                throw InvalidContour();
            }

            double area = LocalSignedArea(loop);
            if (Math.Abs(area) <= tol)
            {
                throw InvalidContour();
            }

            if (area < 0)
            {
                loop.Reverse();
                invertido = true;
            }

            return loop;
        }

        /// <summary>
        /// e1 = arista más cercana al centro (a igual distancia, la más larga: el centro de un muro equidista de sus dos
        /// caras y en una esquina no debe ganar la tapa). Una pareja de caras más separadas que largas no es un tramo de muro
        /// sino sus dos tapas (punto junto al extremo): el "hueco" partiría el muro a lo largo, así que se pasa a la siguiente
        /// arista más cercana y esa pareja solo se usa si no hay otra.
        /// </summary>
        private static WallSpan FindSpan(List<Pt> loop, Pt centro, double tol)
        {
            if (!double.IsFinite(centro.x) || !double.IsFinite(centro.y))
            {
                throw NoSpan();
            }

            var vistas = new HashSet<int>();
            WallSpan? respaldo = null;
            while (vistas.Count < loop.Count)
            {
                int i = NearestEdge(loop, centro, vistas, tol);
                vistas.Add(i);
                WallSpan? s = OppositeFace(loop, i, centro, tol);
                if (s == null)
                {
                    if (respaldo == null)
                    {
                        throw NoSpan();
                    }

                    continue;
                }

                if (s.Longitud >= s.Espesor)
                {
                    return s;
                }

                respaldo ??= s;
            }

            return respaldo ?? throw NoSpan();
        }

        /// <summary>
        /// e2 para la cara e1 = arista <paramref name="i"/>: paralela de sentido opuesto, con el interior entre ambas y solape
        /// no nulo. Se prefiere la que abarca el centro a lo largo del tramo, la que deja el centro entre las dos caras y, a
        /// igualdad, la más cercana. null si no hay ninguna.
        /// </summary>
        private static WallSpan? OppositeFace(List<Pt> loop, int i, Pt centro, double tol)
        {
            WallSpan? mejor = null;
            int mejorRango = int.MaxValue;
            for (int j = 0; j < loop.Count; j++)
            {
                if (j == i || !AreAntiparallel(loop, i, j, tol))
                {
                    continue;
                }

                WallSpan s = BuildSpan(loop, i, j);
                if (s.Espesor <= tol || s.Longitud <= tol)
                {
                    continue;
                }

                double t = Dot(Sub(centro, s.Inicio), s.Direccion);
                bool abarca = t >= -tol && t <= s.Longitud + tol;
                bool entreCaras = Dot(Sub(centro, loop[i]), s.NormalInterior) < s.Espesor;
                int rango = (abarca ? 0 : 2) + (entreCaras ? 0 : 1);
                if (mejor == null || rango < mejorRango || (rango == mejorRango && s.Espesor < mejor.Espesor))
                {
                    mejor = s;
                    mejorRango = rango;
                }
            }

            return mejor;
        }

        private static int NearestEdge(List<Pt> loop, Pt p, HashSet<int> excluidas, double tol)
        {
            int n = loop.Count;
            int mejor = -1;
            double mejorDist = double.PositiveInfinity;
            double mejorLen = 0;
            for (int k = 0; k < n; k++)
            {
                if (excluidas.Contains(k))
                {
                    continue;
                }

                Pt a = loop[k];
                Pt b = loop[(k + 1) % n];
                double dist = DistanceToSegment(p, a, b);
                double len = PolygonMetrics.Dist(a, b);
                if (dist < mejorDist - tol || (dist <= mejorDist + tol && len > mejorLen))
                {
                    mejor = k;
                    mejorDist = dist;
                    mejorLen = len;
                }
            }

            return mejor;
        }

        /// <summary>Par de aristas opuestas de un cuadrilátero con mayor longitud que sean paralelas.</summary>
        private static WallSpan LongestPair(List<Pt> loop, double tol)
        {
            double par0 = PolygonMetrics.Dist(loop[0], loop[1]) + PolygonMetrics.Dist(loop[2], loop[3]);
            double par1 = PolygonMetrics.Dist(loop[1], loop[2]) + PolygonMetrics.Dist(loop[3], loop[0]);
            int primero = par0 >= par1 ? 0 : 1;
            foreach (int i in new[] { primero, 1 - primero })
            {
                if (AreAntiparallel(loop, i, i + 2, tol))
                {
                    WallSpan s = BuildSpan(loop, i, i + 2);
                    if (s.Longitud > tol)
                    {
                        return s;
                    }
                }
            }

            throw NoSpan();
        }

        private static bool AreAntiparallel(List<Pt> loop, int i, int j, double tol)
        {
            int n = loop.Count;
            Pt e1 = Sub(loop[(i + 1) % n], loop[i]);
            Pt e2 = Sub(loop[(j + 1) % n], loop[j]);
            double cross = (e1.x * e2.y) - (e1.y * e2.x);
            return Math.Abs(cross) <= tol * Length(e1) * Length(e2) && Dot(e1, e2) < 0;
        }

        private static WallSpan BuildSpan(List<Pt> loop, int i, int j)
        {
            int n = loop.Count;
            Pt v = loop[i];
            Pt d1 = Unit(Sub(loop[(i + 1) % n], v));
            Pt normal = (-d1.y, d1.x);
            Pt dir = Math.Abs(d1.x) <= AxisSnap ? (d1.y > 0 ? d1 : (-d1.x, -d1.y)) : (d1.x > 0 ? d1 : (-d1.x, -d1.y));

            double t1a = 0;
            double t1b = Dot(Sub(loop[(i + 1) % n], v), dir);
            double t2a = Dot(Sub(loop[j], v), dir);
            double t2b = Dot(Sub(loop[(j + 1) % n], v), dir);
            double lo = Math.Max(Math.Min(t1a, t1b), Math.Min(t2a, t2b));
            double hi = Math.Min(Math.Max(t1a, t1b), Math.Max(t2a, t2b));

            double espesor = (Dot(Sub(loop[j], v), normal) + Dot(Sub(loop[(j + 1) % n], v), normal)) / 2.0;
            Pt inicio = (
                v.x + (lo * dir.x) + (espesor / 2.0 * normal.x),
                v.y + (lo * dir.y) + (espesor / 2.0 * normal.y));
            return new WallSpan(i, j, inicio, dir, hi - lo, espesor, normal);
        }

        /// <summary>Un vértice ajeno a las dos caras entre los cortes y entre las caras: esquina, muesca u otro vano.</summary>
        private static void RejectVertexInStrip(List<Pt> loop, WallSpan s, double t1, double t2, double tol)
        {
            int n = loop.Count;
            for (int k = 0; k < n; k++)
            {
                if (k == s.I || k == (s.I + 1) % n || k == s.J || k == (s.J + 1) % n)
                {
                    continue;
                }

                double t = Dot(Sub(loop[k], s.Inicio), s.Direccion);
                double h = Dot(Sub(loop[k], loop[s.I]), s.NormalInterior);
                if (t >= t1 - tol && t <= t2 + tol && h >= -tol && h <= s.Espesor + tol)
                {
                    throw new WallGeometryException("ancho: el hueco cruza un vértice del contorno (esquina u otro vano)", false);
                }
            }
        }

        /// <summary>Vértices del lazo desde <paramref name="desde"/> hasta <paramref name="hasta"/>, ambos incluidos, en orden.</summary>
        private static List<Pt> LoopPiece(List<Pt> loop, int desde, int hasta)
        {
            var r = new List<Pt>();
            for (int k = desde; ; k = (k + 1) % loop.Count)
            {
                r.Add(loop[k]);
                if (k == hasta)
                {
                    return r;
                }
            }
        }

        /// <summary>Contorno resultante cerrado, simple y con área; si no, el texto de autointersección de §5G.1.</summary>
        private static void CheckPiece(List<Pt> pieza, double tol)
        {
            (int a, int b)? hit = PolylineValidator.FindSelfIntersection(pieza, tol);
            if (hit == null && PolylineValidator.Validate(pieza, true, null, tol).Count == 0 && Math.Abs(LocalSignedArea(pieza)) > tol)
            {
                return;
            }

            (int i, int j) = hit ?? (0, pieza.Count - 1);
            throw new WallGeometryException(
                "muro: el contorno resultante se autointerseca (tramos " + Int(i) + " y " + Int(j) + ")", false);
        }

        private static Pt Along(WallSpan s, double t) => (s.Inicio.x + (t * s.Direccion.x), s.Inicio.y + (t * s.Direccion.y));

        /// <summary>Punto de la arista a→b en la perpendicular a <paramref name="dir"/> que pasa por <paramref name="x"/>.</summary>
        private static Pt OnEdge(Pt a, Pt b, Pt x, Pt dir)
        {
            Pt e = Sub(b, a);
            double u = Dot(Sub(x, a), dir) / Dot(e, dir);
            return (a.x + (u * e.x), a.y + (u * e.y));
        }

        private static double DistanceToSegment(Pt p, Pt a, Pt b)
        {
            Pt e = Sub(b, a);
            double u = Math.Clamp(Dot(Sub(p, a), e) / Dot(e, e), 0.0, 1.0);
            return PolygonMetrics.Dist(p, (a.x + (u * e.x), a.y + (u * e.y)));
        }

        /// <summary>Área con signo respecto al primer vértice: no pierde precisión con las coordenadas grandes de los planos.</summary>
        private static double LocalSignedArea(List<Pt> v)
        {
            Pt o = v[0];
            var local = new List<Pt>(v.Count);
            foreach (Pt p in v)
            {
                local.Add(Sub(p, o));
            }

            return PolygonMetrics.SignedArea(local);
        }

        private static Pt Sub(Pt a, Pt b) => (a.x - b.x, a.y - b.y);

        private static double Dot(Pt a, Pt b) => (a.x * b.x) + (a.y * b.y);

        private static double Length(Pt a) => Math.Sqrt(Dot(a, a));

        private static Pt Unit(Pt a)
        {
            double len = Length(a);
            return (a.x / len, a.y / len);
        }

        private static WallGeometryException InvalidContour() =>
            new WallGeometryException("muro: el contorno debe ser un polígono cerrado simple de al menos 4 vértices", false);

        private static WallGeometryException NoSpan() =>
            new WallGeometryException("muro: no se encontró un tramo recto (dos caras paralelas) cerca del punto indicado", false);

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Int(int i) => i.ToString(CultureInfo.InvariantCulture);
    }
}
