using System.Collections.Generic;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class LayerPatternTests
    {
        [Fact]
        public void SinComodines_ComparacionExactaSinMayusculas()
        {
            Assert.True(LayerPattern.Matches("MUROS", "muros"));
            Assert.True(LayerPattern.Matches("Cotas", "COTAS"));
            Assert.False(LayerPattern.Matches("MUROS2", "MUROS"));
            Assert.False(LayerPattern.Matches("MURO", "MUROS"));
        }

        [Fact]
        public void Asterisco_PrefijoSufijoYCentro()
        {
            Assert.True(LayerPattern.Matches("MCP-DIBUJO", "MCP-*"));
            Assert.True(LayerPattern.Matches("mcp-", "MCP-*"));
            Assert.False(LayerPattern.Matches("XMCP-DIBUJO", "MCP-*"));
            Assert.True(LayerPattern.Matches("MUROS-PB-01", "*-PB-*"));
            Assert.True(LayerPattern.Matches("TEXTO", "*TO"));
            Assert.False(LayerPattern.Matches("TEXTOS", "*TO"));
        }

        [Fact]
        public void Asterisco_PuedeSerVacio()
        {
            Assert.True(LayerPattern.Matches("A", "A*"));
            Assert.True(LayerPattern.Matches("AB", "A*B"));
            Assert.True(LayerPattern.Matches("A", "*"));
        }

        [Fact]
        public void Asterisco_RetrocedeCuandoHaceFaltaVariasVeces()
        {
            // El primer '*' no basta: hay que retroceder al último para encontrar "AB" tras varias 'A'.
            Assert.True(LayerPattern.Matches("XAYAB", "*A*B"));
            Assert.True(LayerPattern.Matches("AAAB", "*AB"));
            Assert.False(LayerPattern.Matches("AAAC", "*AB"));
            Assert.True(LayerPattern.Matches("a-b-c-d", "*-*-d"));
        }

        [Fact]
        public void Interrogacion_ExactamenteUnCaracter()
        {
            Assert.True(LayerPattern.Matches("MURO1", "MURO?"));
            Assert.False(LayerPattern.Matches("MURO", "MURO?"));
            Assert.False(LayerPattern.Matches("MURO12", "MURO?"));
            Assert.True(LayerPattern.Matches("P1-A", "P?-*"));
        }

        [Fact]
        public void Anclado_AToda_LaCadena()
        {
            Assert.False(LayerPattern.Matches("XMUROS", "MUROS"));
            Assert.False(LayerPattern.Matches("MUROSX", "MUROS"));
            Assert.False(LayerPattern.Matches("MUROS", "MURO"));
        }

        [Fact]
        public void CaracteresPropiosDeAutoCad_SonLiterales()
        {
            // '$', '|' (xref) y '[' no son comodines.
            Assert.True(LayerPattern.Matches("X|CAPA", "X|CAPA"));
            Assert.True(LayerPattern.Matches("A$0", "A$0"));
            Assert.True(LayerPattern.Matches("[PRUEBA]", "[PRUEBA]"));
            Assert.False(LayerPattern.Matches("ABC", "[A]BC"));
        }

        [Fact]
        public void MatchesAny_SinFiltroEsTrue_ConFiltroBastaUno()
        {
            Assert.True(LayerPattern.MatchesAny("MUROS", null));
            Assert.True(LayerPattern.MatchesAny("MUROS", new List<string>()));
            Assert.True(LayerPattern.MatchesAny("MUROS", new List<string> { "COTAS", "MU*" }));
            Assert.False(LayerPattern.MatchesAny("MUROS", new List<string> { "COTAS", "MCP-*" }));
            Assert.False(LayerPattern.MatchesAny(null!, new List<string> { "*" }));
        }

        [Fact]
        public void Nulos_NoCoinciden()
        {
            Assert.False(LayerPattern.Matches(null!, "*"));
            Assert.False(LayerPattern.Matches("MUROS", null!));
            Assert.False(LayerPattern.Matches(null!, null!));
        }

        [Fact]
        public void NombreVacio_SoloCoincideConPatronesQueAdmitenVacio()
        {
            Assert.True(LayerPattern.Matches("", "*"));
            Assert.True(LayerPattern.Matches("", "**"));
            Assert.False(LayerPattern.Matches("", "?"));
            Assert.False(LayerPattern.Matches("", "?*"));
            Assert.False(LayerPattern.Matches("", "*?"));
            Assert.True(LayerPattern.Matches("", ""));
            Assert.False(LayerPattern.Matches("A", ""));
        }

        [Fact]
        public void CombinacionesDeComodines_ExigenUnCaracter()
        {
            Assert.True(LayerPattern.Matches("A", "?*"));
            Assert.True(LayerPattern.Matches("A", "*?"));
            Assert.True(LayerPattern.Matches("ABC", "*?*"));
            Assert.False(LayerPattern.Matches("", "*?*"));
        }

        [Fact]
        public void EnieMayuscula_SinDistinguirMayusculas()
        {
            Assert.True(LayerPattern.Matches("ñandú", "ÑANDÚ"));
            Assert.True(LayerPattern.Matches("Ñ", "ñ"));
            Assert.True(LayerPattern.Matches("CAÑO-1", "caño-?"));
            Assert.False(LayerPattern.Matches("CANO-1", "caño-?"));
        }
    }
}
