using System;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class BBoxTests
    {
        private static BBox2 B(double x1, double y1, double x2, double y2) => BBox2.FromCorners(x1, y1, x2, y2);

        [Fact]
        public void FromCorners_Valida()
        {
            BBox2 b = B(0, 1, 5, 3);
            Assert.Equal(0, b.Xmin);
            Assert.Equal(1, b.Ymin);
            Assert.Equal(5, b.Xmax);
            Assert.Equal(3, b.Ymax);
            Assert.Equal(5, b.Width);
            Assert.Equal(2, b.Height);
        }

        [Fact]
        public void FromCorners_Degenerada_EsValida()
        {
            BBox2 b = B(2, 2, 2, 2);
            Assert.Equal(0, b.Width);
        }

        [Theory]
        [InlineData(5, 0, 0, 1)]
        [InlineData(0, 5, 1, 0)]
        public void FromCorners_MinimoMayorQueMaximo(double x1, double y1, double x2, double y2)
        {
            var ex = Assert.Throws<ArgumentException>(() => B(x1, y1, x2, y2));
            Assert.Equal("bbox: mínimo mayor que máximo", ex.Message);
        }

        [Fact]
        public void FromCorners_NoFinito()
        {
            Assert.Throws<ArgumentException>(() => B(double.NaN, 0, 1, 1));
        }

        [Fact]
        public void Intersects_Solapadas()
        {
            Assert.True(B(0, 0, 10, 10).Intersects(B(5, 5, 15, 15)));
        }

        [Fact]
        public void Intersects_Contenida()
        {
            Assert.True(B(0, 0, 10, 10).Intersects(B(2, 2, 3, 3)));
            Assert.True(B(2, 2, 3, 3).Intersects(B(0, 0, 10, 10)));
        }

        [Fact]
        public void Intersects_BordeTocando_Cuenta()
        {
            Assert.True(B(0, 0, 10, 10).Intersects(B(10, 0, 20, 10)));
            Assert.True(B(0, 0, 10, 10).Intersects(B(0, 10, 10, 20)));
        }

        [Fact]
        public void Intersects_EsquinaTocando_Cuenta()
        {
            Assert.True(B(0, 0, 10, 10).Intersects(B(10, 10, 20, 20)));
        }

        [Fact]
        public void Intersects_Separadas()
        {
            Assert.False(B(0, 0, 10, 10).Intersects(B(10.0001, 0, 20, 10)));
            Assert.False(B(0, 0, 10, 10).Intersects(B(0, -5, 10, -0.0001)));
        }

        [Fact]
        public void Intersects_CajaDegenerada_Linea()
        {
            // Una línea horizontal (alto 0) dentro del filtro.
            Assert.True(B(0, 0, 10, 10).Intersects(B(2, 5, 8, 5)));
        }

        [Fact]
        public void FromPoints_IgnoraZ()
        {
            BBox2 b = BBox2.FromPoints(new[] { new Point3(3, -1, 100), new Point3(-2, 4, -5), new Point3(0, 0, 0) });
            Assert.Equal(B(-2, -1, 3, 4), b);
        }

        [Fact]
        public void FromPoints_Vacio_Rechazado()
        {
            Assert.Throws<ArgumentException>(() => BBox2.FromPoints(Array.Empty<Point3>()));
        }

        [Fact]
        public void Union_ContieneAmbas()
        {
            Assert.Equal(B(-1, 0, 10, 12), B(0, 0, 10, 10).Union(B(-1, 5, 3, 12)));
        }

        [Fact]
        public void ToArray2D_Formato()
        {
            double[][] a = B(1, 2, 3, 4).ToArray2D();
            Assert.Equal(new[] { 1.0, 2.0 }, a[0]);
            Assert.Equal(new[] { 3.0, 4.0 }, a[1]);
        }
    }
}
