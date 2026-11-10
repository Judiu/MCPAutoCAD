using System;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class Transform2DTests
    {
        private const int Digits = 9;

        private static void AssertBox(BBox2 b, double xmin, double ymin, double xmax, double ymax)
        {
            Assert.Equal(xmin, b.Xmin, Digits);
            Assert.Equal(ymin, b.Ymin, Digits);
            Assert.Equal(xmax, b.Xmax, Digits);
            Assert.Equal(ymax, b.Ymax, Digits);
        }

        [Fact]
        public void Translate_DesplazaLaCaja()
        {
            BBox2 b = Transform2D.TransformBox(BBox2.FromCorners(0, 0, 2, 1), Transform2D.Translate(3, -4));
            AssertBox(b, 3, -4, 5, -3);
        }

        [Fact]
        public void Translate_Cero_EsIdentidad()
        {
            BBox2 b = Transform2D.TransformBox(BBox2.FromCorners(1, 2, 3, 4), Transform2D.Translate(0, 0));
            AssertBox(b, 1, 2, 3, 4);
        }

        [Fact]
        public void RotateAbout_90Grados_EnElOrigen_ContratoP5D8()
        {
            // §5D.8: rotar 90° una caja [0,0]-[2,1] en el origen → [−1,0]-[0,2].
            BBox2 b = Transform2D.TransformBox(BBox2.FromCorners(0, 0, 2, 1), Transform2D.RotateAbout(0, 0, Math.PI / 2));
            AssertBox(b, -1, 0, 0, 2);
        }

        [Fact]
        public void RotateAbout_PuntoBase_QuedaFijo()
        {
            Affine2 m = Transform2D.RotateAbout(5, 7, Transform2D.ToRadians(37));
            (double x, double y) = m.Apply(5, 7);
            Assert.Equal(5, x, Digits);
            Assert.Equal(7, y, Digits);
        }

        [Fact]
        public void RotateAbout_180Grados_SobreBaseDesplazada()
        {
            // Punto (2, 1) girado 180° alrededor de (1, 1) → (0, 1).
            (double x, double y) = Transform2D.RotateAbout(1, 1, Math.PI).Apply(2, 1);
            Assert.Equal(0, x, Digits);
            Assert.Equal(1, y, Digits);
        }

        [Fact]
        public void RotateAbout_45Grados_CajaEnvolventeDeLasEsquinas()
        {
            // Cuadrado unidad girado 45° en el origen: esquinas (0,0), (√2/2, √2/2), (0, √2), (−√2/2, √2/2).
            double h = Math.Sqrt(2) / 2;
            BBox2 b = Transform2D.TransformBox(BBox2.FromCorners(0, 0, 1, 1), Transform2D.RotateAbout(0, 0, Math.PI / 4));
            AssertBox(b, -h, 0, h, 2 * h);
        }

        [Fact]
        public void ScaleAbout_x2_DesdeUnoUno_ContratoP5D8()
        {
            // §5D.8: escalar ×2 desde (1,1): [1,1]-[3,2] → [1,1]-[5,3]; [0,0]-[2,2] → [−1,−1]-[3,3].
            AssertBox(Transform2D.TransformBox(BBox2.FromCorners(1, 1, 3, 2), Transform2D.ScaleAbout(1, 1, 2)), 1, 1, 5, 3);
            AssertBox(Transform2D.TransformBox(BBox2.FromCorners(0, 0, 2, 2), Transform2D.ScaleAbout(1, 1, 2)), -1, -1, 3, 3);
        }

        [Fact]
        public void ScaleAbout_Reduccion_AcercaAlaBase()
        {
            AssertBox(Transform2D.TransformBox(BBox2.FromCorners(0, 0, 4, 4), Transform2D.ScaleAbout(0, 0, 0.5)), 0, 0, 2, 2);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-2.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ScaleAbout_FactorInvalido_Lanza(double f)
        {
            Assert.Throws<ArgumentException>(() => Transform2D.ScaleAbout(0, 0, f));
        }

        [Fact]
        public void ValoresNoFinitos_Lanzan()
        {
            Assert.Throws<ArgumentException>(() => Transform2D.Translate(double.NaN, 0));
            Assert.Throws<ArgumentException>(() => Transform2D.RotateAbout(0, 0, double.PositiveInfinity));
        }

        [Fact]
        public void TransformBox_CajaPunto_SigueSiendoPunto()
        {
            BBox2 b = Transform2D.TransformBox(BBox2.FromCorners(2, 0, 2, 0), Transform2D.RotateAbout(0, 0, Math.PI / 2));
            AssertBox(b, 0, 2, 0, 2);
        }

        [Fact]
        public void Composicion_RotarYMover_EquivaleAAplicarEnOrden()
        {
            Affine2 m = Transform2D.Translate(10, 0).Compose(Transform2D.RotateAbout(0, 0, Math.PI / 2));
            (double x, double y) = m.Apply(1, 0);
            Assert.Equal(10, x, Digits);
            Assert.Equal(1, y, Digits);
        }

        [Theory]
        [InlineData(0.0, true)]
        [InlineData(360.0, true)]
        [InlineData(-720.0, true)]
        [InlineData(90.0, false)]
        [InlineData(-90.0, false)]
        [InlineData(359.5, false)]
        [InlineData(0.001, false)]
        public void IsNullRotation_CeroModulo360(double deg, bool esperado)
        {
            Assert.Equal(esperado, Transform2D.IsNullRotation(deg));
        }

        [Theory]
        [InlineData(-90.0, 270.0)]
        [InlineData(360.0, 0.0)]
        [InlineData(450.0, 90.0)]
        [InlineData(45.0, 45.0)]
        public void Normalize360_DevuelveCeroATrescientosSesenta(double deg, double esperado)
        {
            Assert.Equal(esperado, Transform2D.Normalize360(deg), Digits);
        }

        [Fact]
        public void ToRadians_180EsPi()
        {
            Assert.Equal(Math.PI, Transform2D.ToRadians(180), 12);
        }
    }
}
