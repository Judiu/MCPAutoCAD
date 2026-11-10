using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Clean;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Clean
{
    /// <summary>
    /// Validación del mapa de normalizar_capas (protocol.md §5F.3) con el lps.json real del repositorio (respaldo
    /// embebido si no se encuentra) y las 33 capas reales del plano limpio (casa6x12_limpio.dwg, ODA + ezdxf, 2026-10-27).
    /// </summary>
    public class LayerMapValidatorTests
    {
        private const string FallbackJson = @"{
  ""capas_preferidas"": { ""muros"": ""MUROS"", ""cotas"": ""Cotas"", ""textos"": ""TEXTOS"", ""achurados"": ""Achurados"",
    ""escaleras"": ""ESCALERA"", ""dibujo_mcp"": ""MCP-DIBUJO"" },
  ""capas_mcp"": { ""MCP-DIBUJO"": { ""color"": 3, ""tipo_linea"": ""Continuous"", ""grosor"": -3, ""plot"": 1 } },
  ""capas_permitidas"": {
    ""MUROS"": { ""color"": 155, ""tipo_linea"": ""Continuous"", ""grosor"": 50, ""plot"": 1 },
    ""TEXTOS"": { ""color"": 7, ""tipo_linea"": ""Continuous"", ""grosor"": -3, ""plot"": 1 },
    ""Achurados"": { ""color"": 8, ""tipo_linea"": ""Continuous"", ""grosor"": -3, ""plot"": 1 },
    ""ESCALERA"": { ""color"": 6, ""tipo_linea"": ""Continuous"", ""grosor"": 5, ""plot"": 1 }
  }
}";

        /// <summary>Capas del plano limpio (orden del archivo); ninguna bloqueada, congelada ni xref; actual MUROS.</summary>
        private static readonly string[] CapasLimpio =
        {
            "0", "EJES", "DEFPOINTS", "TEXT", "Cotas", "MUROS", "MUR BAAAJO", "Achurados", "TEXTOS",
            "3D LINEAS DE PLANTA_Pen_No__7", "Dibujo 2D - General_Pen_No__142", "Dibujo 2D - General_Pen_No__102",
            "3D EJES ESTRUCTURALES_Pen_No__7", "6. ESTRUCTURA", "columnas", "ventanas", "Rotulo", "AC", "m fijos",
            "PUERTAS", "ESC", "Proyeccion", "Antepecho", "Filos", "Sanitarios", "ACHU", "muebles fijos",
            "A-DOOR-____-OTLN", "S-COLS", "I-FURN-____-OTLN", "Q-SPCQ-____-OTLN", "P-SANR-FIXT-OTLN", "Furniture",
        };

        private static readonly Lazy<LpsLayerRules> Loaded = new Lazy<LpsLayerRules>(Load);

        private static LpsLayerRules Rules => Loaded.Value;

        private static LpsLayerRules Load()
        {
            string? path = null;
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && path == null)
            {
                string candidate = Path.Combine(dir.FullName, "standards", "lps.json");
                path = File.Exists(candidate) ? candidate : null;
                dir = dir.Parent;
            }

            using JsonDocument doc = JsonDocument.Parse(path != null ? File.ReadAllText(path) : FallbackJson);
            return LpsLayerRules.FromJson(doc.RootElement.Clone());
        }

        private static Dictionary<string, LayerState> Capas(params LayerState[] extra)
        {
            var d = new Dictionary<string, LayerState>(StringComparer.Ordinal);
            foreach (string n in CapasLimpio)
            {
                d[n] = new LayerState(n, false, false, false, n == "MUROS");
            }

            foreach (LayerState s in extra)
            {
                d[s.Nombre] = s;
            }

            return d;
        }

        private static List<KeyValuePair<string, string>> Mapa(params (string o, string d)[] pares) =>
            pares.Select(p => new KeyValuePair<string, string>(p.o, p.d)).ToList();

        private static LayerMapError? V(
            List<KeyValuePair<string, string>> mapa, out IReadOnlyList<LayerMapEntry> e, bool incluirBloques = false,
            Dictionary<string, LayerState>? capas = null) =>
            LayerMapValidator.Validate(mapa, capas ?? Capas(), Rules, incluirBloques, out e);

        private static void AssertError(LayerMapError? err, string codigo, string detalle, string? capa = null)
        {
            Assert.NotNull(err);
            Assert.Equal(codigo, err!.Codigo);
            Assert.Equal(detalle, err.Detalle);
            if (capa != null)
            {
                Assert.Equal(capa, err.Capa);
            }
        }

        [Fact]
        public void MapaDePruebaDelGate_TresEntradas()
        {
            LayerMapError? err = V(Mapa(("TEXT", "textos"), ("ACHU", "achurados"), ("ESC", "ESCALERA")), out var e);
            Assert.Null(err);
            Assert.Equal(3, e.Count);

            Assert.Equal("TEXT", e[0].Origen);
            Assert.Equal("TEXTOS", e[0].Destino.Nombre);
            Assert.Equal("textos", e[0].Destino.Concepto);
            Assert.Equal(LayerPlan.OrigenConcepto, e[0].Destino.Origen);
            Assert.True(e[0].DestinoExiste);

            Assert.Equal("ACHU", e[1].Origen);
            Assert.Equal("Achurados", e[1].Destino.Nombre);
            Assert.True(e[1].DestinoExiste);

            Assert.Equal("ESC", e[2].Origen);
            Assert.Equal("ESCALERA", e[2].Destino.Nombre);
            Assert.Null(e[2].Destino.Concepto);
            Assert.False(e[2].DestinoExiste);
            Assert.True(e[2].Destino.Creable);
        }

        [Fact]
        public void OrigenConOtrasMayusculas_DevuelveNombreReal_YDestinoExistenteConNombreReal()
        {
            Assert.Null(V(Mapa(("text", "textos"), ("achu", "ACHURADOS")), out var e));
            Assert.Equal("TEXT", e[0].Origen);
            Assert.Equal("ACHU", e[1].Origen);
            Assert.Equal("Achurados", e[1].Destino.Nombre); // concepto "achurados" sin distinguir mayúsculas
        }

        [Fact]
        public void DestinoCapaExistenteQueNoEsConcepto_TalCual()
        {
            Assert.Null(V(Mapa(("Furniture", "muebles fijos"), ("I-FURN-____-OTLN", "MUEBLES FIJOS")), out var e));
            Assert.All(e, x => Assert.Equal("muebles fijos", x.Destino.Nombre));
            Assert.All(e, x => Assert.True(x.DestinoExiste));
        }

        [Fact]
        public void OrigenesInexistentes_NotFound_ConTodos()
        {
            LayerMapError? err = V(Mapa(("TEXT", "textos"), ("NOEXISTE", "textos"), ("MUEBLE", "muros"), ("noexiste", "muros")), out var e);
            AssertError(err, "NOT_FOUND", "mapa: capas de origen inexistentes: NOEXISTE, MUEBLE");
            Assert.Equal(new[] { "NOEXISTE", "MUEBLE" }, err!.Capas);
            Assert.Empty(e);
        }

        [Fact]
        public void OrigenInexistente_GanaAlRestoDeErrores()
        {
            AssertError(V(Mapa(("DEFPOINTS", "muros"), ("X", "Y")), out _), "NOT_FOUND", "mapa: capas de origen inexistentes: X");
        }

        [Fact]
        public void OrigenDefpoints_InvalidParams()
        {
            AssertError(V(Mapa(("defpoints", "muros")), out _), "INVALID_PARAMS", "mapa: la capa DEFPOINTS no se normaliza");
        }

        [Fact]
        public void OrigenCero_ConIncluirBloques_InvalidParams_SinIncluirBloques_Ok()
        {
            AssertError(
                V(Mapa(("0", "muros")), out _, incluirBloques: true),
                "INVALID_PARAMS",
                "mapa: la capa 0 no puede ser origen con incluir_bloques: true (la capa 0 dentro de los bloques es correcta)");

            Assert.Null(V(Mapa(("0", "muros")), out var e));
            Assert.Equal("0", e[0].Origen);
            Assert.Equal("MUROS", e[0].Destino.Nombre);
        }

        [Fact]
        public void OrigenRepetido_ConMayusculasDistintas()
        {
            AssertError(V(Mapa(("text", "textos"), ("TEXT", "textos")), out _), "INVALID_PARAMS", "mapa: origen repetido 'TEXT'");
        }

        [Fact]
        public void DestinoNoCreable_NotFound()
        {
            LayerMapError? err = V(Mapa(("TEXT", "CAPAX")), out _);
            AssertError(
                err, "NOT_FOUND",
                "mapa['TEXT']: la capa destino 'CAPAX' no existe y no está en lps.json (capas_permitidas)",
                "CAPAX");
            Assert.Equal(err!.Detalle, LayerMapValidator.MissingDestination("TEXT", "CAPAX"));
        }

        /// <summary>
        /// Corrección de auditoría (ronda 1): ningún destino del MCP, exista o no, ni por nombre ni por concepto; mover
        /// entidades del usuario a MCP-* les quitaría la protección de §5D.8.
        /// </summary>
        [Theory]
        [InlineData("MCP-REVISAR", "MCP-REVISAR")]
        [InlineData("MCP-NUEVA", "MCP-NUEVA")]
        [InlineData("mcp-preview", "mcp-preview")]
        [InlineData("dibujo_mcp", "MCP-DIBUJO")]
        public void DestinoMcp_Inexistente_InvalidParams(string destino, string capa)
        {
            LayerMapError? err = V(Mapa(("AC", destino)), out var e);
            AssertError(
                err, "INVALID_PARAMS",
                "mapa['AC']: la capa destino '" + capa
                    + "' es una capa del MCP (MCP-*); las entidades del usuario no se mueven a capas del MCP",
                capa);
            Assert.Empty(e);
        }

        [Theory]
        [InlineData("MCP-OBSERVACIONES")]
        [InlineData("mcp-observaciones")]
        [InlineData("MCP-PREVIEW")]
        public void DestinoMcp_Existente_InvalidParams_ConNombreReal(string destino)
        {
            var capas = Capas(new LayerState("MCP-OBSERVACIONES", false, false, false, false),
                new LayerState("MCP-PREVIEW", false, false, false, false));
            string real = destino.ToUpperInvariant();
            AssertError(
                V(Mapa(("text", destino)), out _, capas: capas), "INVALID_PARAMS",
                "mapa['text']: la capa destino '" + real
                    + "' es una capa del MCP (MCP-*); las entidades del usuario no se mueven a capas del MCP",
                real);
        }

        [Fact]
        public void DestinoMcp_GanaAOrigenIgualDestino()
        {
            // Comprobación 3 antes que 4: un destino MCP-* falla por destino del MCP aunque la entrada siguiente
            // sea origen = destino.
            AssertError(
                V(Mapa(("TEXT", "MCP-X"), ("ACHU", "ACHU")), out _), "INVALID_PARAMS",
                LayerMapValidator.McpDestination("TEXT", "MCP-X"), "MCP-X");
        }

        /// <summary>
        /// Corrección de auditoría (ronda 2): un origen del MCP hacia una capa del usuario (p. ej. MCP-PREVIEW → TEXTOS)
        /// convertiría previsualizaciones u observaciones en entidades del usuario: INVALID_PARAMS en la comprobación 2.
        /// </summary>
        [Theory]
        [InlineData("MCP-PREVIEW", "textos", "MCP-PREVIEW")]
        [InlineData("mcp-preview", "TEXTOS", "MCP-PREVIEW")]
        [InlineData("MCP-X", "textos", "MCP-X")]
        [InlineData("MCP-X", "MCP-X", "MCP-X")]
        [InlineData("MCP-DIBUJO", "CAPAX", "MCP-DIBUJO")]
        public void OrigenMcp_InvalidParams_ConNombreReal(string origen, string destino, string real)
        {
            var capas = Capas(new LayerState("MCP-PREVIEW", false, false, false, false),
                new LayerState("MCP-X", false, false, false, false), new LayerState("MCP-DIBUJO", false, false, false, false));
            LayerMapError? err = V(Mapa(("TEXT", "textos"), (origen, destino)), out var e, capas: capas);
            AssertError(
                err, "INVALID_PARAMS",
                "mapa['" + origen + "']: la capa origen '" + real
                    + "' es una capa del MCP (MCP-*); sus entidades no se mueven a capas del usuario",
                real);
            Assert.Equal(err!.Detalle, LayerMapValidator.McpOrigin(origen, real));
            Assert.Empty(e);
        }

        [Fact]
        public void OrigenMcp_OrdenDentroDeLaComprobacion2()
        {
            var capas = Capas(new LayerState("MCP-PREVIEW", false, false, false, false));

            // DEFPOINTS va antes que el origen del MCP; el origen del MCP antes que "0" con incluir_bloques y que los repetidos.
            AssertError(
                V(Mapa(("MCP-PREVIEW", "textos"), ("DEFPOINTS", "textos")), out _, capas: capas), "INVALID_PARAMS",
                "mapa: la capa DEFPOINTS no se normaliza");
            AssertError(
                V(Mapa(("0", "textos"), ("MCP-PREVIEW", "textos")), out _, incluirBloques: true, capas: capas), "INVALID_PARAMS",
                LayerMapValidator.McpOrigin("MCP-PREVIEW", "MCP-PREVIEW"), "MCP-PREVIEW");
            AssertError(
                V(Mapa(("text", "textos"), ("TEXT", "textos"), ("MCP-PREVIEW", "textos")), out _, capas: capas),
                "INVALID_PARAMS", LayerMapValidator.McpOrigin("MCP-PREVIEW", "MCP-PREVIEW"), "MCP-PREVIEW");

            // Los orígenes inexistentes (comprobación 1) siguen ganando.
            AssertError(
                V(Mapa(("MCP-PREVIEW", "textos"), ("NOPE", "textos")), out _, capas: capas), "NOT_FOUND",
                "mapa: capas de origen inexistentes: NOPE");
        }

        [Fact]
        public void DestinoMcp_PrimeraEntradaEnOrdenDelMapa()
        {
            AssertError(
                V(Mapa(("TEXT", "textos"), ("ACHU", "MCP-A"), ("ESC", "CAPAX")), out _), "INVALID_PARAMS",
                LayerMapValidator.McpDestination("ACHU", "MCP-A"), "MCP-A");
        }

        [Fact]
        public void DestinoVacio_InvalidParams_NombreNoValido()
        {
            AssertError(V(Mapa(("TEXT", "   ")), out _), "INVALID_PARAMS", "capa: nombre de capa no válido", "   ");
        }

        /// <summary>
        /// Corrección de auditoría (ronda 3): un destino con caracteres no válidos para un símbolo (los de
        /// SymbolUtilityServices.ValidateSymbolName) es INVALID_PARAMS "capa: nombre de capa no válido" con Capa = el
        /// destino tal como se escribió, antes del rechazo del MCP y del NOT_FOUND (antes salía NOT_FOUND).
        /// </summary>
        [Theory]
        [InlineData("A<B")]
        [InlineData("A>B")]
        [InlineData("A/B")]
        [InlineData("A\\B")]
        [InlineData("A\"B")]
        [InlineData("A:B")]
        [InlineData("A;B")]
        [InlineData("A?B")]
        [InlineData("A*B")]
        [InlineData("A,B")]
        [InlineData("A=B")]
        [InlineData("A`B")]
        [InlineData("A|B")]
        [InlineData(" textos")]
        [InlineData("textos ")]
        [InlineData("MCP-A<B")]
        public void DestinoConCaracteresNoValidos_InvalidParams(string destino)
        {
            LayerMapError? err = V(Mapa(("TEXT", destino)), out var e);
            AssertError(err, "INVALID_PARAMS", LayerResolver.InvalidName, destino);
            Assert.Empty(e);
        }

        [Fact]
        public void DestinoNoValido_PrimeraEntradaEnOrden_YDestinoXrefExistenteSigueALaComprobacion6()
        {
            // En orden del mapa: la entrada anterior con destino no creable responde antes.
            AssertError(V(Mapa(("TEXT", "CAPAX"), ("ACHU", "A<B")), out _), "NOT_FOUND",
                LayerMapValidator.MissingDestination("TEXT", "CAPAX"), "CAPAX");
            AssertError(V(Mapa(("TEXT", "A<B"), ("ACHU", "MCP-A")), out _), "INVALID_PARAMS",
                LayerResolver.InvalidName, "A<B");

            // Una capa existente de xref (con barra vertical) no es "nombre no válido": la rechaza la comprobación 6.
            var capas = Capas(new LayerState("XR|MUROS", false, false, true, false));
            AssertError(V(Mapa(("TEXT", "xr|muros")), out _, capas: capas), "INVALID_PARAMS",
                "capa dependiente de xref", "XR|MUROS");
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void OrigenVacio_InvalidParams_AntesQueInexistentes(string origen)
        {
            LayerMapError? err = V(Mapa(("NOEXISTE", "textos"), (origen, "textos")), out var e);
            AssertError(err, "INVALID_PARAMS", "mapa: la capa origen no puede estar vacía");
            Assert.Null(err!.Capas);
            Assert.Null(err.Capa);
            Assert.Empty(e);
        }

        [Fact]
        public void OrigenIgualDestino_PorConcepto()
        {
            AssertError(V(Mapa(("MUROS", "muros")), out _), "INVALID_PARAMS", "mapa['MUROS']: origen y destino son la misma capa");
        }

        [Fact]
        public void Cadena_AaB_BaC_DestinoEsOrigen()
        {
            var capas = Capas(new LayerState("A", false, false, false, false), new LayerState("B", false, false, false, false),
                new LayerState("C", false, false, false, false));
            AssertError(
                V(Mapa(("A", "B"), ("B", "C")), out _, capas: capas),
                "INVALID_PARAMS",
                "mapa: el destino 'B' es también origen (cadena o ciclo); aplane el mapa para que cada origen vaya directamente a su capa final");
        }

        [Fact]
        public void Ciclo_AaB_BaA_DestinoEsOrigen()
        {
            var capas = Capas(new LayerState("A", false, false, false, false), new LayerState("B", false, false, false, false));
            AssertError(
                V(Mapa(("A", "b"), ("B", "a")), out _, capas: capas),
                "INVALID_PARAMS",
                "mapa: el destino 'B' es también origen (cadena o ciclo); aplane el mapa para que cada origen vaya directamente a su capa final");
        }

        [Theory]
        [InlineData(true, false, false, "capa bloqueada")]
        [InlineData(false, true, false, "capa congelada")]
        [InlineData(false, false, true, "capa dependiente de xref")]
        [InlineData(true, true, true, "capa bloqueada")]
        public void EstadoDelOrigen(bool bloqueada, bool congelada, bool xref, string detalle)
        {
            var capas = Capas(new LayerState("TEXT", bloqueada, congelada, xref, false));
            AssertError(V(Mapa(("text", "textos")), out _, capas: capas), "INVALID_PARAMS", detalle, "TEXT");
        }

        [Theory]
        [InlineData(true, false, false, "capa bloqueada")]
        [InlineData(false, true, false, "capa congelada")]
        [InlineData(false, false, true, "capa dependiente de xref")]
        public void EstadoDelDestinoExistente(bool bloqueada, bool congelada, bool xref, string detalle)
        {
            var capas = Capas(new LayerState("TEXTOS", bloqueada, congelada, xref, false));
            AssertError(V(Mapa(("TEXT", "textos")), out _, capas: capas), "INVALID_PARAMS", detalle, "TEXTOS");
        }

        [Fact]
        public void ForcedPropertiesWarning_Literal()
        {
            Assert.Equal(
                "639 entidad(es) reasignada(s) conservan color, tipo de línea o grosor propios (no ByLayer); no se cambiaron.",
                LayerMapValidator.ForcedPropertiesWarning(639));
        }
    }
}
