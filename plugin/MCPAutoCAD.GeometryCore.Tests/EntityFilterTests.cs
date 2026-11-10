using System;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class EntityFilterTests
    {
        [Theory]
        [InlineData("line", "LINE")]
        [InlineData("  LwPolyline ", "LWPOLYLINE")]
        [InlineData("MTEXT", "MTEXT")]
        public void NormalizeType_MayusculasYTrim(string input, string expected)
        {
            Assert.Equal(expected, EntityFilter.NormalizeType(input));
        }

        [Fact]
        public void MatchesAny_FiltroNuloOVacio_Pasa()
        {
            Assert.True(EntityFilter.MatchesAny("MUROS", null));
            Assert.True(EntityFilter.MatchesAny("MUROS", Array.Empty<string>()));
        }

        [Fact]
        public void MatchesAny_SinDistinguirMayusculas()
        {
            Assert.True(EntityFilter.MatchesAny("Muros", new[] { "PUERTAS", "MUROS" }));
            Assert.True(EntityFilter.MatchesAny("cotas", new[] { "Cotas" }));
        }

        [Fact]
        public void MatchesAny_NoCoincide()
        {
            Assert.False(EntityFilter.MatchesAny("MUROS", new[] { "MURO", "MUROS2" }));
        }

        [Fact]
        public void MatchesAny_Ordinal_AcentosDistinguen()
        {
            Assert.False(EntityFilter.MatchesAny("COCINA", new[] { "CÓCINA" }));
        }

        [Fact]
        public void ContainsText_SinDistinguirMayusculas()
        {
            Assert.True(EntityFilter.ContainsText("Planta COCINA y baño", "cocina"));
            Assert.True(EntityFilter.ContainsText("cocina", "COCI"));
        }

        [Fact]
        public void ContainsText_AcentosDistinguen()
        {
            Assert.False(EntityFilter.ContainsText("canción", "cancion"));
            Assert.False(EntityFilter.ContainsText("cancion", "canción"));
            Assert.True(EntityFilter.ContainsText("CANCIÓN", "canción"));
        }

        [Fact]
        public void ContainsText_NoEncontrado()
        {
            Assert.False(EntityFilter.ContainsText("MUROS", "puerta"));
        }

        [Fact]
        public void ContainsText_TextoVacio()
        {
            Assert.False(EntityFilter.ContainsText("", "a"));
        }
    }
}
