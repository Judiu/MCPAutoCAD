using System;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class Point3Tests
    {
        [Fact]
        public void FromArray_DosComponentes_ZEsCero()
        {
            Point3 p = Point3.FromArray(new[] { 1.5, -2.0 });
            Assert.Equal(1.5, p.X);
            Assert.Equal(-2.0, p.Y);
            Assert.Equal(0.0, p.Z);
        }

        [Fact]
        public void FromArray_TresComponentes()
        {
            Point3 p = Point3.FromArray(new[] { 1.0, 2.0, 3.0 });
            Assert.Equal(new Point3(1, 2, 3), p);
        }

        [Theory]
        [InlineData(double.NaN, 0.0)]
        [InlineData(0.0, double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity, 1.0)]
        public void FromArray_NoFinito_Rechazado(double x, double y)
        {
            var ex = Assert.Throws<ArgumentException>(() => Point3.FromArray(new[] { x, y }));
            Assert.Contains("coordenada no finita", ex.Message);
        }

        [Fact]
        public void FromArray_ZNoFinita_Rechazada()
        {
            Assert.False(Point3.TryFromArray(new[] { 0.0, 0.0, double.NaN }, out _, out string? error));
            Assert.Equal("coordenada no finita", error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(4)]
        public void FromArray_NumeroDeComponentesInvalido_Rechazado(int count)
        {
            Assert.Throws<ArgumentException>(() => Point3.FromArray(new double[count]));
        }

        [Fact]
        public void Distance_Es3D()
        {
            var a = new Point3(0, 0, 0);
            var b = new Point3(2, 3, 6);
            Assert.Equal(7.0, a.Distance(b), 12);
        }

        [Fact]
        public void ToArray_SiempreTresComponentes()
        {
            Assert.Equal(new[] { 1.0, 2.0, 0.0 }, Point3.FromArray(new[] { 1.0, 2.0 }).ToArray());
        }
    }
}
