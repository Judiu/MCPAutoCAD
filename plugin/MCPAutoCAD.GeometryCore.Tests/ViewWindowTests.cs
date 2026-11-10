using System;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class ViewWindowTests
    {
        private const double Tol = 1e-9;

        private static void AssertContains(ViewWindow w, double xMin, double yMin, double xMax, double yMax)
        {
            Assert.True(w.XMin <= xMin && w.YMin <= yMin && w.XMax >= xMax && w.YMax >= yMax, "la ventana recorta la caja pedida");
        }

        [Fact]
        public void CajaMasAncha_AmpliaElAlto()
        {
            // 20 x 5 con imagen 4:3 → ancho 20, alto 15.
            ViewWindow w = ViewWindow.FromBbox(0, 0, 20, 5, 1024, 768);
            Assert.Equal(10, w.CenterX, Tol);
            Assert.Equal(2.5, w.CenterY, Tol);
            Assert.Equal(20, w.Width, Tol);
            Assert.Equal(15, w.Height, Tol);
            Assert.Equal(-5, w.YMin, Tol);
            Assert.Equal(10, w.YMax, Tol);
            AssertContains(w, 0, 0, 20, 5);
        }

        [Fact]
        public void CajaMasAlta_AmpliaElAncho()
        {
            // 3 x 12 con imagen 4:3 → alto 12, ancho 16.
            ViewWindow w = ViewWindow.FromBbox(10, 10, 13, 22, 800, 600);
            Assert.Equal(11.5, w.CenterX, Tol);
            Assert.Equal(16, w.CenterY, Tol);
            Assert.Equal(16, w.Width, Tol);
            Assert.Equal(12, w.Height, Tol);
            AssertContains(w, 10, 10, 13, 22);
        }

        [Fact]
        public void CajaConMismoAspecto_NoCambia()
        {
            ViewWindow w = ViewWindow.FromBbox(0, 0, 20, 15, 800, 600);
            Assert.Equal(20, w.Width, Tol);
            Assert.Equal(15, w.Height, Tol);
            Assert.Equal(new[] { new[] { 0.0, 0.0 }, new[] { 20.0, 15.0 } }, w.ToBbox2D());
        }

        [Fact]
        public void CajaCuadrada_ImagenCuadrada()
        {
            ViewWindow w = ViewWindow.FromBbox(-5, -5, 5, 5, 512, 512);
            Assert.Equal(0, w.CenterX, Tol);
            Assert.Equal(0, w.CenterY, Tol);
            Assert.Equal(10, w.Width, Tol);
            Assert.Equal(10, w.Height, Tol);
        }

        [Fact]
        public void CajaCuadrada_ImagenApaisada_AmpliaAncho()
        {
            ViewWindow w = ViewWindow.FromBbox(0, 0, 10, 10, 2048, 1024);
            Assert.Equal(20, w.Width, Tol);
            Assert.Equal(10, w.Height, Tol);
            Assert.Equal(-5, w.XMin, Tol);
            Assert.Equal(15, w.XMax, Tol);
        }

        [Fact]
        public void CajaDegenerada_Punto_TieneTamanoMinimo()
        {
            ViewWindow w = ViewWindow.FromBbox(3, 4, 3, 4, 1024, 768);
            Assert.Equal(3, w.CenterX, Tol);
            Assert.Equal(4, w.CenterY, Tol);
            Assert.True(w.Height > 0);
            Assert.True(w.Width > 0);
            Assert.Equal(4e-6, w.Height, 1e-15); // 1e-6 × max(1, |4|)
            Assert.Equal(w.Height * 1024.0 / 768.0, w.Width, 1e-15);
        }

        [Fact]
        public void CajaDegenerada_PuntoEnOrigen_UsaEscalaUno()
        {
            ViewWindow w = ViewWindow.FromBbox(0, 0, 0, 0, 100, 100);
            Assert.Equal(1e-6, w.Height, 1e-15);
            Assert.Equal(1e-6, w.Width, 1e-15);
        }

        [Fact]
        public void CajaDegenerada_SegmentoHorizontal_SoloAmpliaAlto()
        {
            ViewWindow w = ViewWindow.FromBbox(0, 2, 8, 2, 800, 600);
            Assert.Equal(8, w.Width, Tol);
            Assert.Equal(6, w.Height, Tol);
            AssertContains(w, 0, 2, 8, 2);
        }

        [Fact]
        public void CajaDegenerada_SegmentoVertical_SoloAmpliaAncho()
        {
            ViewWindow w = ViewWindow.FromBbox(1, 0, 1, 6, 800, 600);
            Assert.Equal(6, w.Height, Tol);
            Assert.Equal(8, w.Width, Tol);
            AssertContains(w, 1, 0, 1, 6);
        }

        [Fact]
        public void CoordenadasGrandes_NuncaRecorta()
        {
            ViewWindow w = ViewWindow.FromBbox(1000000.1, 2000000.3, 1000123.7, 2000045.9, 1024, 768);
            AssertContains(w, 1000000.1, 2000000.3, 1000123.7, 2000045.9);
            Assert.Equal(1024.0 / 768.0, w.Width / w.Height, 1e-9);
        }

        [Fact]
        public void MinimoMayorQueMaximo_Lanza()
        {
            var ex = Assert.Throws<ArgumentException>(() => ViewWindow.FromBbox(5, 0, 0, 5, 100, 100));
            Assert.Equal("bbox: mínimo mayor que máximo", ex.Message);
            ex = Assert.Throws<ArgumentException>(() => ViewWindow.FromBbox(0, 5, 5, 0, 100, 100));
            Assert.Equal("bbox: mínimo mayor que máximo", ex.Message);
        }

        [Fact]
        public void CoordenadaNoFinita_Lanza()
        {
            Assert.Throws<ArgumentException>(() => ViewWindow.FromBbox(double.NaN, 0, 1, 1, 100, 100));
            Assert.Throws<ArgumentException>(() => ViewWindow.FromBbox(0, 0, double.PositiveInfinity, 1, 100, 100));
        }

        [Fact]
        public void TamanoImagenNoPositivo_Lanza()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ViewWindow.FromBbox(0, 0, 1, 1, 0, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => ViewWindow.FromBbox(0, 0, 1, 1, 100, -1));
        }

        [Fact]
        public void FromView_DevuelveCajaDeLaVista()
        {
            ViewWindow w = ViewWindow.FromView(10, 7.5, 20, 15);
            Assert.Equal(new[] { new[] { 0.0, 0.0 }, new[] { 20.0, 15.0 } }, w.ToBbox2D());
            Assert.Equal(20, w.Width, Tol);
            Assert.Equal(15, w.Height, Tol);
        }

        [Fact]
        public void FromView_ValoresInvalidos_Lanza()
        {
            Assert.Throws<ArgumentException>(() => ViewWindow.FromView(double.NaN, 0, 1, 1));
            Assert.Throws<ArgumentException>(() => ViewWindow.FromView(0, 0, -1, 1));
        }
    }
}
