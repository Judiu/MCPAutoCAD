using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class LineExtendGuardTests
    {
        private const double Tol = 1e-6;

        private static Point3 P(double x, double y) => new Point3(x, y, 0);

        private static (Point3, Point3) S(double x1, double y1, double x2, double y2) => (P(x1, y1), P(x2, y2));

        [Fact]
        public void CasiParalela_LaUnicaInterseccionSeRechazaConMotivo()
        {
            // Frontera con ~0,11° de inclinación: corta y=0 en x=500 (extensión enorme). Con limite_extendido se rechaza.
            var frontera = new List<(Point3, Point3)> { S(0, -1, 1000, 1) };
            var ex = Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true));
            Assert.Equal(LineExtend.NearlyParallel, ex.Message);
        }

        [Fact]
        public void CasiParalela_NoAfectaAlComportamientoSinLimite()
        {
            // Sin limite_extendido el segmento real (x∈[0,1000]) no corta a x=... en 500 porque el corte cae dentro: se acepta.
            var frontera = new List<(Point3, Point3)> { S(0, -1, 1000, 1) };
            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol);
            Assert.True(r.Extendida);
            Assert.Equal(500, r.Nuevo.X, 6);
        }

        [Fact]
        public void CasiParalela_SiHayOtraInterseccionValidaSeUsa()
        {
            var frontera = new List<(Point3, Point3)> { S(0, -1, 1000, 1), S(12, -5, 12, -1) };
            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true);
            Assert.Equal(12, r.Nuevo.X, 6);
        }

        [Fact]
        public void Tope_ExtensionMayorQueElTopeSeRechaza()
        {
            var frontera = new List<(Point3, Point3)> { S(1000, -1, 1000, -0.5) };
            var ex = Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true, maxExtension: 100));
            Assert.StartsWith("limite_extendido: la extensión (", ex.Message);
        }

        [Fact]
        public void Tope_ExtensionDentroDelTopeSeAcepta()
        {
            var frontera = new List<(Point3, Point3)> { S(1000, -1, 1000, -0.5) };
            LineExtend.Result r = LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true, maxExtension: 2000);
            Assert.Equal(1000, r.Nuevo.X, 6);
        }

        [Fact]
        public void Infinitos_SoloLosSegmentosMarcadosSeExtienden()
        {
            // Dos segmentos paralelos al eje, ambos fuera de su rango en y=0. Solo el marcado como infinito cuenta.
            var frontera = new List<(Point3, Point3)> { S(10, -5, 10, -1), S(20, -5, 20, -1) };
            LineExtend.Result onlySecond = LineExtend.Compute(
                P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true, infinitos: new[] { false, true });
            Assert.Equal(20, onlySecond.Nuevo.X, 6);

            LineExtend.Result onlyFirst = LineExtend.Compute(
                P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: true, infinitos: new[] { true, false });
            Assert.Equal(10, onlyFirst.Nuevo.X, 6);
        }

        [Fact]
        public void Infinitos_SinLimiteNingunSegmentoEsInfinito()
        {
            var frontera = new List<(Point3, Point3)> { S(10, -5, 10, -1) };
            Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol, limiteExtendido: false, infinitos: new[] { true }));
        }

        [Fact]
        public void FronteraQuePasaPorElExtremoMovil_MensajeEspecifico()
        {
            var frontera = new List<(Point3, Point3)> { S(5, -5, 5, 5) };
            var ex = Assert.Throws<ArgumentException>(() =>
                LineExtend.Compute(P(0, 0), P(5, 0), LineExtend.Extremo.Fin, frontera, Tol));
            Assert.Equal(LineExtend.TouchesMovingEnd, ex.Message);
        }
    }
}
