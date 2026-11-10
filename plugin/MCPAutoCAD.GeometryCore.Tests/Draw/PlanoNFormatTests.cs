using System;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class PlanoNFormatTests
    {
        // Contenido real del ATTDEF PLANO_N de standards/LPS_ROTULO.dwg (una sola barra en el archivo).
        private const string Real = @"{\pqc;\fArial|b1|i0|c0|p34;1\fArial|b1|i0|c0|p34;\H0.500000x; de}";

        // Tal como lo devuelve el MTEXT embebido (ODA + ezdxf, 2026-10-26): con \A1; delante de la llave.
        private const string RealConAlineacion = @"\A1;{\pqc;\fArial|b1|i0|c0|p34;1\fArial|b1|i0|c0|p34;\H0.500000x; de}";

        [Fact]
        public void TrySubstitute_CadenaReal_Con3()
        {
            Assert.True(PlanoNFormat.TrySubstitute(Real, "3", out string r));
            Assert.Equal(@"{\pqc;\fArial|b1|i0|c0|p34;3\fArial|b1|i0|c0|p34;\H0.500000x; de}", r);
        }

        [Fact]
        public void TrySubstitute_CadenaReal_Con12()
        {
            Assert.True(PlanoNFormat.TrySubstitute(Real, "12", out string r));
            Assert.Equal(@"{\pqc;\fArial|b1|i0|c0|p34;12\fArial|b1|i0|c0|p34;\H0.500000x; de}", r);
        }

        [Fact]
        public void TrySubstitute_Con3De_NormalizadoA3()
        {
            Assert.True(PlanoNFormat.TrySubstitute(Real, PlanoNFormat.NormalizeNumber("3 de"), out string r));
            Assert.Equal(@"{\pqc;\fArial|b1|i0|c0|p34;3\fArial|b1|i0|c0|p34;\H0.500000x; de}", r);
        }

        [Fact]
        public void TrySubstitute_ConCodigosAntesDeLaLlave()
        {
            Assert.True(PlanoNFormat.TrySubstitute(RealConAlineacion, "7", out string r));
            Assert.Equal(@"\A1;{\pqc;\fArial|b1|i0|c0|p34;7\fArial|b1|i0|c0|p34;\H0.500000x; de}", r);
        }

        [Fact]
        public void TrySubstitute_EsIdempotenteSobreSuResultado()
        {
            Assert.True(PlanoNFormat.TrySubstitute(Real, "3", out string r1));
            Assert.True(PlanoNFormat.TrySubstitute(r1, "45", out string r2));
            Assert.Equal(@"{\pqc;\fArial|b1|i0|c0|p34;45\fArial|b1|i0|c0|p34;\H0.500000x; de}", r2);
        }

        [Fact]
        public void TrySubstitute_EscapaElNumero()
        {
            Assert.True(PlanoNFormat.TrySubstitute(Real, @"3}{\P", out string r));
            Assert.Equal(@"{\pqc;\fArial|b1|i0|c0|p34;3\}\{\\P\fArial|b1|i0|c0|p34;\H0.500000x; de}", r);
            // El resultado sigue teniendo la forma (el número escapado no rompe el formato).
            Assert.True(PlanoNFormat.IsRecognized(r));
        }

        [Theory]
        [InlineData("1 de")]
        [InlineData(@"\A1;MOSQUERA\PCUNDINAMARCA")]
        [InlineData(@"{\pqc;1 de}")]
        [InlineData(@"{1\fArial|b1;\H0.5x; de}")]
        [InlineData(@"{\pqc;\fArial|b1|i0|c0|p34;1\fArial|b1|i0|c0|p34;\H0.500000x; de")]
        [InlineData("")]
        public void TrySubstitute_SinFormato_False(string contenido)
        {
            Assert.False(PlanoNFormat.TrySubstitute(contenido, "3", out string r));
            Assert.Equal(contenido, r);
            Assert.False(PlanoNFormat.IsRecognized(contenido));
        }

        [Fact]
        public void TrySubstitute_Nulos_Lanzan()
        {
            Assert.Throws<ArgumentNullException>(() => PlanoNFormat.TrySubstitute(null!, "3", out _));
            Assert.Throws<ArgumentNullException>(() => PlanoNFormat.TrySubstitute(Real, null!, out _));
        }

        [Theory]
        [InlineData("3 de", "3")]
        [InlineData("3", "3")]
        [InlineData(" 12 ", "12")]
        [InlineData("3 DE", "3")]
        [InlineData("3  De ", "3")]
        [InlineData("3de", "3")]
        [InlineData("de", "de")]
        [InlineData("", "")]
        [InlineData("A-1 de", "A-1")]
        public void NormalizeNumber_RecortaSufijo(string entrada, string esperado)
        {
            Assert.Equal(esperado, PlanoNFormat.NormalizeNumber(entrada));
        }

        [Fact]
        public void Advertencia_TextoExacto()
        {
            Assert.Equal("PLANO_N: no se reconoció el formato en línea; se escribió el valor sin formato.", PlanoNFormat.NotRecognizedWarning);
        }
    }
}
