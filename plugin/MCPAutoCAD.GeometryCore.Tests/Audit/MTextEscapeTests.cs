using System;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    public class MTextEscapeTests
    {
        [Fact]
        public void BarraYLlaves_SeEscapan()
        {
            // a\b{c} → a\\b\{c\}
            Assert.Equal("a\\\\b\\{c\\}", MTextEscape.Escape("a\\b{c}"));
        }

        [Fact]
        public void CodigoParrafoLiteral_NoSeInterpreta()
        {
            // \P escrito por el cliente queda como texto literal \\P.
            Assert.Equal("\\\\P", MTextEscape.Escape("\\P"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("Área del lote 72,00 m² — revisar")]
        [InlineData("[ALTA] texto normal 123")]
        public void TextoSinEspeciales_NoCambia(string text)
        {
            Assert.Equal(text, MTextEscape.Escape(text));
        }

        [Theory]
        [InlineData("linea1\nlinea2", "linea1\\Plinea2")]
        [InlineData("linea1\r\nlinea2", "linea1\\Plinea2")]
        [InlineData("a\rb", "a\\Pb")]
        [InlineData("a\n\nb", "a\\P\\Pb")]
        public void SaltosDeLinea_ComoParrafo(string input, string expected)
        {
            Assert.Equal(expected, MTextEscape.Escape(input));
        }

        [Fact]
        public void CodigosDeFormato_QuedanLiterales()
        {
            Assert.Equal("\\\\C1;\\{x\\}", MTextEscape.Escape("\\C1;{x}"));
        }

        [Fact]
        public void Nulo_Lanza()
        {
            Assert.Throws<ArgumentNullException>(() => MTextEscape.Escape(null!));
        }
    }
}
