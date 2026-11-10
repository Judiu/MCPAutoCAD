using System;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>Ventana de modelo de un viewport de papel (protocol.md §5C.5, F3 de 0.3.1), con los viewports reales.</summary>
    public class ViewportWindowTests
    {
        [Fact]
        public void Layout2Viewport2_Real_ContieneLosCuadros()
        {
            BBox2 w = ViewportWindow.FromViewport(3359.3424, 3758.9783, 644.9657, 205.6, 156, 0);
            Assert.Equal(2934.3, w.Xmin, 1);
            Assert.Equal(3784.4, w.Xmax, 1);
            Assert.Equal(3436.5, w.Ymin, 1);
            Assert.Equal(4081.5, w.Ymax, 1);
            Assert.True(w.Contains(3275.0, 3990.0));   // cuadro A
            Assert.True(w.Contains(3358.0, 3988.0));   // cuadro B
            Assert.False(w.Contains(-2150.0, 3830.0)); // restos
        }

        [Fact]
        public void Layout1Viewport2_Real_NoContieneLosCuadros()
        {
            BBox2 w = ViewportWindow.FromViewport(2938.2225, 3774.9610, 367.3573, 205.6, 156, 0);
            Assert.Equal(2696.1, w.Xmin, 1);
            Assert.Equal(3180.3, w.Xmax, 1);
            Assert.Equal(3591.3, w.Ymin, 1);
            Assert.Equal(3958.6, w.Ymax, 1);
            Assert.False(w.Contains(3275.0, 3990.0));
        }

        [Fact]
        public void Layout1Viewport3_Real_CentroEsViewTargetMasViewCenter()
        {
            // ViewCenter está en DCS con origen en ViewTarget: centro de modelo = (2590.96 + 703.4832, 1494.53 + 2500.5022).
            BBox2 w = ViewportWindow.FromViewportTarget(2590.96, 1494.53, 703.4832, 2500.5022, 33.0001, 0.869, 0.66, 0);
            Assert.Equal(3294.4432, 0.5 * (w.Xmin + w.Xmax), 4);
            Assert.Equal(3995.0322, 0.5 * (w.Ymin + w.Ymax), 4);
            Assert.Equal(33.0001, w.Height, 6);
            Assert.Equal(33.0001 * 0.869 / 0.66, w.Width, 6);
            Assert.Equal(3272.7, w.Xmin, 1);
            Assert.Equal(3316.2, w.Xmax, 1);
            Assert.True(w.Contains(3275.0, 3990.0));   // cuadro A
            Assert.False(w.Contains(-2150.0, 3830.0)); // restos
            // Solo con ViewCenter la ventana caería en x≈682…725, lejos de cualquier cuadro.
            BBox2 soloCentro = ViewportWindow.FromViewport(703.4832, 2500.5022, 33.0001, 0.869, 0.66, 0);
            Assert.False(soloCentro.Contains(3275.0, 3990.0));
        }

        [Fact]
        public void ViewTargetCero_CoincideConFromViewport()
        {
            BBox2 a = ViewportWindow.FromViewportTarget(0, 0, 100, 200, 10, 30, 10, 0.3);
            BBox2 b = ViewportWindow.FromViewport(100, 200, 10, 30, 10, 0.3);
            Assert.Equal(b, a);
        }

        [Fact]
        public void ViewTargetConTwist_GiraAlrededorDelCentroDeModelo()
        {
            BBox2 w = ViewportWindow.FromViewportTarget(1000, -500, 100, 200, 10, 30, 10, Math.PI / 2);
            Assert.Equal(1100.0, 0.5 * (w.Xmin + w.Xmax), 9);
            Assert.Equal(-300.0, 0.5 * (w.Ymin + w.Ymax), 9);
            Assert.Equal(10.0, w.Width, 9);
            Assert.Equal(30.0, w.Height, 9);
        }

        [Fact]
        public void ViewTargetNoFinito_Lanza()
        {
            Assert.Throws<ArgumentException>(() => ViewportWindow.FromViewportTarget(double.NaN, 0, 0, 0, 10, 30, 10, 0));
        }

        [Fact]
        public void Twist90_IntercambiaAnchoYAlto()
        {
            BBox2 w0 = ViewportWindow.FromViewport(100, 200, 10, 30, 10, 0);
            BBox2 w90 = ViewportWindow.FromViewport(100, 200, 10, 30, 10, Math.PI / 2);
            Assert.Equal(30.0, w0.Width, 9);
            Assert.Equal(10.0, w0.Height, 9);
            Assert.Equal(10.0, w90.Width, 9);
            Assert.Equal(30.0, w90.Height, 9);
            Assert.Equal(100.0, 0.5 * (w90.Xmin + w90.Xmax), 9);
            Assert.Equal(200.0, 0.5 * (w90.Ymin + w90.Ymax), 9);
        }

        [Fact]
        public void TwistDosPi_EquivaleASinGiro()
        {
            BBox2 w = ViewportWindow.FromViewport(0, 0, 10, 20, 10, 2 * Math.PI);
            Assert.Equal(BBox2.FromCorners(-10, -5, 10, 5), w);
        }

        [Fact]
        public void Twist45_CajaEnvolvente()
        {
            BBox2 w = ViewportWindow.FromViewport(0, 0, 2, 2, 2, Math.PI / 4);
            Assert.Equal(Math.Sqrt(2), w.Xmax, 9);
            Assert.Equal(-Math.Sqrt(2), w.Ymin, 9);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void AltoNoPositivo_ArgumentException(double height)
        {
            Assert.Throws<ArgumentException>(() => ViewportWindow.FromViewport(0, 0, 10, 20, height, 0));
        }

        [Theory]
        // Layout no actual: On se ignora (AutoCAD informa On = false si el layout no se ha activado en la sesión).
        [InlineData(false, false, 2, false, true)]
        [InlineData(false, true, 2, false, true)]
        [InlineData(false, false, -1, false, true)]
        // Layout actual: On se respeta.
        [InlineData(true, false, 2, false, false)]
        [InlineData(true, true, 3, false, true)]
        // Number = 1 (espacio papel) nunca, sea o no el layout actual.
        [InlineData(false, true, 1, false, false)]
        [InlineData(true, true, 1, false, false)]
        // Number sin inicializar y primer viewport del layout → es el de papel.
        [InlineData(false, false, 0, true, false)]
        [InlineData(false, false, -1, true, false)]
        [InlineData(true, true, 0, true, false)]
        // Primer viewport con Number válido distinto de 1 sí cuenta.
        [InlineData(false, false, 2, true, true)]
        public void IncludeViewport_Criterio(bool actual, bool on, int number, bool primero, bool esperado)
        {
            Assert.Equal(esperado, ViewportWindow.IncludeViewport(actual, on, number, primero));
        }

        [Fact]
        public void IncludeViewport_CasoRealLayout2NoActivado_CuadroBEnLayout()
        {
            // Gate en vivo 0.3.1: Layout2 nº2 (Layout1 actual) con On = false → debe contarse y su ventana contiene B.
            Assert.True(ViewportWindow.IncludeViewport(isCurrentLayout: false, on: false, number: 2));
            BBox2 w = ViewportWindow.FromViewport(3359.34, 3758.98, 644.97, 205.6, 156, 0);
            Assert.True(w.Intersects(BBox2.FromCorners(3356, 3987, 3361.2, 3992.1)));
        }

        [Fact]
        public void ValoresNoFinitos_ArgumentException()
        {
            Assert.Throws<ArgumentException>(() => ViewportWindow.FromViewport(double.NaN, 0, 10, 20, 10, 0));
            Assert.Throws<ArgumentException>(() => ViewportWindow.FromViewport(0, 0, double.PositiveInfinity, 20, 10, 0));
            Assert.Throws<ArgumentException>(() => ViewportWindow.FromViewport(0, 0, 10, 20, 10, double.NaN));
        }
    }
}
