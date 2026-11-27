using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class PolygonHoleRulesTests
    {
        private static readonly (double x, double y)[] OuterSquare =
        {
            (0, 0), (10, 0), (10, 10), (0, 10),
        };

        [Fact]
        public void ValidateHoles_RectangularAndConcaveU_Valid()
        {
            var rectHole = new (double x, double y)[]
            {
                (1, 1), (4, 1), (4, 4), (1, 4),
            };
            var uHole = new (double x, double y)[]
            {
                (5, 2), (9, 2), (9, 8), (8, 8), (8, 4), (6, 4), (6, 8), (5, 8),
            };

            Assert.Null(PolygonHoleRules.ValidateHoles(OuterSquare, new IReadOnlyList<(double, double)>[] { rectHole, uHole }));
        }

        [Fact]
        public void ValidateHoles_HoleOutsideOuter_Rejected()
        {
            var outside = new (double x, double y)[]
            {
                (12, 2), (15, 2), (15, 5), (12, 5),
            };

            string? err = PolygonHoleRules.ValidateHoles(OuterSquare, new IReadOnlyList<(double, double)>[] { outside });
            Assert.NotNull(err);
            Assert.StartsWith("huecos[0]:", err);
        }

        [Fact]
        public void ValidateHoles_HoleSpanningConcaveNotchOutOfOuter_Rejected()
        {
            // Contorno en forma de U; una caja entre las dos patas cae fuera del contorno aunque 2 esquinas estén dentro.
            var uOuter = new (double x, double y)[]
            {
                (0, 0), (10, 0), (10, 10), (7, 10), (7, 3), (3, 3), (3, 10), (0, 10),
            };
            var crossing = new (double x, double y)[]
            {
                (1, 5), (9, 5), (9, 8), (1, 8),
            };

            string? err = PolygonHoleRules.ValidateHoles(uOuter, new IReadOnlyList<(double, double)>[] { crossing }, "contorno.huecos");
            Assert.NotNull(err);
            Assert.StartsWith("contorno.huecos[0]:", err);
        }

        [Fact]
        public void ValidateHoles_OverlappingOrNestedHoles_Rejected()
        {
            var h0 = new (double x, double y)[] { (2, 2), (8, 2), (8, 8), (2, 8) };
            var hNested = new (double x, double y)[] { (3, 3), (6, 3), (6, 6), (3, 6) };
            var hCross = new (double x, double y)[] { (6, 4), (9, 4), (9, 7), (6, 7) };

            Assert.Contains("se solapa con huecos[0]", PolygonHoleRules.ValidateHoles(OuterSquare, new IReadOnlyList<(double, double)>[] { h0, hNested })!);
            Assert.Contains("se solapa con huecos[0]", PolygonHoleRules.ValidateHoles(OuterSquare, new IReadOnlyList<(double, double)>[] { h0, hCross })!);
        }

        [Fact]
        public void ValidateHoles_SelfIntersectingOrZeroArea_Rejected()
        {
            var bowtie = new (double x, double y)[] { (2, 2), (6, 6), (6, 2), (2, 6) };
            string? err = PolygonHoleRules.ValidateHoles(OuterSquare, new IReadOnlyList<(double, double)>[] { bowtie });
            Assert.NotNull(err);
            Assert.Contains("autointerseca", err);
        }

        [Fact]
        public void ValidateHoles_TooManyHoles_Rejected()
        {
            var list = new List<IReadOnlyList<(double, double)>>(PolygonHoleRules.MaxHoles + 1);
            for (int i = 0; i <= PolygonHoleRules.MaxHoles; i++)
            {
                list.Add(new[] { (1.0, 1.0), (2.0, 1.0), (2.0, 2.0), (1.0, 2.0) });
            }

            string? err = PolygonHoleRules.ValidateHoles(OuterSquare, list);
            Assert.NotNull(err);
            Assert.Contains("máximo", err);
        }
    }
}
