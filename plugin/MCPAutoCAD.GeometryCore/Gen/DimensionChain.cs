using System;
using System.Collections.Generic;
using System.Linq;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Cadena de cotas (protocol.md §5G.3, <c>cotas_automaticas</c>). Puro: a partir de puntos característicos (vértices
    /// de muros/polilíneas, extremos de líneas de eje) y de una dirección, proyecta los puntos sobre esa dirección, los
    /// ordena, funde los que distan menos que la tolerancia y produce las cotas lineales (RotatedDimension) de la cadena:
    /// una por tramo consecutivo (<c>cadena: true</c>) o una total entre el primero y el último (<c>cadena: false</c>). La
    /// línea de cota va a <c>separacion</c> del punto extremo del conjunto en el lado pedido (perpendicular a la dirección).
    /// </summary>
    public static class DimensionChain
    {
        public const double DefaultTol = 1e-3;

        public const string LadoIzquierda = "izquierda";
        public const string LadoDerecha = "derecha";

        private const string TooFewPoints = "puntos: se necesitan al menos 2 puntos distintos en la dirección de la cota";

        /// <summary>Margen (grados) para que 179.9999999999 se lea como 0 en <see cref="DominantDirection"/>.</summary>
        private const double AngleSnap = 1e-9;

        /// <summary>Una cota planificada: puntos extremos (sobre la recta de referencia de los puntos originales), posición de la línea y medida.</summary>
        public sealed record Segment((double x, double y) P1, (double x, double y) P2, (double x, double y) Linea, double Medida);

        /// <summary>Resultado: cotas en orden a lo largo de la dirección, medidas, total, dirección unitaria y lado resuelto.</summary>
        public sealed record ChainResult(
            IReadOnlyList<Segment> Cotas,
            IReadOnlyList<double> Medidas,
            double Total,
            (double x, double y) Direccion,
            (double x, double y) NormalLado,
            int NPuntosFundidos);

        /// <summary>
        /// Construye la cadena. <paramref name="puntos"/> = puntos característicos (≥ 2 distintos tras fundir);
        /// <paramref name="direccionGrados"/> = dirección de la línea de cota (0 = horizontal, 90 = vertical);
        /// <paramref name="lado"/> = "izquierda" | "derecha" respecto a la dirección, o null si se da
        /// <paramref name="ladoPunto"/> (la línea de cota va al lado de ese punto); <paramref name="separacion"/> &gt; 0 =
        /// distancia desde el punto más extremo del conjunto en ese lado hasta la línea de cota; <paramref name="cadena"/> =
        /// una cota por tramo (true) o una total (false). Lanza ArgumentException con estos textos exactos:
        /// "puntos: se necesitan al menos 2 puntos distintos en la dirección de la cota", "separacion: debe ser un número
        /// mayor que 0", "lado: debe ser izquierda o derecha", "lado: indique lado o lado_punto (solo uno)".
        /// Cada cota: P1/P2 = los puntos originales (el más cercano a la línea de cota de cada grupo fundido); Linea = punto
        /// sobre la línea de cota (a <paramref name="separacion"/> del extremo del conjunto); Medida = distancia proyectada.
        /// </summary>
        public static ChainResult Build(
            IReadOnlyList<(double x, double y)> puntos,
            double direccionGrados,
            string? lado,
            (double x, double y)? ladoPunto,
            double separacion,
            bool cadena,
            double tol = DefaultTol)
        {
            if (!double.IsFinite(direccionGrados))
            {
                throw new ArgumentException("direccion: debe ser x, y, auto o un número en grados");
            }

            if (!double.IsFinite(separacion) || separacion <= 0)
            {
                throw new ArgumentException("separacion: debe ser un número mayor que 0");
            }

            if ((lado == null) == (ladoPunto == null))
            {
                throw new ArgumentException("lado: indique lado o lado_punto (solo uno)");
            }

            if (lado != null && lado != LadoIzquierda && lado != LadoDerecha)
            {
                throw new ArgumentException("lado: debe ser izquierda o derecha");
            }

            if (puntos == null || puntos.Count < 2)
            {
                throw new ArgumentException(TooFewPoints);
            }

            double rad = direccionGrados * Math.PI / 180.0;
            (double x, double y) d = (Math.Cos(rad), Math.Sin(rad));
            (double x, double y) n = (-d.y, d.x);
            (double x, double y) normalLado = ResolveSide(puntos, n, lado, ladoPunto);

            List<(double x, double y)> representantes = MergeByProjection(puntos, d, normalLado, tol);
            if (representantes.Count < 2)
            {
                throw new ArgumentException(TooFewPoints);
            }

            double tLinea = puntos.Max(p => Dot(p, normalLado)) + separacion;
            var cotas = new List<Segment>();
            if (cadena)
            {
                for (int i = 1; i < representantes.Count; i++)
                {
                    cotas.Add(MakeSegment(representantes[i - 1], representantes[i], d, normalLado, tLinea));
                }
            }
            else
            {
                cotas.Add(MakeSegment(representantes[0], representantes[representantes.Count - 1], d, normalLado, tLinea));
            }

            List<double> medidas = cotas.Select(c => c.Medida).ToList();
            return new ChainResult(cotas, medidas, medidas.Sum(), d, normalLado, puntos.Count - representantes.Count);
        }

        /// <summary>
        /// Dirección dominante de un conjunto de puntos (grados en [0, 180)): la del segmento más largo entre vértices
        /// consecutivos de <paramref name="puntos"/> (una polilínea o una línea); con un solo punto o todos iguales → 0.
        /// </summary>
        public static double DominantDirection(IReadOnlyList<(double x, double y)> puntos, double tol = DefaultTol)
        {
            if (puntos == null)
            {
                return 0;
            }

            double mejor = tol;
            double grados = 0;
            for (int i = 1; i < puntos.Count; i++)
            {
                double dx = puntos[i].x - puntos[i - 1].x;
                double dy = puntos[i].y - puntos[i - 1].y;
                double len = Math.Sqrt((dx * dx) + (dy * dy));
                if (len > mejor)
                {
                    mejor = len;
                    grados = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                }
            }

            if (grados < 0)
            {
                grados += 180.0;
            }

            return grados >= 180.0 - AngleSnap ? 0 : grados;
        }

        private static (double x, double y) ResolveSide(
            IReadOnlyList<(double x, double y)> puntos, (double x, double y) n, string? lado, (double x, double y)? ladoPunto)
        {
            double signo;
            if (lado != null)
            {
                signo = lado == LadoIzquierda ? 1 : -1;
            }
            else
            {
                (double x, double y) centroide = (puntos.Average(p => p.x), puntos.Average(p => p.y));
                (double x, double y) q = ladoPunto!.Value;
                signo = Dot((q.x - centroide.x, q.y - centroide.y), n) < 0 ? -1 : 1;
            }

            return (signo * n.x, signo * n.y);
        }

        /// <summary>
        /// Ordena por proyección y funde en un grupo los puntos a ≤ <paramref name="tol"/> del PRIMERO del grupo (sin deriva
        /// en cadenas de puntos muy juntos). Representante de cada grupo: el más cercano a la línea de cota.
        /// </summary>
        private static List<(double x, double y)> MergeByProjection(
            IReadOnlyList<(double x, double y)> puntos, (double x, double y) d, (double x, double y) normalLado, double tol)
        {
            var representantes = new List<(double x, double y)>();
            double inicioGrupo = 0;
            foreach ((double x, double y) p in puntos.OrderBy(q => Dot(q, d)))
            {
                double s = Dot(p, d);
                if (representantes.Count == 0 || s - inicioGrupo > tol)
                {
                    representantes.Add(p);
                    inicioGrupo = s;
                }
                else if (Dot(p, normalLado) > Dot(representantes[representantes.Count - 1], normalLado))
                {
                    representantes[representantes.Count - 1] = p;
                }
            }

            return representantes;
        }

        private static Segment MakeSegment(
            (double x, double y) p1, (double x, double y) p2, (double x, double y) d, (double x, double y) normalLado, double tLinea)
        {
            double h = tLinea - Dot(p1, normalLado);
            return new Segment(p1, p2, (p1.x + (h * normalLado.x), p1.y + (h * normalLado.y)), Dot(p2, d) - Dot(p1, d));
        }

        private static double Dot((double x, double y) a, (double x, double y) b) => (a.x * b.x) + (a.y * b.y);
    }
}
