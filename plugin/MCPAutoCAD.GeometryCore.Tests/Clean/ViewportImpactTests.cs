using System;
using MCPAutoCAD.GeometryCore.Clean;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Clean
{
    /// <summary>Advertencia de viewports de borrar_fuera_de (protocol.md §5F.1) con los viewports reales del plano madre (§5F.7).</summary>
    public class ViewportImpactTests
    {
        private static readonly BBox2 Layout2Vp2 = BBox2.FromCorners(2934.33, 3436.50, 3784.36, 4081.46);
        private static readonly BBox2 Layout1Vp3 = BBox2.FromCorners(3272.72, 3978.53, 3316.17, 4011.53);
        private static readonly BBox2 Layout1Vp2 = BBox2.FromCorners(2696.14, 3591.28, 3180.30, 3958.64);

        [Fact]
        public void Real_Layout2_1789D_Parcial_TextoLiteral()
        {
            string estado = ViewportImpact.Classify(4044, 4701);
            Assert.Equal(ViewportImpact.EstadoParcial, estado);
            Assert.Equal("parcial", estado);
            Assert.Equal(
                "El viewport 1789D del layout 'Layout2' mira la ventana de modelo [[2934.33, 3436.5], [3784.36, 4081.46]] que quedará parcialmente borrada: se borran 4044 entidades que la intersecan y se conservan 4701.",
                ViewportImpact.Warning("Layout2", "1789D", estado, Layout2Vp2, 4044, 4701));
        }

        [Fact]
        public void Real_Layout1_3B5B9_SinCambio_SinAdvertencia()
        {
            string estado = ViewportImpact.Classify(0, 4701);
            Assert.Equal("sin_cambio", estado);
            Assert.Null(ViewportImpact.Warning("Layout1", "3B5B9", estado, Layout1Vp3, 0, 4701));
        }

        [Fact]
        public void Real_Layout1_12D44_ZonaVacia_SinCambio()
        {
            string estado = ViewportImpact.Classify(0, 0);
            Assert.Equal(ViewportImpact.EstadoSinCambio, estado);
            Assert.Null(ViewportImpact.Warning("Layout1", "12D44", estado, Layout1Vp2, 0, 0));
        }

        [Fact]
        public void Vacia_TextoLiteral()
        {
            string estado = ViewportImpact.Classify(10, 0);
            Assert.Equal("vacia", estado);
            Assert.Equal(
                "El viewport 12D44 del layout 'Layout1' mira la ventana de modelo [[2696.14, 3591.28], [3180.3, 3958.64]] que quedará vacía: se borran 10 entidades que la intersecan.",
                ViewportImpact.Warning("Layout1", "12D44", estado, Layout1Vp2, 10, 0));
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(0, -1)]
        public void Classify_Negativos_ArgumentException(int b, int c)
        {
            var ex = Assert.Throws<ArgumentException>(() => ViewportImpact.Classify(b, c));
            Assert.Equal("viewport: conteos negativos", ex.Message);
        }

        [Fact]
        public void Warning_EstadoDesconocido_ArgumentException()
        {
            Assert.Throws<ArgumentException>(() => ViewportImpact.Warning("Layout1", "1", "otro", Layout1Vp2, 1, 1));
        }

        [Theory]
        [InlineData(3436.5, "3436.5")]
        [InlineData(4081.46, "4081.46")]
        [InlineData(3180.304, "3180.3")]
        [InlineData(3272.7249, "3272.72")]
        [InlineData(-0.851, "-0.85")]
        [InlineData(4000.0, "4000")]
        public void Coord_HastaDosDecimalesInvariant(double v, string esperado)
        {
            Assert.Equal(esperado, ViewportImpact.Coord(v));
        }
    }
}
