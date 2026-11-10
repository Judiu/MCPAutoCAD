using System;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>Reglas puras de color, grosor, transparencia y escala de tipo de línea (cambiar_propiedades y color de dibujo).</summary>
    public class EntityPropertyRulesTests
    {
        [Fact]
        public void Aci_0_EsPorBloque_256_EsPorCapa_Y_LosDemasSonAci()
        {
            Assert.Equal(ColorKind.PorBloque, ColorRules.FromAci(0).Kind);
            Assert.Equal(ColorKind.PorCapa, ColorRules.FromAci(256).Kind);
            ColorSpec nueve = ColorRules.FromAci(9);
            Assert.Equal(ColorKind.Aci, nueve.Kind);
            Assert.Equal(9, nueve.Aci);
            Assert.Equal(ColorKind.Aci, ColorRules.FromAci(255).Kind);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(257)]
        public void Aci_FueraDeRango_Lanza(int aci)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ColorRules.FromAci(aci));
        }

        [Theory]
        [InlineData(9.5)]
        [InlineData(double.NaN)]
        [InlineData(-0.0001)]
        public void IsAci_RechazaNoEnterosYFuera(double v)
        {
            Assert.False(ColorRules.IsAci(v));
        }

        [Fact]
        public void IsAci_AceptaLosLimites()
        {
            Assert.True(ColorRules.IsAci(0));
            Assert.True(ColorRules.IsAci(256));
        }

        [Fact]
        public void Palabras_PorCapaYPorBloque_SinDistinguirMayusculas()
        {
            Assert.Equal(ColorKind.PorCapa, ColorRules.FromWord("PorCapa")!.Value.Kind);
            Assert.Equal(ColorKind.PorBloque, ColorRules.FromWord(" porbloque ")!.Value.Kind);
            Assert.Null(ColorRules.FromWord("rojo"));
        }

        [Fact]
        public void Rgb_RangoYDescripcion()
        {
            ColorSpec c = ColorRules.FromRgb(255, 0, 12);
            Assert.Equal(ColorKind.Rgb, c.Kind);
            Assert.Equal("RGB(255,0,12)", ColorRules.Describe(c));
            Assert.Throws<ArgumentOutOfRangeException>(() => ColorRules.FromRgb(256, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ColorRules.FromRgb(0, -1, 0));
        }

        [Fact]
        public void Describe_Formas()
        {
            Assert.Equal("porcapa", ColorRules.Describe(ColorRules.FromAci(256)));
            Assert.Equal("porbloque", ColorRules.Describe(ColorRules.FromAci(0)));
            Assert.Equal("ACI 9", ColorRules.Describe(ColorRules.FromAci(9)));
        }

        [Theory]
        [InlineData(0.25, 25)]
        [InlineData(0.05, 5)]
        [InlineData(2.11, 211)]
        [InlineData(0.0, 0)]
        public void Grosor_ValoresValidos_DevuelveCentesimas(double mm, int esperado)
        {
            Assert.True(EntityPropertyRules.TryLineWeightHundredths(mm, out int h));
            Assert.Equal(esperado, h);
        }

        [Theory]
        [InlineData(0.26)]
        [InlineData(0.251)]
        [InlineData(-0.25)]
        [InlineData(3.0)]
        [InlineData(double.NaN)]
        public void Grosor_ValoresNoValidos_Rechazados(double mm)
        {
            Assert.False(EntityPropertyRules.TryLineWeightHundredths(mm, out _));
        }

        [Fact]
        public void Grosor_MensajeCitaLosValores()
        {
            string m = EntityPropertyRules.BadLineWeight(0.26);
            Assert.StartsWith("grosor: 0.26 no es un grosor de línea válido", m);
            Assert.Contains("0.25", m);
        }

        [Fact]
        public void Transparencia_LimitesYConversion()
        {
            Assert.True(EntityPropertyRules.IsTransparencyPercent(0));
            Assert.True(EntityPropertyRules.IsTransparencyPercent(90));
            Assert.False(EntityPropertyRules.IsTransparencyPercent(90.5));
            Assert.False(EntityPropertyRules.IsTransparencyPercent(-1));
            Assert.Equal((byte)255, EntityPropertyRules.AlphaFromPercent(0));
            Assert.Equal((byte)128, EntityPropertyRules.AlphaFromPercent(50));
            Assert.Equal(90, EntityPropertyRules.PercentFromAlpha(EntityPropertyRules.AlphaFromPercent(90)));
            Assert.Equal(50, EntityPropertyRules.PercentFromAlpha(EntityPropertyRules.AlphaFromPercent(50)));
        }

        [Fact]
        public void Transparencia_FueraDeRango_Lanza()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => EntityPropertyRules.AlphaFromPercent(91));
        }

        [Theory]
        [InlineData(1, "acadiso.lin")]
        [InlineData(0, "acad.lin")]
        public void FicheroDeTiposSegunMeasurement(int measurement, string esperado)
        {
            Assert.Equal(esperado, EntityPropertyRules.LinetypeFileFor(measurement));
        }

        [Theory]
        [InlineData("porcapa", true)]
        [InlineData("PorBloque", true)]
        [InlineData("defecto", true)]
        [InlineData("0.25", false)]
        public void PalabrasDeGrosor(string texto, bool esperado)
        {
            Assert.Equal(esperado, EntityPropertyRules.IsLineWeightWord(texto, out _));
        }

        [Theory]
        [InlineData(1.0, true)]
        [InlineData(0.0, false)]
        [InlineData(-2.0, false)]
        [InlineData(double.PositiveInfinity, false)]
        public void EscalaDeTipoDeLinea(double v, bool esperado)
        {
            Assert.Equal(esperado, EntityPropertyRules.IsLinetypeScale(v));
        }

        [Theory]
        [InlineData("DASHED")]
        [InlineData("ACAD_ISO02W100")]
        [InlineData("linea 2")]
        public void NombreDeTipoValido_SinRechazo(string nombre)
        {
            Assert.Null(EntityPropertyRules.LinetypeNameError(nombre));
        }

        [Theory]
        [InlineData("DASH*")]
        [InlineData("A?B")]
        [InlineData("X,Y")]
        [InlineData("[ABC]")]
        [InlineData("A|B")]
        [InlineData("~T")]
        [InlineData("   ")]
        public void NombreDeTipoConComodinesORelleno_Rechazado(string nombre)
        {
            Assert.NotNull(EntityPropertyRules.LinetypeNameError(nombre));
        }

        [Fact]
        public void LimiteDeHandlesYDeTransparencia()
        {
            Assert.Equal(500, EntityPropertyRules.MaxHandles);
            Assert.Equal(90.0, EntityPropertyRules.MaxTransparencyPercent);
        }
    }
}
