using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Geometría pura de <c>unir_lineas</c> (sin AutoCAD). Un conjunto de segmentos es válido si TODOS están sobre la
    /// misma recta dentro de la tolerancia: ambos extremos de cada segmento a distancia ≤ tol de la recta de referencia.
    /// La referencia es el segmento más largo (su dirección es la más fiable); empates, el de menor índice. Los huecos
    /// entre segmentos son admitidos: el resultado es el segmento que va del extremo inicial al final de todo el conjunto.
    /// </summary>
    public static class LineMerge
    {
        /// <summary>Factor de la tolerancia por defecto respecto de la extensión del dibujo (1e-6).</summary>
        public const double DefaultRelativeTolerance = 1e-6;

        /// <summary>Suelo absoluto de la tolerancia por defecto (unidades de dibujo), para dibujos casi vacíos.</summary>
        public const double MinDefaultTolerance = 1e-9;

        /// <summary>Tolerancia por defecto: <see cref="DefaultRelativeTolerance"/> × <paramref name="extentsSpan"/> (mayor lado de la extensión del dibujo), con suelo <see cref="MinDefaultTolerance"/>. Un span no finito o ≤ 0 se trata como 1.</summary>
        public static double DefaultTolerance(double extentsSpan)
        {
            double span = double.IsFinite(extentsSpan) && extentsSpan > 0 ? extentsSpan : 1.0;
            return Math.Max(DefaultRelativeTolerance * span, MinDefaultTolerance);
        }

        /// <summary>
        /// Resultado de <see cref="Analyze"/>. Si <see cref="Degenerate"/> o <see cref="Breaking"/> no están vacíos, el conjunto
        /// no es válido y <see cref="Inicio"/>/<see cref="Fin"/> no tienen sentido.
        /// </summary>
        public sealed class Analysis
        {
            public Analysis(IReadOnlyList<int> degenerate, IReadOnlyList<int> breaking, int reference, double tolerance, Point3 inicio, Point3 fin, double distanceOfBreaks)
            {
                Degenerate = degenerate;
                Breaking = breaking;
                Reference = reference;
                Tolerance = tolerance;
                Inicio = inicio;
                Fin = fin;
                MaxDistance = distanceOfBreaks;
            }

            /// <summary>Índices de segmentos de longitud ≤ tolerancia (sin dirección).</summary>
            public IReadOnlyList<int> Degenerate { get; }

            /// <summary>Índices de segmentos que se salen de la recta de referencia (algún extremo a distancia &gt; tol), en orden.</summary>
            public IReadOnlyList<int> Breaking { get; }

            /// <summary>Índice del segmento de referencia (el más largo), o -1 si la lista está vacía.</summary>
            public int Reference { get; }

            /// <summary>Tolerancia usada.</summary>
            public double Tolerance { get; }

            /// <summary>Extremo del conjunto con menor proyección sobre la dirección de referencia (punto real de un segmento).</summary>
            public Point3 Inicio { get; }

            /// <summary>Extremo del conjunto con mayor proyección sobre la dirección de referencia (punto real de un segmento).</summary>
            public Point3 Fin { get; }

            /// <summary>Mayor distancia de un extremo a la recta de referencia entre los que rompen la colinealidad (0 si no hay).</summary>
            public double MaxDistance { get; }

            /// <summary>true si el conjunto es válido: no vacío, sin degenerados y todo colineal.</summary>
            public bool Ok => Reference >= 0 && Degenerate.Count == 0 && Breaking.Count == 0;
        }

        /// <summary>
        /// Analiza <paramref name="segments"/> (cada uno con sus dos extremos) con tolerancia <paramref name="tol"/> &gt; 0.
        /// <exception cref="ArgumentException">Lista vacía, tolerancia no positiva o finita, o coordenadas no finitas.</exception>
        /// </summary>
        public static Analysis Analyze(IReadOnlyList<(Point3 A, Point3 B)> segments, double tol)
        {
            if (segments == null || segments.Count == 0)
            {
                throw new ArgumentException("se necesita al menos un segmento");
            }

            if (!double.IsFinite(tol) || tol <= 0)
            {
                throw new ArgumentException("la tolerancia debe ser un número positivo");
            }

            for (int i = 0; i < segments.Count; i++)
            {
                if (!segments[i].A.IsFinite || !segments[i].B.IsFinite)
                {
                    throw new ArgumentException("coordenada no finita en el segmento " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            var degenerate = new List<int>();
            int reference = -1;
            double best = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                double len = Length(segments[i]);
                if (len <= tol)
                {
                    degenerate.Add(i);
                }
                else if (len > best)
                {
                    best = len;
                    reference = i;
                }
            }

            if (reference < 0)
            {
                // Todos degenerados: no hay recta de referencia.
                return new Analysis(degenerate, new List<int>(), -1, tol, default, default, 0.0);
            }

            Point3 origin = segments[reference].A;
            Point3 dir = Unit(Sub(segments[reference].B, segments[reference].A));

            var breaking = new List<int>();
            double maxDist = 0.0;
            for (int i = 0; i < segments.Count; i++)
            {
                if (degenerate.Contains(i))
                {
                    continue;
                }

                double dA = DistanceToLine(segments[i].A, origin, dir);
                double dB = DistanceToLine(segments[i].B, origin, dir);
                if (dA > tol || dB > tol)
                {
                    breaking.Add(i);
                    maxDist = Math.Max(maxDist, Math.Max(dA, dB));
                }
            }

            // Extremos: los puntos reales de menor y mayor proyección sobre la dirección de referencia.
            Point3 inicio = origin;
            Point3 fin = origin;
            double tMin = double.PositiveInfinity;
            double tMax = double.NegativeInfinity;
            for (int i = 0; i < segments.Count; i++)
            {
                if (degenerate.Contains(i))
                {
                    continue;
                }

                foreach (Point3 p in new[] { segments[i].A, segments[i].B })
                {
                    double t = Dot(Sub(p, origin), dir);
                    if (t < tMin)
                    {
                        tMin = t;
                        inicio = p;
                    }

                    if (t > tMax)
                    {
                        tMax = t;
                        fin = p;
                    }
                }
            }

            return new Analysis(degenerate, breaking, reference, tol, inicio, fin, maxDist);
        }

        /// <summary>Distancia de un punto a una recta 3D (origen + dirección unitaria).</summary>
        public static double DistanceToLine(Point3 p, Point3 origin, Point3 unitDir)
        {
            Point3 w = Sub(p, origin);
            Point3 c = Cross(w, unitDir);
            return Math.Sqrt((c.X * c.X) + (c.Y * c.Y) + (c.Z * c.Z));
        }

        /// <summary>Longitud de un segmento.</summary>
        public static double Length((Point3 A, Point3 B) s) => Math.Sqrt(LengthSquared(Sub(s.B, s.A)));

        private static double LengthSquared(Point3 v) => (v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z);

        private static Point3 Sub(Point3 a, Point3 b) => new Point3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        private static double Dot(Point3 a, Point3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

        private static Point3 Cross(Point3 a, Point3 b) =>
            new Point3((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

        private static Point3 Unit(Point3 v)
        {
            double len = Math.Sqrt(LengthSquared(v));
            return new Point3(v.X / len, v.Y / len, v.Z / len);
        }
    }
}
