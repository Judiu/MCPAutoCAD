using System;
using System.Collections.Generic;
using System.Globalization;
using MCPAutoCAD.GeometryCore.Draw;
using Pt = (double x, double y);

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Unión de dos contornos de muro rectos (protocol.md §5G.2, <c>union_muros</c>). Alcance reducido y justificado en §5G.2: cada contorno es un cuadrilátero CONVEXO (rectángulo o
    /// paralelogramo: un muro recto de <c>muro</c> o de la oficina), a cualquier ángulo. La unión de dos polígonos convexos
    /// se construye sin librería de booleanos: aristas de A fuera de B + aristas de B fuera de A, encadenadas por sus
    /// extremos (tolerancia); los tramos de arista que quedan SOBRE el borde del otro polígono (caso T: la tapa del muro
    /// que llega apoyada en la cara del otro) se conservan solo desde A y se descartan desde B, salvo los de sentido contrario,
    /// que son costura interior y se descartan de los dos.
    /// </summary>
    public static class WallJoin
    {
        public const double DefaultTol = 1e-6;

        public const string TipoL = "L";
        public const string TipoT = "T";
        public const string TipoCruce = "cruce";
        public const string TipoColineal = "colineal";
        public const string TipoAuto = "auto";

        /// <summary>Tolerancia relativa de la comprobación de áreas (§5G.2).</summary>
        public const double AreaBalanceTol = 1e-6;

        /// <summary>Resultado: polígono unido (cerrado, simple, sin vértices colineales ni duplicados, CCW), tipo detectado, áreas.</summary>
        public sealed record JoinResult(
            IReadOnlyList<(double x, double y)> Contorno,
            string TipoDetectado,
            double AreaA,
            double AreaB,
            double AreaUnion,
            double AreaSolape,
            int NVertices);

        /// <summary>
        /// Une <paramref name="a"/> y <paramref name="b"/>. Lanza <see cref="WallGeometryException"/> (EsParametro = false →
        /// INVALID_GEOMETRY) con estos textos exactos:
        /// <list type="bullet">
        /// <item>"muro_a: el contorno debe ser un cuadrilátero convexo (muro recto)" / "muro_b: …"</item>
        /// <item>"union: los muros no se tocan ni se solapan"</item>
        /// <item>"union: los muros solo se tocan en un punto; no hay encuentro que unir"</item>
        /// <item>"union: un muro está contenido en el otro"</item>
        /// <item>"union: el contorno unido no es un polígono simple"</item>
        /// <item>"tipo: los muros forman un encuentro en &lt;detectado&gt;, no en &lt;pedido&gt;" (solo si <paramref name="tipo"/> ≠ "auto")</item>
        /// </list>
        /// Tipo detectado por la topología del contacto (ver <see cref="Classify"/>): contorno de 4 vértices → "colineal"; cada
        /// muro partido por el otro → "cruce"; un muro que termina en una cara del otro, que sigue a los dos lados → "T"; el
        /// resto (los muros se encuentran por sus extremos, a cualquier ángulo) → "L"; más de dos tramos fuera en algún muro →
        /// "union: encuentro no reconocido (&lt;n&gt; vértices)". <paramref name="tipo"/> inválido → EsParametro = true "tipo: debe
        /// ser L, T, cruce o auto". Comprobación numérica obligatoria: AreaUnion ≈ AreaA + AreaB − AreaSolape, con AreaSolape
        /// calculada aparte por recorte (<see cref="OverlapArea"/>, <see cref="CheckAreaBalance"/>).
        /// </summary>
        public static JoinResult Union(
            IReadOnlyList<(double x, double y)> a,
            IReadOnlyList<(double x, double y)> b,
            string tipo = TipoAuto,
            double tol = DefaultTol)
        {
            string pedido = tipo ?? TipoAuto;
            if (pedido != TipoL && pedido != TipoT && pedido != TipoCruce && pedido != TipoAuto)
            {
                throw new WallGeometryException("tipo: debe ser L, T, cruce o auto", true);
            }

            if (a == null || !IsConvexQuad(a, tol))
            {
                throw Geometry("muro_a: el contorno debe ser un cuadrilátero convexo (muro recto)");
            }

            if (b == null || !IsConvexQuad(b, tol))
            {
                throw Geometry("muro_b: el contorno debe ser un cuadrilátero convexo (muro recto)");
            }

            List<Pt> pa = CounterClockwise(SimplifyLoop(a, tol));
            List<Pt> pb = CounterClockwise(SimplifyLoop(b, tol));
            double areaA = LocalArea(pa);
            double areaB = LocalArea(pb);

            if (AllInsideOrOn(pb, pa, tol) || AllInsideOrOn(pa, pb, tol))
            {
                throw Geometry("union: un muro está contenido en el otro");
            }

            if (!AnyContact(pa, pb, tol))
            {
                throw Geometry("union: los muros no se tocan ni se solapan");
            }

            var nodos = new List<Pt>();
            var tramos = new List<(int desde, int hasta)>();
            int quitados = 0;
            foreach ((int desde, int hasta) sub in Subsegments(pa, pb, nodos, tol))
            {
                Pt medio = Mid(nodos[sub.desde], nodos[sub.hasta]);
                Region r = Locate(medio, pb, tol);
                bool costura = r == Region.Borde && IsOppositeSeam(nodos[sub.desde], nodos[sub.hasta], medio, pb, tol);
                if (r == Region.Dentro || costura)
                {
                    quitados++;
                }
                else
                {
                    tramos.Add(sub);
                }
            }

            foreach ((int desde, int hasta) sub in Subsegments(pb, pa, nodos, tol))
            {
                if (Locate(Mid(nodos[sub.desde], nodos[sub.hasta]), pa, tol) == Region.Fuera)
                {
                    tramos.Add(sub);
                }
                else
                {
                    quitados++;
                }
            }

            // Sin tramos interiores ni compartidos el contacto es un punto: no hay solape ni cara común que unir.
            if (quitados == 0)
            {
                throw Geometry("union: los muros solo se tocan en un punto; no hay encuentro que unir");
            }

            List<Pt> contorno = SimplifyLoop(Chain(nodos, tramos), tol);
            if (contorno.Count < 3 || PolylineValidator.Validate(contorno, true, null, tol).Count > 0)
            {
                throw NotSimple();
            }

            contorno = CounterClockwise(contorno);
            double areaUnion = LocalArea(contorno);
            double areaSolape = OverlapArea(pa, pb);
            CheckAreaBalance(areaA, areaB, areaSolape, areaUnion, tol * (Perimeter(pa) + Perimeter(pb)));

            string detectado = Classify(pa, pb, contorno.Count, tol);
            if (pedido != TipoAuto && pedido != detectado)
            {
                throw Geometry("tipo: los muros forman un encuentro en " + detectado + ", no en " + pedido);
            }

            return new JoinResult(
                contorno, detectado, areaA, areaB, areaUnion, areaSolape, contorno.Count);
        }

        /// <summary>
        /// Área de la intersección de dos polígonos convexos por recorte de Sutherland-Hodgman, sin usar el contorno unido:
        /// es lo que hace independiente la comprobación de <see cref="CheckAreaBalance"/>. Admite cualquier sentido de giro y
        /// trabaja en coordenadas locales para no perder precisión con las coordenadas grandes de los planos.
        /// </summary>
        public static double OverlapArea(IReadOnlyList<(double x, double y)> a, IReadOnlyList<(double x, double y)> b)
        {
            Pt origen = a[0];
            List<Pt> recorte = CounterClockwise(ToLocal(b, origen));
            List<Pt> salida = ToLocal(a, origen);
            for (int i = 0; i < recorte.Count && salida.Count > 0; i++)
            {
                salida = ClipByEdge(salida, recorte[i], recorte[(i + 1) % recorte.Count]);
            }

            return salida.Count < 3 ? 0.0 : LocalArea(salida);
        }

        /// <summary>
        /// Comprobación numérica de §5G.2: area_union ≈ area_a + area_b − area_solape, con tolerancia 1e-6 relativa a area_union
        /// más <paramref name="margen"/> (lo que puede mover la fusión de nodos: tol × suma de perímetros). Si no cuadra, el
        /// contorno encadenado no es la unión de los dos muros: "union: el contorno unido no es un polígono simple".
        /// </summary>
        public static void CheckAreaBalance(double areaA, double areaB, double areaSolape, double areaUnion, double margen)
        {
            if (Math.Abs(areaUnion - (areaA + areaB - areaSolape)) > (AreaBalanceTol * areaUnion) + margen)
            {
                throw NotSimple();
            }
        }

        /// <summary>true si <paramref name="v"/> es un cuadrilátero convexo no degenerado (área &gt; tol², sin vértices repetidos).</summary>
        public static bool IsConvexQuad(IReadOnlyList<(double x, double y)> v, double tol = DefaultTol)
        {
            if (v == null)
            {
                return false;
            }

            List<Pt> s = SimplifyLoop(v, tol);
            if (s.Count != 4)
            {
                return false;
            }

            int positivos = 0;
            int negativos = 0;
            for (int i = 0; i < 4; i++)
            {
                double c = Cross(Sub(s[(i + 1) % 4], s[i]), Sub(s[(i + 2) % 4], s[(i + 1) % 4]));
                positivos += c > 0 ? 1 : 0;
                negativos += c < 0 ? 1 : 0;
            }

            return (positivos == 4 || negativos == 4) && Math.Abs(SignedLocalArea(s)) > tol * tol;
        }

        /// <summary>Quita vértices consecutivos duplicados y colineales (tolerancia) de un lazo cerrado.</summary>
        public static List<(double x, double y)> SimplifyLoop(IReadOnlyList<(double x, double y)> v, double tol = DefaultTol)
        {
            if (v == null)
            {
                throw new ArgumentNullException(nameof(v));
            }

            List<Pt> r = PolygonMetrics.CollapseConsecutiveDuplicates(v, out _, tol);
            bool cambio = true;
            while (cambio && r.Count >= 3)
            {
                cambio = false;
                for (int i = 0; i < r.Count; i++)
                {
                    Pt prev = r[(i - 1 + r.Count) % r.Count];
                    Pt next = r[(i + 1) % r.Count];
                    if (DistanceToLine(r[i], prev, next, tol) <= tol)
                    {
                        r.RemoveAt(i);
                        r = PolygonMetrics.CollapseConsecutiveDuplicates(r, out _, tol);
                        cambio = true;
                        break;
                    }
                }
            }

            return r;
        }

        /// <summary>Posición de un punto respecto a un polígono convexo CCW.</summary>
        private enum Region
        {
            Dentro,
            Borde,
            Fuera,
        }

        /// <summary>
        /// Topología del contacto de un muro con el otro: <c>Aristas</c> = por índice de arista, el tramo (en distancia desde
        /// su inicio) que queda dentro o sobre el otro; <c>TramosFuera</c> = trozos en que el otro parte el borde del muro
        /// (2 = el otro lo cruza de cara a cara).
        /// </summary>
        private sealed record WallContact(SortedDictionary<int, (double desde, double hasta)> Aristas, int TramosFuera);

        /// <summary>
        /// Tipo del encuentro. Con 4 vértices la unión es un solo cuadrilátero (colineal). Si no, se mira qué aristas de cada
        /// muro toca o cruza el otro: si cada uno parte al otro en dos es un cruce; si uno termina en el otro, que sigue a los
        /// dos lados de esa cara (<see cref="IsThroughWall"/>), una T; en otro caso se encuentran por sus extremos: L. El número
        /// de vértices no decide nada: una L con saliente da 8 y una con codo exterior, 7.
        /// </summary>
        private static string Classify(List<Pt> a, List<Pt> b, int nVertices, double tol)
        {
            if (nVertices == 4)
            {
                return TipoColineal;
            }

            WallContact ca = ReadContact(a, b, tol);
            WallContact cb = ReadContact(b, a, tol);
            if (ca.TramosFuera > 2 || cb.TramosFuera > 2)
            {
                throw Geometry("union: encuentro no reconocido (" + Int(nVertices) + " vértices)");
            }

            if (ca.TramosFuera == 2 && cb.TramosFuera == 2)
            {
                return TipoCruce;
            }

            return IsThroughWall(a, ca, cb, tol) || IsThroughWall(b, cb, ca, tol) ? TipoT : TipoL;
        }

        /// <summary>
        /// Contacto de <paramref name="poly"/> con <paramref name="otro"/>: recorre su borde partido por el otro, en orden, y
        /// anota qué aristas lo tocan o lo atraviesan y cuántos trozos de borde quedan fuera.
        /// </summary>
        private static WallContact ReadContact(List<Pt> poly, List<Pt> otro, double tol)
        {
            var aristas = new SortedDictionary<int, (double desde, double hasta)>();
            var fuera = new List<bool>();
            for (int i = 0; i < poly.Count; i++)
            {
                Pt p = poly[i];
                Pt q = poly[(i + 1) % poly.Count];
                double len = PolygonMetrics.Dist(p, q);
                List<double> ts = SplitParameters(p, q, otro, tol);
                for (int k = 0; k + 1 < ts.Count; k++)
                {
                    double desde = ts[k] * len;
                    double hasta = ts[k + 1] * len;
                    if (hasta - desde <= tol)
                    {
                        continue;
                    }

                    double t = (ts[k] + ts[k + 1]) / 2.0;
                    bool esFuera = Locate((p.x + (t * (q.x - p.x)), p.y + (t * (q.y - p.y))), otro, tol) == Region.Fuera;
                    fuera.Add(esFuera);
                    if (!esFuera)
                    {
                        aristas[i] = aristas.TryGetValue(i, out (double desde, double hasta) previo)
                            ? (Math.Min(previo.desde, desde), Math.Max(previo.hasta, hasta))
                            : (desde, hasta);
                    }
                }
            }

            int tramos = 0;
            for (int j = 0; j < fuera.Count; j++)
            {
                tramos += fuera[j] && !fuera[(j - 1 + fuera.Count) % fuera.Count] ? 1 : 0;
            }

            return new WallContact(aristas, Math.Max(1, tramos));
        }

        /// <summary>
        /// true si <paramref name="muro"/> es el muro pasante de una T: el otro termina en él (no queda partido) y lo toca en
        /// una sola cara, o en dos opuestas si lo atraviesa y acaba enrasado con la cara del fondo, con esa cara siguiendo a los
        /// dos lados del contacto. Una prolongación que no supera el ancho del propio muro (medido perpendicular a la cara, con tolerancia) es
        /// un saliente y no un tramo de muro: la topología de una L con saliente es la de una T con una pata muy corta, y
        /// esa medida es lo único que las distingue.
        /// </summary>
        private static bool IsThroughWall(List<Pt> muro, WallContact contacto, WallContact otro, double tol)
        {
            if (otro.TramosFuera != 1 || contacto.Aristas.Count == 0 || contacto.Aristas.Count > 2)
            {
                return false;
            }

            var indices = new List<int>(contacto.Aristas.Keys);
            if (indices.Count == 2 && indices[1] - indices[0] != 2)
            {
                return false;   // caras contiguas: es una esquina del muro
            }

            foreach (KeyValuePair<int, (double desde, double hasta)> arista in contacto.Aristas)
            {
                Pt p = muro[arista.Key];
                Pt q = muro[(arista.Key + 1) % muro.Count];
                double patas = Math.Min(arista.Value.desde, PolygonMetrics.Dist(p, q) - arista.Value.hasta);
                if (patas <= WidthFromEdge(muro, p, q) + tol)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Ancho del polígono perpendicular a la arista p–q: el vértice más alejado de su recta.</summary>
        private static double WidthFromEdge(List<Pt> poly, Pt p, Pt q)
        {
            double ancho = 0.0;
            foreach (Pt v in poly)
            {
                ancho = Math.Max(ancho, Math.Abs(Cross(Sub(q, p), Sub(v, p))) / PolygonMetrics.Dist(p, q));
            }

            return ancho;
        }

        /// <summary>Un paso de Sutherland-Hodgman: lo de <paramref name="sujeto"/> a la izquierda de la arista a→b.</summary>
        private static List<Pt> ClipByEdge(List<Pt> sujeto, Pt a, Pt b)
        {
            var salida = new List<Pt>(sujeto.Count + 1);
            for (int k = 0; k < sujeto.Count; k++)
            {
                Pt s = sujeto[k];
                Pt e = sujeto[(k + 1) % sujeto.Count];
                double ls = Cross(Sub(b, a), Sub(s, a));
                double le = Cross(Sub(b, a), Sub(e, a));
                if (ls >= 0)
                {
                    salida.Add(s);
                }

                if ((ls > 0 && le < 0) || (ls < 0 && le > 0))
                {
                    double t = ls / (ls - le);
                    salida.Add((s.x + (t * (e.x - s.x)), s.y + (t * (e.y - s.y))));
                }
            }

            return salida;
        }

        /// <summary>Distancia de <paramref name="p"/> a la recta a–b (a y b coincidentes = pico: distancia 0, se quita).</summary>
        private static double DistanceToLine(Pt p, Pt a, Pt b, double tol)
        {
            double len = PolygonMetrics.Dist(a, b);
            return len <= tol ? 0 : Math.Abs(Cross(Sub(b, a), Sub(p, a))) / len;
        }

        /// <summary>Menor distancia con signo (positiva = interior) a las rectas de las aristas de un convexo CCW.</summary>
        private static double MinEdgeDistance(Pt p, List<Pt> poly)
        {
            double min = double.PositiveInfinity;
            for (int i = 0; i < poly.Count; i++)
            {
                Pt a = poly[i];
                Pt b = poly[(i + 1) % poly.Count];
                min = Math.Min(min, Cross(Sub(b, a), Sub(p, a)) / PolygonMetrics.Dist(a, b));
            }

            return min;
        }

        private static Region Locate(Pt p, List<Pt> poly, double tol)
        {
            double d = MinEdgeDistance(p, poly);
            return d > tol ? Region.Dentro : (d < -tol ? Region.Fuera : Region.Borde);
        }

        private static bool AllInsideOrOn(List<Pt> interior, List<Pt> poly, double tol)
        {
            foreach (Pt p in interior)
            {
                if (Locate(p, poly, tol) == Region.Fuera)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AnyContact(List<Pt> a, List<Pt> b, double tol)
        {
            for (int i = 0; i < a.Count; i++)
            {
                for (int j = 0; j < b.Count; j++)
                {
                    if (PolylineValidator.SegmentsTouch(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count], tol))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Tramo de A sobre el borde de B en sentido CONTRARIO a esa arista (ambos CCW): los interiores quedan a lados
        /// opuestos, así que es una costura interior del polígono unido y no se conserva. En el mismo sentido (caras
        /// alineadas de muros solapados) los interiores coinciden y el tramo sí es contorno.
        /// </summary>
        private static bool IsOppositeSeam(Pt desde, Pt hasta, Pt medio, List<Pt> poly, double tol)
        {
            Pt dir = Sub(hasta, desde);
            for (int i = 0; i < poly.Count; i++)
            {
                Pt a = poly[i];
                Pt b = poly[(i + 1) % poly.Count];
                if (DistanceToSegment(medio, a, b) <= tol)
                {
                    return Dot(dir, Sub(b, a)) < 0;
                }
            }

            return false;
        }

        /// <summary>
        /// Aristas de <paramref name="poly"/> partidas por el otro polígono, como pares de índices en
        /// <paramref name="nodos"/>. Cada punto se funde con el nodo existente a ≤ tol: así los extremos de A y de B encadenan
        /// por índice, aunque el plano traiga diferencias de redondeo entre muros que se tocan.
        /// </summary>
        private static List<(int desde, int hasta)> Subsegments(List<Pt> poly, List<Pt> otro, List<Pt> nodos, double tol)
        {
            var r = new List<(int desde, int hasta)>();
            for (int i = 0; i < poly.Count; i++)
            {
                Pt p = poly[i];
                Pt q = poly[(i + 1) % poly.Count];
                int previo = -1;
                foreach (double t in SplitParameters(p, q, otro, tol))
                {
                    int nodo = NodeOf((p.x + (t * (q.x - p.x)), p.y + (t * (q.y - p.y))), nodos, tol);
                    if (previo >= 0 && nodo != previo)
                    {
                        r.Add((previo, nodo));
                    }

                    previo = nodo;
                }
            }

            return r;
        }

        /// <summary>Parámetros ordenados en [0, 1] de p→q: extremos, cruces con aristas del otro y vértices suyos sobre el tramo.</summary>
        private static List<double> SplitParameters(Pt p, Pt q, List<Pt> otro, double tol)
        {
            Pt e = Sub(q, p);
            double len = PolygonMetrics.Dist(p, q);
            double margen = tol / len;
            var ts = new List<double> { 0.0, 1.0 };
            for (int j = 0; j < otro.Count; j++)
            {
                Pt r = otro[j];
                Pt s = otro[(j + 1) % otro.Count];
                if (DistanceToSegment(r, p, q) <= tol)
                {
                    ts.Add(Math.Clamp(Dot(Sub(r, p), e) / (len * len), 0.0, 1.0));
                }

                Pt f = Sub(s, r);
                double den = Cross(e, f);
                if (Math.Abs(den) <= tol * len * Length(f))
                {
                    continue; // paralelas: los vértices sobre el tramo ya se añadieron arriba
                }

                double t = Cross(Sub(r, p), f) / den;
                double u = Cross(Sub(r, p), e) / den;
                double margenOtro = tol / Length(f);
                if (t >= -margen && t <= 1 + margen && u >= -margenOtro && u <= 1 + margenOtro)
                {
                    ts.Add(Math.Clamp(t, 0.0, 1.0));
                }
            }

            ts.Sort();
            return ts;
        }

        private static int NodeOf(Pt p, List<Pt> nodos, double tol)
        {
            for (int i = 0; i < nodos.Count; i++)
            {
                if (PolygonMetrics.Dist(nodos[i], p) <= tol)
                {
                    return i;
                }
            }

            nodos.Add(p);
            return nodos.Count - 1;
        }

        /// <summary>
        /// Encadena los tramos dirigidos en un único lazo. Cada nodo debe tener exactamente una entrada y una salida; si no,
        /// o si queda más de un lazo, el contorno no es un polígono simple.
        /// </summary>
        private static List<Pt> Chain(List<Pt> nodos, List<(int desde, int hasta)> tramos)
        {
            var salida = new Dictionary<int, int>();
            var llegada = new HashSet<int>();
            foreach ((int desde, int hasta) in tramos)
            {
                if (!salida.TryAdd(desde, hasta) || !llegada.Add(hasta))
                {
                    throw NotSimple();
                }
            }

            if (tramos.Count == 0 || salida.Count != llegada.Count)
            {
                throw NotSimple();
            }

            var lazo = new List<Pt>(tramos.Count);
            int inicio = tramos[0].desde;
            int actual = inicio;
            do
            {
                lazo.Add(nodos[actual]);
                if (!salida.TryGetValue(actual, out actual) || lazo.Count > tramos.Count)
                {
                    throw NotSimple();
                }
            }
            while (actual != inicio);

            if (lazo.Count != tramos.Count)
            {
                throw NotSimple();
            }

            return lazo;
        }

        private static List<Pt> CounterClockwise(List<Pt> v)
        {
            if (SignedLocalArea(v) < 0)
            {
                v.Reverse();
            }

            return v;
        }

        /// <summary>Área con signo respecto al primer vértice: conserva precisión con las coordenadas grandes de los planos.</summary>
        private static double SignedLocalArea(List<Pt> v)
        {
            return PolygonMetrics.SignedArea(ToLocal(v, v[0]));
        }

        private static double LocalArea(List<Pt> v) => Math.Abs(SignedLocalArea(v));

        private static List<Pt> ToLocal(IReadOnlyList<(double x, double y)> v, Pt origen)
        {
            var local = new List<Pt>(v.Count);
            foreach (Pt p in v)
            {
                local.Add(Sub(p, origen));
            }

            return local;
        }

        private static double Perimeter(List<Pt> v)
        {
            double total = 0.0;
            for (int i = 0; i < v.Count; i++)
            {
                total += PolygonMetrics.Dist(v[i], v[(i + 1) % v.Count]);
            }

            return total;
        }

        private static double DistanceToSegment(Pt p, Pt a, Pt b)
        {
            Pt e = Sub(b, a);
            double u = Math.Clamp(Dot(Sub(p, a), e) / Dot(e, e), 0.0, 1.0);
            return PolygonMetrics.Dist(p, (a.x + (u * e.x), a.y + (u * e.y)));
        }

        private static Pt Mid(Pt a, Pt b) => ((a.x + b.x) / 2.0, (a.y + b.y) / 2.0);

        private static Pt Sub(Pt a, Pt b) => (a.x - b.x, a.y - b.y);

        private static double Dot(Pt a, Pt b) => (a.x * b.x) + (a.y * b.y);

        private static double Cross(Pt a, Pt b) => (a.x * b.y) - (a.y * b.x);

        private static double Length(Pt a) => Math.Sqrt(Dot(a, a));

        private static WallGeometryException Geometry(string detalle) => new WallGeometryException(detalle, false);

        private static WallGeometryException NotSimple() => Geometry("union: el contorno unido no es un polígono simple");

        private static string Int(int i) => i.ToString(CultureInfo.InvariantCulture);
    }
}
