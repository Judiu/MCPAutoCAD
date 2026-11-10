using System;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>Composición de transformaciones para inserciones anidadas (protocol.md §5C.3, 0.3.2).</summary>
    public class Affine2Tests
    {
        [Fact]
        public void Identidad_NoCambiaLaCaja()
        {
            BBox2 b = BBox2.FromCorners(1, 2, 3, 5);
            Assert.Equal(b, Affine2.Identity.TransformBox(b));
        }

        [Fact]
        public void FromInsert_EscalaYTraslada()
        {
            (double x, double y) = Affine2.FromInsert(10, 20, 2, 3, 0).Apply(1, 1);
            Assert.Equal(12, x, 12);
            Assert.Equal(23, y, 12);
        }

        [Fact]
        public void FromInsert_Rotacion90()
        {
            (double x, double y) = Affine2.FromInsert(0, 0, 1, 1, Math.PI / 2).Apply(1, 0);
            Assert.Equal(0, x, 12);
            Assert.Equal(1, y, 12);
        }

        [Fact]
        public void Compose_AplicaPrimeroElInterior()
        {
            Affine2 interior = Affine2.FromInsert(5, 0, 2, 2, 0);
            Affine2 exterior = Affine2.FromInsert(100, 100, 10, 10, 0);
            (double x, double y) = exterior.Compose(interior).Apply(1, 1);
            // interior: (1,1) → (7, 2); exterior: (7,2) → (170, 120).
            Assert.Equal(170, x, 9);
            Assert.Equal(120, y, 9);
        }

        [Fact]
        public void BoxInSpace_EscalasSeMultiplican()
        {
            // Inserción anidada de 0.045 dentro de un contenedor a escala 2 dentro de otro a escala 0.5 → 0.045.
            BBox2 local = BBox2.FromCorners(0, 0, 0.045, 0.045);
            BBox2 b = Affine2.BoxInSpace(local, new[] { Affine2.FromInsert(1, 1, 2, 2, 0), Affine2.FromInsert(3300, 3990, 0.5, 0.5, 0) });
            Assert.Equal(0.045, b.Width, 12);
            Assert.Equal(0.045, b.Height, 12);
            Assert.Equal(3300.5, b.Xmin, 9);
        }

        [Fact]
        public void BoxInSpace_SinContenedores_EsLaCajaLocal()
        {
            BBox2 local = BBox2.FromCorners(0, 0, 1, 2);
            Assert.Equal(local, Affine2.BoxInSpace(local, Array.Empty<Affine2>()));
        }

        [Fact]
        public void TransformBox_Rotada45_CajaEnvolvente()
        {
            BBox2 b = Affine2.FromInsert(0, 0, 1, 1, Math.PI / 4).TransformBox(BBox2.FromCorners(-1, -1, 1, 1));
            Assert.Equal(2 * Math.Sqrt(2), b.Width, 9);
        }

        [Fact]
        public void Espejo_ConservaTamano()
        {
            BBox2 b = Affine2.FromInsert(0, 0, -0.045, 0.045, 0).TransformBox(BBox2.FromCorners(0, 0, 1, 1));
            Assert.Equal(0.045, b.Width, 12);
        }

        [Fact]
        public void NoFinito_Lanza()
        {
            Assert.Throws<ArgumentException>(() => new Affine2(double.NaN, 0, 0, 1, 0, 0));
        }
    }
}
