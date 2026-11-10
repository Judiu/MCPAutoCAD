using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Geometría pura de <c>extender_linea</c> (sin AutoCAD). Se mueve UN extremo de la línea hasta la intersección más
    /// cercana con la frontera:
    /// <list type="number">
    /// <item>Primero se busca hacia fuera: intersecciones de la recta con la frontera más allá del extremo elegido, en el
    /// sentido que se aleja del otro extremo. Gana la más cercana (extensión).</item>
    /// <item>Si no hay ninguna (extender es imposible), se toma la intersección más cercana al extremo sobre la recta
    /// infinita, en cualquier sentido (recorte, o extensión si estaba por detrás). Así el extremo nunca se queda donde está.</item>
    /// </list>
    /// Intersecciones: la recta y cada segmento de la frontera se consideran a distancia ≤ tol (ambos extremos del segmento
    /// cuentan). Segmentos paralelos a la recta no producen intersección. Coordenadas 3D; la frontera se entrega como segmentos
    /// (la polilínea se trocea en el handler; los arcos (bulge) no se admiten).
    /// <para>limite_extendido (0.9): los segmentos marcados como infinitos (<paramref name="infinitos"/>; null = todos) se tratan
    /// como la recta infinita que los contiene. Rechazos propios: intersección con ángulo menor de
    /// <see cref="MinAngleDegrees"/> grados (casi paralelas) y extensión mayor que <paramref name="maxExtension"/>.</para>
    /// </summary>
    public static class LineExtend
    {
        /// <summary>Mensaje cuando la frontera no corta a la recta.</summary>
        public const string NoIntersection = "no hay intersección con la frontera en la línea";

        /// <summary>Mensaje cuando la línea de entrada ya tiene longitud cero.</summary>
        public const string ZeroLengthLine = "la línea tiene longitud cero";

        /// <summary>Mensaje cuando el nuevo extremo coincide con el otro (la línea quedaría de longitud cero).</summary>
        public const string ZeroLengthResult = "la línea quedaría de longitud cero";

        /// <summary>Mensaje cuando la proyección de 'hasta_punto' cae al otro lado del extremo fijo (la línea se invertiría).</summary>
        public const string InvertedResult = "la proyección queda al otro lado del extremo fijo (la línea se invertiría)";

        /// <summary>Mensaje cuando la frontera pasa justo por el extremo que se mueve (nada que extender ni recortar).</summary>
        public const string TouchesMovingEnd = "la frontera pasa por el extremo que se mueve: no hay nada que extender ni recortar";

        /// <summary>Ángulo mínimo (grados) entre la línea y una frontera infinita (limite_extendido) para aceptar la intersección.</summary>
        public const double MinAngleDegrees = 0.5;

        /// <summary>Mensaje cuando, con limite_extendido, la única intersección es casi paralela.</summary>
        public const string NearlyParallel = "limite_extendido: la frontera es casi paralela a la línea (ángulo menor de 0,5°): la extensión sería enorme";

        /// <summary>Mensaje de extensión que supera el tope (con limite_extendido).</summary>
        public static string ExtensionTooLarge(double extension, double maxExtension) =>
            "limite_extendido: la extensión (" + extension.ToString("F1", CultureInfo.InvariantCulture) + ") supera el tope ("
            + maxExtension.ToString("F1", CultureInfo.InvariantCulture) + "); la frontera es casi paralela o está muy lejos";

        /// <summary>Extremo de la línea que se mueve.</summary>
        public enum Extremo
        {
            Inicio,
            Fin,
        }

        /// <summary>Resultado: punto anterior del extremo, nuevo punto y si fue una extensión (true) o un recorte (false).</summary>
        public sealed class Result
        {
            public Result(Point3 anterior, Point3 nuevo, bool extendida)
            {
                Anterior = anterior;
                Nuevo = nuevo;
                Extendida = extendida;
            }

            public Point3 Anterior { get; }

            public Point3 Nuevo { get; }

            public bool Extendida { get; }
        }

        /// <summary>
        /// Calcula el nuevo extremo. <paramref name="a"/>/<paramref name="b"/> = extremos de la línea; <paramref name="tol"/> &gt; 0.
        /// <paramref name="limiteExtendido"/>: los segmentos con <paramref name="infinitos"/>[i] = true (null = todos) son rectas
        /// infinitas. <paramref name="maxExtension"/>: tope de la distancia entre el extremo anterior y el nuevo (solo con
        /// limiteExtendido).
        /// <exception cref="ArgumentException">Línea de longitud cero, frontera vacía, valores no finitos, sin intersección, casi
        /// paralela, extensión sobre el tope, frontera que pasa por el extremo, o resultado de longitud cero (mensajes de esta clase).</exception>
        /// </summary>
        public static Result Compute(
            Point3 a,
            Point3 b,
            Extremo extremo,
            IReadOnlyList<(Point3 A, Point3 B)> boundary,
            double tol,
            bool limiteExtendido = false,
            double maxExtension = double.PositiveInfinity,
            IReadOnlyList<bool>? infinitos = null)
        {
            if (!a.IsFinite || !b.IsFinite || !double.IsFinite(tol) || tol <= 0)
            {
                throw new ArgumentException("coordenadas o tolerancia no válidas");
            }

            if (boundary == null || boundary.Count == 0)
            {
                throw new ArgumentException("la frontera no tiene segmentos");
            }

            Point3 dirAB = Sub(b, a);
            double len = Math.Sqrt(Dot(dirAB, dirAB));
            if (len <= tol)
            {
                throw new ArgumentException(ZeroLengthLine);
            }

            // E = extremo que se mueve; O = el otro. u = dirección que se aleja de O.
            Point3 e = extremo == Extremo.Fin ? b : a;
            Point3 o = extremo == Extremo.Fin ? a : b;
            Point3 u = Scale(extremo == Extremo.Fin ? dirAB : Sub(a, b), 1.0 / len);
            double minSin = Math.Sin(MinAngleDegrees * Math.PI / 180.0);

            bool haveBeyond = false;
            double bestBeyond = double.PositiveInfinity;
            Point3 pointBeyond = default;

            bool haveAny = false;
            double bestAbs = double.PositiveInfinity;
            Point3 pointAny = default;

            bool touchesEnd = false;
            bool nearlyParallel = false;

            for (int i = 0; i < boundary.Count; i++)
            {
                (Point3 c, Point3 d) = boundary[i];
                bool infinita = limiteExtendido && (infinitos == null || infinitos[i]);
                if (!TryIntersect(e, u, c, d, tol, infinita, out double tau, out Point3 x))
                {
                    continue;
                }

                if (infinita && Sin(u, Sub(d, c)) < minSin)
                {
                    // Intersección de recta casi paralela (ángulo < 0,5°): se descarta y se informa si es la única.
                    nearlyParallel = true;
                    continue;
                }

                if (Math.Abs(tau) <= tol)
                {
                    touchesEnd = true;
                    continue;
                }

                if (tau > tol && tau < bestBeyond)
                {
                    bestBeyond = tau;
                    pointBeyond = x;
                    haveBeyond = true;
                }

                // Recorte: solo cortes ENTRE los dos extremos (O está en τ = −len). Más allá de O la línea se invertiría.
                double abs = Math.Abs(tau);
                if (tau < -tol && tau > -len + tol && abs < bestAbs)
                {
                    bestAbs = abs;
                    pointAny = x;
                    haveAny = true;
                }
            }

            Point3 nuevo;
            bool extendida;
            if (haveBeyond)
            {
                nuevo = pointBeyond;
                extendida = true;
            }
            else if (haveAny)
            {
                nuevo = pointAny;
                extendida = false;
            }
            else if (nearlyParallel)
            {
                throw new ArgumentException(NearlyParallel);
            }
            else if (touchesEnd)
            {
                throw new ArgumentException(TouchesMovingEnd);
            }
            else
            {
                throw new ArgumentException(NoIntersection);
            }

            if (limiteExtendido && Distance(nuevo, e) > maxExtension)
            {
                throw new ArgumentException(ExtensionTooLarge(Distance(nuevo, e), maxExtension));
            }

            if (Distance(nuevo, o) <= tol)
            {
                throw new ArgumentException(ZeroLengthResult);
            }

            return new Result(e, nuevo, extendida);
        }

        /// <summary>
        /// 'hasta_punto': el extremo que se mueve se coloca en la PROYECCIÓN ORTOGONAL de <paramref name="punto"/> sobre la
        /// recta de la línea (= el pie de la perpendicular a la línea trazada por el punto). Si el punto ya está sobre la
        /// recta, el nuevo extremo es ese mismo punto. No necesita frontera: el punto puede estar a cualquier lado (extiende
        /// o recorta). Rechazos: longitud cero de la línea de entrada, proyección que coincide con el extremo fijo
        /// (<see cref="ZeroLengthResult"/>) o que queda al otro lado del extremo fijo (<see cref="InvertedResult"/>).
        /// <c>Extendida</c> = el nuevo extremo queda más allá del extremo anterior (alargar); false = recorta o no mueve.
        /// </summary>
        public static Result ComputeToPoint(Point3 a, Point3 b, Extremo extremo, Point3 punto, double tol)
        {
            if (!a.IsFinite || !b.IsFinite || !punto.IsFinite || !double.IsFinite(tol) || tol <= 0)
            {
                throw new ArgumentException("coordenadas o tolerancia no válidas");
            }

            Point3 dirAB = Sub(b, a);
            double len = Math.Sqrt(Dot(dirAB, dirAB));
            if (len <= tol)
            {
                throw new ArgumentException(ZeroLengthLine);
            }

            Point3 e = extremo == Extremo.Fin ? b : a;
            Point3 o = extremo == Extremo.Fin ? a : b;
            Point3 u = Scale(extremo == Extremo.Fin ? dirAB : Sub(a, b), 1.0 / len);

            // Pie de la perpendicular: X = E + ((P − E)·u) u (recta infinita de la línea, paso por E en dirección u).
            Point3 x = Add(e, Scale(u, Dot(Sub(punto, e), u)));

            double along = Dot(Sub(x, o), u);   // distancia firmada del nuevo extremo medida desde el extremo fijo
            if (along <= tol)
            {
                throw new ArgumentException(Distance(x, o) <= tol ? ZeroLengthResult : InvertedResult);
            }

            bool extendida = Dot(Sub(x, e), u) > tol;
            return new Result(e, x, extendida);
        }

        /// <summary>
        /// Intersección de la recta E + τ·u (u unitario) con el segmento C–D a distancia ≤ tol. Devuelve τ (distancia con
        /// signo desde E en la dirección u) y el punto. False si son paralelos, están separados o el corte cae fuera del segmento.
        /// Con <paramref name="infinita"/> el segmento C–D se trata como la recta infinita que lo contiene (sin límite de
        /// extremos); la distancia ≤ tol entre rectas sigue exigiéndose (rectas que se cruzan en 3D no cuentan).
        /// </summary>
        private static bool TryIntersect(Point3 e, Point3 u, Point3 c, Point3 d, double tol, bool infinita, out double tau, out Point3 x)
        {
            tau = 0;
            x = default;
            Point3 v = Sub(d, c);
            double vv = Dot(v, v);
            if (vv <= tol * tol)
            {
                return false;   // segmento degenerado de la frontera
            }

            Point3 w0 = Sub(e, c);
            double bUv = Dot(u, v);
            double cVv = vv;
            double dUw = Dot(u, w0);
            double eVw = Dot(v, w0);
            double denom = cVv - (bUv * bUv);   // a = u·u = 1
            if (Math.Abs(denom) <= 1e-12 * cVv)
            {
                return false;   // paralelos
            }

            double tauCandidate = ((bUv * eVw) - (cVv * dUw)) / denom;
            double sigma = (eVw - (bUv * dUw)) / denom;
            Point3 onLine = Add(e, Scale(u, tauCandidate));
            Point3 onSeg = Add(c, Scale(v, sigma));
            if (Distance(onLine, onSeg) > tol)
            {
                return false;   // se cruzan en el espacio sin cortarse (3D)
            }

            double epsSigma = tol / Math.Sqrt(vv);
            if (!infinita && (sigma < -epsSigma || sigma > 1.0 + epsSigma))
            {
                return false;   // corta la recta fuera del segmento de la frontera
            }

            tau = tauCandidate;
            x = onLine;
            return true;
        }

        /// <summary>Seno del ángulo entre dos vectores (|u × v| / (|u|·|v|)); 0 si alguno es nulo.</summary>
        private static double Sin(Point3 u, Point3 v)
        {
            double lu = Math.Sqrt(Dot(u, u));
            double lv = Math.Sqrt(Dot(v, v));
            if (lu == 0 || lv == 0)
            {
                return 0;
            }

            var cross = new Point3(
                (u.Y * v.Z) - (u.Z * v.Y),
                (u.Z * v.X) - (u.X * v.Z),
                (u.X * v.Y) - (u.Y * v.X));
            return Math.Sqrt(Dot(cross, cross)) / (lu * lv);
        }

        private static Point3 Sub(Point3 p, Point3 q) => new Point3(p.X - q.X, p.Y - q.Y, p.Z - q.Z);

        private static Point3 Add(Point3 p, Point3 q) => new Point3(p.X + q.X, p.Y + q.Y, p.Z + q.Z);

        private static Point3 Scale(Point3 p, double k) => new Point3(p.X * k, p.Y * k, p.Z * k);

        private static double Dot(Point3 p, Point3 q) => (p.X * q.X) + (p.Y * q.Y) + (p.Z * q.Z);

        private static double Distance(Point3 p, Point3 q) => Math.Sqrt(Dot(Sub(p, q), Sub(p, q)));
    }
}
