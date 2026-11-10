using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>Alturas de texto fuera de escala (protocol.md §5C.7).</summary>
    public class HeightOutliersTests
    {
        private static List<(string, double)> H(params double[] heights) =>
            heights.Select((h, i) => ("H" + i, h)).ToList();

        [Fact]
        public void Mediana_Impar()
        {
            Assert.Equal(0.2, HeightOutliers.Analyze(H(0.3, 0.2, 0.1)).Mediana!.Value, 12);
        }

        [Fact]
        public void Mediana_Par_MediaDeLasDosCentrales()
        {
            HeightOutliersResult r = HeightOutliers.Analyze(H(0.1, 0.4, 0.2, 0.3));
            Assert.Equal(0.25, r.Mediana!.Value, 12);
            Assert.Equal(4, r.N);
        }

        [Fact]
        public void TresMotivos_ConOrdenDelContrato()
        {
            // Mediana 0.2: 2.5 (ratio 12.5) y 1.5 (7.5) mayores; 0.01 (0.05) y 0.03 (0.15) menores; 0 no positiva.
            HeightOutliersResult r = HeightOutliers.Analyze(H(0.2, 1.5, 0.2, 0.01, 0.2, 0.0, 0.2, 2.5, 0.2, 0.03, 0.2, 0.2, 0.2));
            Assert.Equal(0.2, r.Mediana!.Value, 12);
            Assert.Equal(new[] { "H7", "H1", "H3", "H9", "H5" }, r.Outliers.Select(o => o.Handle));
            Assert.Equal(new[] { "mayor", "mayor", "menor", "menor", "no_positiva" }, r.Outliers.Select(o => o.Motivo));
            Assert.Equal(12.5, r.Outliers[0].Ratio!.Value, 9);
            Assert.Equal(0.05, r.Outliers[2].Ratio!.Value, 9);
        }

        [Fact]
        public void Limites_NoSonOutliers()
        {
            // Exactamente 0.2× y 5× la mediana no cuentan (desigualdad estricta).
            HeightOutliersResult r = HeightOutliers.Analyze(H(1, 1, 1, 0.2, 5));
            Assert.Empty(r.Outliers);
        }

        [Fact]
        public void Negativa_EsNoPositiva()
        {
            HeightOutliersResult r = HeightOutliers.Analyze(H(1, 1, -0.5));
            Assert.Single(r.Outliers);
            Assert.Equal(HeightOutliers.NoPositiva, r.Outliers[0].Motivo);
        }

        [Fact]
        public void MenosDeDos_SinOutliers()
        {
            Assert.Empty(HeightOutliers.Analyze(H(0)).Outliers);
            Assert.Equal(1, HeightOutliers.Analyze(H(7)).N);
            HeightOutliersResult empty = HeightOutliers.Analyze(H());
            Assert.Null(empty.Mediana);
            Assert.Empty(empty.Outliers);
        }

        [Fact]
        public void MedianaCero_RatioNull()
        {
            HeightOutliersResult r = HeightOutliers.Analyze(H(0, 0, 0, 1));
            Assert.Equal(0.0, r.Mediana!.Value);
            Assert.All(r.Outliers, o => Assert.Null(o.Ratio));
            Assert.Equal(3, r.Outliers.Count(o => o.Motivo == HeightOutliers.NoPositiva));
        }

        [Fact]
        public void Empates_PorOrdenDeBaseDeDatos()
        {
            HeightOutliersResult r = HeightOutliers.Analyze(H(1, 1, 1, 9, 9));
            Assert.Equal(new[] { "H3", "H4" }, r.Outliers.Select(o => o.Handle));
        }
    }
}
