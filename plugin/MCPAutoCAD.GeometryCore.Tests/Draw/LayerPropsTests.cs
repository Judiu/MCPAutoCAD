using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class LayerPropsTests
    {
        private static readonly LayerProps.Values Actual = new LayerProps.Values(LayerProps.FormatAci(151), "Continuous", 25, 0);

        [Fact]
        public void LineWeight_AceptaLosValoresDeAutoCADYRechazaLosDemas()
        {
            Assert.True(LayerProps.IsValidLineWeight(25));
            Assert.False(LayerProps.IsValidLineWeight(-1));
            Assert.False(LayerProps.IsValidLineWeight(-2));
            Assert.True(LayerProps.IsValidLineWeight(-3));
            Assert.True(LayerProps.IsValidLineWeight(211));
            Assert.False(LayerProps.IsValidLineWeight(26));
            Assert.False(LayerProps.IsValidLineWeight(7));
        }

        [Fact]
        public void Transparencia_ConvierteEnPorcentajeConLaConvencionDeNormalizarCapas()
        {
            Assert.Equal(255, LayerProps.AlphaFromPercent(0));
            Assert.Equal(128, LayerProps.AlphaFromPercent(50));
            Assert.Equal(0, LayerProps.PercentFromAlpha(255));
            Assert.Equal(50, LayerProps.PercentFromAlpha(LayerProps.AlphaFromPercent(50)));
        }

        [Fact]
        public void FormatoDeColor()
        {
            Assert.Equal("ACI 7", LayerProps.FormatAci(7));
            Assert.Equal("RGB(1, 2, 3)", LayerProps.FormatRgb(1, 2, 3));
        }

        [Fact]
        public void Diff_SinCamposPedidosNoHayCambios()
        {
            Assert.Empty(LayerProps.Diff(Actual, new LayerProps.Request(null, null, null, null)));
        }

        [Fact]
        public void Diff_CoincidenciaNoGeneraCambios_TipoLineaSinMayusculas()
        {
            var pedido = new LayerProps.Request(LayerProps.FormatAci(151), "continuous", 25, 0);
            Assert.Empty(LayerProps.Diff(Actual, pedido));
        }

        [Fact]
        public void Diff_ListaSoloLoQueCambia_ConAntesYDespues()
        {
            var pedido = new LayerProps.Request(LayerProps.FormatAci(3), null, 35, 20);
            IReadOnlyList<LayerProps.Change> cambios = LayerProps.Diff(Actual, pedido);

            Assert.Equal(3, cambios.Count);
            Assert.Equal("color", cambios[0].Campo);
            Assert.Equal("ACI 151", cambios[0].Antes);
            Assert.Equal("ACI 3", cambios[0].Despues);
            Assert.Equal("grosor", cambios[1].Campo);
            Assert.Equal("25", cambios[1].Antes);
            Assert.Equal("35", cambios[1].Despues);
            Assert.Equal("transparencia", cambios[2].Campo);
            Assert.Equal("0", cambios[2].Antes);
            Assert.Equal("20", cambios[2].Despues);
        }

        [Fact]
        public void Diff_ColorVerdaderoDistintoSeInforma()
        {
            var pedido = new LayerProps.Request(LayerProps.FormatRgb(10, 20, 30), null, null, null);
            IReadOnlyList<LayerProps.Change> cambios = LayerProps.Diff(Actual, pedido);
            Assert.Single(cambios);
            Assert.Equal("RGB(10, 20, 30)", cambios[0].Despues);
        }
    }
}
