using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class PlacementPendingTests
    {
        private static BBox2 Box(double x0, double y0, double x1, double y1) => BBox2.FromCorners(x0, y0, x1, y1);

        [Fact]
        public void Refresh_SinExtension_NoRetiraNada()
        {
            var p = new PlacementPending(new[] { Box(0, 0, 1, 1) });
            Assert.False(p.Refresh(null));
            Assert.Equal(1, p.Count);
        }

        [Fact]
        public void Refresh_ExtensionCreciente_RetiraLasQueCabenYNoLasDevuelve()
        {
            var cajas = new[] { Box(0, 0, 1, 1), Box(100, 100, 101, 101) };
            var p = new PlacementPending(cajas);

            Assert.False(p.Refresh(Box(0, 0, 10, 10)));
            Assert.Equal(1, p.Count);

            // Al crecer la extensión, la segunda también cabe: todas dentro.
            Assert.True(p.Refresh(Box(0, 0, 100, 100)));
            Assert.Equal(0, p.Count);
        }

        [Fact]
        public void Refresh_CoincideConOutsideIndicesSobreLaExtensionFinal()
        {
            var cajas = new List<BBox2> { Box(0, 0, 1, 1), Box(50, 50, 51, 51), Box(1000, 1000, 1001, 1001) };
            var p = new PlacementPending(cajas);
            BBox2 ext = Box(0, 0, 40, 40);   // ampliada 10 % del lado mayor (4) → hasta 44: la de 50 no cabe
            p.Refresh(ext);
            Assert.Equal(MarkPlacement.OutsideIndices(cajas, ext).Count, p.Count);
        }

        [Fact]
        public void SinCajas_TodasDentroConExtension()
        {
            var p = new PlacementPending(Array.Empty<BBox2>());
            Assert.True(p.Refresh(Box(0, 0, 1, 1)));
        }

        [Fact]
        public void Nulo_Lanza()
        {
            Assert.Throws<ArgumentNullException>(() => new PlacementPending(null!));
        }
    }
}
