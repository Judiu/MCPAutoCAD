using System;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class PlotCoverageTests
    {
        private const double Eps = 1e-9;

        [Fact]
        public void Compute_VentanaCuadradaEnPapelMasAltoMuestraMargenHorizontal()
        {
            // Ventana 13×13 centrada en (100, 50); papel 1006×1302 px: escala = min(1006/13, 1302/13) = 1006/13.
            var window = ViewWindow.FromView(100, 50, 13, 13);
            ImageCoverage c = PlotCoverage.Compute(window, 1006, 1302);

            Assert.Equal(13.0 / 1006.0, c.UnitsPerPixel, 12);
            Assert.Equal(100 - 6.5, c.XMin, 9);
            Assert.Equal(100 + 6.5, c.XMax, 9);
            double halfH = 1302 * 13.0 / 1006.0 / 2.0;
            Assert.Equal(50 - halfH, c.YMin, 9);
            Assert.Equal(50 + halfH, c.YMax, 9);
            Assert.True(c.YMax - c.YMin > 13.0);
        }

        [Fact]
        public void Compute_ImagenQueEsLaVentanaDaElBboxPedido()
        {
            var window = ViewWindow.FromBbox(0, 0, 20, 10, 800, 400);
            ImageCoverage c = PlotCoverage.Compute(window, 800, 400);
            Assert.Equal(0, c.XMin, 9);
            Assert.Equal(20, c.XMax, 9);
            Assert.Equal(0, c.YMin, 9);
            Assert.Equal(10, c.YMax, 9);
            Assert.Equal(20.0 / 800.0, c.UnitsPerPixel, 12);
        }

        [Fact]
        public void Compute_LaZonaCubreLaVentanaSiempre()
        {
            var window = ViewWindow.FromBbox(-3, 2, 7, 4, 1000, 1000);
            ImageCoverage c = PlotCoverage.Compute(window, 1024, 768);
            Assert.True(c.XMin <= -3 + Eps);
            Assert.True(c.XMax >= 7 - Eps);
            Assert.True(c.YMin <= 2 + Eps);
            Assert.True(c.YMax >= 4 - Eps);
        }

        [Fact]
        public void Compute_RechazaPixelesONVentanaNoValidos()
        {
            var window = ViewWindow.FromView(0, 0, 10, 10);
            Assert.Throws<ArgumentOutOfRangeException>(() => PlotCoverage.Compute(window, 0, 100));
            Assert.Throws<ArgumentException>(() => PlotCoverage.Compute(ViewWindow.FromView(0, 0, 0, 5), 100, 100));
        }

        [Fact]
        public void ToBbox2D_DevuelveLaZonaEnFormatoDeSalida()
        {
            var c = new ImageCoverage(1, 2, 3, 4, 0.5);
            double[][] b = c.ToBbox2D();
            Assert.Equal(new[] { 1.0, 2.0 }, b[0]);
            Assert.Equal(new[] { 3.0, 4.0 }, b[1]);
        }

        [Fact]
        public void MediaRequest_AjustarAspectoUsaLaPeticion()
        {
            Assert.Equal((1000, 1000), PlotCoverage.MediaRequest(0, 0, 20, 5, 1000, 1000, ajustarAspecto: true));
        }

        [Fact]
        public void MediaRequest_SinAjustarAjustaElAspectoDelBbox()
        {
            // bbox 20×5 (4:1) dentro de 1000×1000 → 1000×250.
            Assert.Equal((1000, 250), PlotCoverage.MediaRequest(0, 0, 20, 5, 1000, 1000, ajustarAspecto: false));
            // bbox 5×20 (1:4) → 250×1000.
            Assert.Equal((250, 1000), PlotCoverage.MediaRequest(0, 0, 5, 20, 1000, 1000, ajustarAspecto: false));
            // bbox degenerado: se usa la petición.
            Assert.Equal((800, 600), PlotCoverage.MediaRequest(0, 0, 0, 5, 800, 600, ajustarAspecto: false));
        }

        [Fact]
        public void Window_SinAjustarEsExactamenteElBbox()
        {
            ViewWindow w = PlotCoverage.Window(0, 0, 20, 5, 1000, 1000, ajustarAspecto: false);
            Assert.Equal(0, w.XMin, 9);
            Assert.Equal(20, w.XMax, 9);
            Assert.Equal(0, w.YMin, 9);
            Assert.Equal(5, w.YMax, 9);
        }

        [Fact]
        public void Window_AjustarAmplíaAlAspectoPedido()
        {
            ViewWindow w = PlotCoverage.Window(0, 0, 20, 5, 1000, 1000, ajustarAspecto: true);
            Assert.Equal(20, w.Width, 9);
            Assert.Equal(20, w.Height, 9);
            Assert.True(w.YMin <= 0 && w.YMax >= 5);
        }

        [Fact]
        public void Window_BboxDegeneradoUsaLaAmpliacionNormal()
        {
            ViewWindow w = PlotCoverage.Window(5, 5, 5, 5, 800, 600, ajustarAspecto: false);
            Assert.True(w.Width > 0 && w.Height > 0);
        }
    }
}
