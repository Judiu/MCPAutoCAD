using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    /// <summary>Fallo 46 (0.10.4): recorte de la hoja del PC3 a la ventana de trazado.</summary>
    public class PlotCoverageCropTests
    {
        [Fact]
        public void PlantaAlargada_RecortaLosMargenesVerticales()
        {
            // Caso del fallo 46: bbox 180×43 pedido 1600×600, hoja real 1697×1131.
            ViewWindow w = PlotCoverage.Window(115, 75, 295, 118, 1600, 600, true);
            ImageCoverage hoja = PlotCoverage.Compute(w, 1697, 1131);

            CropRect? r = PlotCoverage.CropToWindow(hoja, w, 1697, 1131);

            Assert.NotNull(r);
            Assert.Equal(0, r!.X);
            Assert.Equal(1697, r.Width);
            Assert.True(r.Height < 700, "alto recortado " + r.Height);
            Assert.True(r.Y > 200);
            // La ventana trazada queda entera dentro de la zona recortada.
            Assert.True(r.Coverage.XMin <= w.XMin + 1e-9 && r.Coverage.XMax >= w.XMax - 1e-9);
            Assert.True(r.Coverage.YMin <= w.YMin + 1e-9 && r.Coverage.YMax >= w.YMax - 1e-9);
            // Y como mucho un píxel de más por lado.
            Assert.True(w.YMin - r.Coverage.YMin <= hoja.UnitsPerPixel + 1e-9);
            Assert.True(r.Coverage.YMax - w.YMax <= hoja.UnitsPerPixel + 1e-9);
            Assert.Equal(hoja.UnitsPerPixel, r.Coverage.UnitsPerPixel, 12);
        }

        [Fact]
        public void SinAjustar_RecortaAlBboxExacto()
        {
            ViewWindow w = PlotCoverage.Window(0, 0, 100, 10, 1000, 1000, false);
            ImageCoverage hoja = PlotCoverage.Compute(w, 1000, 800);

            CropRect? r = PlotCoverage.CropToWindow(hoja, w, 1000, 800);

            Assert.NotNull(r);
            Assert.Equal(1000, r!.Width);
            Assert.InRange(r.Height, 100, 101);
            Assert.Equal(0, r.Coverage.XMin, 9);
            Assert.Equal(100, r.Coverage.XMax, 9);
        }

        [Fact]
        public void VentanaQueYaOcupaLaHoja_NoRecorta()
        {
            ViewWindow w = ViewWindow.FromBbox(0, 0, 20, 10, 800, 400);
            ImageCoverage hoja = PlotCoverage.Compute(w, 800, 400);
            Assert.Null(PlotCoverage.CropToWindow(hoja, w, 800, 400));
        }

        [Fact]
        public void MargenMenorQueElMinimo_NoRecorta()
        {
            ViewWindow w = ViewWindow.FromBbox(0, 0, 20, 10, 800, 400);
            ImageCoverage hoja = PlotCoverage.Compute(w, 800, 404);   // 2 px por lado
            Assert.Null(PlotCoverage.CropToWindow(hoja, w, 800, 404));
        }

        [Fact]
        public void DatosNoValidos_DevuelveNull()
        {
            ViewWindow w = ViewWindow.FromBbox(0, 0, 20, 10, 800, 400);
            ImageCoverage hoja = PlotCoverage.Compute(w, 800, 600);
            Assert.Null(PlotCoverage.CropToWindow(hoja, w, 0, 600));
            Assert.Null(PlotCoverage.CropToWindow(new ImageCoverage(0, 0, 1, 1, 0), w, 800, 600));
        }

        [Fact]
        public void EscalaDistintaEnXyY_NoRecorta()
        {
            // Auditoría M3: zona cuya escala en Y no coincide con la declarada (márgenes u orientación mal leídos).
            ViewWindow w = ViewWindow.FromBbox(0, 0, 20, 10, 800, 400);
            var anisotropa = new ImageCoverage(-5, -5, 25, 25, 30.0 / 1000.0);   // 30 u en 1000 px de ancho y 600 de alto
            Assert.Null(PlotCoverage.CropToWindow(anisotropa, w, 1000, 600));
        }

        [Fact]
        public void MargenAsimetrico_ElRecorteContieneLaVentana()
        {
            // Hoja 100×200 con margen izquierdo 10 (como PlotCoverageMarginTests): la ventana queda desplazada en la hoja.
            ViewWindow w = ViewWindow.FromView(0, 0, 50, 50);
            ImageCoverage hoja = PlotCoverage.ComputeInPrintable(w, 100, 200, 100, 200, 10, 0, 0, 0);

            CropRect? r = PlotCoverage.CropToWindow(hoja, w, 100, 200);

            Assert.NotNull(r);
            Assert.True(r!.Coverage.XMin <= w.XMin + 1e-9 && r.Coverage.XMax >= w.XMax - 1e-9);
            Assert.True(r.Coverage.YMin <= w.YMin + 1e-9 && r.Coverage.YMax >= w.YMax - 1e-9);
            Assert.True(r.X + r.Width <= 100 && r.Y + r.Height <= 200);
        }

        [Fact]
        public void VentanaParcialmenteFuera_SeLimitaALaHoja()
        {
            ViewWindow trazada = ViewWindow.FromBbox(0, 0, 20, 10, 800, 400);
            ImageCoverage hoja = PlotCoverage.Compute(trazada, 800, 600);
            ViewWindow desplazada = ViewWindow.FromBbox(10, 0, 30, 10, 800, 400);

            CropRect? r = PlotCoverage.CropToWindow(hoja, desplazada, 800, 600);

            Assert.NotNull(r);
            Assert.True(r!.X >= 0 && r.X + r.Width == 800);
        }

        [Fact]
        public void VentanaFueraDeLaHoja_DevuelveNull()
        {
            ViewWindow lejos = ViewWindow.FromBbox(1000, 1000, 1020, 1010, 800, 400);
            ViewWindow w = ViewWindow.FromBbox(0, 0, 20, 10, 800, 400);
            ImageCoverage hoja = PlotCoverage.Compute(w, 800, 600);
            Assert.Null(PlotCoverage.CropToWindow(hoja, lejos, 800, 600));
        }
    }
}
