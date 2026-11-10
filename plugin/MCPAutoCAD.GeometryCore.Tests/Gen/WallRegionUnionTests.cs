using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>muros_desde_lineas: unión de caras en regiones con agujeros (<see cref="WallRegionUnion"/>).</summary>
    public class WallRegionUnionTests
    {
        private const double Tol = 0.012;

        private static List<(double x, double y)> Rect(double x0, double y0, double x1, double y1) =>
            new List<(double x, double y)> { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };

        /// <summary>Ningún contorno (exterior ni agujero) repite un vértice: un HATCH con vértices repetidos puede fallar.</summary>
        private static void AssertSimple(WallUnionResult r)
        {
            foreach (WallRegion g in r.Regions)
            {
                AssertNoRepeats(g.Outer);
                foreach (IReadOnlyList<(double x, double y)> h in g.Holes)
                {
                    AssertNoRepeats(h);
                }
            }
        }

        private static void AssertNoRepeats(IReadOnlyList<(double x, double y)> v)
        {
            var seen = new HashSet<(long, long)>();
            foreach ((double x, double y) p in v)
            {
                Assert.True(seen.Add(((long)Math.Round(p.x * 1e6), (long)Math.Round(p.y * 1e6))), "vértice repetido");
            }
        }

        private static WallUnionResult U(params List<(double x, double y)>[] quads)
        {
            WallUnionResult r = WallRegionUnion.Union(quads, Tol);
            AssertSimple(r);
            return r;
        }

        [Fact]
        public void UnaCara_UnaRegionDeCuatroVertices()
        {
            WallUnionResult r = U(Rect(0, 0, 4, 0.15));

            Assert.Single(r.Regions);
            Assert.Equal(4, r.Regions[0].Outer.Count);
            Assert.Equal(0.6, r.Regions[0].Area, 9);
            Assert.Equal(0, r.ContornosAbiertos);
        }

        [Fact]
        public void CarasIdenticas_UnaSola()
        {
            WallUnionResult r = U(Rect(0, 0, 4, 0.15), Rect(0, 0, 4, 0.15));

            Assert.Single(r.Regions);
            Assert.Equal(0.6, r.Regions[0].Area, 9);
        }

        [Fact]
        public void CruzSolapada_DoceVerticesYAreaSinDoble()
        {
            // H: [0,4]x[0,1] (4) + V: [1,2]x[-1,2] (3) - solape [1,2]x[0,1] (1) = 6.
            WallUnionResult r = U(Rect(0, 0, 4, 1), Rect(1, -1, 2, 2));

            Assert.Single(r.Regions);
            Assert.Equal(12, r.Regions[0].Outer.Count);
            Assert.Equal(6.0, r.Regions[0].Area, 9);
        }

        [Fact]
        public void UnionEnTEnTocandoSinSolape_NoDuplicaArea()
        {
            // A: [0;4,9]x[-0,1;0,1]; B: [4,9;5,1]x[-0,1;3]; se tocan en x = 4,9.
            WallUnionResult r = U(Rect(0, -0.1, 4.9, 0.1), Rect(4.9, -0.1, 5.1, 3));

            Assert.Single(r.Regions);
            Assert.Equal(6, r.Regions[0].Outer.Count);
            Assert.Equal(1.6, r.Regions[0].Area, 9);
        }

        [Fact]
        public void HuecoMenorQueTolerancia_SeCierra()
        {
            // Hueco de 5 mm entre A y B (< 12 mm): se unen en una región.
            WallUnionResult r = U(Rect(0, -0.1, 4.895, 0.1), Rect(4.9, -0.1, 5.1, 3));

            Assert.Single(r.Regions);
            Assert.Equal(1.6, r.Regions[0].Area, 2);
        }

        [Fact]
        public void HuecoMayorQueTolerancia_DosRegiones()
        {
            WallUnionResult r = U(Rect(0, -0.1, 4.9, 0.1), Rect(4.95, -0.1, 5.1, 3));

            Assert.Equal(2, r.Regions.Count);
        }

        [Fact]
        public void TramosColineales_UnaRegionDeCuatroVertices()
        {
            WallUnionResult r = U(Rect(0, 0, 2, 1), Rect(2, 0, 4, 1));

            Assert.Single(r.Regions);
            Assert.Equal(4, r.Regions[0].Outer.Count);
            Assert.Equal(4.0, r.Regions[0].Area, 9);
        }

        [Fact]
        public void MarcoConAgujero_UnExteriorYUnAgujero()
        {
            WallUnionResult r = U(
                Rect(0, 0, 4, 1),
                Rect(0, 3, 4, 4),
                Rect(0, 1, 1, 3),
                Rect(3, 1, 4, 3));

            Assert.Single(r.Regions);
            Assert.Equal(4, r.Regions[0].Outer.Count);
            Assert.Single(r.Regions[0].Holes);
            Assert.Equal(4, r.Regions[0].Holes[0].Count);
            Assert.Equal(12.0, r.Regions[0].Area, 9);
        }

        [Fact]
        public void Caras_SinContactoDosRegiones_OrdenadasPorX()
        {
            WallUnionResult r = U(Rect(10, 0, 12, 0.2), Rect(0, 0, 2, 0.2));

            Assert.Equal(2, r.Regions.Count);
            Assert.True(r.Regions[0].MinX < r.Regions[1].MinX);
        }

        [Fact]
        public void CarasRotadas_SolapadasFormanUnaRegionConAreaEntreMaximoYSuma()
        {
            // Dos muros diagonales que se cruzan.
            var a = new List<(double x, double y)> { (0, 0), (3, 3), (3.1, 2.9), (0.1, -0.1) };
            var b = new List<(double x, double y)> { (0, 3), (3, 0), (3.1, 0.1), (0.1, 3.1) };
            double areaA = Math.Abs(PolygonMetrics.SignedArea(a));
            double areaB = Math.Abs(PolygonMetrics.SignedArea(b));

            WallUnionResult r = U(a, b);

            Assert.Single(r.Regions);
            Assert.True(r.Regions[0].Area > Math.Max(areaA, areaB) - 1e-9);
            Assert.True(r.Regions[0].Area < areaA + areaB);
        }

        [Fact]
        public void SinCaras_SinRegiones()
        {
            WallUnionResult r = WallRegionUnion.Union(new List<IReadOnlyList<(double x, double y)>>(), Tol);

            Assert.Empty(r.Regions);
        }

        [Fact]
        public void ToleranciaNegativa_Lanza()
        {
            Assert.Throws<ArgumentException>(() => WallRegionUnion.Union(new[] { Rect(0, 0, 1, 1) }, -1));
        }

        [Fact]
        public void DosCuadradosEnUnaEsquina_DosRegionesSinVerticesRepetidos()
        {
            // Dos muros que solo se tocan en un vértice: el encadenado no debe mezclarlos en un contorno que se toca a sí mismo.
            WallUnionResult r = U(Rect(0, 0, 1, 1), Rect(1, 1, 2, 2));

            Assert.Equal(2, r.Regions.Count);
            Assert.All(r.Regions, g => Assert.Equal(4, g.Outer.Count));
            Assert.All(r.Regions, g => Assert.Empty(g.Holes));
        }

        [Fact]
        public void AgujeroQueTocaUnaIsla_SeAsignaAlExteriorQueLoContiene()
        {
            // Marco con un hueco en L que toca en una esquina a una isla pequeña: el hueco es del marco, no de la isla.
            WallUnionResult r = U(
                Rect(0, 0, 3, 0.5),
                Rect(0, 2.5, 3, 3),
                Rect(0, 0, 0.5, 3),
                Rect(2.5, 0, 3, 3),
                Rect(0.5, 0.5, 1.2, 1.2),
                Rect(1.2, 1.2, 2, 2));

            Assert.Equal(2, r.Regions.Count);
            Assert.Single(r.Regions[0].Holes);
            Assert.Empty(r.Regions[1].Holes);
            Assert.True(r.Regions[0].Area > 0);
            Assert.True(r.Regions[1].Area > 0);
            Assert.Equal(0.64, r.Regions[1].Area, 9);
        }

        [Fact]
        public void EsquinaDeCaraColineal_NoSeDesplazaLaGeometria()
        {
            // El vértice (4,9; 0) de A cae a 5 mm de la cara de B: se pega a ella (junta a tope). El vértice (4,905; 0) de B
            // ya está sobre la recta de la cara de A: no se mueve y la unión queda rectangular.
            WallUnionResult r = U(Rect(0, 0, 4.9, 0.2), Rect(4.905, 0, 5.1, 0.2));

            Assert.Single(r.Regions);
            Assert.Equal(4, r.Regions[0].Outer.Count);
            Assert.Equal(1.02, r.Regions[0].Area, 6);
        }
    }
}
