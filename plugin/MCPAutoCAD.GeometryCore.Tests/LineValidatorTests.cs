using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class LineValidatorTests
    {
        private static (Point3, Point3) L(double x1, double y1, double x2, double y2) =>
            (new Point3(x1, y1, 0), new Point3(x2, y2, 0));

        [Fact]
        public void ListaVacia_EsValida()
        {
            Assert.Empty(LineValidator.ValidateLines(new List<(Point3, Point3)>()));
        }

        [Fact]
        public void LineasValidas_SinErrores()
        {
            var lines = new[] { L(0, 0, 5, 0), L(5, 0, 5, 3), L(5, 3, 0, 0) };
            Assert.Empty(LineValidator.ValidateLines(lines));
        }

        [Fact]
        public void LongitudCero_EnIndiceCorrecto()
        {
            var lines = new[] { L(0, 0, 5, 0), L(5, 0, 5, 0), L(5, 3, 0, 0) };
            IReadOnlyList<string> errors = LineValidator.ValidateLines(lines);
            Assert.Equal(new[] { "lineas[1]: longitud cero" }, errors);
        }

        [Fact]
        public void VariasLineasInvalidas_TodasReportadasEnOrden()
        {
            var lines = new[] { L(1, 1, 1, 1), L(0, 0, 1, 0), L(2, 2, 2, 2) };
            Assert.Equal(
                new[] { "lineas[0]: longitud cero", "lineas[2]: longitud cero" },
                LineValidator.ValidateLines(lines));
        }

        [Fact]
        public void Tolerancia_LongitudIgualALaToleranciaEsCero()
        {
            var lines = new[] { L(0, 0, 1e-9, 0) };
            Assert.Equal(new[] { "lineas[0]: longitud cero" }, LineValidator.ValidateLines(lines));
        }

        [Fact]
        public void Tolerancia_LongitudMayorQueLaToleranciaEsValida()
        {
            var lines = new[] { L(0, 0, 1e-8, 0) };
            Assert.Empty(LineValidator.ValidateLines(lines));
        }

        [Fact]
        public void Tolerancia_Personalizada()
        {
            var lines = new[] { L(0, 0, 0.001, 0) };
            Assert.Equal(new[] { "lineas[0]: longitud cero" }, LineValidator.ValidateLines(lines, 0.01));
            Assert.Empty(LineValidator.ValidateLines(lines, 0.0001));
        }

        [Fact]
        public void LongitudCero_Usa3D()
        {
            var lines = new[] { (new Point3(0, 0, 0), new Point3(0, 0, 2)) };
            Assert.Empty(LineValidator.ValidateLines(lines));
        }

        [Fact]
        public void CoordenadaNoFinita_Reportada()
        {
            var lines = new[] { L(0, 0, 1, 0), (new Point3(double.NaN, 0, 0), new Point3(1, 0, 0)) };
            Assert.Equal(new[] { "lineas[1]: coordenada no finita" }, LineValidator.ValidateLines(lines));
        }

        [Fact]
        public void ToleranciaNegativa_Rechazada()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LineValidator.ValidateLines(new[] { L(0, 0, 1, 0) }, -1));
        }
    }
}
