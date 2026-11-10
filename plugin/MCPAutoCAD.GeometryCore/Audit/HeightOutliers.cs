using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>Texto con altura fuera de escala.</summary>
    public sealed class HeightOutlier
    {
        public HeightOutlier(int index, string handle, double altura, string motivo, double? ratio)
        {
            Index = index;
            Handle = handle;
            Altura = altura;
            Motivo = motivo;
            Ratio = ratio;
        }

        /// <summary>Posición en la lista de entrada (orden de base de datos).</summary>
        public int Index { get; }

        public string Handle { get; }

        public double Altura { get; }

        /// <summary>"mayor" | "menor" | "no_positiva".</summary>
        public string Motivo { get; }

        /// <summary>altura / mediana, o null si la mediana es 0.</summary>
        public double? Ratio { get; }
    }

    /// <summary>Resultado de <see cref="HeightOutliers.Analyze"/>.</summary>
    public sealed class HeightOutliersResult
    {
        public HeightOutliersResult(double? mediana, int n, List<HeightOutlier> outliers)
        {
            Mediana = mediana;
            N = n;
            Outliers = outliers;
        }

        /// <summary>Mediana de todas las alturas (null si no hay ninguna).</summary>
        public double? Mediana { get; }

        /// <summary>Número de alturas consideradas.</summary>
        public int N { get; }

        /// <summary>Outliers en el orden del contrato: mayores (ratio desc), menores (ratio asc), no positivas.</summary>
        public List<HeightOutlier> Outliers { get; }
    }

    /// <summary>
    /// Alturas de texto fuera de escala (protocol.md §5C.7): outlier si altura ≤ 0 (no_positiva),
    /// &lt; 0.2·mediana (menor) o &gt; 5·mediana (mayor). Con n &lt; 2 no hay outliers.
    /// </summary>
    public static class HeightOutliers
    {
        public const string Mayor = "mayor";
        public const string Menor = "menor";
        public const string NoPositiva = "no_positiva";

        public const double LowFactor = 0.2;
        public const double HighFactor = 5.0;

        /// <summary>
        /// Analiza las alturas (handle, altura) en orden de base de datos. Las alturas no finitas se ignoran. Si la
        /// mediana no es positiva no se evalúan "menor"/"mayor" (la comparación no tiene sentido).
        /// </summary>
        public static HeightOutliersResult Analyze(IReadOnlyList<(string handle, double altura)> alturas)
        {
            if (alturas == null)
            {
                throw new ArgumentNullException(nameof(alturas));
            }

            var values = new List<double>(alturas.Count);
            foreach ((_, double h) in alturas)
            {
                if (double.IsFinite(h))
                {
                    values.Add(h);
                }
            }

            int n = values.Count;
            double? median = Median(values);
            var outliers = new List<HeightOutlier>();
            if (n < 2 || median == null)
            {
                return new HeightOutliersResult(median, n, outliers);
            }

            double m = median.Value;
            var mayores = new List<HeightOutlier>();
            var menores = new List<HeightOutlier>();
            var noPos = new List<HeightOutlier>();
            for (int i = 0; i < alturas.Count; i++)
            {
                (string handle, double h) = alturas[i];
                if (!double.IsFinite(h))
                {
                    continue;
                }

                double? ratio = m == 0 ? null : h / m;
                if (h <= 0)
                {
                    noPos.Add(new HeightOutlier(i, handle, h, NoPositiva, ratio));
                }
                else if (m > 0 && h < LowFactor * m)
                {
                    menores.Add(new HeightOutlier(i, handle, h, Menor, ratio));
                }
                else if (m > 0 && h > HighFactor * m)
                {
                    mayores.Add(new HeightOutlier(i, handle, h, Mayor, ratio));
                }
            }

            // Orden estable: empates por orden de base de datos (Index).
            mayores.Sort((a, b) =>
            {
                int c = (b.Ratio ?? 0).CompareTo(a.Ratio ?? 0);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            });
            menores.Sort((a, b) =>
            {
                int c = (a.Ratio ?? 0).CompareTo(b.Ratio ?? 0);
                return c != 0 ? c : a.Index.CompareTo(b.Index);
            });

            outliers.AddRange(mayores);
            outliers.AddRange(menores);
            outliers.AddRange(noPos);
            return new HeightOutliersResult(median, n, outliers);
        }

        /// <summary>Mediana (media de las dos centrales si n es par); null si la lista está vacía.</summary>
        public static double? Median(IReadOnlyList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }

            var sorted = new List<double>(values);
            sorted.Sort();
            int n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
        }
    }
}
