using System;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class DimTextFillRulesTests
    {
        [Theory]
        [InlineData("ninguno", DimTextFillRules.ModeNone, "ninguno")]
        [InlineData(" Ninguno ", DimTextFillRules.ModeNone, "ninguno")]
        [InlineData("fondo_dibujo", DimTextFillRules.ModeBackground, "fondo_dibujo")]
        [InlineData("FONDO_DIBUJO", DimTextFillRules.ModeBackground, "fondo_dibujo")]
        public void TryParseWord_Validos(string texto, int modeEsperado, string labelEsperada)
        {
            Assert.True(DimTextFillRules.TryParseWord(texto, out DimTextFillSpec? spec));
            Assert.NotNull(spec);
            Assert.Equal(modeEsperado, spec!.Mode);
            Assert.Equal(labelEsperada, spec.Label);
            Assert.Null(spec.Aci);
            Assert.Null(spec.Rgb);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("rojo")]
        [InlineData("porcapa")]
        public void TryParseWord_Invalidos(string? texto)
        {
            Assert.False(DimTextFillRules.TryParseWord(texto, out DimTextFillSpec? spec));
            Assert.Null(spec);
        }

        [Theory]
        [InlineData(1, "ACI 1")]
        [InlineData(255, "ACI 255")]
        public void FromAci_LimitesValidos(int aci, string label)
        {
            DimTextFillSpec spec = DimTextFillSpec.FromAci(aci);
            Assert.Equal(DimTextFillRules.ModeColor, spec.Mode);
            Assert.Equal(aci, spec.Aci);
            Assert.Null(spec.Rgb);
            Assert.Equal(label, spec.Label);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(256)]
        public void FromAci_FueraDeRango_Lanza(int aci)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => DimTextFillSpec.FromAci(aci));
        }

        [Fact]
        public void FromRgb_ValidosYLimites()
        {
            DimTextFillSpec zero = DimTextFillSpec.FromRgb(0, 128, 255);
            Assert.Equal(DimTextFillRules.ModeColor, zero.Mode);
            Assert.Null(zero.Aci);
            Assert.Equal((0, 128, 255), zero.Rgb);
            Assert.Equal("RGB(0,128,255)", zero.Label);

            Assert.Throws<ArgumentOutOfRangeException>(() => DimTextFillSpec.FromRgb(-1, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => DimTextFillSpec.FromRgb(0, 256, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => DimTextFillSpec.FromRgb(0, 0, 256));
        }

        [Fact]
        public void Describe_TodosLosModos()
        {
            Assert.Equal("ninguno", DimTextFillRules.Describe(0, true, true, 1, 0, 0, 0));
            Assert.Equal("fondo_dibujo", DimTextFillRules.Describe(1, true, true, 1, 0, 0, 0));
            Assert.Equal("ACI 7", DimTextFillRules.Describe(2, true, true, 7, 0, 0, 0));
            Assert.Equal("RGB(10,20,30)", DimTextFillRules.Describe(2, true, false, 0, 10, 20, 30));
            Assert.Equal("color", DimTextFillRules.Describe(2, false, false, 0, 0, 0, 0));
            Assert.Equal("ninguno", DimTextFillRules.Describe(99, false, false, 0, 0, 0, 0));
        }
    }
}
