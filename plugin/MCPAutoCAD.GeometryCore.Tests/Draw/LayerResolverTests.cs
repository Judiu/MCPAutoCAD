using System;
using System.Text.Json;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>Regla R3 de protocol.md §5D.0 con un lps.json reducido (mismos valores que standards/lps.json).</summary>
    public class LayerResolverTests
    {
        private static LpsLayerRules Rules()
        {
            const string json = @"{
              ""capas_preferidas"": { ""muros"": ""MUROS"", ""cotas"": ""Cotas"", ""achurados"": ""Achurados"", ""dibujo_mcp"": ""MCP-DIBUJO"" },
              ""capas_mcp"": { ""MCP-OBSERVACIONES"": { ""color"": 1, ""tipo_linea"": ""Continuous"", ""grosor"": 35, ""plot"": 1 },
                              ""MCP-DIBUJO"": { ""color"": 3, ""tipo_linea"": ""Continuous"", ""grosor"": -3, ""plot"": 1 } },
              ""capas_permitidas"": { ""MUROS"": { ""color"": 155, ""tipo_linea"": ""Continuous"", ""grosor"": 50, ""plot"": 1 },
                                     ""Cotas"": { ""color"": 8, ""tipo_linea"": ""Continuous"", ""grosor"": 13, ""plot"": 1 },
                                     ""EJES"": { ""color"": 1, ""tipo_linea"": ""CENTER"", ""grosor"": 5, ""plot"": 1 } }
            }";
            using JsonDocument d = JsonDocument.Parse(json);
            return LpsLayerRules.FromJson(d.RootElement.Clone());
        }

        [Fact]
        public void SinCapa_UsaMcpDibujoConSuEspecificacion()
        {
            LayerPlan p = LayerResolver.Resolve(null, null, Rules());
            Assert.Equal("MCP-DIBUJO", p.Nombre);
            Assert.Equal(LayerPlan.OrigenDefecto, p.Origen);
            Assert.True(p.Creable);
            Assert.Equal(3, p.Especificacion!.Color);
        }

        [Fact]
        public void SinCapa_ConConceptoPorDefecto_UsaLaPreferida()
        {
            LayerPlan p = LayerResolver.Resolve(null, "cotas", Rules());
            Assert.Equal("Cotas", p.Nombre);
            Assert.Equal(LayerPlan.OrigenDefecto, p.Origen);
            Assert.Equal("cotas", p.Concepto);
            Assert.Equal(13, p.Especificacion!.Grosor);
        }

        [Fact]
        public void SinCapa_ConceptoDesconocido_CaeAMcpDibujo()
        {
            LayerPlan p = LayerResolver.Resolve(null, "no_existe", Rules());
            Assert.Equal("MCP-DIBUJO", p.Nombre);
        }

        [Theory]
        [InlineData("muros", "MUROS", true)]
        [InlineData("MUROS", "MUROS", true)]
        [InlineData("Achurados", "Achurados", false)]
        public void Concepto_SinMayusculas_DevuelveLaCapaPreferida(string clave, string esperada, bool creable)
        {
            LayerPlan p = LayerResolver.Resolve(clave, null, Rules());
            Assert.Equal(esperada, p.Nombre);
            Assert.Equal(LayerPlan.OrigenConcepto, p.Origen);
            // Un concepto cuya capa no está en capas_permitidas del lps.json reducido no es creable (debe existir).
            Assert.Equal(creable, p.Creable);
            if (creable)
            {
                Assert.Equal(155, p.Especificacion!.Color);
            }
        }

        [Fact]
        public void NombreDelEstandar_EsCreableConSusPropiedades()
        {
            LayerPlan p = LayerResolver.Resolve("ejes", null, Rules());
            Assert.Equal("ejes", p.Nombre); // se busca tal cual (AutoCAD no distingue mayúsculas)
            Assert.Equal(LayerPlan.OrigenNombre, p.Origen);
            Assert.True(p.Creable);
            Assert.Equal("CENTER", p.Especificacion!.TipoLinea);
        }

        [Fact]
        public void NombreMcp_NoDefinido_EsCreableConRespaldo()
        {
            LayerPlan p = LayerResolver.Resolve("MCP-EJES-PROVISIONALES", null, Rules());
            Assert.True(p.Creable);
            Assert.Same(LayerResolver.McpFallback, p.Especificacion);
            Assert.Equal(3, p.Especificacion!.Color);
            Assert.Equal(-3, p.Especificacion.Grosor);
        }

        [Fact]
        public void NombreArbitrario_NoEsCreable()
        {
            LayerPlan p = LayerResolver.Resolve("CAPA_INVENTADA", null, Rules());
            Assert.False(p.Creable);
            Assert.Null(p.Especificacion);
            Assert.Equal("CAPA_INVENTADA", p.Nombre);
        }

        [Fact]
        public void SinEstandar_SoloMcpEsCreable()
        {
            Assert.False(LayerResolver.Resolve("MUROS", null, LpsLayerRules.Empty).Creable);
            Assert.True(LayerResolver.Resolve("MCP-X", null, LpsLayerRules.Empty).Creable);
            Assert.True(LayerResolver.Resolve(null, "cotas", LpsLayerRules.Empty).Creable);
            Assert.Equal("MCP-DIBUJO", LayerResolver.Resolve(null, "cotas", LpsLayerRules.Empty).Nombre);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void NombreVacio_Lanza(string capa)
        {
            var ex = Assert.Throws<ArgumentException>(() => LayerResolver.Resolve(capa, null, Rules()));
            Assert.Equal(LayerResolver.InvalidName, ex.Message);
        }

        [Fact]
        public void NombreConEspacios_SeRecorta()
        {
            Assert.Equal("MUROS", LayerResolver.Resolve("  muros ", null, Rules()).Nombre);
        }
    }
}
