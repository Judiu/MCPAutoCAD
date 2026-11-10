using System;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class EntityFilterBoxTests
    {
        private static readonly BBox2 Query = BBox2.FromCorners(0, 0, 10, 10);

        [Fact]
        public void Interseca_ConservaElComportamientoAnterior()
        {
            BBox2 cruza = BBox2.FromCorners(9, 9, 20, 20);
            Assert.True(EntityFilter.MatchesBox(cruza, Query, EntityFilter.BboxModeIntersects));
            Assert.True(EntityFilter.MatchesBox(BBox2.FromCorners(20, 20, 30, 30).Union(BBox2.FromCorners(10, 10, 10, 10)), Query, EntityFilter.BboxModeIntersects));
        }

        [Fact]
        public void Dentro_RechazaLoQueSoloCruza()
        {
            BBox2 cruza = BBox2.FromCorners(9, 9, 20, 20);
            Assert.False(EntityFilter.MatchesBox(cruza, Query, EntityFilter.BboxModeInside));
        }

        [Fact]
        public void Dentro_AceptaLoContenidoIncluidoBordes()
        {
            Assert.True(EntityFilter.MatchesBox(BBox2.FromCorners(2, 2, 8, 8), Query, EntityFilter.BboxModeInside));
            Assert.True(EntityFilter.MatchesBox(BBox2.FromCorners(0, 0, 10, 10), Query, EntityFilter.BboxModeInside));
        }

        [Fact]
        public void Dentro_RechazaLoQueSaleAunqueUnaParteEste()
        {
            Assert.False(EntityFilter.MatchesBox(BBox2.FromCorners(-1, 2, 5, 8), Query, EntityFilter.BboxModeInside));
            Assert.False(EntityFilter.MatchesBox(BBox2.FromCorners(2, 2, 10.001, 8), Query, EntityFilter.BboxModeInside));
        }

        [Fact]
        public void Dentro_RechazaLoQueEstaFueraDelTodo()
        {
            Assert.False(EntityFilter.MatchesBox(BBox2.FromCorners(20, 20, 30, 30), Query, EntityFilter.BboxModeInside));
        }

        [Fact]
        public void Dentro_EntidadMayorQueLaCajaNoCabe()
        {
            Assert.False(EntityFilter.IsInside(BBox2.FromCorners(-5, -5, 15, 15), Query));
            Assert.True(EntityFilter.IsInside(BBox2.FromCorners(3, 3, 3, 3), Query));
        }

        [Fact]
        public void NormalizeBboxMode_DefectoEsIntersecaYRechazaOtrosValores()
        {
            Assert.Equal(EntityFilter.BboxModeIntersects, EntityFilter.NormalizeBboxMode(null));
            Assert.Equal(EntityFilter.BboxModeInside, EntityFilter.NormalizeBboxMode("dentro"));
            ArgumentException ex = Assert.Throws<ArgumentException>(() => EntityFilter.NormalizeBboxMode("contiene"));
            Assert.Equal(EntityFilter.InvalidBboxMode, ex.Message);
        }
    }
}
