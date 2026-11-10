using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Layouts;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Layouts
{
    public class LayoutRulesTests
    {
        [Theory]
        [InlineData("E1")]
        [InlineData("PLIEGO")]
        [InlineData("Planta 2do nivel")]
        [InlineData("E-7_ñ")]
        public void NombreValido(string n) => Assert.Null(LayoutRules.NameProblem(n));

        [Theory]
        [InlineData("")]
        [InlineData(" E1")]
        [InlineData("E1 ")]
        [InlineData("E<1")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("a:b")]
        [InlineData("a;b")]
        [InlineData("a?b")]
        [InlineData("a*b")]
        [InlineData("a|b")]
        [InlineData("a,b")]
        [InlineData("a=b")]
        [InlineData("a`b")]
        [InlineData("a\"b")]
        [InlineData("model")]
        [InlineData("Model")]
        [InlineData("a\nb")]
        [InlineData("a​b")]
        public void NombreInvalido(string n) => Assert.NotNull(LayoutRules.NameProblem(n));

        [Fact]
        public void NombreLargo()
        {
            Assert.Null(LayoutRules.NameProblem(new string('a', 255)));
            Assert.NotNull(LayoutRules.NameProblem(new string('a', 256)));
            Assert.NotNull(LayoutRules.NameProblem(null));
        }

        [Theory]
        [InlineData("1:50", 50)]
        [InlineData("1/100", 100)]
        [InlineData(" 1 : 75 ", 75)]
        [InlineData("1:12.5", 12.5)]
        public void EscalaValida(string s, double n)
        {
            Assert.True(LayoutRules.TryParseScale(s, out double d));
            Assert.Equal(n, d, 9);
        }

        [Theory]
        [InlineData("50")]
        [InlineData("2:1")]
        [InlineData("1:0")]
        [InlineData("1:-5")]
        [InlineData("1:")]
        [InlineData(":50")]
        [InlineData("1:abc")]
        [InlineData("1:1e3")]
        [InlineData("1:200000")]
        [InlineData(null)]
        public void EscalaInvalida(string? s) => Assert.False(LayoutRules.TryParseScale(s, out _));

        [Fact]
        public void Etiqueta()
        {
            Assert.Equal("1:50", LayoutRules.ScaleLabel(50));
            Assert.Equal("1:47.33", LayoutRules.ScaleLabel(47.3333));
        }

        [Fact]
        public void Denominador_PapelMm_ModeloMetros()
        {
            // 1:50 con modelo en m y papel en mm → CustomScale = 20.
            Assert.Equal(20, LayoutRules.CustomScale(50, 1, 1.0), 9);
            Assert.Equal(50, LayoutRules.Denominator(20, 1, 1.0)!.Value, 9);
            // Modelo en cm: 1:50 → 1 cm real = 0,2 mm de papel.
            Assert.Equal(0.2, LayoutRules.CustomScale(50, 1, 0.01), 9);
            Assert.Equal(50, LayoutRules.Denominator(0.2, 1, 0.01)!.Value, 9);
            // Papel en pulgadas.
            Assert.Equal(100, LayoutRules.Denominator(10 / 25.4, 25.4, 1.0)!.Value, 6);
        }

        [Fact]
        public void Denominador_SinUnidades_Null()
        {
            Assert.Null(LayoutRules.Denominator(20, 1, null));
            Assert.Null(LayoutRules.Denominator(0, 1, 1.0));
            Assert.Null(LayoutRules.Denominator(double.NaN, 1, 1.0));
        }

        [Fact]
        public void BordesDeEtiquetaYTolerancia()
        {
            Assert.Equal("1:48", LayoutRules.ScaleLabel(47.999));
            Assert.Equal(50, LayoutRules.FittingStandard(49.75));
            Assert.NotNull(LayoutRules.NameProblem("E	1"));
            Assert.NotNull(LayoutRules.NameProblem("	E1"));
            Assert.NotNull(LayoutRules.NameProblem("MODEL"));
        }

        [Fact]
        public void EscalaDeImpresion()
        {
            Assert.Equal(1.0, LayoutRules.PaperMmPerSpaceUnit(1.0, 1, 1, false)!.Value, 12);
            Assert.Equal(0.5, LayoutRules.PaperMmPerSpaceUnit(1.0, 1, 2, false)!.Value, 12);   // imprime a 1:2
            Assert.Equal(25.4, LayoutRules.PaperMmPerSpaceUnit(25.4, 1, 1, false)!.Value, 12);
            Assert.Null(LayoutRules.PaperMmPerSpaceUnit(1.0, 1, 1, true));                      // ajustada al papel
            Assert.Null(LayoutRules.PaperMmPerSpaceUnit(null, 1, 1, false));                    // píxeles
            Assert.Null(LayoutRules.PaperMmPerSpaceUnit(1.0, 0, 1, false));
            // Viewport a CustomScale 20 en un layout que imprime a 1:2 → en el papel real es 1:100.
            Assert.Equal(100, LayoutRules.Denominator(20, LayoutRules.PaperMmPerSpaceUnit(1.0, 1, 2, false)!.Value, 1.0)!.Value, 9);
        }

        [Fact]
        public void Estandares()
        {
            Assert.Equal(50, LayoutRules.MatchingStandard(50.2));
            Assert.Null(LayoutRules.MatchingStandard(47));
            Assert.Equal(50, LayoutRules.FittingStandard(47));
            Assert.Equal(50, LayoutRules.FittingStandard(50.1));
            Assert.Equal(75, LayoutRules.FittingStandard(51));
            Assert.Null(LayoutRules.FittingStandard(6000));
            Assert.Null(LayoutRules.FittingStandard(0));
            Assert.True(LayoutRules.StandardDenominators.SequenceEqual(LayoutRules.StandardDenominators.OrderBy(x => x)));
        }
    }
}
