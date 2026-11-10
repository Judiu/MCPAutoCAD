using System;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Transformaciones 2D de mover/copiar/rotar/escalar (protocol.md §5D.8, paso 6 / R4): matriz <see cref="Affine2"/>
    /// en la planta XY y caja transformada de una entidad (caja de las 4 esquinas transformadas) para comprobar su
    /// ubicación ANTES de escribir. La escritura real la hace AutoCAD con Matrix3d (Displacement / Rotation / Scaling);
    /// esta clase solo replica su efecto en XY. Puro, sin AutoCAD.
    /// </summary>
    public static class Transform2D
    {
        /// <summary>Traslación (dx, dy). Valores no finitos → ArgumentException.</summary>
        public static Affine2 Translate(double dx, double dy) => new Affine2(1, 0, 0, 1, dx, dy);

        /// <summary>
        /// Rotación antihoraria de <paramref name="rad"/> radianes alrededor del punto base (bx, by):
        /// p' = R·(p − b) + b. Valores no finitos → ArgumentException.
        /// </summary>
        public static Affine2 RotateAbout(double bx, double by, double rad)
        {
            if (!double.IsFinite(rad))
            {
                throw new ArgumentException("rotación: ángulo no finito");
            }

            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            return new Affine2(cos, -sin, sin, cos, bx - (cos * bx) + (sin * by), by - (sin * bx) - (cos * by));
        }

        /// <summary>
        /// Escala uniforme de factor <paramref name="f"/> (&gt; 0) desde el punto base (bx, by): p' = f·(p − b) + b.
        /// Factor ≤ 0 o no finito → ArgumentException.
        /// </summary>
        public static Affine2 ScaleAbout(double bx, double by, double f)
        {
            if (!double.IsFinite(f) || f <= 0)
            {
                throw new ArgumentException("escala: el factor debe ser un número finito mayor que 0");
            }

            return new Affine2(f, 0, 0, f, bx - (f * bx), by - (f * by));
        }

        /// <summary>Caja envolvente de las 4 esquinas de <paramref name="box"/> transformadas por <paramref name="m"/>.</summary>
        public static BBox2 TransformBox(BBox2 box, Affine2 m) => m.TransformBox(box);

        /// <summary>Grados → radianes.</summary>
        public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

        /// <summary>
        /// true si el ángulo en grados es 0 (mod 360): "angulo: no puede ser 0" (§5D.8). Tolerancia 1e-9 grados para
        /// absorber el redondeo de valores como 720.0000000000001.
        /// </summary>
        public static bool IsNullRotation(double degrees)
        {
            if (!double.IsFinite(degrees))
            {
                return false;
            }

            double r = Math.Abs(degrees % 360.0);
            return r <= 1e-9 || Math.Abs(r - 360.0) <= 1e-9;
        }

        /// <summary>Grados normalizados a [0, 360) (convención de ángulos de §5D.0).</summary>
        public static double Normalize360(double degrees)
        {
            if (!double.IsFinite(degrees))
            {
                throw new ArgumentException("ángulo no finito");
            }

            double d = degrees % 360.0;
            if (d < 0)
            {
                d += 360.0;
            }

            return d >= 360.0 ? 0.0 : d;
        }
    }
}
