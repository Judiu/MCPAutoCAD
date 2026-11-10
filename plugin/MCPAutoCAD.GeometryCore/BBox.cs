using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Caja 2D alineada a los ejes en WCS (protocol.md §2.5). Inmutable. Se construye con
    /// <see cref="FromCorners"/>, <see cref="FromPoints"/> o <see cref="Union"/>.
    /// </summary>
    public readonly struct BBox2 : IEquatable<BBox2>
    {
        public const string MinGreaterThanMax = "bbox: mínimo mayor que máximo";

        private BBox2(double xmin, double ymin, double xmax, double ymax)
        {
            Xmin = xmin;
            Ymin = ymin;
            Xmax = xmax;
            Ymax = ymax;
        }

        public double Xmin { get; }
        public double Ymin { get; }
        public double Xmax { get; }
        public double Ymax { get; }

        public double Width => Xmax - Xmin;
        public double Height => Ymax - Ymin;

        /// <summary>
        /// Caja desde la esquina mínima y la máxima. xmin &gt; xmax o ymin &gt; ymax →
        /// ArgumentException("bbox: mínimo mayor que máximo"); coordenadas no finitas → ArgumentException.
        /// </summary>
        public static BBox2 FromCorners(double xmin, double ymin, double xmax, double ymax)
        {
            if (!double.IsFinite(xmin) || !double.IsFinite(ymin) || !double.IsFinite(xmax) || !double.IsFinite(ymax))
            {
                throw new ArgumentException("bbox: coordenada no finita");
            }

            if (xmin > xmax || ymin > ymax)
            {
                throw new ArgumentException(MinGreaterThanMax);
            }

            return new BBox2(xmin, ymin, xmax, ymax);
        }

        /// <summary>Caja mínima que contiene los puntos (proyección XY). Sin puntos → ArgumentException.</summary>
        public static BBox2 FromPoints(IEnumerable<Point3> points)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            bool any = false;
            double xmin = double.PositiveInfinity, ymin = double.PositiveInfinity;
            double xmax = double.NegativeInfinity, ymax = double.NegativeInfinity;
            foreach (Point3 p in points)
            {
                if (!p.IsFinite)
                {
                    throw new ArgumentException("bbox: coordenada no finita");
                }

                any = true;
                xmin = Math.Min(xmin, p.X);
                ymin = Math.Min(ymin, p.Y);
                xmax = Math.Max(xmax, p.X);
                ymax = Math.Max(ymax, p.Y);
            }

            if (!any)
            {
                throw new ArgumentException("bbox: sin puntos");
            }

            return new BBox2(xmin, ymin, xmax, ymax);
        }

        /// <summary>Intersección inclusiva: dos cajas que solo se tocan en un borde o esquina SÍ se intersecan.</summary>
        public bool Intersects(BBox2 other) =>
            Xmin <= other.Xmax && other.Xmin <= Xmax && Ymin <= other.Ymax && other.Ymin <= Ymax;

        /// <summary>true si el punto (x, y) está dentro o en el borde.</summary>
        public bool Contains(double x, double y) => x >= Xmin && x <= Xmax && y >= Ymin && y <= Ymax;

        /// <summary>Caja mínima que contiene a ambas.</summary>
        public BBox2 Union(BBox2 other) =>
            new BBox2(
                Math.Min(Xmin, other.Xmin),
                Math.Min(Ymin, other.Ymin),
                Math.Max(Xmax, other.Xmax),
                Math.Max(Ymax, other.Ymax));

        /// <summary>Salida 2D [[xmin, ymin], [xmax, ymax]].</summary>
        public double[][] ToArray2D() => new[] { new[] { Xmin, Ymin }, new[] { Xmax, Ymax } };

        public bool Equals(BBox2 other) =>
            Xmin.Equals(other.Xmin) && Ymin.Equals(other.Ymin) && Xmax.Equals(other.Xmax) && Ymax.Equals(other.Ymax);

        public override bool Equals(object? obj) => obj is BBox2 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Xmin, Ymin, Xmax, Ymax);

        public static bool operator ==(BBox2 left, BBox2 right) => left.Equals(right);

        public static bool operator !=(BBox2 left, BBox2 right) => !left.Equals(right);

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "[[{0}, {1}], [{2}, {3}]]", Xmin, Ymin, Xmax, Ymax);
    }
}
