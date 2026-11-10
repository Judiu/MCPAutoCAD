using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Pt = (double x, double y);

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Hueco en un muro dibujado con dos caras LINE (protocol.md §5G.1): la forma normal del plano (263 LINE contra 70
    /// LWPOLYLINE en MUROS). Puro. Las dos líneas se parten en cuatro y las dos tapas cierran el muro a cada lado del hueco.
    /// El hueco debe caer dentro del solape de las caras; espesor = distancia entre ellas; dirección = la de la línea, con el
    /// inicio en el extremo de menor x (a igual x, menor y), como en <see cref="WallOpening"/>.
    /// </summary>
    public static class LineWallOpening
    {
        /// <summary>Separación máxima al buscar la cara opuesta sin que el cliente la indique.</summary>
        public const double MaxSeparation = 0.40;

        /// <summary>Seno del ángulo máximo entre dos líneas que se consideran paralelas (redondeo del plano).</summary>
        public const double ParallelTol = 1e-4;

        /// <summary>Diferencia de separación por debajo de la cual dos candidatas están igual de cerca.</summary>
        public const double TieTol = 1e-3;

        /// <summary>Tolerancia al encadenar los extremos de un bucle de sombreado.</summary>
        public const double ChainTol = 1e-6;

        private const double AxisSnap = 1e-9;

        /// <summary>Cara del muro: una LINE en planta.</summary>
        public sealed record Face(string Handle, (double x, double y) P1, (double x, double y) P2);

        /// <summary>Tramo resultante, orientado del inicio al fin del eje.</summary>
        public sealed record Piece((double x, double y) Desde, (double x, double y) Hasta);

        /// <summary>
        /// Resultado de <see cref="Cut"/>: <see cref="Hueco"/> es el mismo registro que da <see cref="WallOpening.Cut"/> (sin
        /// contornos: LadoInicio y LadoFin van vacíos), así que la colocación del bloque no cambia; las piezas son las cuatro
        /// partes de las caras y las dos tapas.
        /// </summary>
        public sealed record LineCut(
            WallOpening.OpeningResult Hueco,
            Piece MuroInicio,
            Piece MuroFin,
            Piece OpuestaInicio,
            Piece OpuestaFin,
            Piece TapaInicio,
            Piece TapaFin);

        /// <summary>Candidata a cara opuesta con su separación al muro.</summary>
        public sealed record Candidate(Face Cara, double Separacion);

        /// <summary>Resultado de <see cref="FindOpposite"/>: la cara elegida, o null, y las candidatas que hay.</summary>
        public sealed record OppositeSearch(Face? Elegida, IReadOnlyList<Candidate> Candidatas);

        /// <summary>Marco común de las dos caras: eje, solape sobre el eje y separación con signo (positiva = opuesta a la izquierda).</summary>
        private sealed record Frame(Pt Direccion, Pt Normal, Pt Origen, double Lo, double Hi, double Separacion, double[] TMuro, double[] TOpuesta);

        /// <summary>
        /// Parte <paramref name="muro"/> y <paramref name="opuesta"/> por un hueco de ancho <paramref name="ancho"/> centrado en la
        /// proyección de <paramref name="centro"/> sobre la línea media. Lanza <see cref="WallGeometryException"/> con los textos
        /// de §5G.1: las caras no paralelas, sin solape o coincidentes (EsParametro = true) y los de hueco fuera del tramo
        /// (EsParametro = false), iguales que en <see cref="WallOpening.Cut"/>.
        /// </summary>
        public static LineCut Cut(Face muro, Face opuesta, (double x, double y) centro, double ancho, double tol = WallOpening.DefaultTol)
        {
            if (!double.IsFinite(ancho) || ancho <= 0)
            {
                throw new WallGeometryException("ancho: debe ser un número mayor que 0", true);
            }

            Frame f = BuildFrame(muro, opuesta, tol);
            double longitud = f.Hi - f.Lo;
            if (ancho >= longitud - tol)
            {
                throw new WallGeometryException("ancho: debe ser menor que la longitud del tramo", false);
            }

            double t = AxisOf(f, centro);
            double t1 = t - (ancho / 2.0);
            double t2 = t + (ancho / 2.0);
            if (t1 < WallOpening.MinMargin || t2 > longitud - WallOpening.MinMargin)
            {
                throw new WallGeometryException(
                    "ancho: el hueco (" + Num(ancho) + ") no cabe en el tramo (longitud " + Num(longitud)
                    + "): se sale del tramo o cruza una esquina",
                    false);
            }

            Pt mA1 = OnFace(muro, f.TMuro, f.Lo + t1);
            Pt mA2 = OnFace(muro, f.TMuro, f.Lo + t2);
            Pt oA1 = OnFace(opuesta, f.TOpuesta, f.Lo + t1);
            Pt oA2 = OnFace(opuesta, f.TOpuesta, f.Lo + t2);

            // A1/B1 en la cara izquierda respecto a la dirección, A2/B2 en la derecha (contrato de OpeningResult).
            bool opuestaIzquierda = f.Separacion > 0;
            var hueco = new WallOpening.OpeningResult(
                Array.Empty<Pt>(),
                Array.Empty<Pt>(),
                opuestaIzquierda ? oA1 : mA1,
                opuestaIzquierda ? mA1 : oA1,
                opuestaIzquierda ? oA2 : mA2,
                opuestaIzquierda ? mA2 : oA2,
                Midline(f, t),
                f.Direccion,
                Math.Abs(f.Separacion),
                longitud,
                t);

            return new LineCut(
                hueco,
                new Piece(FaceEnd(muro, f.TMuro, false), mA1),
                new Piece(mA2, FaceEnd(muro, f.TMuro, true)),
                new Piece(FaceEnd(opuesta, f.TOpuesta, false), oA1),
                new Piece(oA2, FaceEnd(opuesta, f.TOpuesta, true)),
                new Piece(mA1, oA1),
                new Piece(mA2, oA2));
        }

        /// <summary>
        /// Centro del hueco a una distancia de un extremo del solape, sobre la línea media. <paramref name="desdeFin"/> = false
        /// mide desde el extremo de menor x (a igual x, menor y). Fuera del tramo: "posicion: la distancia &lt;d&gt; se sale del
        /// tramo (longitud &lt;L&gt;)" (EsParametro = true).
        /// </summary>
        public static (double x, double y) CenterFromDistance(Face muro, Face opuesta, double distancia, bool desdeFin, double tol = WallOpening.DefaultTol)
        {
            Frame f = BuildFrame(muro, opuesta, tol);
            double longitud = f.Hi - f.Lo;
            if (!double.IsFinite(distancia) || distancia < -tol || distancia > longitud + tol)
            {
                throw new WallGeometryException(
                    "posicion: la distancia " + Num(distancia) + " se sale del tramo (longitud " + Num(longitud) + ")", true);
            }

            return Midline(f, desdeFin ? longitud - distancia : distancia);
        }

        /// <summary>
        /// Busca la cara opuesta de <paramref name="muro"/> entre <paramref name="lineas"/> (las de su capa): paralelas, con
        /// solape y separación entre 0 y <see cref="MaxSeparation"/>. Si se conoce <paramref name="centro"/> (PuntoRef), solo
        /// cuentan las que abarcan el hueco, si hay alguna: una cara partida por un vano anterior son dos líneas, y el trozo más
        /// cercano puede no llegar a él. Entre las que cuentan gana la más cercana. Sin una única ganadora,
        /// <see cref="OppositeSearch.Elegida"/> es null.
        /// </summary>
        public static OppositeSearch FindOpposite(
            Face muro,
            IReadOnlyList<Face> lineas,
            (double x, double y)? centro,
            double ancho,
            double tol = WallOpening.DefaultTol)
        {
            var candidatas = new List<Candidate>();
            foreach (Face l in lineas)
            {
                if (string.Equals(l.Handle, muro.Handle, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Frame? f = TryFrame(muro, l, tol);
                if (f != null && Math.Abs(f.Separacion) > tol && Math.Abs(f.Separacion) <= MaxSeparation + tol)
                {
                    candidatas.Add(new Candidate(l, Math.Abs(f.Separacion)));
                }
            }

            if (candidatas.Count == 0)
            {
                return new OppositeSearch(null, candidatas);
            }

            List<Candidate> abarcan = centro == null
                ? new List<Candidate>()
                : candidatas.Where(c => Covers(muro, c.Cara, centro.Value, ancho, tol)).ToList();
            List<Candidate> cuentan = abarcan.Count > 0 ? abarcan : candidatas;
            double minimo = cuentan.Min(c => c.Separacion);
            List<Candidate> cercanas = cuentan.Where(c => c.Separacion <= minimo + TieTol).ToList();
            return cercanas.Count == 1
                ? new OppositeSearch(cercanas[0].Cara, candidatas)
                : new OppositeSearch(null, cercanas);
        }

        /// <summary>Rechazo de §5G.1 sin candidatas.</summary>
        public static string NoOppositeDetail(string handle, string capa) =>
            "cara_opuesta: ninguna LINE paralela de la capa '" + capa + "' solapa el muro " + handle + " a "
            + Num(MaxSeparation) + " o menos; indique cara_opuesta";

        /// <summary>Rechazo de §5G.1 con varias candidatas: las lista con su separación.</summary>
        public static string ManyOppositesDetail(string capa, IReadOnlyList<Candidate> candidatas) =>
            "cara_opuesta: hay varias LINE candidatas en la capa '" + capa + "': "
            + string.Join(", ", candidatas.Select(c => c.Cara.Handle + " (" + Num(c.Separacion) + ")"))
            + "; indique cara_opuesta";

        /// <summary>
        /// Reparte un bucle de sombreado que usa las dos caras: <paramref name="otras"/> son sus demás entidades (handle y
        /// extremos). Deben formar dos cadenas que unan cada extremo de la cara opuesta con el del muro, una en cada lado del
        /// hueco. Devuelve los handles de cada cadena en orden, desde la cara opuesta hacia el muro, o null si no se puede.
        /// </summary>
        public static (IReadOnlyList<string> Inicio, IReadOnlyList<string> Fin)? SplitHatchLoop(
            LineCut corte,
            IReadOnlyList<(string Handle, (double x, double y) P, (double x, double y) Q)> otras)
        {
            var libres = new List<(string Handle, Pt P, Pt Q)>(otras);
            List<string>? inicio = Chain(corte.OpuestaInicio.Desde, corte.MuroInicio.Desde, libres);
            List<string>? fin = inicio == null ? null : Chain(corte.OpuestaFin.Hasta, corte.MuroFin.Hasta, libres);
            return inicio == null || fin == null || libres.Count > 0 ? null : (inicio, fin);
        }

        private static List<string>? Chain(Pt desde, Pt hasta, List<(string Handle, Pt P, Pt Q)> libres)
        {
            var cadena = new List<string>();
            Pt actual = desde;
            while (Dist(actual, hasta) > ChainTol)
            {
                int k = libres.FindIndex(l => Dist(l.P, actual) <= ChainTol || Dist(l.Q, actual) <= ChainTol);
                if (k < 0)
                {
                    return null;
                }

                actual = Dist(libres[k].P, actual) <= ChainTol ? libres[k].Q : libres[k].P;
                cadena.Add(libres[k].Handle);
                libres.RemoveAt(k);
            }

            return cadena;
        }

        private static bool Covers(Face muro, Face cara, Pt centro, double ancho, double tol)
        {
            Frame? f = TryFrame(muro, cara, tol);
            if (f == null)
            {
                return false;
            }

            double t = f.Lo + AxisOf(f, centro);
            return t - (ancho / 2.0) >= f.Lo - tol && t + (ancho / 2.0) <= f.Hi + tol;
        }

        private static Frame BuildFrame(Face muro, Face opuesta, double tol)
        {
            if (Dist(muro.P1, muro.P2) <= tol || Dist(opuesta.P1, opuesta.P2) <= tol)
            {
                throw new WallGeometryException("muro: la línea tiene longitud 0", false);
            }

            if (!AreParallel(muro, opuesta))
            {
                throw new WallGeometryException("cara_opuesta: la línea " + opuesta.Handle + " no es paralela a la cara del muro", true);
            }

            Frame? f = TryFrame(muro, opuesta, tol)
                ?? throw new WallGeometryException("cara_opuesta: las líneas " + muro.Handle + " y " + opuesta.Handle + " no se solapan a lo largo del tramo", true);
            if (Math.Abs(f.Separacion) <= tol)
            {
                throw new WallGeometryException("cara_opuesta: las líneas " + muro.Handle + " y " + opuesta.Handle + " coinciden", true);
            }

            return f;
        }

        /// <summary>Marco de las dos caras, o null si no son paralelas o no se solapan.</summary>
        private static Frame? TryFrame(Face muro, Face opuesta, double tol)
        {
            if (Dist(muro.P1, muro.P2) <= tol || Dist(opuesta.P1, opuesta.P2) <= tol || !AreParallel(muro, opuesta))
            {
                return null;
            }

            Pt d = Unit(Sub(muro.P2, muro.P1));
            Pt dir = Math.Abs(d.x) <= AxisSnap ? (d.y > 0 ? d : (-d.x, -d.y)) : (d.x > 0 ? d : (-d.x, -d.y));
            Pt normal = (-dir.y, dir.x);
            Pt origen = muro.P1;
            double[] tm = { Dot(Sub(muro.P1, origen), dir), Dot(Sub(muro.P2, origen), dir) };
            double[] to = { Dot(Sub(opuesta.P1, origen), dir), Dot(Sub(opuesta.P2, origen), dir) };
            double lo = Math.Max(tm.Min(), to.Min());
            double hi = Math.Min(tm.Max(), to.Max());
            if (hi - lo <= tol)
            {
                return null;
            }

            double separacion = (Dot(Sub(opuesta.P1, origen), normal) + Dot(Sub(opuesta.P2, origen), normal)) / 2.0;
            return new Frame(dir, normal, origen, lo, hi, separacion, tm, to);
        }

        private static bool AreParallel(Face a, Face b)
        {
            Pt u = Unit(Sub(a.P2, a.P1));
            Pt v = Unit(Sub(b.P2, b.P1));
            return Math.Abs((u.x * v.y) - (u.y * v.x)) <= ParallelTol;
        }

        /// <summary>Coordenada de un punto sobre el eje, medida desde el inicio del solape.</summary>
        private static double AxisOf(Frame f, Pt p) => Dot(Sub(p, f.Origen), f.Direccion) - f.Lo;

        /// <summary>Punto de la línea media a <paramref name="t"/> del inicio del solape.</summary>
        private static Pt Midline(Frame f, double t) =>
            (f.Origen.x + ((f.Lo + t) * f.Direccion.x) + (f.Separacion / 2.0 * f.Normal.x),
             f.Origen.y + ((f.Lo + t) * f.Direccion.y) + (f.Separacion / 2.0 * f.Normal.y));

        /// <summary>Punto de la cara con coordenada <paramref name="t"/> sobre el eje; interpola para no perder una cara apenas inclinada.</summary>
        private static Pt OnFace(Face cara, double[] ts, double t)
        {
            double u = (t - ts[0]) / (ts[1] - ts[0]);
            return (cara.P1.x + (u * (cara.P2.x - cara.P1.x)), cara.P1.y + (u * (cara.P2.y - cara.P1.y)));
        }

        private static Pt FaceEnd(Face cara, double[] ts, bool fin) => (ts[0] <= ts[1]) == fin ? cara.P2 : cara.P1;

        private static Pt Sub(Pt a, Pt b) => (a.x - b.x, a.y - b.y);

        private static double Dot(Pt a, Pt b) => (a.x * b.x) + (a.y * b.y);

        private static double Dist(Pt a, Pt b) => Math.Sqrt(Dot(Sub(a, b), Sub(a, b)));

        private static Pt Unit(Pt a)
        {
            double len = Math.Sqrt(Dot(a, a));
            return (a.x / len, a.y / len);
        }

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
