using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>Datos geométricos de una entidad candidata a duplicado (protocol.md §5C.6). Independiente de AutoCAD.</summary>
    public abstract class DuplicateInput
    {
        /// <summary>Tipo §5B.0 (LINE, LWPOLYLINE, CIRCLE, ARC, INSERT, TEXT, MTEXT).</summary>
        public abstract string Tipo { get; }
    }

    public sealed class LineInput : DuplicateInput
    {
        public LineInput(Point3 start, Point3 end)
        {
            Start = start;
            End = end;
        }

        public override string Tipo => "LINE";
        public Point3 Start { get; }
        public Point3 End { get; }
    }

    /// <summary>LWPOLYLINE: vértices (x, y, bulge del tramo que sale de ese vértice), z = elevación.</summary>
    public sealed class PolylineInput : DuplicateInput
    {
        public PolylineInput(bool closed, double elevation, IReadOnlyList<(double x, double y, double bulge)> vertices)
        {
            Closed = closed;
            Elevation = elevation;
            Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
        }

        public override string Tipo => "LWPOLYLINE";
        public bool Closed { get; }
        public double Elevation { get; }
        public IReadOnlyList<(double x, double y, double bulge)> Vertices { get; }
    }

    public sealed class CircleInput : DuplicateInput
    {
        public CircleInput(Point3 center, double radius, Point3 normal)
        {
            Center = center;
            Radius = radius;
            Normal = normal;
        }

        public override string Tipo => "CIRCLE";
        public Point3 Center { get; }
        public double Radius { get; }
        public Point3 Normal { get; }
    }

    /// <summary>ARC: ángulos en radianes (se normalizan a [0, 2π)).</summary>
    public sealed class ArcInput : DuplicateInput
    {
        public ArcInput(Point3 center, double radius, double startAngle, double endAngle)
        {
            Center = center;
            Radius = radius;
            StartAngle = startAngle;
            EndAngle = endAngle;
        }

        public override string Tipo => "ARC";
        public Point3 Center { get; }
        public double Radius { get; }
        public double StartAngle { get; }
        public double EndAngle { get; }
    }

    /// <summary>INSERT: nombre efectivo, posición, escala (sx, sy, sz) y rotación en radianes.</summary>
    public sealed class InsertInput : DuplicateInput
    {
        public InsertInput(string name, Point3 position, Point3 scale, double rotation)
        {
            Name = name ?? "";
            Position = position;
            Scale = scale;
            Rotation = rotation;
        }

        public override string Tipo => "INSERT";
        public string Name { get; }
        public Point3 Position { get; }
        public Point3 Scale { get; }
        public double Rotation { get; }
    }

    /// <summary>TEXT o MTEXT: texto exacto, posición y altura.</summary>
    public sealed class TextInput : DuplicateInput
    {
        public TextInput(bool isMText, string text, Point3 position, double height)
        {
            IsMText = isMText;
            Text = text ?? "";
            Position = position;
            Height = height;
        }

        public override string Tipo => IsMText ? "MTEXT" : "TEXT";
        public bool IsMText { get; }
        public string Text { get; }
        public Point3 Position { get; }
        public double Height { get; }
    }

    /// <summary>
    /// Clave canónica de geometría redondeada (protocol.md §5C.6): cada número se redondea a múltiplos de la
    /// tolerancia (<c>Math.Round(v / tol)</c> como entero de 64 bits) y se escribe con cultura invariante. Dos
    /// entidades con la misma clave son duplicadas; el agrupamiento es un diccionario (nunca O(n²)). Limitación
    /// aceptada: dos valores a distancia &lt; tol pueden caer en celdas contiguas y no agruparse. La capa no forma
    /// parte de la clave.
    /// </summary>
    public static class DuplicateHasher
    {
        public const double MinTolerance = 1e-12;
        public const double MaxTolerance = 1.0;
        public const double DefaultTolerance = 1e-6;

        /// <summary>Tipos admitidos, en el orden del contrato.</summary>
        public static readonly IReadOnlyList<string> Types = new[] { "LINE", "LWPOLYLINE", "CIRCLE", "ARC", "INSERT", "TEXT", "MTEXT" };

        /// <summary>Clave de la entidad. Tolerancia fuera de [1e-12, 1] o no finita → ArgumentOutOfRangeException.</summary>
        public static string Key(DuplicateInput input, double tolerancia)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (!double.IsFinite(tolerancia) || tolerancia < MinTolerance || tolerancia > MaxTolerance)
            {
                throw new ArgumentOutOfRangeException(nameof(tolerancia), "tolerancia: debe ser un número entre 1e-12 y 1");
            }

            switch (input)
            {
                case LineInput l:
                    return LineKey(l, tolerancia);
                case PolylineInput p:
                    return PolylineKey(p, tolerancia);
                case CircleInput c:
                    return "CIRCLE|" + P(c.Center, tolerancia) + "|" + R(c.Radius, tolerancia) + "|" + P(c.Normal, tolerancia);
                case ArcInput a:
                    return "ARC|" + P(a.Center, tolerancia) + "|" + R(a.Radius, tolerancia) + "|"
                        + R(NormalizeAngle(a.StartAngle), tolerancia) + "," + R(NormalizeAngle(a.EndAngle), tolerancia);
                case InsertInput ins:
                    return "INSERT|" + S(ins.Name.ToUpperInvariant()) + "|" + P(ins.Position, tolerancia) + "|"
                        + P(ins.Scale, tolerancia) + "|" + R(NormalizeAngle(ins.Rotation), tolerancia);
                case TextInput t:
                    return t.Tipo + "|" + S(t.Text) + "|" + P(t.Position, tolerancia) + "|" + R(t.Height, tolerancia);
                default:
                    throw new ArgumentException("tipo no admitido: " + input.GetType().Name, nameof(input));
            }
        }

        /// <summary>Ángulo normalizado a [0, 2π).</summary>
        public static double NormalizeAngle(double radians)
        {
            if (!double.IsFinite(radians))
            {
                return radians;
            }

            double twoPi = 2.0 * Math.PI;
            double a = radians % twoPi;
            if (a < 0)
            {
                a += twoPi;
            }

            return a >= twoPi ? 0.0 : a;
        }

        private static string LineKey(LineInput l, double tol)
        {
            string a = P(l.Start, tol);
            string b = P(l.End, tol);
            long[] ra = Round3(l.Start, tol);
            long[] rb = Round3(l.End, tol);
            bool swap = CompareTuple(ra, rb) > 0;
            return "LINE|" + (swap ? b + "|" + a : a + "|" + b);
        }

        private static string PolylineKey(PolylineInput p, double tol)
        {
            int n = p.Vertices.Count;
            string head = "LWPOLYLINE|" + (p.Closed ? "c" : "o") + "|" + R(p.Elevation, tol) + "|" + n.ToString(CultureInfo.InvariantCulture);
            if (n == 0)
            {
                return head;
            }

            // Directa: el bulge del último vértice solo cuenta si la polilínea es cerrada (tramo de cierre).
            var direct = new (string x, string y, string b)[n];
            for (int i = 0; i < n; i++)
            {
                (double x, double y, double bulge) = p.Vertices[i];
                double b = (i == n - 1 && !p.Closed) ? 0.0 : bulge;
                direct[i] = (R(x, tol), R(y, tol), R(b, tol));
            }

            // Invertida: vn..v0; el tramo que sale de v(n-j) es el original (n-j-1) recorrido al revés (−bulge).
            // En cerradas, el último (v0) sale por el tramo de cierre invertido (−b(n-1) del original = −bulge de vn).
            var reversed = new (string x, string y, string b)[n];
            for (int j = 0; j < n; j++)
            {
                int vi = n - 1 - j;
                (double x, double y, _) = p.Vertices[vi];
                double b;
                if (j < n - 1)
                {
                    b = -p.Vertices[vi - 1].bulge;
                }
                else
                {
                    b = p.Closed ? -p.Vertices[n - 1].bulge : 0.0;
                }

                reversed[j] = (R(x, tol), R(y, tol), R(b, tol));
            }

            string sd = Join(direct);
            string sr = Join(reversed);
            return head + "|" + (string.CompareOrdinal(sd, sr) <= 0 ? sd : sr);
        }

        private static string Join((string x, string y, string b)[] seq)
        {
            var sb = new StringBuilder(seq.Length * 24);
            for (int i = 0; i < seq.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(';');
                }

                sb.Append(seq[i].x).Append(',').Append(seq[i].y).Append(',').Append(seq[i].b);
            }

            return sb.ToString();
        }

        private static int CompareTuple(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return 0;
        }

        private static long[] Round3(Point3 p, double tol) => new[] { RoundLong(p.X, tol), RoundLong(p.Y, tol), RoundLong(p.Z, tol) };

        private static long RoundLong(double v, double tol)
        {
            double q = Math.Round(v / tol);
            if (!double.IsFinite(q))
            {
                return q > 0 ? long.MaxValue : (q < 0 ? long.MinValue : 0);
            }

            if (q >= 9.2e18)
            {
                return long.MaxValue;
            }

            if (q <= -9.2e18)
            {
                return long.MinValue;
            }

            return (long)q;
        }

        /// <summary>Número redondeado a múltiplo de tol como entero invariante (exponencial si no cabe en 64 bits).</summary>
        private static string R(double v, double tol)
        {
            if (!double.IsFinite(v))
            {
                return "nan";
            }

            double q = Math.Round(v / tol);
            if (!double.IsFinite(q) || Math.Abs(q) >= 9.2e18)
            {
                return "e" + q.ToString("R", CultureInfo.InvariantCulture);
            }

            return ((long)q).ToString(CultureInfo.InvariantCulture);
        }

        private static string P(Point3 p, double tol) => R(p.X, tol) + "," + R(p.Y, tol) + "," + R(p.Z, tol);

        /// <summary>Cadena con prefijo de longitud (evita ambigüedad con separadores dentro del texto).</summary>
        private static string S(string s) => s.Length.ToString(CultureInfo.InvariantCulture) + ":" + s;
    }
}
