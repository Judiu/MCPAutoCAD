using System;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Ángulos de las herramientas de dibujo (protocol.md §5D.0, §5D.3): en grados, antihorarios desde +X; se admite
    /// cualquier valor finito y se normaliza a [0, 360). Puro.
    /// </summary>
    public static class Angles
    {
        /// <summary>Tolerancia (grados) para considerar iguales dos ángulos normalizados.</summary>
        public const double DefaultToleranceDegrees = 1e-9;

        public const string SameStartEnd = "angulo_fin: debe ser distinto de angulo_inicio";

        /// <summary>Grados → [0, 360). −90 → 270, 360 → 0, 450 → 90. No finito → ArgumentOutOfRangeException.</summary>
        public static double Normalize360(double degrees)
        {
            if (!double.IsFinite(degrees))
            {
                throw new ArgumentOutOfRangeException(nameof(degrees), "El ángulo debe ser un número finito.");
            }

            double d = degrees % 360.0;
            if (d < 0)
            {
                d += 360.0;
            }

            // -1e-15 + 360 = 360 en doble precisión: se lleva a 0.
            return d >= 360.0 ? 0.0 : d;
        }

        /// <summary>Grados → radianes (sin normalizar).</summary>
        public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

        /// <summary>Radianes → grados (sin normalizar).</summary>
        public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

        /// <summary>
        /// true si los dos ángulos (grados, cualquier finito) señalan la misma dirección: normalizados, su diferencia
        /// circular es ≤ <paramref name="tolDegrees"/> (359.9999999999 y 0 son iguales).
        /// </summary>
        public static bool SameDirection(double a, double b, double tolDegrees = DefaultToleranceDegrees)
        {
            double diff = Math.Abs(Normalize360(a) - Normalize360(b));
            return Math.Min(diff, 360.0 - diff) <= tolDegrees;
        }
    }
}
