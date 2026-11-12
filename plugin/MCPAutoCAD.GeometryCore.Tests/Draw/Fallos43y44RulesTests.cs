using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>Fallos 43 (copiar con origen en capa bloqueada) y 44 (mensajes de mover_por_filtro), plugin 0.10.4.</summary>
    public class Fallos43y44RulesTests
    {
        private static EntityInfo Info(string handle, bool bloqueada = false, bool xref = false) =>
            new EntityInfo(handle, "Cotas", bloqueada, xref, false, false, false, false, "modelo");

        [Fact]
        public void CapaBloqueada_SeRechazaPorDefecto()
        {
            ProtectionError? e = EntityProtection.Classify(new List<EntityInfo> { Info("A1", bloqueada: true) });
            Assert.NotNull(e);
            Assert.Contains("capa bloqueada", e!.Detalle);
        }

        [Fact]
        public void CapaBloqueada_SeAdmiteConPermiso()
        {
            Assert.Null(EntityProtection.Classify(new List<EntityInfo> { Info("A1", bloqueada: true) }, permitirCapaBloqueada: true));
        }

        [Fact]
        public void CapaXref_SeSigueRechazandoConPermiso()
        {
            ProtectionError? e = EntityProtection.Classify(
                new List<EntityInfo> { Info("A1", bloqueada: true, xref: true) }, permitirCapaBloqueada: true);
            Assert.NotNull(e);
            Assert.Contains("xref", e!.Detalle);
        }

        [Fact]
        public void ValidateFilters_MensajeSegunLaAccion()
        {
            Assert.Contains("no se borra el espacio", BorrarPorFiltroRules.ValidateFilters(null, false, false, null));
            Assert.Contains("no se mueve el espacio", BorrarPorFiltroRules.ValidateFilters(null, false, false, null, "mueve"));
        }

        [Fact]
        public void CheckExpected_MensajeSegunLaAccion()
        {
            Assert.Contains("no se borró nada", BorrarPorFiltroRules.CheckExpected(5, 6));
            Assert.Contains("no se movió nada", BorrarPorFiltroRules.CheckExpected(5, 6, "movió"));
            Assert.Null(BorrarPorFiltroRules.CheckExpected(6, 6, "movió"));
        }
    }
}
