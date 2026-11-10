using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Clean;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Clean
{
    /// <summary>
    /// Exclusiones fijas y punto fijo de purgar (protocol.md §5F.2) con nombres reales del plano madre (§5F.7):
    /// capa actual MUROS, estilo de cota actual COTA75, bloque gigante sin uso "VS 2 2025_6X12  ARQ CLIENTE_E".
    /// </summary>
    public class PurgeRulesTests
    {
        private static readonly PurgeContext Ctx = new PurgeContext("MUROS", "ByLayer", "75", "COTA75");

        private const string Gigante = "VS 2 2025_6X12  ARQ CLIENTE_E";

        private static (string, string) Blk(string n) => (PurgeCategories.Bloques, n);

        private static (string, string) Lay(string n) => (PurgeCategories.Capas, n);

        private static (string, string) Lt(string n) => (PurgeCategories.TiposLinea, n);

        private static (string, string) Dim(string n) => (PurgeCategories.EstilosCota, n);

        private static readonly (string, string) Modelo = ("espacio", "modelo");

        private static bool P(string cat, string name, bool xref = false, IReadOnlyCollection<string>? excluir = null) =>
            PurgeRules.IsProtected(cat, name, Ctx, xref, excluir);

        [Theory]
        [InlineData("0")]
        [InlineData("DEFPOINTS")]
        [InlineData("Defpoints")]
        [InlineData("MUROS")]
        [InlineData("muros")]
        [InlineData("MCP-DIBUJO")]
        [InlineData("mcp-observaciones")]
        public void Capas_Protegidas(string nombre)
        {
            Assert.True(P(PurgeCategories.Capas, nombre));
        }

        [Theory]
        [InlineData("MUEBLE")]
        [InlineData("3. MOBILIARIO")]
        [InlineData("m fijos")]
        public void Capas_NoProtegidas(string nombre)
        {
            Assert.False(P(PurgeCategories.Capas, nombre));
        }

        [Theory]
        [InlineData("ByLayer", true)]
        [InlineData("BYBLOCK", true)]
        [InlineData("continuous", true)]
        [InlineData("DASHED", false)]
        public void TiposLinea(string nombre, bool esperado)
        {
            Assert.Equal(esperado, P(PurgeCategories.TiposLinea, nombre));
        }

        [Fact]
        public void TipoLineaActual_Protegido()
        {
            var ctx = Ctx with { TipoLineaActual = "DASHED" };
            Assert.True(PurgeRules.IsProtected(PurgeCategories.TiposLinea, "dashed", ctx, false, null));
        }

        [Theory]
        [InlineData("Standard", true)]
        [InlineData("75", true)]
        [InlineData("ROMANS", false)]
        public void EstilosTexto(string nombre, bool esperado)
        {
            Assert.Equal(esperado, P(PurgeCategories.EstilosTexto, nombre));
        }

        [Theory]
        [InlineData("STANDARD", true)]
        [InlineData("cota75", true)]
        [InlineData("ISO-25", false)]
        public void EstilosCota(string nombre, bool esperado)
        {
            Assert.Equal(esperado, P(PurgeCategories.EstilosCota, nombre));
        }

        [Theory]
        [InlineData("*Model_Space", false, true)]
        [InlineData("*MODEL_SPACE", false, true)]
        [InlineData("*Paper_Space", false, true)]
        [InlineData("*Paper_Space0", false, true)]
        [InlineData("*paper_space12", false, true)]
        [InlineData("PLANTA_XREF", true, true)]
        [InlineData(Gigante, false, false)]
        [InlineData("*U1234", false, false)]
        [InlineData("_ArchTick", false, false)]
        public void Bloques(string nombre, bool xref, bool esperado)
        {
            Assert.Equal(esperado, P(PurgeCategories.Bloques, nombre, xref));
        }

        [Theory]
        [InlineData("ACAD", true)]
        [InlineData("acad", true)]
        [InlineData("ACAD_PSEXT", false)]
        public void Regapps(string nombre, bool esperado)
        {
            Assert.Equal(esperado, P(PurgeCategories.Regapps, nombre));
        }

        [Fact]
        public void Excluir_SinDistinguirMayusculas_EnCualquierCategoria()
        {
            var excluir = new[] { "mueble", "Puerta Dinamica LM", "acad_psext" };
            Assert.True(P(PurgeCategories.Capas, "MUEBLE", excluir: excluir));
            Assert.True(P(PurgeCategories.Bloques, "PUERTA DINAMICA LM", excluir: excluir));
            Assert.True(P(PurgeCategories.Regapps, "ACAD_PSEXT", excluir: excluir));
            Assert.False(P(PurgeCategories.Capas, "3. MOBILIARIO", excluir: excluir));
        }

        [Fact]
        public void CategoriaDesconocida_ArgumentException()
        {
            var ex = Assert.Throws<ArgumentException>(() => P("materiales", "X"));
            Assert.Equal(PurgeCategories.InvalidCategory(0), ex.Message);
            Assert.Throws<ArgumentException>(() => P(PurgeCategories.Todo, "X"));
        }

        [Fact]
        public void Categoria_SinDistinguirMayusculas()
        {
            Assert.True(P("CAPAS", "0"));
        }

        /// <summary>Grafo simplificado del plano madre tras borrar_fuera_de.</summary>
        private static IReadOnlyList<IReadOnlyList<(string categoria, string nombre)>> PlanoMadre()
        {
            var nodos = new[]
            {
                Blk(Gigante), Blk("car"), Blk("PERSO13"), Blk("C"), Blk("A"), Blk("B"), Blk("Puerta Dinamica LM"),
                Blk("*U12"), Blk("VENTANA01"), Blk("*U99"),
                Lay("MUEBLE"), Lay("Cotas"), Lay("MUROS"),
                Lt("DASHED"), Lt("CENTER"),
                Dim("COTA75"), Dim("ISO-25"),
            };
            var refs = new List<((string, string), (string, string))>
            {
                (Modelo, Blk("C")),
                (Modelo, Lay("Cotas")),
                (Modelo, Dim("COTA75")),
                (Modelo, Blk("Puerta Dinamica LM")),     // lo que B hará con las representaciones dinámicas
                (Modelo, Blk("*U12")),
                (Blk("*U12"), Blk("VENTANA01")),          // anónimo vivo que referencia un bloque con nombre
                (Blk(Gigante), Blk("car")),
                (Blk(Gigante), Blk("PERSO13")),
                (Blk(Gigante), Lay("Cotas")),            // también la usa el modelo: no se purga
                (Blk(Gigante), Lay("muros")),            // capa actual (raíz) con otras mayúsculas
                (Blk("car"), Lay("MUEBLE")),
                (Lay("MUEBLE"), Lt("DASHED")),
                (Lay("Cotas"), Lt("CENTER")),            // capa viva → CENTER vivo
                (Blk("A"), Blk("B")),
                (Blk("B"), Blk("A")),
                (Blk("*U99"), Blk("*U99")),              // autorreferencia: se ignora
                (Blk("C"), Blk("NO_ES_NODO")),           // destino desconocido: se ignora
            };
            var raices = new[] { Modelo, Lay("MUROS"), Dim("ISO-25") };
            return PurgeRules.FixedPoint(nodos, refs, raices);
        }

        [Fact]
        public void FixedPoint_PlanoMadre_Rondas()
        {
            var rondas = PlanoMadre();
            Assert.Equal(4, rondas.Count);
            Assert.Equal(new[] { Blk("*U99"), Blk("A"), Blk("B"), Blk(Gigante) }, rondas[0]);
            Assert.Equal(new[] { Blk("car"), Blk("PERSO13") }, rondas[1]);
            Assert.Equal(new[] { Lay("MUEBLE") }, rondas[2]);
            Assert.Equal(new[] { Lt("DASHED") }, rondas[3]);
        }

        [Fact]
        public void FixedPoint_PlanoMadre_NoPurgaLoVivoNiLasRaices()
        {
            var todos = PlanoMadre().SelectMany(r => r).Select(s => s.nombre).ToList();
            foreach (string vivo in new[] { "C", "Cotas", "COTA75", "Puerta Dinamica LM", "*U12", "VENTANA01", "CENTER", "MUROS", "ISO-25", "NO_ES_NODO" })
            {
                Assert.DoesNotContain(vivo, todos);
            }
        }

        [Fact]
        public void FixedPoint_CicloSinEntrada_MismaRonda_YArrastraLoQueReferencia()
        {
            var nodos = new[] { Blk("A"), Blk("B"), Blk("D"), Lay("L1") };
            var refs = new[]
            {
                (Blk("A"), Blk("B")), (Blk("B"), Blk("A")), (Blk("B"), Blk("D")), (Blk("D"), Lay("L1")),
            };
            var rondas = PurgeRules.FixedPoint(nodos, refs, Array.Empty<(string, string)>());
            Assert.Equal(3, rondas.Count);
            Assert.Equal(new[] { Blk("A"), Blk("B") }, rondas[0]);
            Assert.Equal(new[] { Blk("D") }, rondas[1]);
            Assert.Equal(new[] { Lay("L1") }, rondas[2]);
        }

        [Fact]
        public void FixedPoint_CicloAlcanzableDesdeRaiz_NoSePurga()
        {
            var nodos = new[] { Blk("A"), Blk("B") };
            var refs = new[] { (Modelo, Blk("A")), (Blk("A"), Blk("B")), (Blk("B"), Blk("A")) };
            Assert.Empty(PurgeRules.FixedPoint(nodos, refs, new[] { Modelo }));
        }

        [Fact]
        public void FixedPoint_RondaEsLaMasLarga_DiamanteConCaminoLargo()
        {
            // X → Y → Z y X → Z: Z solo cae cuando caen X y Y (ronda 3).
            var nodos = new[] { Blk("X"), Blk("Y"), Blk("Z") };
            var refs = new[] { (Blk("X"), Blk("Y")), (Blk("Y"), Blk("Z")), (Blk("X"), Blk("Z")) };
            var rondas = PurgeRules.FixedPoint(nodos, refs, Array.Empty<(string, string)>());
            Assert.Equal(new[] { new[] { Blk("X") }, new[] { Blk("Y") }, new[] { Blk("Z") } }, rondas.Select(r => r.ToArray()).ToArray());
        }

        [Fact]
        public void FixedPoint_OrdenDentroDeRonda_CategoriaYNombreSinMayusculas()
        {
            var nodos = new[]
            {
                (PurgeCategories.EstilosTexto, "ROMANS"), Lay("b"), Blk("_DotSmall"), Lay("A"), Blk("_ArchTick"),
                (PurgeCategories.Regapps, "ACAD_PSEXT"), Lt("DASHED"), Dim("ISO-25"),
            };
            var rondas = PurgeRules.FixedPoint(nodos, Array.Empty<((string, string), (string, string))>(), Array.Empty<(string, string)>());
            Assert.Single(rondas);
            Assert.Equal(
                new[]
                {
                    Blk("_ArchTick"), Blk("_DotSmall"), Lay("A"), Lay("b"), Lt("DASHED"),
                    (PurgeCategories.EstilosTexto, "ROMANS"), Dim("ISO-25"), (PurgeCategories.Regapps, "ACAD_PSEXT"),
                },
                rondas[0]);
        }

        [Fact]
        public void FixedPoint_SinNodos_SinRondas()
        {
            Assert.Empty(PurgeRules.FixedPoint(
                Array.Empty<(string, string)>(), Array.Empty<((string, string), (string, string))>(), new[] { Modelo }));
        }

        [Fact]
        public void FixedPoint_CadenaLargaDeAnonimos_SinDesbordarLaPila()
        {
            const int n = 7413; // anónimos del plano madre
            var nodos = Enumerable.Range(0, n).Select(i => Blk("*U" + i)).ToArray();
            var refs = Enumerable.Range(0, n - 1).Select(i => (Blk("*U" + i), Blk("*U" + (i + 1)))).ToArray();
            var rondas = PurgeRules.FixedPoint(nodos, refs, Array.Empty<(string, string)>());
            Assert.Equal(n, rondas.Count);
            Assert.Equal(Blk("*U7412"), rondas[n - 1].Single());
        }

        [Fact]
        public void Capa_mcp_solo_se_libera_si_solo_la_nombra()
        {
            Assert.True(PurgeRules.McpLayerReleasedBySolo(PurgeCategories.Capas, "MCP-PRUEBA", new[] { "mcp-prueba" }));
            Assert.False(PurgeRules.McpLayerReleasedBySolo(PurgeCategories.Capas, "MCP-PRUEBA", null));
            Assert.False(PurgeRules.McpLayerReleasedBySolo(PurgeCategories.Capas, "MCP-PRUEBA", new[] { "MCP-OTRA" }));
            Assert.False(PurgeRules.McpLayerReleasedBySolo(PurgeCategories.Capas, "MUROS", new[] { "MUROS" }));
            Assert.False(PurgeRules.McpLayerReleasedBySolo(PurgeCategories.Bloques, "MCP-PRUEBA", new[] { "MCP-PRUEBA" }));
            Assert.True(PurgeRules.IsProtected(PurgeCategories.Capas, "MCP-PRUEBA", Ctx, false, null));
        }
    }
}
