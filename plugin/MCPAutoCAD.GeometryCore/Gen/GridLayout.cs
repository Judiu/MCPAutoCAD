using System;
using System.Collections.Generic;
using System.Globalization;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Rejilla de ejes estructurales (protocol.md §5E.3.2): líneas, burbujas tangentes al extremo de cada eje y rótulos
    /// TEXT "medio_centro" en el centro de cada burbuja, como el plano madre. Puro.
    /// </summary>
    public static class GridLayout
    {
        public const int MaxSeparations = 100;
        public const int MaxLabelLength = 10;

        public sealed record GridResult(
            IReadOnlyList<PlannedEntity> Entidades,
            IReadOnlyList<double> PosicionesX,
            IReadOnlyList<double> PosicionesY,
            double Ancho,
            double Alto,
            int NBurbujas,
            BBox2 Bbox);

        /// <summary>"1", "2", …, "n".</summary>
        public static IReadOnlyList<string> DefaultNumericLabels(int n)
        {
            if (n < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(n), "n debe ser mayor o igual que 0.");
            }

            var r = new List<string>(n);
            for (int i = 1; i <= n; i++)
            {
                r.Add(i.ToString(CultureInfo.InvariantCulture));
            }

            return r;
        }

        /// <summary>"A", "B", …, "Z", "AA", "AB", … (numeración biyectiva en base 26, como las columnas de una hoja).</summary>
        public static IReadOnlyList<string> DefaultLetterLabels(int n)
        {
            if (n < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(n), "n debe ser mayor o igual que 0.");
            }

            var r = new List<string>(n);
            for (int i = 1; i <= n; i++)
            {
                int k = i;
                string s = string.Empty;
                while (k > 0)
                {
                    k--;
                    s = (char)('A' + (k % 26)) + s;
                    k /= 26;
                }

                r.Add(s);
            }

            return r;
        }

        /// <summary>
        /// Construye la rejilla. <paramref name="burbujas"/> = "inicio" | "fin" | "ambos". <paramref name="capa"/> null →
        /// Concepto "ejes" en todas las entidades. Lanza ArgumentException con los textos de §5E.3.2.
        /// Rótulos null → los de por defecto (números en x, letras en y).
        /// </summary>
        public static GridResult Build(
            Point3 origen,
            IReadOnlyList<double> separacionesX,
            IReadOnlyList<double> separacionesY,
            IReadOnlyList<string> rotulosX,
            IReadOnlyList<string> rotulosY,
            double extension,
            double radioBurbuja,
            double alturaTexto,
            string burbujas,
            double rotacionGrados,
            string? estilo,
            string? capa)
        {
            if (!origen.IsFinite)
            {
                throw new ArgumentException("origen: coordenada no finita");
            }

            ValidateSeparations(separacionesX, "separaciones_x");
            ValidateSeparations(separacionesY, "separaciones_y");
            if (separacionesX.Count == 0 && separacionesY.Count == 0)
            {
                throw new ArgumentException("separaciones: se necesita al menos un eje en x o en y");
            }

            int nx = separacionesX.Count + 1;
            int ny = separacionesY.Count + 1;
            IReadOnlyList<string> labelsX = rotulosX ?? DefaultNumericLabels(nx);
            IReadOnlyList<string> labelsY = rotulosY ?? DefaultLetterLabels(ny);
            ValidateLabels(labelsX, nx, "rotulos_x", "separaciones_x");
            ValidateLabels(labelsY, ny, "rotulos_y", "separaciones_y");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string r in Concat(labelsX, labelsY))
            {
                if (!seen.Add(r))
                {
                    throw new ArgumentException("rotulos: rótulo repetido '" + r + "'");
                }
            }

            if (!double.IsFinite(extension) || extension < 0)
            {
                throw new ArgumentException("extension: debe ser un número mayor o igual que 0");
            }

            if (!double.IsFinite(radioBurbuja) || radioBurbuja <= 0)
            {
                throw new ArgumentException("radio_burbuja: debe ser un número mayor que 0");
            }

            if (!double.IsFinite(alturaTexto) || alturaTexto <= 0)
            {
                throw new ArgumentException("altura_texto: debe ser un número mayor que 0");
            }

            bool atStart;
            bool atEnd;
            switch (burbujas)
            {
                case "inicio":
                    atStart = true;
                    atEnd = false;
                    break;
                case "fin":
                    atStart = false;
                    atEnd = true;
                    break;
                case "ambos":
                    atStart = true;
                    atEnd = true;
                    break;
                default:
                    throw new ArgumentException("burbujas: debe ser uno de inicio, fin, ambos");
            }

            if (!double.IsFinite(rotacionGrados))
            {
                throw new ArgumentException("rotacion: debe ser un número finito");
            }

            // Posiciones sin girar.
            double x0 = origen.X;
            double y0 = origen.Y;
            List<double> xs = Positions(x0, separacionesX);
            List<double> ys = Positions(y0, separacionesY);
            double ancho = xs[nx - 1] - x0;
            double alto = ys[ny - 1] - y0;
            double xn = xs[nx - 1];
            double yn = ys[ny - 1];

            Affine2 rot = Transform2D.RotateAbout(x0, y0, Transform2D.ToRadians(rotacionGrados));
            double z = origen.Z;
            Point3 P(double x, double y)
            {
                (double px, double py) = rot.Apply(x, y);
                return new Point3(px, py, z);
            }

            string? concepto = capa == null ? "ejes" : null;
            var lines = new List<PlannedEntity>(nx + ny);
            var centers = new List<(Point3 c, string label)>();

            for (int i = 0; i < nx; i++)
            {
                lines.Add(new PlannedLine(P(xs[i], y0 - extension), P(xs[i], yn + extension), concepto, capa));
                if (atStart)
                {
                    centers.Add((P(xs[i], y0 - extension - radioBurbuja), labelsX[i]));
                }

                if (atEnd)
                {
                    centers.Add((P(xs[i], yn + extension + radioBurbuja), labelsX[i]));
                }
            }

            for (int j = 0; j < ny; j++)
            {
                lines.Add(new PlannedLine(P(x0 - extension, ys[j]), P(xn + extension, ys[j]), concepto, capa));
                if (atStart)
                {
                    centers.Add((P(x0 - extension - radioBurbuja, ys[j]), labelsY[j]));
                }

                if (atEnd)
                {
                    centers.Add((P(xn + extension + radioBurbuja, ys[j]), labelsY[j]));
                }
            }

            var entities = new List<PlannedEntity>(lines.Count + (2 * centers.Count));
            entities.AddRange(lines);
            foreach ((Point3 c, string _) in centers)
            {
                entities.Add(new PlannedCircle(c, radioBurbuja, concepto, capa));
            }

            foreach ((Point3 c, string label) in centers)
            {
                entities.Add(new PlannedText(c, label, alturaTexto, 0.0, "medio_centro", estilo, concepto, capa));
            }

            BBox2? bbox = null;
            foreach (PlannedEntity e in entities)
            {
                BBox2? b = e.Caja();
                if (b != null)
                {
                    bbox = bbox == null ? b : bbox.Value.Union(b.Value);
                }
            }

            return new GridResult(entities, xs, ys, ancho, alto, centers.Count, bbox!.Value);
        }

        private static void ValidateSeparations(IReadOnlyList<double> seps, string campo)
        {
            if (seps == null || seps.Count > MaxSeparations)
            {
                throw new ArgumentException(campo + ": debe tener entre 0 y " + MaxSeparations.ToString(CultureInfo.InvariantCulture) + " elementos");
            }

            for (int i = 0; i < seps.Count; i++)
            {
                if (!double.IsFinite(seps[i]) || seps[i] <= 0)
                {
                    throw new ArgumentException(campo + "[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe ser un número mayor que 0");
                }
            }
        }

        private static void ValidateLabels(IReadOnlyList<string> labels, int n, string campo, string campoSep)
        {
            if (labels.Count != n)
            {
                throw new ArgumentException(
                    campo + ": debe tener " + n.ToString(CultureInfo.InvariantCulture) + " elementos (" + campoSep + " + 1)");
            }

            for (int i = 0; i < n; i++)
            {
                string? r = labels[i];
                if (r == null || r.Length < 1 || r.Length > MaxLabelLength)
                {
                    throw new ArgumentException(campo + "[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe tener entre 1 y 10 caracteres");
                }
            }
        }

        private static List<double> Positions(double start, IReadOnlyList<double> seps)
        {
            var r = new List<double>(seps.Count + 1) { start };
            double acc = start;
            foreach (double s in seps)
            {
                acc += s;
                r.Add(acc);
            }

            return r;
        }

        private static IEnumerable<string> Concat(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            foreach (string s in a)
            {
                yield return s;
            }

            foreach (string s in b)
            {
                yield return s;
            }
        }
    }
}
