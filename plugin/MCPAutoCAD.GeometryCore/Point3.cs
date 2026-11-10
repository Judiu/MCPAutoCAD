using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Punto 3D inmutable en unidades de dibujo. Independiente de AutoCAD.
    /// </summary>
    public readonly struct Point3 : IEquatable<Point3>
    {
        public Point3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        /// <summary>True si las tres componentes son finitas (ni NaN ni ±Infinity).</summary>
        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

        /// <summary>Distancia euclidiana 3D a otro punto.</summary>
        public double Distance(Point3 other)
        {
            double dx = other.X - X;
            double dy = other.Y - Y;
            double dz = other.Z - Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Componentes como arreglo [x, y, z] (la salida siempre lleva 3 componentes).</summary>
        public double[] ToArray() => new[] { X, Y, Z };

        /// <summary>
        /// Construye un punto desde 2 componentes [x, y] (z = 0) o 3 componentes [x, y, z].
        /// Lanza <see cref="ArgumentException"/> si el número de componentes no es 2 ni 3,
        /// o si alguna componente no es finita (NaN, ±Infinity).
        /// </summary>
        public static Point3 FromArray(IReadOnlyList<double> components)
        {
            if (!TryFromArray(components, out Point3 point, out string? error))
            {
                throw new ArgumentException(error, nameof(components));
            }

            return point;
        }

        /// <summary>
        /// Variante sin excepción de <see cref="FromArray"/>. Si falla devuelve false y un mensaje en español.
        /// </summary>
        public static bool TryFromArray(IReadOnlyList<double>? components, out Point3 point, out string? error)
        {
            point = default;
            if (components == null)
            {
                error = "el punto es nulo";
                return false;
            }

            if (components.Count != 2 && components.Count != 3)
            {
                error = "un punto debe tener 2 o 3 componentes";
                return false;
            }

            for (int i = 0; i < components.Count; i++)
            {
                if (!double.IsFinite(components[i]))
                {
                    error = "coordenada no finita";
                    return false;
                }
            }

            double z = components.Count == 3 ? components[2] : 0.0;
            point = new Point3(components[0], components[1], z);
            error = null;
            return true;
        }

        public bool Equals(Point3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object? obj) => obj is Point3 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        public static bool operator ==(Point3 left, Point3 right) => left.Equals(right);

        public static bool operator !=(Point3 left, Point3 right) => !left.Equals(right);

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "[{0}, {1}, {2}]", X, Y, Z);
    }
}
