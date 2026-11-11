using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class BorrarPorFiltroRulesTests
    {
        private static BorrarPorFiltroRules.Candidato C(
            string handle,
            string tipo,
            string capa,
            bool bloqueada = false,
            bool xref = false,
            bool congelada = false,
            bool atributo = false,
            bool viewport = false,
            bool enBloque = false,
            bool insertXref = false) =>
            new BorrarPorFiltroRules.Candidato(
                new EntityInfo(handle, capa, bloqueada, xref, congelada, atributo, viewport, enBloque, "modelo"),
                tipo,
                insertXref);

        private static readonly List<string> Muros = new List<string> { "MUROS" };

        // ---- Combinación de filtros (ValidateFilters).

        [Fact]
        public void SinNingunFiltro_SeRechaza()
        {
            Assert.NotNull(BorrarPorFiltroRules.ValidateFilters(null, false, false, null));
        }

        [Fact]
        public void CapasOTiposSinBbox_SonValidos_SinModo()
        {
            Assert.Null(BorrarPorFiltroRules.ValidateFilters(Muros, false, false, null));
            Assert.Null(BorrarPorFiltroRules.ValidateFilters(null, true, false, null));
        }

        [Fact]
        public void BboxSinModo_SeRechaza_ModoExplicitoPasa()
        {
            string? error = BorrarPorFiltroRules.ValidateFilters(null, false, true, null);
            Assert.NotNull(error);
            Assert.Contains("modo_bbox", error);
            Assert.Null(BorrarPorFiltroRules.ValidateFilters(null, false, true, "interseca"));
            Assert.Null(BorrarPorFiltroRules.ValidateFilters(null, false, true, "dentro"));
        }

        [Fact]
        public void ModoDentroSinBbox_SeRechaza()
        {
            Assert.Equal(
                "modo_bbox: 'dentro' requiere bbox",
                BorrarPorFiltroRules.ValidateFilters(Muros, false, false, "dentro"));
        }

        [Fact]
        public void FiltroQueCoincideConTodo_SeRechazaSinTiposNiBbox()
        {
            var todo = new List<string> { "*" };
            Assert.NotNull(BorrarPorFiltroRules.ValidateFilters(todo, false, false, null));
            Assert.NotNull(BorrarPorFiltroRules.ValidateFilters(new List<string> { "?*" }, false, false, null));
            Assert.NotNull(BorrarPorFiltroRules.ValidateFilters(new List<string> { "*", "**" }, false, false, null));
        }

        [Fact]
        public void FiltroQueCoincideConTodo_PasaConTiposOBbox()
        {
            var todo = new List<string> { "*" };
            Assert.Null(BorrarPorFiltroRules.ValidateFilters(todo, true, false, null));
            Assert.Null(BorrarPorFiltroRules.ValidateFilters(todo, false, true, "dentro"));
        }

        [Fact]
        public void PatronMixtoOEspecifico_NoCuentaComoTodo()
        {
            Assert.Null(BorrarPorFiltroRules.ValidateFilters(new List<string> { "*", "MUROS" }, false, false, null));
            Assert.Null(BorrarPorFiltroRules.ValidateFilters(new List<string> { "?" }, false, false, null));
            Assert.Null(BorrarPorFiltroRules.ValidateFilters(new List<string> { "MCP-*" }, false, false, null));
        }

        [Theory]
        [InlineData("*", true)]
        [InlineData("**", true)]
        [InlineData("?*", true)]
        [InlineData("*?", true)]
        [InlineData("*?*", true)]
        [InlineData("?", false)]
        [InlineData("", false)]
        [InlineData("MUROS*", false)]
        [InlineData("*A", false)]
        public void IsUniversalPattern_Casos(string patron, bool esperado)
        {
            Assert.Equal(esperado, BorrarPorFiltroRules.IsUniversalPattern(patron));
        }

        [Fact]
        public void IsUniversalPattern_Null_EsFalso()
        {
            Assert.False(BorrarPorFiltroRules.IsUniversalPattern(null));
        }

        // ---- Tope.

        [Fact]
        public void Tope_IgualAlMaximoPasa_SuperiorSeRechaza()
        {
            Assert.Equal(20000, BorrarPorFiltroRules.MaxEntities);
            Assert.Null(BorrarPorFiltroRules.CheckCap(20000));
            Assert.Null(BorrarPorFiltroRules.CheckCap(0));

            string? detalle = BorrarPorFiltroRules.CheckCap(20001);
            Assert.NotNull(detalle);
            Assert.Contains("20001", detalle);
            Assert.Contains("20000", detalle);
            Assert.Contains("acote", detalle);
        }

        // ---- n_esperado.

        [Fact]
        public void NEsperado_AusenteOIgual_PasaYDistintoSeRechaza()
        {
            Assert.Null(BorrarPorFiltroRules.CheckExpected(null, 7));
            Assert.Null(BorrarPorFiltroRules.CheckExpected(7, 7));
            Assert.Null(BorrarPorFiltroRules.CheckExpected(0, 0));
        }

        [Fact]
        public void NEsperado_Distinto_MensajeConAmbosNumeros()
        {
            string? detalle = BorrarPorFiltroRules.CheckExpected(5, 7);
            Assert.NotNull(detalle);
            Assert.Contains("mostró 5", detalle);
            Assert.Contains("ahora coinciden 7", detalle);
        }

        // ---- Motivos de omisión.

        [Fact]
        public void Motivos_SeAplicanEnPrecedencia()
        {
            Assert.Equal(BorrarPorFiltroRules.SkipReason.Atributo,
                BorrarPorFiltroRules.SkipReasonFor(C("1", "ATTRIB", "MUROS", bloqueada: true, atributo: true).Info));
            Assert.Equal(BorrarPorFiltroRules.SkipReason.DentroDeBloque,
                BorrarPorFiltroRules.SkipReasonFor(C("2", "LINE", "MUROS", enBloque: true, xref: true).Info));
            Assert.Equal(BorrarPorFiltroRules.SkipReason.Viewport,
                BorrarPorFiltroRules.SkipReasonFor(C("3", "VIEWPORT", "MUROS", viewport: true, bloqueada: true).Info));
            Assert.Equal(BorrarPorFiltroRules.SkipReason.CapaBloqueada,
                BorrarPorFiltroRules.SkipReasonFor(C("4", "LINE", "MUROS", bloqueada: true, xref: true).Info));
            Assert.Equal(BorrarPorFiltroRules.SkipReason.CapaXref,
                BorrarPorFiltroRules.SkipReasonFor(C("5", "LINE", "X|MUROS", xref: true).Info));
        }

        [Fact]
        public void CongeladaOApagada_NoOmite()
        {
            Assert.Null(BorrarPorFiltroRules.SkipReasonFor(C("6", "LINE", "MUROS", congelada: true).Info));
        }

        [Fact]
        public void NombresDeMotivo_SonEstables()
        {
            Assert.Equal("atributo", BorrarPorFiltroRules.SkipReasonName(BorrarPorFiltroRules.SkipReason.Atributo));
            Assert.Equal("dentro_de_bloque", BorrarPorFiltroRules.SkipReasonName(BorrarPorFiltroRules.SkipReason.DentroDeBloque));
            Assert.Equal("viewport", BorrarPorFiltroRules.SkipReasonName(BorrarPorFiltroRules.SkipReason.Viewport));
            Assert.Equal("xref", BorrarPorFiltroRules.SkipReasonName(BorrarPorFiltroRules.SkipReason.XrefBloque));
            Assert.Equal("capa_bloqueada", BorrarPorFiltroRules.SkipReasonName(BorrarPorFiltroRules.SkipReason.CapaBloqueada));
            Assert.Equal("capa_xref", BorrarPorFiltroRules.SkipReasonName(BorrarPorFiltroRules.SkipReason.CapaXref));
        }

        // ---- Plan.

        [Fact]
        public void Plan_SinOmitidas_CuentaTipoCapaYUsuario()
        {
            var cands = new List<BorrarPorFiltroRules.Candidato>
            {
                C("10", "LINE", "MUROS"),
                C("11", "LWPOLYLINE", "MCP-DIBUJO"),
                C("12", "LINE", "MUROS"),
                C("13", "TEXT", "cotas"),
            };

            BorrarPorFiltroRules.Plan plan = BorrarPorFiltroRules.Build(cands);

            Assert.Equal(4, plan.NCoincidentes);
            Assert.Equal(new List<int> { 0, 1, 2, 3 }, plan.Borrar);
            Assert.Empty(plan.OmitidasPorMotivo);
            Assert.Equal(0, plan.NOmitidas);
            Assert.Equal(2, plan.PorTipo["LINE"]);
            Assert.Equal(1, plan.PorTipo["LWPOLYLINE"]);
            Assert.Equal(1, plan.PorTipo["TEXT"]);
            Assert.Equal(2, plan.PorCapa["MUROS"]);
            Assert.Equal(3, plan.NUsuario); // MUROS x2 y cotas; MCP-DIBUJO no cuenta
            Assert.Equal(4, plan.InfoBorrar.Count);
            Assert.Equal(new List<string> { "10", "11", "12", "13" }, plan.Muestra);
        }

        [Fact]
        public void Plan_OmiteConMotivoYNoBorra()
        {
            var cands = new List<BorrarPorFiltroRules.Candidato>
            {
                C("20", "LINE", "MUROS"),
                C("21", "ATTRIB", "MUROS", atributo: true),
                C("22", "LINE", "BLOQ", bloqueada: true),
                C("23", "LINE", "X|MUROS", xref: true),
                C("24", "VIEWPORT", "MCP-VISTA", viewport: true),
                C("25", "ATTRIB", "BLOQ", atributo: true, bloqueada: true),
                C("26", "LINE", "MUROS", congelada: true),
            };

            BorrarPorFiltroRules.Plan plan = BorrarPorFiltroRules.Build(cands);

            Assert.Equal(7, plan.NCoincidentes);
            Assert.Equal(new List<int> { 0, 6 }, plan.Borrar);
            Assert.Equal(5, plan.NOmitidas);
            Assert.Equal(
                new[] { "atributo", "viewport", "capa_bloqueada", "capa_xref" },
                plan.OmitidasPorMotivo.Keys.ToArray());
            Assert.Equal(2, plan.OmitidasPorMotivo["atributo"]);
            Assert.Equal(1, plan.OmitidasPorMotivo["viewport"]);
            Assert.Equal(1, plan.OmitidasPorMotivo["capa_bloqueada"]);
            Assert.Equal(1, plan.OmitidasPorMotivo["capa_xref"]);
            Assert.False(plan.OmitidasPorMotivo.ContainsKey("dentro_de_bloque"));
            Assert.Equal(2, plan.PorCapa["MUROS"]);
            Assert.Equal(2, plan.NUsuario);
            Assert.Equal(1, plan.InfoBorrar.Count(e => e.Handle == "26"));
        }

        [Fact]
        public void InsertDeBloqueXref_SeOmiteComoXref()
        {
            var cands = new List<BorrarPorFiltroRules.Candidato>
            {
                C("30", "INSERT", "MUROS", insertXref: true),
                C("31", "INSERT", "MUROS"),
                C("32", "INSERT", "BLOQ", bloqueada: true, insertXref: true),
            };

            BorrarPorFiltroRules.Plan plan = BorrarPorFiltroRules.Build(cands);

            Assert.Equal(new List<int> { 1 }, plan.Borrar);
            Assert.Equal(1, plan.OmitidasPorMotivo["xref"]);
            // Capa bloqueada tiene precedencia sobre el INSERT xref.
            Assert.Equal(1, plan.OmitidasPorMotivo["capa_bloqueada"]);
        }

        [Fact]
        public void Plan_MuestraLimitadaA20()
        {
            var cands = new List<BorrarPorFiltroRules.Candidato>();
            for (int i = 0; i < 25; i++)
            {
                cands.Add(C(i.ToString("X"), "LINE", "MCP-X"));
            }

            BorrarPorFiltroRules.Plan plan = BorrarPorFiltroRules.Build(cands);

            Assert.Equal(BorrarPorFiltroRules.MaxSample, plan.Muestra.Count);
            Assert.Equal("0", plan.Muestra[0]);
            Assert.Equal(25, plan.Borrar.Count);
            Assert.Equal(0, plan.NUsuario);
        }

        [Fact]
        public void Plan_SinCandidatos_EsVacio()
        {
            BorrarPorFiltroRules.Plan plan = BorrarPorFiltroRules.Build(new List<BorrarPorFiltroRules.Candidato>());

            Assert.Equal(0, plan.NCoincidentes);
            Assert.Empty(plan.Borrar);
            Assert.Empty(plan.Muestra);
            Assert.Equal(0, plan.NOmitidas);
        }

        [Fact]
        public void Build_NullLanza()
        {
            Assert.Throws<ArgumentNullException>(() => BorrarPorFiltroRules.Build(null!));
        }
    }
}
