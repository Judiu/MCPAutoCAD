using System;
using System.Collections.Generic;
using System.Globalization;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>Geometría de muro no construible. <see cref="EsParametro"/> = true → INVALID_PARAMS; false → INVALID_GEOMETRY.</summary>
    public sealed class WallGeometryException : ArgumentException
    {
        public WallGeometryException(string detalle, bool esParametro)
            : base(detalle)
        {
            EsParametro = esParametro;
        }

        public bool EsParametro { get; }
    }

    /// <summary>Posición del eje respecto al muro, mirando en el sentido de recorrido del eje (§5E.3.1).</summary>
    public enum WallJustification
    {
        Centro,
        Izquierda,
        Derecha,
    }

    /// <summary>Resultado de <see cref="WallOutline.Build"/> (§5E.9).</summary>
    public sealed record WallResult(
        IReadOnlyList<(double x, double y)> CaraIzquierda,
        IReadOnlyList<(double x, double y)> CaraDerecha,
        IReadOnlyList<(double x, double y)> Contorno,
        IReadOnlyList<(double x, double y)>? Interior,
        double LongitudEje,
        double LongitudContorno,
        double Area,
        int NTramos);

    /// <summary>
    /// Contorno de un muro con esquinas a inglete a partir de su eje (protocol.md §5E.3.1). Puro.
    /// Cada cara es la paralela al eje a una distancia con signo <c>o</c> según la normal IZQUIERDA de cada tramo
    /// (n = (−dy, dx) del tramo unitario): Centro → ±espesor/2; Izquierda (muro a la izquierda) → +espesor y 0;
    /// Derecha (muro a la derecha) → 0 y −espesor. El vértice de inglete en el vértice i del eje es
    /// p_i + o·(n₁ + n₂)/(1 + n₁·n₂): intersección de las dos paralelas (tramos colineales → p_i + o·n).
    /// </summary>
    public static class WallOutline
    {
        public const double DefaultMiterLimit = 4.0;

        private const string SelfIntersectionPrefix = "vertices: la polilínea cerrada se autointerseca";

        public static WallResult Build(
            IReadOnlyList<(double x, double y)> eje,
            double espesor,
            WallJustification justificacion,
            bool cerrado,
            double limiteInglete = DefaultMiterLimit,
            double tol = 1e-9)
        {
            // --- 1. Parámetros (INVALID_PARAMS) ---
            if (eje == null || eje.Count < 2)
            {
                throw new WallGeometryException("eje: se necesitan al menos 2 vértices", true);
            }

            int n = eje.Count;
            if (cerrado && n < 3)
            {
                throw new WallGeometryException("eje: un muro cerrado necesita al menos 3 vértices", true);
            }

            if (!double.IsFinite(tol) || tol < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tol), "La tolerancia debe ser un número finito no negativo.");
            }

            for (int i = 0; i < n; i++)
            {
                if (!double.IsFinite(eje[i].x) || !double.IsFinite(eje[i].y))
                {
                    throw new WallGeometryException("eje[" + Int(i) + "]: coordenada no finita", true);
                }
            }

            for (int i = 1; i < n; i++)
            {
                if (PolygonMetrics.Dist(eje[i - 1], eje[i]) <= tol)
                {
                    throw new WallGeometryException("eje[" + Int(i) + "]: vértice duplicado con el anterior", true);
                }
            }

            if (cerrado && PolygonMetrics.Dist(eje[n - 1], eje[0]) <= tol)
            {
                // Se señala el último vértice (el que sobra cuando el cliente repite el primero al final).
                throw new WallGeometryException("eje[" + Int(n - 1) + "]: vértice duplicado con el anterior", true);
            }

            if (!double.IsFinite(espesor) || espesor <= 0)
            {
                throw new WallGeometryException("espesor: debe ser mayor que 0", true);
            }

            if (!double.IsFinite(limiteInglete) || limiteInglete < 1)
            {
                throw new WallGeometryException("limite_inglete: debe ser un número mayor o igual que 1", true);
            }

            // --- 2. Geometría (INVALID_GEOMETRY) ---
            int nSeg = cerrado ? n : n - 1;
            var dirs = new (double x, double y)[nSeg];
            var lens = new double[nSeg];
            for (int k = 0; k < nSeg; k++)
            {
                (double x, double y) a = eje[k];
                (double x, double y) b = eje[(k + 1) % n];
                double len = PolygonMetrics.Dist(a, b);
                lens[k] = len;
                dirs[k] = ((b.x - a.x) / len, (b.y - a.y) / len);
            }

            (int i, int j)? axisHit = cerrado ? PolylineValidator.FindSelfIntersection(eje, tol) : FindOpenSelfIntersection(eje, tol);
            if (axisHit != null)
            {
                throw new WallGeometryException(
                    "eje: el eje se autointerseca (tramos " + Int(axisHit.Value.i) + " y " + Int(axisHit.Value.j) + ")", false);
            }

            for (int k = 0; k < nSeg; k++)
            {
                if (lens[k] < espesor - tol)
                {
                    throw new WallGeometryException(
                        "espesor: el tramo " + Int(k) + " (longitud " + Num(lens[k]) + ") es más corto que el espesor " + Num(espesor), false);
                }
            }

            (double left, double right) = Offsets(espesor, justificacion);
            double maxMiter = limiteInglete * espesor / 2.0;

            // Límite de inglete medido respecto a la LÍNEA MEDIA del muro (corrección de auditoría 2026-10-26): el
            // vértice de inglete de cada cara está a (espesor/2)·factor del vértice de inglete de la línea media, sea cual
            // sea la justificación (|o − m| = espesor/2 para las dos caras, con m = (left + right)/2). Así el resultado
            // no depende de dónde esté el eje: un muro justificado a izquierda/derecha admite los mismos ángulos que uno
            // centrado (antes se medía desde el eje y uno justificado se rechazaba ya por debajo de 60°).
            double halfWidth = espesor / 2.0;

            // Vértices de esquina: todos en cerrado; los interiores en abierto.
            int firstCorner = cerrado ? 0 : 1;
            int lastCorner = cerrado ? n - 1 : n - 2;
            for (int i = firstCorner; i <= lastCorner; i++)
            {
                (double x, double y) dIn = dirs[(i - 1 + nSeg) % nSeg];
                (double x, double y) dOut = dirs[i % nSeg];
                double denom = 1 + Dot(Normal(dIn), Normal(dOut)); // = 1 + cos(giro)
                double sumLen = Len(Add(Normal(dIn), Normal(dOut)));
                double factor = denom <= 1e-12 ? double.PositiveInfinity : sumLen / denom; // = 1/cos(giro/2)
                double dist = halfWidth * factor;
                if (dist > maxMiter + tol)
                {
                    double cosInterior = Math.Clamp(-Dot(dIn, dOut), -1.0, 1.0);
                    double g = Math.Acos(cosInterior) * 180.0 / Math.PI;
                    throw new WallGeometryException(
                        "eje: el ángulo en el vértice " + Int(i) + " (" + g.ToString("0.0", CultureInfo.InvariantCulture)
                        + "°) es demasiado agudo para el inglete (límite " + Num(limiteInglete) + "× el espesor)", false);
                }
            }

            List<(double x, double y)> leftFace = Face(eje, dirs, left, cerrado);
            List<(double x, double y)> rightFace = Face(eje, dirs, right, cerrado);

            double longitudEje = 0;
            foreach (double l in lens)
            {
                longitudEje += l;
            }

            if (!cerrado)
            {
                var contorno = new List<(double x, double y)>(2 * n);
                contorno.AddRange(leftFace);
                for (int i = n - 1; i >= 0; i--)
                {
                    contorno.Add(rightFace[i]);
                }

                CheckLoop(contorno, tol);
                CheckReversedOpen(leftFace, rightFace, dirs, n, tol);

                (double per, double? area) = PolygonMetrics.LengthAndArea(contorno, true);
                return new WallResult(leftFace, rightFace, contorno, null, longitudEje, per, area ?? 0, nSeg);
            }

            double aLeft = Math.Abs(PolygonMetrics.SignedArea(leftFace));
            double aRight = Math.Abs(PolygonMetrics.SignedArea(rightFace));
            List<(double x, double y)> exterior = aLeft >= aRight ? leftFace : rightFace;
            List<(double x, double y)> interior = aLeft >= aRight ? rightFace : leftFace;

            CheckLoop(exterior, tol);
            CheckLoop(interior, tol);
            CheckReversedClosed(exterior, dirs, tol);
            CheckReversedClosed(interior, dirs, tol);

            (double perExt, double? areaExt) = PolygonMetrics.LengthAndArea(exterior, true);
            (double perInt, double? areaInt) = PolygonMetrics.LengthAndArea(interior, true);
            return new WallResult(
                leftFace, rightFace, exterior, interior, longitudEje, perExt + perInt, (areaExt ?? 0) - (areaInt ?? 0), nSeg);
        }

        /// <summary>Distancias con signo (según la normal izquierda) de la cara izquierda y la derecha.</summary>
        private static (double left, double right) Offsets(double e, WallJustification j) => j switch
        {
            WallJustification.Izquierda => (e, 0.0),
            WallJustification.Derecha => (0.0, -e),
            _ => (e / 2.0, -e / 2.0),
        };

        /// <summary>Cara paralela al eje a distancia con signo <paramref name="o"/>, con ingletes y extremos perpendiculares.</summary>
        private static List<(double x, double y)> Face(
            IReadOnlyList<(double x, double y)> eje, (double x, double y)[] dirs, double o, bool cerrado)
        {
            int n = eje.Count;
            int nSeg = dirs.Length;
            var face = new List<(double x, double y)>(n);
            for (int i = 0; i < n; i++)
            {
                (double x, double y) p = eje[i];
                (double x, double y) off;
                if (!cerrado && i == 0)
                {
                    off = Normal(dirs[0]);
                }
                else if (!cerrado && i == n - 1)
                {
                    off = Normal(dirs[nSeg - 1]);
                }
                else
                {
                    (double x, double y) n1 = Normal(dirs[(i - 1 + nSeg) % nSeg]);
                    (double x, double y) n2 = Normal(dirs[i % nSeg]);
                    double denom = 1 + Dot(n1, n2); // ya validado > 0 por el límite de inglete
                    off = ((n1.x + n2.x) / denom, (n1.y + n2.y) / denom);
                }

                face.Add((p.x + (o * off.x), p.y + (o * off.y)));
            }

            return face;
        }

        /// <summary>
        /// Autointersección del lazo cerrado con <see cref="PolylineValidator.Validate"/>; el texto del validador se traduce
        /// al de §5E.3.1. Si el validador se detiene antes (vértices coincidentes porque el inglete "comió" un tramo), se
        /// busca el cruce directamente con <see cref="PolylineValidator.FindSelfIntersection"/>.
        /// </summary>
        private static void CheckLoop(IReadOnlyList<(double x, double y)> loop, double tol)
        {
            IReadOnlyList<string> errors = PolylineValidator.Validate(loop, true, null, tol);
            if (errors.Count == 0)
            {
                return;
            }

            foreach (string e in errors)
            {
                if (e.StartsWith(SelfIntersectionPrefix, StringComparison.Ordinal))
                {
                    throw new WallGeometryException("eje: el contorno resultante se autointerseca" + e.Substring(SelfIntersectionPrefix.Length), false);
                }
            }

            (int a, int b)? hit = PolylineValidator.FindSelfIntersection(loop, tol);
            if (hit != null)
            {
                throw ContourError(hit.Value.a, hit.Value.b);
            }

            // Vértices coincidentes sin cruce detectable: se señalan los dos tramos que rodean el tramo nulo.
            for (int i = 0; i < loop.Count; i++)
            {
                int k = (i + 1) % loop.Count;
                if (PolygonMetrics.Dist(loop[i], loop[k]) <= tol)
                {
                    int prev = (i - 1 + loop.Count) % loop.Count;
                    throw ContourError(Math.Min(prev, k), Math.Max(prev, k));
                }
            }
        }

        /// <summary>
        /// Eje abierto: un tramo de cara que va al revés que su tramo de eje significa que el inglete se lo ha "comido"
        /// (tramo interior más corto que el retranqueo). Se informa como autointersección con los tramos vecinos del
        /// contorno (numeración: cara izquierda 0..n−2, tapa final n−1, cara derecha invertida n..2n−2, tapa inicial 2n−1).
        /// </summary>
        private static void CheckReversedOpen(
            List<(double x, double y)> leftFace, List<(double x, double y)> rightFace, (double x, double y)[] dirs, int n, double tol)
        {
            int total = 2 * n;
            for (int k = 0; k < dirs.Length; k++)
            {
                if (IsReversed(leftFace[k], leftFace[k + 1], dirs[k], tol))
                {
                    throw ContourError(Math.Min((k - 1 + total) % total, k + 1), Math.Max((k - 1 + total) % total, k + 1));
                }

                if (IsReversed(rightFace[k], rightFace[k + 1], dirs[k], tol))
                {
                    int t = (2 * n) - 2 - k; // índice del tramo en el contorno
                    throw ContourError(t - 1, (t + 1) % total);
                }
            }
        }

        /// <summary>Lazo cerrado (anillo): ningún tramo de la cara puede ir al revés que su tramo de eje.</summary>
        private static void CheckReversedClosed(List<(double x, double y)> loop, (double x, double y)[] dirs, double tol)
        {
            int n = loop.Count;
            for (int k = 0; k < n; k++)
            {
                if (IsReversed(loop[k], loop[(k + 1) % n], dirs[k], tol))
                {
                    int prev = (k - 1 + n) % n;
                    int next = (k + 1) % n;
                    throw ContourError(Math.Min(prev, next), Math.Max(prev, next));
                }
            }
        }

        private static bool IsReversed((double x, double y) a, (double x, double y) b, (double x, double y) dir, double tol) =>
            (((b.x - a.x) * dir.x) + ((b.y - a.y) * dir.y)) <= tol;

        private static WallGeometryException ContourError(int i, int j) =>
            new WallGeometryException("eje: el contorno resultante se autointerseca (tramos " + Int(i) + " y " + Int(j) + ")", false);

        /// <summary>Primer par (i &lt; j) de tramos NO adyacentes de la polilínea ABIERTA que se cruzan o se tocan, o null.</summary>
        private static (int i, int j)? FindOpenSelfIntersection(IReadOnlyList<(double x, double y)> v, double tol)
        {
            int nSeg = v.Count - 1;
            for (int i = 0; i < nSeg; i++)
            {
                for (int j = i + 2; j < nSeg; j++)
                {
                    if (PolylineValidator.SegmentsTouch(v[i], v[i + 1], v[j], v[j + 1], tol))
                    {
                        return (i, j);
                    }
                }
            }

            return null;
        }

        private static (double x, double y) Normal((double x, double y) d) => (-d.y, d.x);

        private static double Dot((double x, double y) a, (double x, double y) b) => (a.x * b.x) + (a.y * b.y);

        private static (double x, double y) Add((double x, double y) a, (double x, double y) b) => (a.x + b.x, a.y + b.y);

        private static double Len((double x, double y) a) => Math.Sqrt((a.x * a.x) + (a.y * a.y));

        private static string Int(int i) => i.ToString(CultureInfo.InvariantCulture);

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
