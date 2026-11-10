using System;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class MTextBoxTests
    {
        private const double Tol = 1e-9;

        [Theory]
        [InlineData("TL", 0.0, 0.0)]
        [InlineData("MC", 0.5, 0.5)]
        [InlineData("BR", 1.0, 1.0)]
        [InlineData("TR", 1.0, 0.0)]
        [InlineData("BL", 0.0, 1.0)]
        public void Cell_mapea_justificacion(string code, double col, double row)
        {
            Assert.Equal((col, row), MTextBox.Cell(code));
        }

        [Theory]
        [InlineData("BASE_L")]
        [InlineData("XX")]
        [InlineData("")]
        public void Cell_rechaza_codigos_invalidos(string code)
        {
            Assert.Throws<ArgumentException>(() => MTextBox.Cell(code));
        }

        [Fact]
        public void Centro_desde_superior_izquierda_sin_giro()
        {
            // Caja 4 × 2 con la esquina superior izquierda en (10, 20): centro (12, 19).
            (double x, double y) = MTextBox.Center(10, 20, 0, "TL", 4, 2);
            Assert.Equal(12, x, 9);
            Assert.Equal(19, y, 9);
        }

        [Fact]
        public void Centro_desde_inferior_derecha_sin_giro()
        {
            (double x, double y) = MTextBox.Center(10, 20, 0, "BR", 4, 2);
            Assert.Equal(8, x, 9);
            Assert.Equal(21, y, 9);
        }

        [Fact]
        public void Medio_centro_es_el_propio_centro()
        {
            Assert.Equal((5.0, 7.0), MTextBox.Center(5, 7, 1.234, "MC", 9, 3));
        }

        [Fact]
        public void Giro_de_90_grados()
        {
            // Texto girado 90°: el eje x local apunta a +Y y el eje y local a -X.
            (double x, double y) = MTextBox.Center(0, 0, Math.PI / 2, "TL", 4, 2);
            Assert.Equal(1, x, 9);  // (2, -1) girado 90° → (1, 2)
            Assert.Equal(2, y, 9);
        }

        [Fact]
        public void Caso_fallo_30_crece_de_2_a_4_lineas_y_conserva_el_centro()
        {
            // Rótulo TL de ancho 2,95: con 2 líneas mide 0,5 de alto; tras el cambio, 4 líneas = 1,0 de alto.
            (double cx, double cy) = MTextBox.Center(100, 50, 0, "TL", 2.95, 0.5);
            (double lx, double ly) = MTextBox.LocationFor(cx, cy, 0, "TL", 2.95, 1.0);
            Assert.Equal(100, lx, 9);
            Assert.Equal(50.25, ly, 9); // sube la mitad del crecimiento
            (double cx2, double cy2) = MTextBox.Center(lx, ly, 0, "TL", 2.95, 1.0);
            Assert.Equal(cx, cx2, 9);
            Assert.Equal(cy, cy2, 9);
        }

        [Fact]
        public void Cambio_de_justificacion_y_ancho_conserva_el_centro_con_giro()
        {
            double ang = 0.7;
            (double cx, double cy) = MTextBox.Center(3, -4, ang, "BL", 6, 1.5);
            (double lx, double ly) = MTextBox.LocationFor(cx, cy, ang, "TR", 2.5, 3.2);
            (double cx2, double cy2) = MTextBox.Center(lx, ly, ang, "TR", 2.5, 3.2);
            Assert.True(Math.Abs(cx - cx2) < Tol && Math.Abs(cy - cy2) < Tol);
        }

        [Theory]
        [InlineData(-1.0, 1.0)]
        [InlineData(1.0, double.NaN)]
        [InlineData(double.PositiveInfinity, 1.0)]
        public void Rechaza_tamanos_invalidos(double w, double h)
        {
            Assert.Throws<ArgumentException>(() => MTextBox.Center(0, 0, 0, "TL", w, h));
        }
    }
}
