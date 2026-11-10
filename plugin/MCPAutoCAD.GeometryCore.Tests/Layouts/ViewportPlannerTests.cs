using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Layouts;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Layouts
{
    public class ViewportPlannerTests
    {
        [Theory]
        [InlineData("m", 1.0)]
        [InlineData("cm", 0.01)]
        [InlineData("mm", 0.001)]
        public void UnidadesModelo(string u, double m) => Assert.Equal(m, ViewportPlanner.MetersPerModelUnit(u));

        [Theory]
        [InlineData("M")]
        [InlineData("km")]
        [InlineData("")]
        [InlineData(null)]
        public void UnidadesModelo_Desconocidas(string? u) => Assert.Null(ViewportPlanner.MetersPerModelUnit(u));

        [Fact]
        public void Resolver_SinInsunits_UsaParametro()
        {
            Assert.Equal(1.0, ViewportPlanner.ResolveModelMeters(null, "m", out string? p));
            Assert.Null(p);
        }

        [Fact]
        public void Resolver_SinInsunitsNiParametro_Problema()
        {
            Assert.Null(ViewportPlanner.ResolveModelMeters(null, null, out string? p));
            Assert.Contains("unidades_modelo", p);
        }

        [Fact]
        public void Resolver_ConInsunits_SinParametro()
        {
            Assert.Equal(0.001, ViewportPlanner.ResolveModelMeters(0.001, null, out string? p));
            Assert.Null(p);
        }

        [Fact]
        public void Resolver_ConInsunitsIgual_Acepta()
        {
            Assert.Equal(1.0, ViewportPlanner.ResolveModelMeters(1.0, "m", out string? p));
            Assert.Null(p);
        }

        [Fact]
        public void Resolver_ConInsunitsDistinto_Rechaza()
        {
            Assert.Null(ViewportPlanner.ResolveModelMeters(1.0, "mm", out string? p));
            Assert.Contains("no coincide", p);
        }

        [Fact]
        public void Resolver_ParametroInvalido_Rechaza()
        {
            Assert.Null(ViewportPlanner.ResolveModelMeters(1.0, "km", out string? p));
            Assert.Contains("m, cm o mm", p);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void Resolver_InsunitsNoValido_UsaParametro(double ins)
        {
            Assert.Equal(0.01, ViewportPlanner.ResolveModelMeters(ins, "cm", out string? p));
            Assert.Null(p);
        }

        [Fact]
        public void DesdeVentana_Metros150_MmPapel()
        {
            // Planta de 12 × 6 m a 1:50 en papel en mm: 240 × 120 mm, factor 20.
            BBox2 w = BBox2.FromCorners(10, 20, 22, 26);
            ViewportGeometry g = ViewportPlanner.FromWindow(w, 50, 1, 1, 300, 200);
            Assert.Equal(20, g.CustomScale, 9);
            Assert.Equal(240, g.Width, 9);
            Assert.Equal(120, g.Height, 9);
            Assert.Equal(16, g.ModelX, 9);
            Assert.Equal(23, g.ModelY, 9);
            Assert.Equal(300, g.PaperX);
            Assert.Equal(200, g.PaperY);
            Assert.Equal(BBox2.FromCorners(180, 140, 420, 260), g.PaperBox);
            AssertBox(w, g.ModelWindow);
        }

        [Fact]
        public void DesdeVentana_ModeloEnCm_Escala100()
        {
            // 1200 × 600 cm a 1:100 → 120 × 60 mm, factor 0,1.
            ViewportGeometry g = ViewportPlanner.FromWindow(BBox2.FromCorners(0, 0, 1200, 600), 100, 1, 0.01, 0, 0);
            Assert.Equal(0.1, g.CustomScale, 12);
            Assert.Equal(120, g.Width, 9);
            Assert.Equal(60, g.Height, 9);
        }

        [Fact]
        public void DesdeVentana_PapelEnPulgadas()
        {
            // 25,4 m a 1:100 → 254 mm = 10 pulgadas.
            ViewportGeometry g = ViewportPlanner.FromWindow(BBox2.FromCorners(0, 0, 25.4, 2.54), 100, 25.4, 1, 0, 0);
            Assert.Equal(10, g.Width, 9);
            Assert.Equal(1, g.Height, 9);
        }

        [Fact]
        public void DesdeVentana_ConsistenteConDenominador()
        {
            ViewportGeometry g = ViewportPlanner.FromWindow(BBox2.FromCorners(0, 0, 7, 3), 75, 1, 1, 0, 0);
            Assert.Equal(75, LayoutRules.Denominator(g.CustomScale, 1, 1)!.Value, 9);
        }

        [Fact]
        public void DesdeVentana_SinAlto_Rechaza() =>
            Assert.Throws<ArgumentException>(() => ViewportPlanner.FromWindow(BBox2.FromCorners(0, 0, 5, 0), 50, 1, 1, 0, 0));

        [Fact]
        public void DesdeVentana_Enorme_Rechaza()
        {
            var ex = Assert.Throws<ArgumentException>(() => ViewportPlanner.FromWindow(BBox2.FromCorners(0, 0, 1000, 10), 1, 1, 1, 0, 0));
            Assert.Contains("escala menor", ex.Message);
        }

        [Fact]
        public void DesdeVentana_Diminuto_Rechaza()
        {
            var ex = Assert.Throws<ArgumentException>(() => ViewportPlanner.FromWindow(BBox2.FromCorners(0, 0, 1e-6, 1e-6), 100000, 1, 1, 0, 0));
            Assert.Contains("escala mayor", ex.Message);
        }

        [Theory]
        [InlineData(double.NaN, 0)]
        [InlineData(0, double.PositiveInfinity)]
        [InlineData(2e7, 0)]
        public void DesdeVentana_CentroPapelNoValido(double x, double y) =>
            Assert.Throws<ArgumentException>(() => ViewportPlanner.FromWindow(BBox2.FromCorners(0, 0, 1, 1), 50, 1, 1, x, y));

        [Theory]
        [InlineData(0, 1, 1)]
        [InlineData(50, 0, 1)]
        [InlineData(50, 1, 0)]
        [InlineData(-50, 1, 1)]
        [InlineData(double.NaN, 1, 1)]
        public void Escala_NoValida(double n, double paper, double model) =>
            Assert.Throws<ArgumentException>(() => ViewportPlanner.CheckedScale(n, paper, model));

        [Fact]
        public void Reescalar_ConservaTamanoYCentro()
        {
            ViewportGeometry g = ViewportPlanner.Rescale(240, 120, 300, 200, 16, 23, 10);
            Assert.Equal(240, g.Width);
            Assert.Equal(120, g.Height);
            Assert.Equal(16, g.ModelX);
            Assert.Equal(23, g.ModelY);
            AssertBox(BBox2.FromCorners(4, 17, 28, 29), g.ModelWindow);
        }

        [Fact]
        public void Reescalar_CentroModeloNoFinito() =>
            Assert.Throws<ArgumentException>(() => ViewportPlanner.Rescale(1, 1, 0, 0, double.NaN, 0, 1));

        [Fact]
        public void ConFactor_NoValido() =>
            Assert.Throws<ArgumentException>(() => ViewportPlanner.FromWindowWithScale(BBox2.FromCorners(0, 0, 1, 1), 0, 0, 0));

        [Fact]
        public void Reordenar_ParcialPoneDelanteYConservaResto()
        {
            var cur = new[] { "Layout1", "E3", "E1", "E2" };
            Assert.Equal(new[] { "E1", "E2", "E3", "Layout1" }, ViewportPlanner.Reorder(cur, new[] { "e1", "E2", "E3" }));
        }

        [Fact]
        public void Reordenar_DevuelveNombresReales()
        {
            Assert.Equal(new[] { "Planta", "Corte" }, ViewportPlanner.Reorder(new[] { "Corte", "Planta" }, new[] { "PLANTA" }));
        }

        [Fact]
        public void Reordenar_MismoOrden()
        {
            var cur = new[] { "A", "B" };
            Assert.Equal(cur, ViewportPlanner.Reorder(cur, new[] { "A" }));
        }

        [Fact]
        public void Reordenar_Repetido()
        {
            var ex = Assert.Throws<ArgumentException>(() => ViewportPlanner.Reorder(new[] { "A", "B" }, new[] { "A", "a" }));
            Assert.Contains("orden[1]", ex.Message);
        }

        [Fact]
        public void Reordenar_Inexistente()
        {
            var ex = Assert.Throws<ArgumentException>(() => ViewportPlanner.Reorder(new[] { "A", "B" }, new[] { "C", "A", "D" }));
            Assert.Contains("C, D", ex.Message);
        }

        [Fact]
        public void Reordenar_Vacio() =>
            Assert.Throws<ArgumentException>(() => ViewportPlanner.Reorder(new[] { "A" }, new List<string>()));

        [Fact]
        public void Solape_ConArea() =>
            Assert.True(ViewportPlanner.Overlaps(BBox2.FromCorners(0, 0, 2, 2), BBox2.FromCorners(1, 1, 3, 3)));

        [Fact]
        public void Solape_SoloBorde_NoCuenta() =>
            Assert.False(ViewportPlanner.Overlaps(BBox2.FromCorners(0, 0, 2, 2), BBox2.FromCorners(2, 0, 4, 2)));

        [Fact]
        public void Solape_Separadas() =>
            Assert.False(ViewportPlanner.Overlaps(BBox2.FromCorners(0, 0, 1, 1), BBox2.FromCorners(5, 5, 6, 6)));

        [Fact]
        public void Solape_Contenida() =>
            Assert.True(ViewportPlanner.Overlaps(BBox2.FromCorners(0, 0, 10, 10), BBox2.FromCorners(4, 4, 5, 5)));

        private static void AssertBox(BBox2 expected, BBox2 actual)
        {
            Assert.Equal(expected.Xmin, actual.Xmin, 9);
            Assert.Equal(expected.Ymin, actual.Ymin, 9);
            Assert.Equal(expected.Xmax, actual.Xmax, 9);
            Assert.Equal(expected.Ymax, actual.Ymax, 9);
        }
    }
}
