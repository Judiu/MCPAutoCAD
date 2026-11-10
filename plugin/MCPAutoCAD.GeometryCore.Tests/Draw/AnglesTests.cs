using System;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>protocol.md §5D.3: normalización de ángulos a [0, 360).</summary>
    public class AnglesTests
    {
        [Theory]
        [InlineData(-90.0, 270.0)]
        [InlineData(360.0, 0.0)]
        [InlineData(450.0, 90.0)]
        [InlineData(0.0, 0.0)]
        [InlineData(-360.0, 0.0)]
        [InlineData(-720.5, 359.5)]
        [InlineData(1080.25, 0.25)]
        [InlineData(359.999, 359.999)]
        public void Normalize360(double entrada, double esperado)
        {
            Assert.Equal(esperado, Angles.Normalize360(entrada), 9);
        }

        [Fact]
        public void Normalize360_NegativoDiminuto_EsCero()
        {
            double r = Angles.Normalize360(-1e-15);
            Assert.True(r >= 0 && r < 360);
            Assert.Equal(0.0, r);
        }

        [Fact]
        public void Normalize360_NoFinito_Lanza()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Angles.Normalize360(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => Angles.Normalize360(double.PositiveInfinity));
        }

        [Fact]
        public void ToRadians_YToDegrees()
        {
            Assert.Equal(Math.PI, Angles.ToRadians(180), 12);
            Assert.Equal(-Math.PI / 2, Angles.ToRadians(-90), 12);
            Assert.Equal(90.0, Angles.ToDegrees(Math.PI / 2), 12);
        }

        [Fact]
        public void SameDirection()
        {
            Assert.True(Angles.SameDirection(0, 360));
            Assert.True(Angles.SameDirection(-90, 270));
            Assert.True(Angles.SameDirection(359.9999999999999, 0));
            Assert.False(Angles.SameDirection(0, 90));
            Assert.False(Angles.SameDirection(10, 10.001));
        }
    }
}
