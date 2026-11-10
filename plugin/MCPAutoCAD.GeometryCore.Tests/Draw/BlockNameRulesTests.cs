using System;
using System.IO;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class BlockNameRulesTests
    {
        private const string Rutas = "nombre: solo se admite el nombre del bloque, sin rutas";
        private const string Anonimos = "nombre: no se insertan bloques anónimos";

        [Theory]
        [InlineData("LPS_ROTULO")]
        [InlineData("DP")]
        [InlineData("PuertaBaño")]
        [InlineData("TANQUE 1000 LTS")]
        [InlineData("ESTUFA CHALLENGER")]
        [InlineData("V.1")]
        [InlineData("A$C1A2B3C4D")]
        public void NombresSeguros(string nombre)
        {
            Assert.True(BlockNameRules.IsSafeName(nombre));
            Assert.Null(BlockNameRules.Validate(nombre));
        }

        [Theory]
        [InlineData(@"..\LPS_ROTULO")]
        [InlineData(@"C:\temp\x")]
        [InlineData("a/b")]
        [InlineData("C:x")]
        [InlineData("a|b")]
        [InlineData("a?b")]
        [InlineData("a\"b")]
        [InlineData("a<b")]
        [InlineData("a>b")]
        [InlineData("a*b")]
        [InlineData("..")]
        [InlineData("a..b")]
        [InlineData("LPS_ROTULO.dwg")]
        [InlineData("LPS_ROTULO.DWG")]
        [InlineData("plano.dxf")]
        [InlineData("x.")]
        [InlineData("x ")]
        [InlineData(" x")]
        [InlineData("CON")]
        [InlineData("nul.txt")]
        [InlineData("COM1")]
        [InlineData("a\tb")]
        [InlineData("")]
        public void NombresInseguros_MensajeDeRutas(string nombre)
        {
            Assert.False(BlockNameRules.IsSafeName(nombre));
            Assert.Equal(Rutas, BlockNameRules.Validate(nombre));
        }

        [Theory]
        [InlineData("*U12")]
        [InlineData("*Model_Space")]
        [InlineData("*D5")]
        public void Anonimos_MensajePropio(string nombre)
        {
            Assert.False(BlockNameRules.IsSafeName(nombre));
            Assert.Equal(Anonimos, BlockNameRules.Validate(nombre));
        }

        [Fact]
        public void Null_NoEsSeguro()
        {
            Assert.False(BlockNameRules.IsSafeName(null));
        }

        [Fact]
        public void LibraryFile_ConstruyeRutaDentroDeLaCarpeta()
        {
            string carpeta = Path.Combine(Path.GetTempPath(), "biblioteca");
            string ruta = BlockNameRules.LibraryFile(carpeta, "LPS_ROTULO");
            Assert.Equal(Path.Combine(carpeta, "LPS_ROTULO.dwg"), ruta);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta)),
                Path.GetDirectoryName(Path.GetFullPath(ruta)));
        }

        [Fact]
        public void LibraryFile_ConSeparadorFinal()
        {
            string carpeta = Path.Combine(Path.GetTempPath(), "biblioteca") + Path.DirectorySeparatorChar;
            string ruta = BlockNameRules.LibraryFile(carpeta, "DP");
            Assert.Equal("DP.dwg", Path.GetFileName(ruta));
        }

        [Theory]
        [InlineData(@"..\x")]
        [InlineData(@"sub\x")]
        [InlineData("x.dwg")]
        [InlineData(@"C:\Windows\x")]
        public void LibraryFile_NombreInseguro_Lanza(string nombre)
        {
            var ex = Assert.Throws<ArgumentException>(() => BlockNameRules.LibraryFile(@"C:\biblioteca", nombre));
            Assert.Equal(Rutas, ex.Message);
        }

        [Fact]
        public void LibraryFile_Anonimo_Lanza()
        {
            var ex = Assert.Throws<ArgumentException>(() => BlockNameRules.LibraryFile(@"C:\biblioteca", "*U1"));
            Assert.Equal(Anonimos, ex.Message);
        }

        [Fact]
        public void LibraryFile_CarpetaVacia_Lanza()
        {
            Assert.Throws<ArgumentException>(() => BlockNameRules.LibraryFile("  ", "DP"));
        }
    }
}
