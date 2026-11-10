using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class LineExtendTests
    {
        private const double Tol = 1e-6;

        private static Point3 P(double x, double y) => new Point3(x, y, 0);

        private static (Point3, Point3) S(double x1, double y1, double x2, double y2) => (P(x1, y1), P(x2, y2));

        [Fact]
        public void ExtremoFin_SeExtiendeHastaLaPareddeMasCercana()
        {
            var frontera = new List<(Point3, Point3)> { S(10, -5, 10, 5), S(20, -5, 20, 5) };
            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol);

            Assert.True(r.Extendida);
            Assert.Equal(P(5, 0), r.Anterior);
            Assert.Equal(P(10, 0), r.Nuevo);
        }

        [Fact]
        public void ExtremoInicio_SeExtiendeHaciaAtras()
        {
            var frontera = new List<(Point3, Point3)> { S(0, -5, 0, 5) };
            LineExtend.Result r = LineExtend.Compute(P(5, 0), P(10, 0), LineExtend.Extremo.Inicio, frontera, Tol);

            Assert.True(r.Extendida);
            Assert.Equal(P(5, 0), r.Anterior);
            Assert.Equal(P(0, 0), r.Nuevo);
        }

        [Fact]
        public void FronteraEnLaMismaDireccionDelOtroExtremo_SeRecorta()
        {
            // Extremo fin en x=10; la pared está en x=5 (por dentro). Extender es imposible: recorte al punto más cercano.
            var frontera = new List<(Point3, Point3)> { S(5, -5, 5, 5) };
            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, frontera, Tol);

            Assert.False(r.Extendida);
            Assert.Equal(P(5, 0), r.Nuevo);
        }

        [Fact]
        public void FronteraDetrasDelOtroExtremo_NoInvierteLaLinea()
        {
            // Extremo fin en x=10, otro extremo en x=0; la única pared está en x=-5 (detrás del otro extremo).
            // Moverlo ahí invertiría la línea: se rechaza.
            var frontera = new List<(Point3, Point3)> { S(-5, -5, -5, 5) };
            var ex = Assert.Throws<ArgumentException>(
                () => LineExtend.Compute(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, frontera, Tol));
            Assert.Equal(LineExtend.NoIntersection, ex.Message);
        }

        [Fact]
        public void HayVariasIntersecciones_GanaLaMasCercanaHaciaFuera()
        {
            var frontera = new List<(Point3, Point3)> { S(30, -5, 30, 5), S(12, -5, 12, 5), S(4, -5, 4, 5) };
            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, frontera, Tol);

            Assert.True(r.Extendida);
            Assert.Equal(P(12, 0), r.Nuevo);
        }

        [Fact]
        public void InterseccionEnElExtremoMismo_NoCuenta()
        {
            // Un segmento que toca justo el extremo no provoca movimiento; sin otra intersección, error.
            var frontera = new List<(Point3, Point3)> { S(10, -5, 10, 5) };
            Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, frontera, Tol));
        }

        [Fact]
        public void FronteraParalela_NoIntersecta()
        {
            var frontera = new List<(Point3, Point3)> { S(0, 3, 20, 3) };
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol));

            Assert.Equal(LineExtend.NoIntersection, ex.Message);
        }

        [Fact]
        public void CorteFueraDelSegmentoDeFrontera_NoCuenta()
        {
            // La recta corta a la frontera en y=0 pero el segmento está en y in [1, 5].
            var frontera = new List<(Point3, Point3)> { S(10, 1, 10, 5) };
            Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol));
        }

        [Fact]
        public void CorteEnElExtremoDelSegmentoDeFrontera_Cuenta()
        {
            // El corte cae exactamente en el extremo del segmento de frontera (s = 1).
            var frontera = new List<(Point3, Point3)> { S(10, 0, 10, 5) };
            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol);

            Assert.Equal(P(10, 0), r.Nuevo);
        }

        [Fact]
        public void FronteraEnDiagonal_CalculaElPuntoExacto()
        {
            var frontera = new List<(Point3, Point3)> { S(8, 0, 0, 8) };   // corta a y = x en (4, 4)
            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(2, 2), LineExtend.Extremo.Fin, frontera, Tol);

            Assert.True(r.Extendida);
            Assert.Equal(4.0, r.Nuevo.X, 9);
            Assert.Equal(4.0, r.Nuevo.Y, 9);
        }

        [Fact]
        public void LineaDeLongitudCero_Lanza()
        {
            var frontera = new List<(Point3, Point3)> { S(10, -5, 10, 5) };
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(1, 1), P(1, 1), LineExtend.Extremo.Fin, frontera, Tol));

            Assert.Equal(LineExtend.ZeroLengthLine, ex.Message);
        }

        [Fact]
        public void FronteraVacia_Lanza()
        {
            Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, new List<(Point3, Point3)>(), Tol));
        }

        [Fact]
        public void Interseccion3D_UsaLaRectaEnElEspacio()
        {
            // Línea vertical en z, pared horizontal en z=8 que la corta: el extremo fin sube hasta z=8.
            var a = new Point3(0, 0, 0);
            var b = new Point3(0, 0, 5);
            var frontera = new List<(Point3, Point3)> { (new Point3(-2, 0, 8), new Point3(2, 0, 8)) };
            LineExtend.Result r = LineExtend.Compute(a, b, LineExtend.Extremo.Fin, frontera, Tol);

            Assert.True(r.Extendida);
            Assert.Equal(8.0, r.Nuevo.Z, 9);
        }
    }
}
