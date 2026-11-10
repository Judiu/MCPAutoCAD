using System;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class PagingTests
    {
        [Fact]
        public void ParseCursor_Null_EsCero()
        {
            Assert.Equal(0, Paging.ParseCursor(null));
        }

        [Theory]
        [InlineData("0", 0)]
        [InlineData("200", 200)]
        [InlineData("007", 7)]
        [InlineData("999999999", 999999999)]
        public void ParseCursor_Valido(string cursor, int expected)
        {
            Assert.Equal(expected, Paging.ParseCursor(cursor));
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("-1")]
        [InlineData("+1")]
        [InlineData(" 1")]
        [InlineData("1.5")]
        [InlineData("1000000000")]
        [InlineData("١٢")]
        public void ParseCursor_Invalido(string cursor)
        {
            var ex = Assert.Throws<ArgumentException>(() => Paging.ParseCursor(cursor));
            Assert.Equal("cursor: valor inválido", ex.Message);
        }

        [Fact]
        public void Slice_TotalCero_SinCursor()
        {
            Assert.Equal((0, (string?)null), Paging.Slice(0, 0, 200));
        }

        [Fact]
        public void Slice_PrimeraPaginaConSiguiente()
        {
            Assert.Equal((200, "200"), Paging.Slice(1532, 0, 200));
        }

        [Fact]
        public void Slice_UltimaPaginaParcial()
        {
            Assert.Equal((132, (string?)null), Paging.Slice(1532, 1400, 200));
        }

        [Fact]
        public void Slice_LimiteExacto_SinSiguiente()
        {
            Assert.Equal((200, (string?)null), Paging.Slice(200, 0, 200));
        }

        [Fact]
        public void Slice_UltimaFila()
        {
            Assert.Equal((1, (string?)null), Paging.Slice(10, 9, 5));
        }

        [Theory]
        [InlineData(10, 10)]
        [InlineData(10, 11)]
        [InlineData(0, 1)]
        public void Slice_FueraDeRango(int total, int start)
        {
            var ex = Assert.Throws<ArgumentException>(() => Paging.Slice(total, start, 200));
            Assert.Equal("cursor: fuera de rango", ex.Message);
        }

        [Fact]
        public void Slice_CursorCeroConTotalCero_EsValido()
        {
            Assert.Equal((0, (string?)null), Paging.Slice(0, Paging.ParseCursor("0"), 1));
        }

        [Fact]
        public void Slice_LimiteInvalido()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Paging.Slice(10, 0, 0));
        }
    }
}
