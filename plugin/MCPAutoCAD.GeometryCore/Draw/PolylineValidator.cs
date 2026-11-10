using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Validación pura de una polilínea 2D antes de crearla (protocol.md §5D.2). Los textos de error forman parte del
    /// contrato: no cambiarlos. Orden de comprobación (el handler responde con el primero):
    /// (1) coordenadas finitas; (2) cerrada con menos de 3 vértices; (3) bulges (longitud y finitud);
    /// (4) vértices duplicados consecutivos; (5) cerrada repitiendo el primero; (6) autointersección de la cerrada.
    /// La autointersección se evalúa solo si no hubo errores anteriores (un tramo de longitud cero no tiene dirección).
    /// </summary>
    public static class PolylineValidator
    {
        /// <summary>Tolerancia por defecto (unidades de dibujo).</summary>
        public const double DefaultTolerance = 1e-9;

        public const string ClosedNeedsThree = "vertices: una polilínea cerrada necesita al menos 3 vértices";
        public const string BulgesLength = "bulges: debe tener tantos elementos como vertices";

        /// <summary>Advertencia del handler cuando algún vértice trae z ≠ 0 (LWPOLYLINE es plana).</summary>
        public const string ZIgnoredWarning = "vertices: la componente z se ignoró (LWPOLYLINE en el plano XY).";

        public static string Duplicate(int i) => "vertices[" + Int(i) + "]: vértice duplicado con el anterior";

        public static string RepeatsFirst(int last) =>
            "vertices[" + Int(last) + "]: coincide con el primero (la polilínea cerrada no debe repetir el vértice inicial)";

        public static string BulgeNotFinite(int i) => "bulges[" + Int(i) + "]: debe ser un número finito";

        public static string SelfIntersection(int i, int j) =>
            "vertices: la polilínea cerrada se autointerseca (tramos " + Int(i) + " y " + Int(j) + ")";

        /// <summary>Autointersección con el punto aproximado del cruce (variante de <see cref="ValidateDetailed"/>).</summary>
        public static string SelfIntersectionAt(int i, int j, double x, double y) =>
            "vertices: la polilínea cerrada se autointerseca (tramos " + Int(i) + " y " + Int(j)
            + ", punto aproximado [" + Num(x) + ", " + Num(y) + "])";

        /// <summary>
        /// Errores de la polilínea (lista vacía si es válida). <paramref name="bulges"/> null = todos 0.
        /// La autointersección solo se comprueba con <paramref name="cerrada"/> y sobre las CUERDAS (los arcos de los
        /// bulges no se evalúan: limitación documentada en §5D.2). Tramo k = vértice k → k+1 (el último, al cierre).
        /// </summary>
        public static IReadOnlyList<string> Validate(
            IReadOnlyList<(double x, double y)> vertices, bool cerrada, IReadOnlyList<double>? bulges, double tol = DefaultTolerance)
        {
            return Run(vertices, cerrada, bulges, tol, false);
        }

        /// <summary>
        /// Igual que <see cref="Validate"/>, pero la autointersección cita además el punto aproximado del cruce:
        /// "vertices: la polilínea cerrada se autointerseca (tramos 0 y 2, punto aproximado [0.5, 0.5])". Lo usan las
        /// herramientas de dibujo (polilinea); <see cref="Validate"/> conserva su texto para los demás llamadores.
        /// </summary>
        public static IReadOnlyList<string> ValidateDetailed(
            IReadOnlyList<(double x, double y)> vertices, bool cerrada, IReadOnlyList<double>? bulges, double tol = DefaultTolerance)
        {
            return Run(vertices, cerrada, bulges, tol, true);
        }

        /// <summary>
        /// Punto aproximado de cruce de los segmentos [a,b] y [c,d]: punto medio de los dos puntos más cercanos entre ambos
        /// segmentos (exacto si se cruzan en un punto; en solapes colineales, uno del tramo común).
        /// </summary>
        public static (double x, double y) ApproxIntersection((double x, double y) a, (double x, double y) b, (double x, double y) c, (double x, double y) d)
        {
            double ux = b.x - a.x, uy = b.y - a.y;
            double vx = d.x - c.x, vy = d.y - c.y;
            double wx = a.x - c.x, wy = a.y - c.y;
            double A = ux * ux + uy * uy, B = ux * vx + uy * vy, C = vx * vx + vy * vy;
            double D = ux * wx + uy * wy, E = vx * wx + vy * wy;
            double DD = A * C - B * B;
            double sN, sD = DD, tN, tD = DD;
            const double Small = 1e-12;

            if (DD < Small)
            {
                sN = 0.0;
                sD = 1.0;
                tN = E;
                tD = C;
            }
            else
            {
                sN = B * E - C * D;
                tN = A * E - B * D;
                if (sN < 0.0)
                {
                    sN = 0.0;
                    tN = E;
                    tD = C;
                }
                else if (sN > sD)
                {
                    sN = sD;
                    tN = E + B;
                    tD = C;
                }
            }

            if (tN < 0.0)
            {
                tN = 0.0;
                if (-D < 0.0)
                {
                    sN = 0.0;
                }
                else if (-D > A)
                {
                    sN = sD;
                }
                else
                {
                    sN = -D;
                    sD = A;
                }
            }
            else if (tN > tD)
            {
                tN = tD;
                if (-D + B < 0.0)
                {
                    sN = 0.0;
                }
                else if (-D + B > A)
                {
                    sN = sD;
                }
                else
                {
                    sN = -D + B;
                    sD = A;
                }
            }

            double s = Math.Abs(sN) < Small ? 0.0 : sN / sD;
            double t = Math.Abs(tN) < Small ? 0.0 : tN / tD;
            double px = a.x + s * ux, py = a.y + s * uy;
            double qx = c.x + t * vx, qy = c.y + t * vy;
            return ((px + qx) / 2.0, (py + qy) / 2.0);
        }

        private static IReadOnlyList<string> Run(
            IReadOnlyList<(double x, double y)> vertices, bool cerrada, IReadOnlyList<double>? bulges, double tol, bool conPunto)
        {
            if (vertices == null)
            {
                throw new ArgumentNullException(nameof(vertices));
            }

            if (!double.IsFinite(tol) || tol < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tol), "La tolerancia debe ser un número finito no negativo.");
            }

            var errors = new List<string>();
            int n = vertices.Count;

            for (int i = 0; i < n; i++)
            {
                if (!double.IsFinite(vertices[i].x) || !double.IsFinite(vertices[i].y))
                {
                    errors.Add("vertices[" + Int(i) + "]: coordenada no finita");
                }
            }

            if (errors.Count > 0)
            {
                return errors;
            }

            if (cerrada && n < 3)
            {
                errors.Add(ClosedNeedsThree);
            }

            if (bulges != null)
            {
                if (bulges.Count != n)
                {
                    errors.Add(BulgesLength);
                }

                for (int i = 0; i < bulges.Count; i++)
                {
                    if (!double.IsFinite(bulges[i]))
                    {
                        errors.Add(BulgeNotFinite(i));
                    }
                }
            }

            for (int i = 1; i < n; i++)
            {
                if (Distance(vertices[i - 1], vertices[i]) <= tol)
                {
                    errors.Add(Duplicate(i));
                }
            }

            if (cerrada && n >= 2 && Distance(vertices[n - 1], vertices[0]) <= tol)
            {
                errors.Add(RepeatsFirst(n - 1));
            }

            if (errors.Count == 0 && cerrada && n >= 4)
            {
                (int a, int b)? hit = FindSelfIntersection(vertices, tol);
                if (hit != null)
                {
                    if (conPunto)
                    {
                        (double px, double py) = ApproxIntersection(
                            vertices[hit.Value.a], vertices[(hit.Value.a + 1) % n],
                            vertices[hit.Value.b], vertices[(hit.Value.b + 1) % n]);
                        errors.Add(SelfIntersectionAt(hit.Value.a, hit.Value.b, px, py));
                    }
                    else
                    {
                        errors.Add(SelfIntersection(hit.Value.a, hit.Value.b));
                    }
                }
            }

            return errors;
        }

        /// <summary>
        /// Primer par (i &lt; j, orden lexicográfico) de tramos NO adyacentes de la polilínea cerrada que se cruzan o se
        /// tocan (incluido el solape colineal), o null. O(n²).
        /// </summary>
        public static (int a, int b)? FindSelfIntersection(IReadOnlyList<(double x, double y)> v, double tol = DefaultTolerance)
        {
            if (v == null)
            {
                throw new ArgumentNullException(nameof(v));
            }

            int n = v.Count;
            for (int i = 0; i < n; i++)
            {
                (double x, double y) p1 = v[i];
                (double x, double y) p2 = v[(i + 1) % n];
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1)
                    {
                        continue; // adyacentes por el cierre
                    }

                    if (SegmentsTouch(p1, p2, v[j], v[(j + 1) % n], tol))
                    {
                        return (i, j);
                    }
                }
            }

            return null;
        }

        /// <summary>true si los segmentos [a,b] y [c,d] se cruzan o se tocan (con tolerancia de distancia).</summary>
        public static bool SegmentsTouch(
            (double x, double y) a, (double x, double y) b, (double x, double y) c, (double x, double y) d, double tol = DefaultTolerance)
        {
            int o1 = Orientation(a, b, c, tol);
            int o2 = Orientation(a, b, d, tol);
            int o3 = Orientation(c, d, a, tol);
            int o4 = Orientation(c, d, b, tol);

            if (o1 != 0 && o2 != 0 && o3 != 0 && o4 != 0)
            {
                return o1 != o2 && o3 != o4;
            }

            if (o1 == 0 && OnSegment(a, b, c, tol))
            {
                return true;
            }

            if (o2 == 0 && OnSegment(a, b, d, tol))
            {
                return true;
            }

            if (o3 == 0 && OnSegment(c, d, a, tol))
            {
                return true;
            }

            if (o4 == 0 && OnSegment(c, d, b, tol))
            {
                return true;
            }

            // Algún extremo sobre la recta del otro tramo pero fuera de él: no se tocan.
            return false;
        }

        /// <summary>
        /// Lado de <paramref name="p"/> respecto de la recta a→b: +1 izquierda, −1 derecha, 0 si su distancia a la recta
        /// es ≤ tol (o a = b y p dista ≤ tol de a).
        /// </summary>
        private static int Orientation((double x, double y) a, (double x, double y) b, (double x, double y) p, double tol)
        {
            double dx = b.x - a.x;
            double dy = b.y - a.y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double cross = dx * (p.y - a.y) - dy * (p.x - a.x);
            if (len <= tol)
            {
                return Distance(a, p) <= tol ? 0 : 1;
            }

            double dist = cross / len;
            if (dist > tol)
            {
                return 1;
            }

            return dist < -tol ? -1 : 0;
        }

        /// <summary>p (ya colineal con a-b dentro de tol) está dentro del tramo [a, b] ampliado tol.</summary>
        private static bool OnSegment((double x, double y) a, (double x, double y) b, (double x, double y) p, double tol)
        {
            return p.x >= Math.Min(a.x, b.x) - tol && p.x <= Math.Max(a.x, b.x) + tol
                && p.y >= Math.Min(a.y, b.y) - tol && p.y <= Math.Max(a.y, b.y) + tol;
        }

        private static double Distance((double x, double y) a, (double x, double y) b)
        {
            double dx = a.x - b.x;
            double dy = a.y - b.y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string Int(int i) => i.ToString(CultureInfo.InvariantCulture);

        private static string Num(double x) => (Math.Round(x, 6) + 0.0).ToString("0.######", CultureInfo.InvariantCulture);
    }
}
