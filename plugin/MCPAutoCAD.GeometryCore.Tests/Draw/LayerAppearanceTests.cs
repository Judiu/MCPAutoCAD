using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>protocol.md §5G.5: normalizar_capas conservar_aspecto.</summary>
    public class LayerAppearanceTests
    {
        private static readonly ByLayerFlags Todo = new ByLayerFlags(true, true, true, true);
        private static readonly ByLayerFlags Nada = new ByLayerFlags(false, false, false, false);

        private static readonly LayerLook Origen = new LayerLook(1, "HIDDEN", 25, 0.5, true);
        private static readonly LayerLook Destino = new LayerLook(7, "Continuous", 50, 0, true);

        [Fact]
        public void TodoByLayerYDistinto_SeFijaElValorDeOrigen()
        {
            AppearanceFix f = LayerAppearance.Compute(Todo, Origen, Destino);
            Assert.Equal(1, f.Color);
            Assert.Equal("HIDDEN", f.TipoLinea);
            Assert.Equal(25, f.Grosor);
            Assert.Equal(0.5, f.Transparencia);
            Assert.False(f.PierdeNoPlot);
        }

        [Fact]
        public void NadaByLayer_NoSeFijaNada()
        {
            AppearanceFix f = LayerAppearance.Compute(Nada, Origen, Destino);
            Assert.Null(f.Color);
            Assert.Null(f.TipoLinea);
            Assert.Null(f.Grosor);
            Assert.Null(f.Transparencia);
            Assert.False(LayerAppearance.Changes(f));
        }

        [Fact]
        public void CapasIguales_SigueByLayer()
        {
            Assert.False(LayerAppearance.Changes(LayerAppearance.Compute(Todo, Origen, Origen)));
        }

        [Fact]
        public void SoloColorByLayer()
        {
            AppearanceFix f = LayerAppearance.Compute(new ByLayerFlags(true, false, false, false), Origen, Destino);
            Assert.Equal(1, f.Color);
            Assert.Null(f.TipoLinea);
            Assert.True(LayerAppearance.Changes(f));
        }

        [Fact]
        public void SoloTipoLineaByLayer()
        {
            AppearanceFix f = LayerAppearance.Compute(new ByLayerFlags(false, true, false, false), Origen, Destino);
            Assert.Equal("HIDDEN", f.TipoLinea);
            Assert.True(LayerAppearance.Changes(f));
        }

        [Fact]
        public void SoloGrosorByLayer()
        {
            AppearanceFix f = LayerAppearance.Compute(new ByLayerFlags(false, false, true, false), Origen, Destino);
            Assert.Equal(25, f.Grosor);
            Assert.True(LayerAppearance.Changes(f));
        }

        [Fact]
        public void SoloTransparenciaByLayer()
        {
            AppearanceFix f = LayerAppearance.Compute(new ByLayerFlags(false, false, false, true), Origen, Destino);
            Assert.Equal(0.5, f.Transparencia);
            Assert.True(LayerAppearance.Changes(f));
        }

        [Fact]
        public void TipoLinea_SinMayusculas()
        {
            AppearanceFix f = LayerAppearance.Compute(Todo, Origen, Origen with { TipoLinea = "hidden" });
            Assert.Null(f.TipoLinea);
        }

        [Fact]
        public void Transparencia_DentroDeTolerancia()
        {
            AppearanceFix f = LayerAppearance.Compute(Todo, Origen, Origen with { Transparencia = 0.5 + 1e-10 });
            Assert.Null(f.Transparencia);
        }

        [Fact]
        public void Transparencia_FueraDeTolerancia()
        {
            AppearanceFix f = LayerAppearance.Compute(Todo, Origen, Origen with { Transparencia = 0.5 + 1e-8 });
            Assert.Equal(0.5, f.Transparencia);
        }

        [Fact]
        public void PierdeNoPlot_OrigenNoImprimeDestinoSi()
        {
            AppearanceFix f = LayerAppearance.Compute(Nada, Origen with { Plot = false }, Destino);
            Assert.True(f.PierdeNoPlot);
            Assert.False(LayerAppearance.Changes(f)); // no se fija por entidad: solo se avisa
        }

        [Fact]
        public void PierdeNoPlot_OrigenImprime()
        {
            Assert.False(LayerAppearance.Compute(Nada, Origen, Destino with { Plot = false }).PierdeNoPlot);
        }

        [Fact]
        public void PierdeNoPlot_NingunaImprime()
        {
            Assert.False(LayerAppearance.Compute(Nada, Origen with { Plot = false }, Destino with { Plot = false }).PierdeNoPlot);
        }

        [Fact]
        public void WouldChangeWarning_Cero_Null()
        {
            Assert.Null(LayerAppearance.WouldChangeWarning(0));
        }

        [Fact]
        public void WouldChangeWarning_Exacta()
        {
            Assert.Equal(
                "12 entidad(es) cambiarían de aspecto al pasar a su capa destino (color, tipo de línea, grosor o transparencia heredados de la capa origen); pase conservar_aspecto: true para fijarlos en cada entidad antes de moverla.",
                LayerAppearance.WouldChangeWarning(12));
        }

        [Fact]
        public void FixedWarning_NadaQueAvisar_Null()
        {
            Assert.Null(LayerAppearance.FixedWarning(0, 0));
        }

        [Fact]
        public void FixedWarning_SinNoPlot()
        {
            Assert.Equal(
                "conservar_aspecto: se fijaron color, tipo de línea, grosor o transparencia en 3 entidad(es) para conservar su aspecto.",
                LayerAppearance.FixedWarning(3, 0));
        }

        [Fact]
        public void FixedWarning_ConNoPlot()
        {
            Assert.Equal(
                "conservar_aspecto: se fijaron color, tipo de línea, grosor o transparencia en 3 entidad(es) para conservar su aspecto; 2 pierden el 'no imprimir' de su capa origen (no se fija por entidad).",
                LayerAppearance.FixedWarning(3, 2));
        }

        [Fact]
        public void FixedWarning_SoloNoPlot()
        {
            Assert.Equal(
                "conservar_aspecto: se fijaron color, tipo de línea, grosor o transparencia en 0 entidad(es) para conservar su aspecto; 2 pierden el 'no imprimir' de su capa origen (no se fija por entidad).",
                LayerAppearance.FixedWarning(0, 2));
        }
    }
}
