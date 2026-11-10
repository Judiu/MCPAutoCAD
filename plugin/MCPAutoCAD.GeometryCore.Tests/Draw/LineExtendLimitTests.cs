using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class LineExtendLimitTests
    {
        private const double Tol = 1e-6;

        private static Point3 P(double x, double y) => new Point3(x, y, 0);

        private static (Point3, Point3) S(double x1, double y1, double x2, double y2) => (P(x1, y1), P(x2, y2));

        [Fact]
        public void LimiteExtendido_CortaLaRectaInfinitaAunqueElSegmentoNoLlegue()
        {
            // El límite (cara de muro) está en x=10 pero solo ocupa y∈[-5,-1]: no toca la línea en y=0.
            // Con el segmento real no hay intersección; con limite_extendido la recta del límite sí corta.
            var frontera = new List<(Point3, Point3)> { S(10, -5, 10, -1) };

            Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: false));

            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true);
            Assert.True(r.Extendida);
            Assert.Equal(P(10, 0), r.Nuevo);
        }

        [Fact]
        public void LimiteExtendido_SinParametroEquivaleAlComportamientoAnterior()
        {
            var frontera = new List<(Point3, Point3)> { S(10, -5, 10, 5), S(20, -5, 20, 5) };
            LineExtend.Result a = LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol);
            LineExtend.Result b = LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: false);
            Assert.Equal(a.Nuevo, b.Nuevo);
            Assert.Equal(a.Extendida, b.Extendida);
        }

        [Fact]
        public void LimiteExtendido_PrefiereLaInterseccionMasCercanaHaciaFuera()
        {
            var frontera = new List<(Point3, Point3)> { S(30, -5, 30, -1), S(12, -5, 12, -1) };
            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true);
            Assert.Equal(P(12, 0), r.Nuevo);
        }

        [Fact]
        public void LimiteExtendido_ParaleloSigueSinInterseccion()
        {
            // Un límite paralelo a la línea nunca la corta, aunque sea infinito.
            var frontera = new List<(Point3, Point3)> { S(-100, 3, 100, 3) };
            var ex = Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true));
            Assert.Equal(LineExtend.NoIntersection, ex.Message);
        }

        [Fact]
        public void LimiteExtendido_NoPermiteInvertirLaLinea()
        {
            // La recta del límite corta la línea detrás del otro extremo: se rechaza igual que sin limite_extendido.
            var frontera = new List<(Point3, Point3)> { S(-5, -5, -5, -1) };
            Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true));
        }

        [Fact]
        public void LimiteExtendido_RectaQueSeCruzaEnElEspacioNoCuenta()
        {
            // Un límite en z=1 no corta la línea en z=0 aunque su recta infinita pase por (10, 0) en XY.
            var frontera = new List<(Point3, Point3)>
            {
                (new Point3(10, -5, 1), new Point3(10, -1, 1)),
            };
            Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true));
        }

        [Fact]
        public void HastaPunto_ProyectaElExtremoSobreLaPerpendicularDelPunto()
        {
            // Línea de (0,0) a (5,0). Punto (12, 3): su perpendicular a la línea corta el eje en (12, 0).
            LineExtend.Result r = LineExtend.ComputeToPoint(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, P(12, 3), Tol);
            Assert.Equal(P(5, 0), r.Anterior);
            Assert.Equal(P(12, 0), r.Nuevo);
            Assert.True(r.Extendida);
        }

        [Fact]
        public void HastaPunto_RecortaCuandoLaProyeccionCaeAntesDelExtremo()
        {
            LineExtend.Result r = LineExtend.ComputeToPoint(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, P(6, -2), Tol);
            Assert.Equal(P(6, 0), r.Nuevo);
            Assert.False(r.Extendida);
        }

        [Fact]
        public void HastaPunto_ColinealDevuelveElPropioPunto()
        {
            LineExtend.Result r = LineExtend.ComputeToPoint(P(0, 0), P(10, 0), LineExtend.Extremo.Inicio, P(-4, 0), Tol);
            Assert.Equal(P(-4, 0), r.Nuevo);
            Assert.True(r.Extendida);
        }

        [Fact]
        public void HastaPunto_ExtremoInicio_ProyectaHaciaAtras()
        {
            LineExtend.Result r = LineExtend.ComputeToPoint(P(5, 0), P(10, 0), LineExtend.Extremo.Inicio, P(2, 7), Tol);
            Assert.Equal(P(2, 0), r.Nuevo);
            Assert.True(r.Extendida);
        }

        [Fact]
        public void HastaPunto_ProyeccionAlOtroLadoDelExtremoFijoSeRechaza()
        {
            // Línea (0,0)-(10,0), se mueve el fin; la proyección cae en x=-3, detrás del inicio fijo.
            var ex = Assert.Throws<ArgumentException>(() =>
                LineExtend.ComputeToPoint(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, P(-3, 4), Tol));
            Assert.Equal(LineExtend.InvertedResult, ex.Message);
        }

        [Fact]
        public void HastaPunto_ProyeccionSobreElOtroExtremoEsLongitudCero()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                LineExtend.ComputeToPoint(P(0, 0), P(10, 0), LineExtend.Extremo.Fin, P(0, 4), Tol));
            Assert.Equal(LineExtend.ZeroLengthResult, ex.Message);
        }

        [Fact]
        public void HastaPunto_LineaDeLongitudCeroYPuntoNoFinitoSeRechazan()
        {
            Assert.Throws<ArgumentException>(() => LineExtend.ComputeToPoint(P(1, 1), P(1, 1), LineExtend.Extremo.Fin, P(2, 2), Tol));
            Assert.Throws<ArgumentException>(() => LineExtend.ComputeToPoint(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, new Point3(double.NaN, 0, 0), Tol));
        }
    }
}
