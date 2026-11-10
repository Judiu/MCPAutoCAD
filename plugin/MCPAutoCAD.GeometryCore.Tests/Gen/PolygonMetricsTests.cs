using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>protocol.md §5E.3.3 / §5E.9: centroide, pertenencia, distancia al borde, punto interior y formato de área.</summary>
    public class PolygonMetricsTests
    {
        private const int P = 9;

        private static List<(double x, double y)> Pts(params double[] xy)
        {
            var r = new List<(double x, double y)>();
            for (int i = 0; i < xy.Length; i += 2)
            {
                r.Add((xy[i], xy[i + 1]));
            }

            return r;
        }

        private static readonly List<(double x, double y)> Square = Pts(0, 0, 4, 0, 4, 4, 0, 4);

        // L gruesa: centroide (5/3, 5/3), dentro.
        private static readonly List<(double x, double y)> LShape = Pts(0, 0, 4, 0, 4, 2, 2, 2, 2, 4, 0, 4);

        // C profunda (brazos de 1): centroide (26/7, 5) en el hueco, fuera.
        private static readonly List<(double x, double y)> CShape = Pts(0, 0, 10, 0, 10, 1, 1, 1, 1, 9, 10, 9, 10, 10, 0, 10);

        [Fact]
        public void Centroid_Cuadrado()
        {
            (double x, double y) c = PolygonMetrics.Centroid(Square);
            Assert.Equal(2, c.x, P);
            Assert.Equal(2, c.y, P);
        }

        [Fact]
        public void Centroid_CuadradoHorarioYCoordenadasGrandes()
        {
            (double x, double y) c = PolygonMetrics.Centroid(Pts(3000, 3977, 3000, 3981, 3004, 3981, 3004, 3977));
            Assert.Equal(3002, c.x, P);
            Assert.Equal(3979, c.y, P);
        }

        [Fact]
        public void Centroid_AreaCero_CentroDeLaCaja()
        {
            (double x, double y) c = PolygonMetrics.Centroid(Pts(0, 0, 2, 0, 4, 0));
            Assert.Equal(2, c.x, P);
            Assert.Equal(0, c.y, P);
        }

        [Fact]
        public void Centroid_L_Dentro()
        {
            (double x, double y) c = PolygonMetrics.Centroid(LShape);
            Assert.Equal(5.0 / 3, c.x, P);
            Assert.Equal(5.0 / 3, c.y, P);
            Assert.True(PolygonMetrics.Contains(LShape, c.x, c.y));
            (double x, double y) ip = PolygonMetrics.InteriorPoint(LShape, out bool esCentroide);
            Assert.True(esCentroide);
            Assert.Equal(c, ip);
        }

        [Fact]
        public void Contains_DentroFueraYBorde()
        {
            Assert.True(PolygonMetrics.Contains(Square, 2, 2));
            Assert.False(PolygonMetrics.Contains(Square, 5, 2));
            Assert.False(PolygonMetrics.Contains(Square, -0.001, 2));
            Assert.True(PolygonMetrics.Contains(Square, 0, 2));      // borde
            Assert.True(PolygonMetrics.Contains(Square, 4, 4));      // vértice
            Assert.True(PolygonMetrics.Contains(Square, 4 + 1e-10, 1)); // dentro de la tolerancia
            Assert.False(PolygonMetrics.Contains(LShape, 3, 3));     // hueco de la L
            Assert.False(PolygonMetrics.Contains(CShape, 5, 5));     // hueco de la C
        }

        [Fact]
        public void DistanceToBoundary_Basico()
        {
            Assert.Equal(1, PolygonMetrics.DistanceToBoundary(Square, 1, 2), P);
            Assert.Equal(2, PolygonMetrics.DistanceToBoundary(Square, 2, 2), P);
            Assert.Equal(1, PolygonMetrics.DistanceToBoundary(Square, 5, 2), P);
            Assert.Equal(Math.Sqrt(2), PolygonMetrics.DistanceToBoundary(Square, 5, 5), P);
            Assert.Equal(0, PolygonMetrics.DistanceToBoundary(Square, 0, 3), P);
        }

        [Fact]
        public void InteriorPoint_C_CentroideFuera_PoloDentro()
        {
            (double x, double y) c = PolygonMetrics.Centroid(CShape);
            Assert.Equal(26.0 / 7, c.x, P);
            Assert.Equal(5, c.y, P);
            Assert.False(PolygonMetrics.Contains(CShape, c.x, c.y));

            (double x, double y) ip = PolygonMetrics.InteriorPoint(CShape, out bool esCentroide);
            Assert.False(esCentroide);
            Assert.True(PolygonMetrics.Contains(CShape, ip.x, ip.y));
            double d = PolygonMetrics.DistanceToBoundary(CShape, ip.x, ip.y);
            Assert.True(d > 0.5, "distancia al borde " + d);
            // Máximo teórico: en una esquina interior, (t, t) con t = √2·(1 − t) → t = 2 − √2 ≈ 0,586.
            Assert.True(d <= (2 - Math.Sqrt(2)) + 1e-9, "distancia al borde " + d);
        }

        [Fact]
        public void InteriorPoint_Cuadrado_EsCentroide()
        {
            (double x, double y) ip = PolygonMetrics.InteriorPoint(Square, out bool esCentroide);
            Assert.True(esCentroide);
            Assert.Equal(2, ip.x, P);
            Assert.Equal(2, ip.y, P);
        }

        [Theory]
        [InlineData(12.345, 2, "12,35 m²")]
        [InlineData(1234.5, 1, "1234,5 m²")]
        [InlineData(7, 0, "7 m²")]
        [InlineData(72, 2, "72,00 m²")]
        [InlineData(0.0005, 3, "0,001 m²")]
        [InlineData(2.5, 0, "3 m²")]
        [InlineData(1234567.891, 2, "1234567,89 m²")]
        [InlineData(-0.001, 2, "0,00 m²")]
        public void FormatArea(double area, int decimales, string esperado)
        {
            Assert.Equal(esperado, PolygonMetrics.FormatArea(area, decimales));
        }

        [Fact]
        public void FormatArea_SimboloSuperindice()
        {
            Assert.EndsWith(" m²", PolygonMetrics.FormatArea(1, 2));
        }

        [Fact]
        public void FormatArea_DecimalesFueraDeRango()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PolygonMetrics.FormatArea(1, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => PolygonMetrics.FormatArea(1, -1));
        }

        // ---- Colapso de vértices consecutivos duplicados (recinto por handle, §5E.3.3) ----

        [Fact]
        public void Colapso_QuitaDuplicadosConsecutivosYLosQueRepitenElPrimero()
        {
            List<(double x, double y)> v = PolygonMetrics.CollapseConsecutiveDuplicates(
                Pts(0, 0, 4, 0, 4, 0, 4 + 1e-12, 0, 4, 4, 0, 4, 0, 4, 0, 0), out int n);
            Assert.Equal(Square, v);
            Assert.Equal(4, n);
            Assert.Empty(MCPAutoCAD.GeometryCore.Draw.PolylineValidator.Validate(v, true, null));
            Assert.Equal(16, PolygonMetrics.LengthAndArea(v, true).Item2!.Value, P);
        }

        [Fact]
        public void Colapso_SinDuplicados_NoCambiaNada()
        {
            List<(double x, double y)> v = PolygonMetrics.CollapseConsecutiveDuplicates(LShape, out int n);
            Assert.Equal(LShape, v);
            Assert.Equal(0, n);
        }

        [Fact]
        public void Colapso_QuedanMenosDeTres_ElValidadorSigueRechazando()
        {
            List<(double x, double y)> v = PolygonMetrics.CollapseConsecutiveDuplicates(Pts(0, 0, 0, 0, 4, 0, 4, 0), out int n);
            Assert.Equal(2, v.Count);
            Assert.Equal(2, n);
            Assert.Contains(MCPAutoCAD.GeometryCore.Draw.PolylineValidator.ClosedNeedsThree,
                MCPAutoCAD.GeometryCore.Draw.PolylineValidator.Validate(v, true, null));
        }

        [Fact]
        public void Colapso_PajaritaConDuplicados_SigueAutointersecada()
        {
            List<(double x, double y)> v = PolygonMetrics.CollapseConsecutiveDuplicates(Pts(0, 0, 4, 4, 4, 4, 4, 0, 0, 4), out int n);
            Assert.Equal(1, n);
            IReadOnlyList<string> errors = MCPAutoCAD.GeometryCore.Draw.PolylineValidator.Validate(v, true, null);
            Assert.Contains(errors, e => e.StartsWith("vertices: la polilínea cerrada se autointerseca", StringComparison.Ordinal));
        }

        [Fact]
        public void Colapso_ToleranciaEuclidea()
        {
            // Diferencias de 8e-10 en X y en Y: cada una ≤ 1e-9, pero la distancia euclídea (≈ 1,13e-9) no → no se colapsa,
            // igual que en WallOutline/PolylineValidator.
            List<(double x, double y)> v = PolygonMetrics.CollapseConsecutiveDuplicates(Pts(0, 0, 4, 0, 4 + 8e-10, 8e-10, 4, 4), out int n);
            Assert.Equal(0, n);
            Assert.Equal(4, v.Count);
            List<(double x, double y)> w = PolygonMetrics.CollapseConsecutiveDuplicates(Pts(0, 0, 4, 0, 4 + 6e-10, 6e-10, 4, 4), out int m);
            Assert.Equal(1, m);
            Assert.Equal(3, w.Count);
        }

        [Fact]
        public void Colapso_PolilineaAbierta_ConservaElUltimoIgualAlPrimero()
        {
            List<(double x, double y)> v = PolygonMetrics.CollapseConsecutiveDuplicates(
                Pts(0, 0, 4, 0, 4, 0, 4, 3, 0, 0), out int n, lazoCerrado: false);
            Assert.Equal(1, n);
            Assert.Equal(Pts(0, 0, 4, 0, 4, 3, 0, 0), v);
        }

        [Fact]
        public void Colapso_AdvertenciaDeEje()
        {
            Assert.Equal(
                "eje: la polilínea 2A3F tiene 1 vértice(s) consecutivo(s) duplicado(s); se ignoraron.",
                PolygonMetrics.CollapsedAxisDuplicatesWarning("2A3F", 1));
        }

        [Fact]
        public void Colapso_Advertencia()
        {
            Assert.Equal(
                "contorno: la polilínea 2A3F tiene 2 vértice(s) consecutivo(s) duplicado(s); se ignoraron para el área, el centroide y la etiqueta.",
                PolygonMetrics.CollapsedDuplicatesWarning("2A3F", 2));
        }
    }
}
