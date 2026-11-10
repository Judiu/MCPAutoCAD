using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class TextEscapeTests
    {
        [Theory]
        [InlineData("COCINA", "COCINA")]
        [InlineData("50%", "50%")]
        [InlineData("%%d", "%%%%%%d")]
        [InlineData("a%%%b", "a%%%%%%%b")]
        [InlineData("%%%%", "%%%%%%%%%%%%")]
        [InlineData("", "")]
        public void EscapeDbText_NeutralizaCodigos(string entrada, string esperado)
        {
            Assert.Equal(esperado, TextEscape.EscapeDbText(entrada));
        }

        [Fact]
        public void CollapseNewlines_SustituyeSaltosPorEspacio()
        {
            Assert.Equal("a b c", TextEscape.CollapseNewlines("a\r\nb\nc", out bool had));
            Assert.True(had);
            Assert.Equal("abc", TextEscape.CollapseNewlines("abc", out had));
            Assert.False(had);
        }
    }
}
