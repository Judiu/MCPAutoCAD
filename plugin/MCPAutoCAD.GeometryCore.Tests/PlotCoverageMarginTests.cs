using System;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class PlotCoverageMarginTests
    {
        [Fact]
        public void ConMargenesCeroEquivaleAComputeNormal()
        {
            var window = ViewWindow.FromView(100, 50, 13, 13);
            ImageCoverage a = PlotCoverage.Compute(window, 1006, 1302);
            ImageCoverage b = PlotCoverage.ComputeInPrintable(window, 1006, 1302, 1006, 1302, 0, 0, 0, 0);
            Assert.Equal(a.XMin, b.XMin, 9);
            Assert.Equal(a.XMax, b.XMax, 9);
            Assert.Equal(a.YMin, b.YMin, 9);
            Assert.Equal(a.YMax, b.YMax, 9);
            Assert.Equal(a.UnitsPerPixel, b.UnitsPerPixel, 12);
        }

        [Fact]
        public void MargenesSimetricosNoDesplazanElCentro()
        {
            var window = ViewWindow.FromView(7, -3, 50, 50);
            ImageCoverage c = PlotCoverage.ComputeInPrintable(window, 100, 100, 100, 100, 10, 10, 10, 10);
            Assert.Equal(7, (c.XMin + c.XMax) / 2.0, 9);
            Assert.Equal(-3, (c.YMin + c.YMax) / 2.0, 9);
        }

        [Fact]
        public void MargenAsimetrico_LaZonaSeDesplazaYLaEscalaSigueLaArea()
        {
            // Hoja 100×100, margen izquierdo 10: área imprimible 90×100; ventana 50×50 → escala 1,8 (limita el ancho).
            // La ventana se centra en el área imprimible: la hoja completa va de x=-30,555 a x=25 (en WCS).
            var window = ViewWindow.FromView(0, 0, 50, 50);
            ImageCoverage c = PlotCoverage.ComputeInPrintable(window, 100, 100, 100, 100, 10, 0, 0, 0);
            Assert.Equal(-(55.0 / 1.8), c.XMin, 6);
            Assert.Equal((100.0 - 55.0) / 1.8, c.XMax, 6);
            Assert.Equal(100.0 / 100.0 / 1.8, c.UnitsPerPixel, 9);
        }

        [Fact]
        public void AreaImprimibleNoValidaSeRechaza()
        {
            var window = ViewWindow.FromView(0, 0, 10, 10);
            Assert.Throws<ArgumentException>(() => PlotCoverage.ComputeInPrintable(window, 100, 100, 100, 100, 60, 0, 60, 0));
        }
    }
}
