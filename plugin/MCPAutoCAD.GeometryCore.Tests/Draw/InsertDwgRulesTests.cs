using System;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class InsertDwgRulesTests
    {
        [Theory]
        [InlineData(@"C:\proyectos\nivel1.dwg")]
        [InlineData("D:/obra/planta.DWG")]
        public void ValidatePath_AcceptsLocalDwgPaths(string ruta)
        {
            Assert.Null(InsertDwgRules.ValidatePath(ruta));
        }

        [Theory]
        [InlineData("")]
        [InlineData(@"\\servidor\compartida\plano.dwg")]
        [InlineData(@"..\plano.dwg")]
        [InlineData(@"C:\obra\*.dwg")]
        [InlineData(@"C:\obra\plano.dxf")]
        [InlineData(@"C:\obra\plano.dwg:stream")]
        public void ValidatePath_RejectsUnsafeOrNonDwg(string ruta)
        {
            Assert.NotNull(InsertDwgRules.ValidatePath(ruta));
        }

        [Fact]
        public void ValidatePrefix_ChecksLengthAndIllegalChars()
        {
            Assert.Null(InsertDwgRules.ValidatePrefix(null, "prefijo_capas"));
            Assert.Null(InsertDwgRules.ValidatePrefix("P2_", "prefijo_capas"));
            Assert.NotNull(InsertDwgRules.ValidatePrefix("", "prefijo_capas"));
            Assert.NotNull(InsertDwgRules.ValidatePrefix("P2|", "prefijo_capas"));
            Assert.NotNull(InsertDwgRules.ValidatePrefix("A*B", "prefijo_bloques"));
        }

        [Fact]
        public void ApplyPrefixes_PreservesSystemLayersAndAnonymousBlocks()
        {
            Assert.Equal("0", InsertDwgRules.ApplyLayerPrefix("0", "P2_"));
            Assert.Equal("DEFPOINTS", InsertDwgRules.ApplyLayerPrefix("DEFPOINTS", "P2_"));
            Assert.Equal("P2_A-WALL", InsertDwgRules.ApplyLayerPrefix("A-WALL", "P2_"));

            Assert.Equal("*U12", InsertDwgRules.ApplyBlockPrefix("*U12", "P2_"));
            Assert.Equal("P2_PUERTA", InsertDwgRules.ApplyBlockPrefix("PUERTA", "P2_"));
        }
    }
}
