using System;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class ZoomFitTests
    {
        [Fact]
        public void Margen_SeAñadeACadaLadoComoFraccionDelLadoMayor()
        {
            // Caja 10 × 4, aspecto 2:1 → ancho 10, alto 4; margen 0.1 → pad 1 por lado → 12 × 6 → aspecto 2:1 cumplido.
            ViewWindow w = ZoomFit.Fit(0, 0, 10, 4, 0.1, 2.0);

            Assert.Equal(5.0, w.CenterX, 9);
            Assert.Equal(2.0, w.CenterY, 9);
            Assert.Equal(12.0, w.Width, 9);
            Assert.Equal(6.0, w.Height, 9);
        }

        [Fact]
        public void AspectoMasAncho_SeAgrandaElAlto()
        {
            // Caja cuadrada 10 × 10 en una vista 2:1: el alto se mantiene (10) y el ancho crece a 20.
            ViewWindow w = ZoomFit.Fit(0, 0, 10, 10, 0, 2.0);

            Assert.Equal(20.0, w.Width, 9);
            Assert.Equal(10.0, w.Height, 9);
        }

        [Fact]
        public void AspectoMasAlto_SeAgrandaElAlto()
        {
            // Caja cuadrada 10 × 10 en una vista 1:2 (ancho:alto = 0.5): el ancho se mantiene (10) y el alto crece a 20.
            ViewWindow w = ZoomFit.Fit(0, 0, 10, 10, 0, 0.5);

            Assert.Equal(10.0, w.Width, 9);
            Assert.Equal(20.0, w.Height, 9);
        }

        [Fact]
        public void VentanaContieneIntegraLaCajaPedida()
        {
            ViewWindow w = ZoomFit.Fit(-3, 7, 12, 9, 0.05, 1.6);

            Assert.True(w.XMin <= -3 && w.XMax >= 12);
            Assert.True(w.YMin <= 7 && w.YMax >= 9);
        }

        [Fact]
        public void Punto_UsaUnLadoDeReferenciaYNuncaTamanoCero()
        {
            ViewWindow w = ZoomFit.Fit(5, 5, 5, 5, 0, 1.0);

            Assert.True(w.Width > 0 && w.Height > 0);
            Assert.Equal(5.0, w.CenterX, 9);
            Assert.Equal(5.0, w.CenterY, 9);
        }

        [Fact]
        public void MargenCero_NoRecortaLaCaja()
        {
            ViewWindow w = ZoomFit.Fit(0, 0, 10, 2, 0, 1.0);

            Assert.Equal(10.0, w.Width, 9);
            Assert.Equal(10.0, w.Height, 9);
        }

        [Theory]
        [InlineData(double.NaN, 0, 1, 1, 0.1, 1.0)]
        [InlineData(0, 0, 1, 1, -0.1, 1.0)]
        [InlineData(0, 0, 1, 1, 10.5, 1.0)]
        [InlineData(0, 0, 1, 1, 0.1, 0.0)]
        [InlineData(5, 0, 1, 1, 0.1, 1.0)]
        public void EntradasNoValidas_Lanzan(double x0, double y0, double x1, double y1, double margen, double aspecto)
        {
            Assert.ThrowsAny<ArgumentException>(() => ZoomFit.Fit(x0, y0, x1, y1, margen, aspecto));
        }
    }
}
