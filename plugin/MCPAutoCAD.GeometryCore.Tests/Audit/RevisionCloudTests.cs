using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    public class RevisionCloudTests
    {
        private const double Tol = 1e-9;

        private static BBox2 B(double x1, double y1, double x2, double y2) => BBox2.FromCorners(x1, y1, x2, y2);

        private static double SignedArea(IReadOnlyList<(double X, double Y)> v)
        {
            double a = 0;
            for (int i = 0; i < v.Count; i++)
            {
                (double x1, double y1) = v[i];
                (double x2, double y2) = v[(i + 1) % v.Count];
                a += x1 * y2 - x2 * y1;
            }

            return a / 2;
        }

        private static bool OnPerimeter(BBox2 r, double x, double y)
        {
            bool insideX = x >= r.Xmin - Tol && x <= r.Xmax + Tol;
            bool insideY = y >= r.Ymin - Tol && y <= r.Ymax + Tol;
            bool onVertical = Math.Abs(x - r.Xmin) <= Tol || Math.Abs(x - r.Xmax) <= Tol;
            bool onHorizontal = Math.Abs(y - r.Ymin) <= Tol || Math.Abs(y - r.Ymax) <= Tol;
            return insideX && insideY && (onVertical || onHorizontal);
        }

        [Fact]
        public void Caja5x3_Altura02_NumerosFijados()
        {
            // m = max(0.2, 0.05·5) = 0.25 → R = [-0.25,-0.25]..[5.25,3.25] (5.5 × 3.5)
            // c = max(0.3, 0.08·5.5) = 0.44 → n horizontal = ceil(12.5) = 13, n vertical = ceil(7.95) = 8
            RevisionCloudShape s = RevisionCloud.Build(B(0, 0, 5, 3), 0.2);
            Assert.Equal(0.25, s.Margen, 12);
            Assert.Equal(-0.25, s.Bbox.Xmin, 12);
            Assert.Equal(-0.25, s.Bbox.Ymin, 12);
            Assert.Equal(5.25, s.Bbox.Xmax, 12);
            Assert.Equal(3.25, s.Bbox.Ymax, 12);
            Assert.Equal(0.44, s.Cuerda, 12);
            Assert.Equal(13, s.ArcosHorizontal);
            Assert.Equal(8, s.ArcosVertical);
            Assert.Equal(42, s.Vertices.Count);
            Assert.Equal((-0.25, -0.25), s.Vertices[0]);
        }

        [Theory]
        [InlineData(0, 0, 5, 3, 0.2)]
        [InlineData(0, 0, 10, 10, 0.2)]
        [InlineData(-100, 50, 300, 51, 2.5)]
        [InlineData(0, 0, 0.01, 1000, 0.2)]
        [InlineData(3, 3, 3, 3, 0.5)]
        public void NumeroDeVertices_EsSumaDeArcosPorLado_AlMenos16(double x1, double y1, double x2, double y2, double h)
        {
            RevisionCloudShape s = RevisionCloud.Build(B(x1, y1, x2, y2), h);
            Assert.True(s.ArcosHorizontal >= 4);
            Assert.True(s.ArcosVertical >= 4);
            Assert.Equal(2 * (s.ArcosHorizontal + s.ArcosVertical), s.Vertices.Count);
            Assert.True(s.Vertices.Count >= 16);
            Assert.Equal(0, s.Vertices.Count % 2);
            Assert.Equal(s.Vertices.Count, s.Bulges.Count);

            // n = max(4, ceil(L / c)) en cada lado.
            Assert.Equal(Math.Max(4, (int)Math.Ceiling(s.Bbox.Width / s.Cuerda - 1e-9)), s.ArcosHorizontal);
            Assert.Equal(Math.Max(4, (int)Math.Ceiling(s.Bbox.Height / s.Cuerda - 1e-9)), s.ArcosVertical);
        }

        [Theory]
        [InlineData(0, 0, 10, 10, 0.2)]
        [InlineData(0, 0, 1, 1, 0.2)]
        [InlineData(5, 5, 5, 5, 0.2)]
        public void CajaCuadrada_VerticesMultiploDe4(double x1, double y1, double x2, double y2, double h)
        {
            RevisionCloudShape s = RevisionCloud.Build(B(x1, y1, x2, y2), h);
            Assert.Equal(0, s.Vertices.Count % 4);
            Assert.True(s.Vertices.Count >= 16);
        }

        [Fact]
        public void Bulges_IgualesYPositivos_Arco110()
        {
            RevisionCloudShape s = RevisionCloud.Build(B(0, 0, 5, 3), 0.2);
            Assert.Equal(Math.Tan(27.5 * Math.PI / 180), RevisionCloud.BulgeArc110, 12);
            Assert.Equal(0.5206, RevisionCloud.BulgeArc110, 4);
            foreach (double b in s.Bulges)
            {
                Assert.True(b > 0);
                Assert.Equal(RevisionCloud.BulgeArc110, b);
            }
        }

        [Theory]
        [InlineData(0, 0, 5, 3, 0.2)]
        [InlineData(-7.5, 2, 12, 2.5, 0.3)]
        [InlineData(1, 1, 1, 1, 0.2)]
        public void Vertices_SobreElPerimetroDeR(double x1, double y1, double x2, double y2, double h)
        {
            RevisionCloudShape s = RevisionCloud.Build(B(x1, y1, x2, y2), h);
            foreach ((double x, double y) in s.Vertices)
            {
                Assert.True(OnPerimeter(s.Bbox, x, y), "vértice fuera del perímetro: " + x + ", " + y);
            }
        }

        [Fact]
        public void Vertices_SinDuplicadosConsecutivos_YEsquinasIncluidas()
        {
            RevisionCloudShape s = RevisionCloud.Build(B(0, 0, 5, 3), 0.2);
            for (int i = 0; i < s.Vertices.Count; i++)
            {
                (double x1, double y1) = s.Vertices[i];
                (double x2, double y2) = s.Vertices[(i + 1) % s.Vertices.Count];
                Assert.True(Math.Abs(x1 - x2) + Math.Abs(y1 - y2) > 1e-6);
            }

            Assert.Contains((5.25, -0.25), s.Vertices);
            Assert.Contains((5.25, 3.25), s.Vertices);
            Assert.Contains((-0.25, 3.25), s.Vertices);
        }

        [Theory]
        [InlineData(0, 0, 5, 3, 0.2)]
        [InlineData(10, 10, 10, 10, 1)]
        [InlineData(0, 0, 400, 0, 0.2)]
        public void AreaConSigno_Positiva_Antihorario(double x1, double y1, double x2, double y2, double h)
        {
            RevisionCloudShape s = RevisionCloud.Build(B(x1, y1, x2, y2), h);
            double area = SignedArea(s.Vertices);
            Assert.True(area > 0);
            Assert.Equal(s.Bbox.Width * s.Bbox.Height, area, 6);
        }

        [Fact]
        public void CajaPunto_CuadradoDeLado2m()
        {
            RevisionCloudShape s = RevisionCloud.Build(B(2, 3, 2, 3), 0.5);
            Assert.Equal(0.5, s.Margen, 12);
            Assert.Equal(1.0, s.Bbox.Width, 12);
            Assert.Equal(1.0, s.Bbox.Height, 12);
            Assert.Equal(1.5, s.Bbox.Xmin, 12);
            Assert.Equal(2.5, s.Bbox.Ymin, 12);
            Assert.Equal(16, s.Vertices.Count);
        }

        [Fact]
        public void MargenGrande_UsaCincoPorCientoDelLadoMayor()
        {
            RevisionCloudShape s = RevisionCloud.Build(B(0, 0, 100, 20), 0.2);
            Assert.Equal(5.0, s.Margen, 12);
            Assert.Equal(-5.0, s.Bbox.Xmin, 12);
            Assert.Equal(105.0, s.Bbox.Xmax, 12);
            Assert.Equal(0.08 * 110, s.Cuerda, 12);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void AlturaInvalida_ArgumentException(double h)
        {
            var ex = Assert.Throws<ArgumentException>(() => RevisionCloud.Build(B(0, 0, 5, 3), h));
            Assert.Equal("altura_texto: debe ser un número mayor que 0", ex.Message);
        }
    }
}
