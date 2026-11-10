using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class LineMergeTests
    {
        private const double Tol = 1e-6;

        private static (Point3, Point3) L(double x1, double y1, double x2, double y2) =>
            (new Point3(x1, y1, 0), new Point3(x2, y2, 0));

        [Fact]
        public void SolapadasEnLaMismaRecta_DanLosExtremosDelConjunto()
        {
            var segs = new[] { L(0, 0, 6, 0), L(4, 0, 10, 0) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, Tol);

            Assert.True(r.Ok);
            Assert.Equal(new Point3(0, 0, 0), r.Inicio);
            Assert.Equal(new Point3(10, 0, 0), r.Fin);
        }

        [Fact]
        public void ConHueco_CierraElHuecoConUnSoloSegmento()
        {
            // Tras borrar un tabique: dos trozos con hueco entre x=3 y x=7.
            var segs = new[] { L(0, 0, 3, 0), L(7, 0, 10, 0) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, Tol);

            Assert.True(r.Ok);
            Assert.Equal(new Point3(0, 0, 0), r.Inicio);
            Assert.Equal(new Point3(10, 0, 0), r.Fin);
        }

        [Fact]
        public void SentidosOpuestos_LosExtremosSonLosMismosPuntos()
        {
            // Referencia 6→0 (hacia -X): Inicio es el de menor proyección sobre esa dirección (x=10), Fin el de x=0.
            var segs = new[] { L(6, 0, 0, 0), L(10, 0, 4, 0) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, Tol);

            Assert.True(r.Ok);
            Assert.Equal(new Point3(10, 0, 0), r.Inicio);
            Assert.Equal(new Point3(0, 0, 0), r.Fin);
        }

        [Fact]
        public void SegmentoParalelo_RompeLaColinealidadYSeNombra()
        {
            var segs = new[] { L(0, 0, 5, 0), L(5, 0, 10, 0), L(0, 2, 5, 2) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, Tol);

            Assert.False(r.Ok);
            Assert.Equal(new[] { 2 }, r.Breaking);
            Assert.Equal(2.0, r.MaxDistance, 9);
        }

        [Fact]
        public void SegmentoNoParalelo_RompeLaColinealidad()
        {
            var segs = new[] { L(0, 0, 5, 0), L(5, 0, 10, 0), L(5, 0, 5, 5) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, Tol);

            Assert.Equal(new[] { 2 }, r.Breaking);
        }

        [Fact]
        public void DesviacionDentroDeLaTolerancia_SeAdmite()
        {
            var segs = new[] { L(0, 0, 5, 0), L(5, 0.0000005, 10, 0.0000005) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, 1e-6);

            Assert.True(r.Ok);
        }

        [Fact]
        public void DesviacionFueraDeLaTolerancia_Rompe()
        {
            var segs = new[] { L(0, 0, 5, 0), L(5, 0.00001, 10, 0.00001) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, 1e-6);

            Assert.Equal(new[] { 1 }, r.Breaking);
        }

        [Fact]
        public void ReferenciaEsElSegmentoMasLargo_AunqueNoSeaElPrimero()
        {
            // El primero es corto y su dirección sería poco fiable; el más largo fija la recta.
            var segs = new[] { L(0, 0, 0.5, 0.0000002), L(0, 0, 10, 0) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, 1e-3);

            Assert.Equal(1, r.Reference);
            Assert.True(r.Ok);
        }

        [Fact]
        public void LongitudCero_SeMarcaComoDegenerado()
        {
            var segs = new[] { L(0, 0, 5, 0), L(5, 0, 5, 0) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, Tol);

            Assert.False(r.Ok);
            Assert.Equal(new[] { 1 }, r.Degenerate);
            Assert.Empty(r.Breaking);
        }

        [Fact]
        public void TodosDegenerados_NoHayReferencia()
        {
            var segs = new[] { L(1, 1, 1, 1) };
            LineMerge.Analysis r = LineMerge.Analyze(segs, Tol);

            Assert.Equal(-1, r.Reference);
            Assert.False(r.Ok);
        }

        [Fact]
        public void Colinealidad3D_UsaLaDistanciaEnElEspacio()
        {
            var a = (new Point3(0, 0, 0), new Point3(0, 0, 5));
            var b = (new Point3(0, 0, 6), new Point3(0, 0, 9));
            var c = (new Point3(0, 1, 6), new Point3(0, 1, 9));   // desplazado en Y
            LineMerge.Analysis ok = LineMerge.Analyze(new[] { a, b }, Tol);
            LineMerge.Analysis roto = LineMerge.Analyze(new[] { a, b, c }, Tol);

            Assert.True(ok.Ok);
            Assert.Equal(new[] { 2 }, roto.Breaking);
        }

        [Fact]
        public void ListaVacia_Lanza()
        {
            Assert.Throws<ArgumentException>(() => LineMerge.Analyze(new List<(Point3, Point3)>(), Tol));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        public void TolercanciaNoValida_Lanza(double tol)
        {
            Assert.Throws<ArgumentException>(() => LineMerge.Analyze(new[] { L(0, 0, 1, 0) }, tol));
        }

        [Fact]
        public void ToleranciaPorDefecto_EsUnaMillonesimaDelDibujo()
        {
            Assert.Equal(1e-6 * 1000.0, LineMerge.DefaultTolerance(1000.0), 15);
            Assert.Equal(1e-6, LineMerge.DefaultTolerance(0.0), 15);          // span no válido → 1
            Assert.Equal(1e-6, LineMerge.DefaultTolerance(double.NaN), 15);
            Assert.Equal(LineMerge.MinDefaultTolerance, LineMerge.DefaultTolerance(1e-4), 15);   // suelo absoluto
        }
    }
}
