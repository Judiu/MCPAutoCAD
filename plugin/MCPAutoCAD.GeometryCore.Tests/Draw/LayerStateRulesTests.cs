using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class LayerStateRulesTests
    {
        private static LayerStateSnapshot Capa(string nombre, bool off = false, bool frozen = false, bool locked = false, bool plot = true, bool dep = false) =>
            new LayerStateSnapshot(nombre, off, frozen, locked, plot, dep);

        private static LayerStateChange Off() => new LayerStateChange(true, null, null, null);

        private static LayerStateItem Item(LayerStatePlan plan, string nombre) =>
            plan.Capas.Single(c => c.Nombre == nombre);

        // ---- Comodines ----

        [Theory]
        [InlineData("PLANO_A-AREA-PATT", "*_A-AREA-PATT", true)]
        [InlineData("A1_A-AREA-PATT", "*_A-AREA-PATT", true)]
        [InlineData("A1_A-AREA", "*_A-AREA-PATT", false)]
        [InlineData("MURO", "MURO", true)]
        [InlineData("MURO2", "MURO", false)]
        [InlineData("MURO", "MUR", false)]
        [InlineData("MURO", "*", true)]
        [InlineData("", "*", true)]
        [InlineData("", "", true)]
        [InlineData("MURO", "", false)]
        [InlineData("A-MURO-02", "A-*-02", true)]
        [InlineData("A-MURO-02", "A-*-03", false)]
        [InlineData("AB", "A?", true)]
        [InlineData("A", "A?", false)]
        [InlineData("ABC", "A?", false)]
        [InlineData("ABC", "A??", true)]
        [InlineData("AXC", "A?C", true)]
        [InlineData("AC", "A?C", false)]
        [InlineData("", "?", false)]
        [InlineData("A*B", "A*B", true)]
        [InlineData("ABAB", "*A*B", true)]
        [InlineData("ABAC", "*A*B", false)]
        [InlineData("AAAB", "*AB", true)]
        [InlineData("ab", "AB", true)]
        [InlineData("Muros_Ejes", "muros_*", true)]
        [InlineData("X_ESTRUC", "*estruc", true)]
        [InlineData("A_B_C", "*_*_*", true)]
        [InlineData("AB", "*?*", true)]
        public void WildcardMatch_SigueLaConvencionDeAutoCAD(string nombre, string patron, bool esperado)
        {
            Assert.Equal(esperado, LayerStateRules.WildcardMatch(nombre, patron));
        }

        [Fact]
        public void WildcardMatch_NullDevuelveFalse()
        {
            Assert.False(LayerStateRules.WildcardMatch(null!, "*"));
            Assert.False(LayerStateRules.WildcardMatch("MURO", null!));
        }

        // ---- Validación de patrones ----

        [Fact]
        public void ValidatePatterns_AceptaUnoOVariosPatronesValidos()
        {
            Assert.Null(LayerStateRules.ValidatePatterns(new[] { "*_A-AREA-PATT" }));
            Assert.Null(LayerStateRules.ValidatePatterns(new[] { "MURO?", "A-*", "CAPA" }));
        }

        [Fact]
        public void ValidatePatterns_RechazaListaNulaOVacia()
        {
            Assert.NotNull(LayerStateRules.ValidatePatterns(null));
            Assert.NotNull(LayerStateRules.ValidatePatterns(new List<string>()));
        }

        [Fact]
        public void ValidatePatterns_LimiteDe200()
        {
            var ok = Enumerable.Repeat("CAPA", LayerStateRules.MaxPatrones).ToList();
            var demasiados = Enumerable.Repeat("CAPA", LayerStateRules.MaxPatrones + 1).ToList();
            Assert.Null(LayerStateRules.ValidatePatterns(ok));
            Assert.Contains("200", LayerStateRules.ValidatePatterns(demasiados));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ValidatePatterns_RechazaVacios(string patron)
        {
            Assert.Contains("no puede estar vacío", LayerStateRules.ValidatePatterns(new[] { patron }));
        }

        [Fact]
        public void ValidatePatterns_RechazaNullComoElemento()
        {
            Assert.NotNull(LayerStateRules.ValidatePatterns(new List<string> { null! }));
        }

        [Fact]
        public void ValidatePatterns_RechazaLongitudMayorDe255()
        {
            string largo = new string('A', LayerStateRules.MaxLongitudPatron + 1);
            Assert.NotNull(LayerStateRules.ValidatePatterns(new[] { largo }));
            Assert.Null(LayerStateRules.ValidatePatterns(new[] { new string('A', LayerStateRules.MaxLongitudPatron) }));
        }

        [Theory]
        [InlineData("MU\tRO")]
        [InlineData("MU\nRO")]
        [InlineData("MURO<")]
        [InlineData("A/B")]
        [InlineData("A\\B")]
        [InlineData("A:B")]
        [InlineData("A;B")]
        [InlineData("A=B")]
        [InlineData("A`B")]
        [InlineData("A\"B")]
        public void ValidatePatterns_RechazaCaracteresNoAdmitidosEnNombresDeCapa(string patron)
        {
            Assert.NotNull(LayerStateRules.ValidatePatterns(new[] { patron }));
        }

        [Fact]
        public void ValidatePatterns_IndicaLaPosicionDelErrorEnElDetalle()
        {
            string? detalle = LayerStateRules.ValidatePatterns(new[] { "MURO", "   " });
            Assert.NotNull(detalle);
            Assert.StartsWith("capas[1]", detalle);
        }

        // ---- Validación de banderas ----

        [Fact]
        public void ValidateChange_SinNingunaBanderaEsInvalida()
        {
            Assert.Equal(LayerStateRules.SinBanderas, LayerStateRules.ValidateChange(new LayerStateChange(null, null, null, null)));
        }

        [Theory]
        [InlineData(true, null, null, null)]
        [InlineData(null, false, null, null)]
        [InlineData(null, null, true, null)]
        [InlineData(null, null, null, false)]
        public void ValidateChange_CualquierBanderaBasta(bool? a, bool? c, bool? b, bool? i)
        {
            var cambio = new LayerStateChange(a, c, b, i);
            Assert.True(cambio.HasAny);
            Assert.Null(LayerStateRules.ValidateChange(cambio));
        }

        // ---- Plan ----

        [Fact]
        public void Plan_SinCoincidenciasNoHayCapasYListaPatrones()
        {
            var capas = new[] { Capa("0"), Capa("MURO") };
            LayerStatePlan plan = LayerStateRules.Plan(capas, new[] { "*_A-AREA-PATT" }, Off(), "0");

            Assert.Empty(plan.Capas);
            Assert.Equal(0, plan.Coincidencias);
            Assert.Equal(new[] { "*_A-AREA-PATT" }, plan.PatronesSinCoincidencia);
        }

        [Fact]
        public void Plan_PatronParcialmenteSinCoincidenciaSeInforma()
        {
            var capas = new[] { Capa("A1_A-AREA-PATT"), Capa("MURO") };
            LayerStatePlan plan = LayerStateRules.Plan(capas, new[] { "*_A-AREA-PATT", "NO-EXISTE" }, Off(), "MURO");

            Assert.Equal(1, plan.Coincidencias);
            Assert.Equal(new[] { "NO-EXISTE" }, plan.PatronesSinCoincidencia);
        }

        [Fact]
        public void Plan_SoloCambianLasCapasQueCoincidenYLasDemasNoAparecen()
        {
            var capas = new[] { Capa("A1_A-AREA-PATT"), Capa("A1_A-AREA"), Capa("MURO") };
            LayerStatePlan plan = LayerStateRules.Plan(capas, new[] { "*_A-AREA-PATT" }, Off(), "MURO");

            Assert.Single(plan.Capas);
            Assert.Equal(1, plan.Cambian);
            Assert.Equal(LayerStateOutcome.Cambia, plan.Capas[0].Estado);
            Assert.True(plan.Capas[0].Despues.Apagada);
            Assert.False(plan.Capas[0].Antes.Apagada);
        }

        [Fact]
        public void Plan_BanderasNoPedidasSeConservan()
        {
            var antes = Capa("MURO", off: false, frozen: false, locked: true, plot: false);
            LayerStatePlan plan = LayerStateRules.Plan(new[] { antes }, new[] { "MURO" }, Off(), "0");

            LayerStateSnapshot despues = plan.Capas[0].Despues;
            Assert.True(despues.Apagada);
            Assert.True(despues.Bloqueada);
            Assert.False(despues.Imprimir);
            Assert.False(despues.Congelada);
        }

        [Fact]
        public void Plan_ApagarUnaCapaYaApagadaEsSinCambio()
        {
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("MURO", off: true) }, new[] { "MURO" }, Off(), "0");

            Assert.Equal(LayerStateOutcome.SinCambio, plan.Capas[0].Estado);
            Assert.Equal(0, plan.Cambian);
            Assert.Equal(1, plan.SinCambio);
        }

        [Fact]
        public void Plan_EncenderDescongelarDesbloquearYPlotOn()
        {
            var antes = Capa("MURO", off: true, frozen: true, locked: true, plot: false);
            var cambio = new LayerStateChange(false, false, false, true);
            LayerStatePlan plan = LayerStateRules.Plan(new[] { antes }, new[] { "MURO" }, cambio, "0");

            LayerStateSnapshot d = plan.Capas[0].Despues;
            Assert.False(d.Apagada);
            Assert.False(d.Congelada);
            Assert.False(d.Bloqueada);
            Assert.True(d.Imprimir);
            Assert.Equal(LayerStateOutcome.Cambia, plan.Capas[0].Estado);
        }

        [Fact]
        public void Plan_CapaActualNoSeCongelaPeroSiSeApaga()
        {
            var cambio = new LayerStateChange(true, true, null, null);
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("MURO") }, new[] { "MURO" }, cambio, "muro");

            LayerStateItem item = plan.Capas[0];
            Assert.Equal(LayerStateOutcome.Cambia, item.Estado);
            Assert.False(item.Despues.Congelada);
            Assert.True(item.Despues.Apagada);
            Assert.Contains(plan.Advertencias, a => a.Contains("no se puede congelar"));
        }

        [Fact]
        public void Plan_CapaActualSoloCongelarSeOmiteConMotivo()
        {
            var cambio = new LayerStateChange(null, true, null, null);
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("MURO") }, new[] { "MURO" }, cambio, "MURO");

            LayerStateItem item = plan.Capas[0];
            Assert.Equal(LayerStateOutcome.Omitida, item.Estado);
            Assert.Equal(LayerStateRules.CapaActualNoCongelable, item.Motivo);
            Assert.Equal(0, plan.Cambian);
            Assert.Equal(1, plan.Omitidas);
            Assert.NotEmpty(plan.Advertencias);
        }

        [Fact]
        public void Plan_CapaActualYaCongeladaNoGeneraAdvertenciaDeCongelado()
        {
            var cambio = new LayerStateChange(null, true, null, null);
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("MURO", frozen: true) }, new[] { "MURO" }, cambio, "MURO");

            Assert.Equal(LayerStateOutcome.SinCambio, plan.Capas[0].Estado);
            Assert.Empty(plan.Advertencias);
        }

        [Fact]
        public void Plan_CongelarOtraCapaSiEsPosible()
        {
            var cambio = new LayerStateChange(null, true, null, null);
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("MURO") }, new[] { "MURO" }, cambio, "0");

            Assert.Equal(LayerStateOutcome.Cambia, plan.Capas[0].Estado);
            Assert.True(plan.Capas[0].Despues.Congelada);
            Assert.Empty(plan.Advertencias);
        }

        [Fact]
        public void Plan_CapaActualPuedeBloquearseSinAdvertencia()
        {
            var cambio = new LayerStateChange(null, null, true, null);
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("MURO") }, new[] { "MURO" }, cambio, "MURO");

            Assert.Equal(LayerStateOutcome.Cambia, plan.Capas[0].Estado);
            Assert.True(plan.Capas[0].Despues.Bloqueada);
            Assert.Empty(plan.Advertencias);
        }

        [Fact]
        public void Plan_ApagarLaCapaActualSeAdmiteConAdvertencia()
        {
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("MURO") }, new[] { "MURO" }, Off(), "muro");

            Assert.Equal(LayerStateOutcome.Cambia, plan.Capas[0].Estado);
            Assert.True(plan.Capas[0].Despues.Apagada);
            Assert.Single(plan.Advertencias);
            Assert.Contains("capa actual", plan.Advertencias[0]);
        }

        [Fact]
        public void Plan_CapaActualYaApagadaNoRepiteAdvertencia()
        {
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("MURO", off: true) }, new[] { "MURO" }, Off(), "MURO");

            Assert.Empty(plan.Advertencias);
            Assert.Equal(LayerStateOutcome.SinCambio, plan.Capas[0].Estado);
        }

        [Fact]
        public void Plan_CapaSinCapaActualNoLanzaNiAdvierte()
        {
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("MURO") }, new[] { "MURO" }, Off(), null);

            Assert.Equal(LayerStateOutcome.Cambia, plan.Capas[0].Estado);
            Assert.Empty(plan.Advertencias);
        }

        [Fact]
        public void Plan_CapaXrefSeOmiteConAdvertenciaYNoCambia()
        {
            var xref = Capa("XREF|A-MURO", dep: true);
            LayerStatePlan plan = LayerStateRules.Plan(new[] { xref }, new[] { "XREF|*" }, Off(), "0");

            LayerStateItem item = plan.Capas[0];
            Assert.Equal(LayerStateOutcome.Omitida, item.Estado);
            Assert.Equal(LayerStateRules.MotivoXref, item.Motivo);
            Assert.False(item.Despues.Apagada);
            Assert.Single(plan.Advertencias);
            Assert.Contains("xref", plan.Advertencias[0]);
        }

        [Fact]
        public void Plan_Layer0YDefpointsSeCambianConNormalidad()
        {
            var capas = new[] { Capa("0"), Capa("DEFPOINTS") };
            LayerStatePlan plan = LayerStateRules.Plan(capas, new[] { "0", "DEFPOINTS" }, Off(), "MURO");

            Assert.Equal(2, plan.Cambian);
            Assert.Empty(plan.Advertencias);
        }

        [Fact]
        public void Plan_Layer0PuedeCongelarseSiNoEsLaActual()
        {
            var cambio = new LayerStateChange(null, true, null, null);
            LayerStatePlan plan = LayerStateRules.Plan(new[] { Capa("0") }, new[] { "0" }, cambio, "MURO");

            Assert.Equal(LayerStateOutcome.Cambia, plan.Capas[0].Estado);
            Assert.True(plan.Capas[0].Despues.Congelada);
        }

        [Fact]
        public void Plan_UnaCapaQueCoincideConVariosPatronesApareceUnaVez()
        {
            var capas = new[] { Capa("A1_A-AREA-PATT"), Capa("MURO") };
            LayerStatePlan plan = LayerStateRules.Plan(capas, new[] { "*_A-AREA-PATT", "A1*" }, Off(), "0");

            Assert.Single(plan.Capas);
            Assert.Equal("A1_A-AREA-PATT", plan.Capas[0].Nombre);
        }

        [Fact]
        public void Plan_PatronesRepetidosYConEspaciosSeNormalizan()
        {
            var capas = new[] { Capa("MURO") };
            LayerStatePlan plan = LayerStateRules.Plan(capas, new[] { "  MURO ", "muro", "MURO" }, Off(), "0");

            Assert.Single(plan.Capas);
            Assert.Empty(plan.PatronesSinCoincidencia);
        }

        [Fact]
        public void Plan_RespetaElOrdenDeLaTabla()
        {
            var capas = new[] { Capa("C-AREA-PATT"), Capa("A-AREA-PATT"), Capa("B-AREA-PATT") };
            LayerStatePlan plan = LayerStateRules.Plan(capas, new[] { "*-AREA-PATT" }, Off(), "0");

            Assert.Equal(new[] { "C-AREA-PATT", "A-AREA-PATT", "B-AREA-PATT" }, plan.Capas.Select(c => c.Nombre));
            Assert.Equal(3, plan.Coincidencias);
        }

        [Fact]
        public void Plan_RecuentosCuadranConLasTresSituaciones()
        {
            var capas = new[]
            {
                Capa("P-1"),                       // cambia
                Capa("P-2", off: true),            // sin cambio
                Capa("P-3", dep: true),            // omitida (xref)
                Capa("P-4"),                       // cambia
            };
            LayerStatePlan plan = LayerStateRules.Plan(capas, new[] { "P-*" }, Off(), "0");

            Assert.Equal(4, plan.Coincidencias);
            Assert.Equal(2, plan.Cambian);
            Assert.Equal(1, plan.SinCambio);
            Assert.Equal(1, plan.Omitidas);
        }

        [Fact]
        public void Plan_NoModificaLaListaDeSnapshots()
        {
            var capas = new List<LayerStateSnapshot> { Capa("MURO") };
            LayerStateRules.Plan(capas, new[] { "MURO" }, Off(), "0");

            Assert.False(capas[0].Apagada);
            Assert.Single(capas);
        }

        [Fact]
        public void Plan_ReglasDeEntradaNulasLanzan()
        {
            var capas = new List<LayerStateSnapshot>();
            var pats = new List<string> { "X" };
            Assert.Throws<System.ArgumentNullException>(() => LayerStateRules.Plan(null!, pats, Off(), null));
            Assert.Throws<System.ArgumentNullException>(() => LayerStateRules.Plan(capas, null!, Off(), null));
            Assert.Throws<System.ArgumentNullException>(() => LayerStateRules.Plan(capas, pats, null!, null));
        }

        [Fact]
        public void Snapshot_IgualCompara4BanderasYNoElNombreNiLaDependencia()
        {
            Assert.True(Capa("A", off: true).Igual(Capa("B", off: true, dep: true)));
            Assert.False(Capa("A", off: true).Igual(Capa("A", off: false)));
            Assert.False(Capa("A", plot: true).Igual(Capa("A", plot: false)));
        }

        [Fact]
        public void ValidatePatterns_RechazaListaConComa()
        {
            string? e = LayerStateRules.ValidatePatterns(new[] { "A*,B*" });
            Assert.NotNull(e);
            Assert.Contains("','", e);
        }

        [Fact]
        public void Plan_AgrupaLasCapasDeXrefEnUnaAdvertencia()
        {
            var capas = new[] { Capa("X|A", dep: true), Capa("X|B", dep: true), Capa("X|C", dep: true), Capa("LOCAL") };
            LayerStatePlan plan = LayerStateRules.Plan(capas, new[] { "*" }, Off(), "0");
            Assert.Single(plan.Advertencias, a => a.Contains("xref"));
            Assert.Contains("3 capa(s)", plan.Advertencias[0]);
            Assert.Equal(1, plan.Cambian);
        }
    }
}
