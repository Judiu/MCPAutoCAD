using System;
using System.Collections.Generic;
using System.Globalization;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>Posiciones de array_rectangular / array_polar (protocol.md §5E.3.5) como transformaciones afines XY. Puro.</summary>
    public static class ArrayLayout
    {
        public const int MaxCopies = 500;

        /// <summary>Una posición de copia: transformación, índice correlativo (desde 1), fila/columna (rectangular) y resumen.</summary>
        public sealed record Placement(Affine2 Transformacion, int Indice, int? Fila, int? Columna, double Dx, double Dy, double RotacionGrados);

        /// <summary>
        /// filas × columnas − 1 copias (la (0,0) es el original), orden fila mayor → columna; la retícula gira
        /// <paramref name="anguloGrados"/> alrededor de (baseX, baseY): p' = R·(p + d − b) + b con d = (c·sepColumnas,
        /// f·sepFilas). ArgumentException: "array: el número de copias (&lt;n&gt;) supera el tope de 500",
        /// "filas/columnas: se necesita al menos 2 posiciones".
        /// </summary>
        public static IReadOnlyList<Placement> Rectangular(int filas, int columnas, double sepFilas, double sepColumnas, double anguloGrados, double baseX, double baseY)
        {
            long posiciones = (long)Math.Max(filas, 0) * Math.Max(columnas, 0);
            if (filas < 1 || columnas < 1 || posiciones < 2)
            {
                throw new ArgumentException("filas/columnas: se necesita al menos 2 posiciones");
            }

            long copias = posiciones - 1;
            if (copias > MaxCopies)
            {
                throw new ArgumentException(TooManyCopies(copias));
            }

            RequireFinite(sepFilas, sepColumnas, anguloGrados, baseX, baseY);
            double rad = Transform2D.ToRadians(anguloGrados);
            Affine2 rot = Transform2D.RotateAbout(baseX, baseY, rad);
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            var result = new List<Placement>((int)copias);
            int indice = 0;
            for (int f = 0; f < filas; f++)
            {
                for (int c = 0; c < columnas; c++)
                {
                    if (f == 0 && c == 0)
                    {
                        continue;
                    }

                    double dxLocal = c * sepColumnas;
                    double dyLocal = f * sepFilas;
                    Affine2 t = rot.Compose(Transform2D.Translate(dxLocal, dyLocal));
                    double dx = (cos * dxLocal) - (sin * dyLocal);
                    double dy = (sin * dxLocal) + (cos * dyLocal);
                    result.Add(new Placement(t, ++indice, f, c, dx, dy, anguloGrados));
                }
            }

            return result;
        }

        /// <summary>
        /// n − 1 copias alrededor de (centroX, centroY): |anguloTotal| = 360 → paso = total / n; otro → paso = total / (n − 1).
        /// rotarElementos → rotación alrededor del centro; si no → traslación del punto base a su posición girada.
        /// ArgumentException: "n: debe ser un entero entre 2 y 501", "array: el número de copias (&lt;n&gt;) supera el tope
        /// de 500", "angulo_total: no puede ser 0".
        /// </summary>
        public static IReadOnlyList<Placement> Polar(double centroX, double centroY, int n, double anguloTotal, bool rotarElementos, double baseX, double baseY)
        {
            if (n < 2)
            {
                throw new ArgumentException("n: debe ser un entero entre 2 y 501");
            }

            if (n - 1 > MaxCopies)
            {
                throw new ArgumentException(TooManyCopies(n - 1));
            }

            if (!double.IsFinite(anguloTotal) || anguloTotal == 0)
            {
                throw new ArgumentException("angulo_total: no puede ser 0");
            }

            RequireFinite(centroX, centroY, baseX, baseY, 0);
            bool vueltaCompleta = Math.Abs(Math.Abs(anguloTotal) - 360.0) <= 1e-9;
            double paso = vueltaCompleta ? anguloTotal / n : anguloTotal / (n - 1);

            var result = new List<Placement>(n - 1);
            for (int i = 1; i < n; i++)
            {
                double grados = i * paso;
                Affine2 rot = Transform2D.RotateAbout(centroX, centroY, Transform2D.ToRadians(grados));
                (double bx, double by) = rot.Apply(baseX, baseY);
                double dx = bx - baseX;
                double dy = by - baseY;
                result.Add(rotarElementos
                    ? new Placement(rot, i, null, null, dx, dy, grados)
                    : new Placement(Transform2D.Translate(dx, dy), i, null, null, dx, dy, 0.0));
            }

            return result;
        }

        private static string TooManyCopies(long n) =>
            "array: el número de copias (" + n.ToString(CultureInfo.InvariantCulture) + ") supera el tope de "
            + MaxCopies.ToString(CultureInfo.InvariantCulture);

        private static void RequireFinite(double a, double b, double c, double d, double e)
        {
            if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c) || !double.IsFinite(d) || !double.IsFinite(e))
            {
                throw new ArgumentException("array: valor no finito");
            }
        }
    }
}
