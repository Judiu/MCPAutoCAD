using System;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class BlockInsertScaleTests
    {
        private static void AssertVec(double[] esperado, double[] real)
        {
            Assert.Equal(3, real.Length);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(esperado[i], real[i], 12);
            }
        }

        [Fact]
        public void Apply_MmEnM_AplicaFactor()
        {
            double? factor = UnitsScale.Factor(4, 6);
            (double[] a, bool aplicado) = BlockInsertScale.Apply(new[] { 1.0, 1.0, 1.0 }, factor, "auto");
            Assert.True(aplicado);
            AssertVec(new[] { 0.001, 0.001, 0.001 }, a);
        }

        [Fact]
        public void Apply_Ninguna_NoConvierte()
        {
            (double[] a, bool aplicado) = BlockInsertScale.Apply(new[] { 2.0, 2.0, 2.0 }, 0.001, "ninguna");
            Assert.False(aplicado);
            AssertVec(new[] { 2.0, 2.0, 2.0 }, a);
        }

        [Fact]
        public void Apply_FactorNull_NoAplica()
        {
            (double[] a, bool aplicado) = BlockInsertScale.Apply(new[] { 1.0, 1.0, 1.0 }, null, "auto");
            Assert.False(aplicado);
            AssertVec(new[] { 1.0, 1.0, 1.0 }, a);
        }

        [Fact]
        public void Apply_FactorUno_NoSeConsideraAplicado()
        {
            (double[] a, bool aplicado) = BlockInsertScale.Apply(new[] { 1.0, 1.0, 1.0 }, 1.0, "auto");
            Assert.False(aplicado);
            AssertVec(new[] { 1.0, 1.0, 1.0 }, a);
        }

        [Fact]
        public void Apply_Espejo_ConservaSigno()
        {
            (double[] a, bool aplicado) = BlockInsertScale.Apply(new[] { -1.0, 1.0, 1.0 }, 0.001, "auto");
            Assert.True(aplicado);
            AssertVec(new[] { -0.001, 0.001, 0.001 }, a);
        }

        [Fact]
        public void Apply_PulgadasEnM()
        {
            (double[] a, _) = BlockInsertScale.Apply(new[] { 1.0, 2.0, 1.0 }, UnitsScale.Factor(1, 6), "auto");
            AssertVec(new[] { 0.0254, 0.0508, 0.0254 }, a);
        }

        [Fact]
        public void Apply_NoModificaLaEntrada()
        {
            var entrada = new[] { 1.0, 1.0, 1.0 };
            BlockInsertScale.Apply(entrada, 0.001, "auto");
            AssertVec(new[] { 1.0, 1.0, 1.0 }, entrada);
        }

        [Fact]
        public void Apply_ModoInvalido_Lanza()
        {
            Assert.Throws<ArgumentException>(() => BlockInsertScale.Apply(new[] { 1.0, 1.0, 1.0 }, 0.001, "AUTO"));
        }

        [Fact]
        public void ParseEscala_NumeroUniforme()
        {
            AssertVec(new[] { 2.5, 2.5, 2.5 }, BlockInsertScale.ParseEscala(2.5));
            AssertVec(new[] { -1.0, -1.0, -1.0 }, BlockInsertScale.ParseEscala(-1.0));
        }

        [Fact]
        public void ParseEscala_DosComponentes_ZIgualAX()
        {
            AssertVec(new[] { 2.0, 3.0, 2.0 }, BlockInsertScale.ParseEscala(new[] { 2.0, 3.0 }));
        }

        [Fact]
        public void ParseEscala_TresComponentes()
        {
            AssertVec(new[] { -1.0, 1.0, 0.5 }, BlockInsertScale.ParseEscala(new[] { -1.0, 1.0, 0.5 }));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void ParseEscala_NumeroInvalido_MensajeExacto(double s)
        {
            var ex = Assert.Throws<ArgumentException>(() => BlockInsertScale.ParseEscala(s));
            Assert.Equal("escala: debe ser un número distinto de 0 o un array [sx, sy] / [sx, sy, sz]", ex.Message);
        }

        [Fact]
        public void ParseEscala_ArrayInvalido_MensajeExacto()
        {
            Assert.Equal(BlockInsertScale.InvalidScale, Assert.Throws<ArgumentException>(() => BlockInsertScale.ParseEscala(new[] { 1.0 })).Message);
            Assert.Equal(BlockInsertScale.InvalidScale, Assert.Throws<ArgumentException>(() => BlockInsertScale.ParseEscala(new[] { 1.0, 1.0, 1.0, 1.0 })).Message);
            Assert.Equal(BlockInsertScale.InvalidScale, Assert.Throws<ArgumentException>(() => BlockInsertScale.ParseEscala(new[] { 1.0, 0.0 })).Message);
            Assert.Equal(BlockInsertScale.InvalidScale, Assert.Throws<ArgumentException>(() => BlockInsertScale.ParseEscala(new[] { 1.0, 1.0, double.NaN })).Message);
            Assert.Equal(BlockInsertScale.InvalidScale, Assert.Throws<ArgumentException>(() => BlockInsertScale.ParseEscala(Array.Empty<double>())).Message);
        }

        [Fact]
        public void ReportedFactor_Casos()
        {
            Assert.Equal(0.001, BlockInsertScale.ReportedFactor(4, 6, out bool sin), 12);
            Assert.False(sin);
            Assert.Equal(1.0, BlockInsertScale.ReportedFactor(0, 6, out sin));
            Assert.True(sin);
            Assert.Equal(1.0, BlockInsertScale.ReportedFactor(6, 0, out sin));
            Assert.True(sin);
            Assert.Equal(1.0, BlockInsertScale.ReportedFactor(18, 6, out sin));
            Assert.True(sin);
            Assert.Equal(1.0, BlockInsertScale.ReportedFactor(6, 6, out sin));
            Assert.False(sin);
        }

        [Fact]
        public void AppliedWarning_TextoExacto()
        {
            string w = BlockInsertScale.AppliedWarning(0.001, 4, 6, new[] { 0.001, 0.001, 0.001 });
            Assert.Equal(
                "Se aplicó el factor de unidades 0.001 (bloque en mm, dibujo en m): escala final 0.001, 0.001, 0.001. "
                + "Si la geometría del bloque ya está en las unidades del dibujo (metadato de unidades incorrecto, ver auditar_bloques), "
                + "repita con escala_unidades: \"ninguna\".",
                w);
        }

        [Fact]
        public void AppliedWarning_SinRuidoDeComaFlotante()
        {
            double f = UnitsScale.Factor(2, 1)!.Value; // pies → pulgadas = 12.000000000000002
            string w = BlockInsertScale.AppliedWarning(f, 2, 1, new[] { f, f, f });
            Assert.StartsWith("Se aplicó el factor de unidades 12 (bloque en ft, dibujo en in): escala final 12, 12, 12.", w);
        }

        [Fact]
        public void NoFactorWarning_TextoExacto()
        {
            Assert.Equal(
                "No se aplicó factor de unidades: el bloque (INSUNITS=0) o el dibujo (INSUNITS=6) no declaran unidades con equivalencia métrica.",
                BlockInsertScale.NoFactorWarning(0, 6));
        }
    }
}
