using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Clean;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Clean
{
    public class PurgeScopeTests
    {
        [Fact]
        public void SinSolo_TodoEstaEnAmbito()
        {
            Assert.True(PurgeRules.InScope("CUALQUIERA", null));
        }

        [Fact]
        public void ConSolo_SoloLosNombresPedidosSonCandidatos_SinMayusculas()
        {
            var solo = new List<string> { "MURO-01" };
            Assert.True(PurgeRules.InScope("muro-01", solo));
            Assert.False(PurgeRules.InScope("MURO-02", solo));
            Assert.False(PurgeRules.InScope("", solo));
        }

        [Fact]
        public void MotivoNoPurgable_PrioridadCategoriaXrefProtegidoExcluidoEnUso()
        {
            Assert.Equal("categoría no pedida en 'categorias'", PurgeRules.MotivoNoPurgable(false, true, true, true));
            Assert.StartsWith("dependiente de xref", PurgeRules.MotivoNoPurgable(true, true, true, true));
            Assert.StartsWith("protegido", PurgeRules.MotivoNoPurgable(true, false, true, true));
            Assert.Equal("excluido por 'excluir'", PurgeRules.MotivoNoPurgable(true, false, false, true));
            Assert.StartsWith("en uso", PurgeRules.MotivoNoPurgable(true, false, false, false));
        }
    }
}
