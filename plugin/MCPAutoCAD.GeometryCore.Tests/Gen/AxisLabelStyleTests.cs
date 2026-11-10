using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>AxisLabelStyle (protocol.md §5E.3.2; corrección del gate en vivo de Fase 4A, 2026-10-27). Puro.</summary>
    public class AxisLabelStyleTests
    {
        private static Func<string, string?> Dibujo(params string[] estilos)
        {
            var set = new List<string>(estilos);
            return nombre => set.Find(s => string.Equals(s, nombre, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void SinEstilo_Y75Existe_Usa75SinAdvertencia()
        {
            (string? estilo, string? adv) = AxisLabelStyle.Choose(null, null, Dibujo("Standard", "75", "COTA75"), "Standard");
            Assert.Equal("75", estilo);
            Assert.Null(adv);
        }

        [Fact]
        public void SinEstilo_Y75NoExiste_UsaElActualConAdvertencia()
        {
            (string? estilo, string? adv) = AxisLabelStyle.Choose(null, null, Dibujo("Standard", "Arial"), "Standard");
            Assert.Null(estilo);
            Assert.Equal(
                "ejes_rejilla: el estilo de texto '75' de los rótulos de eje de la oficina no existe en el dibujo; los rótulos usan el estilo actual 'Standard' (indique estilo para otro).",
                adv);
        }

        [Fact]
        public void EstiloDado_SeRespetaSinConsultarElDibujo()
        {
            (string? estilo, string? adv) = AxisLabelStyle.Choose("Arial", null, _ => throw new InvalidOperationException("no debe consultarse"), "Standard");
            Assert.Equal("Arial", estilo);
            Assert.Null(adv);
        }

        [Fact]
        public void ConfiguradoEnLps_TienePrioridadSobreLaConstante()
        {
            (string? estilo, string? adv) = AxisLabelStyle.Choose(null, "romans", Dibujo("Standard", "75", "ROMANS"), "Standard");
            Assert.Equal("ROMANS", estilo);
            Assert.Null(adv);

            (string? e2, string? a2) = AxisLabelStyle.Choose(null, "romans", Dibujo("Standard", "75"), "Standard");
            Assert.Null(e2);
            Assert.Contains("'romans'", a2);
        }

        [Fact]
        public void ConfiguradoVacio_UsaLaConstante()
        {
            Assert.Equal("75", AxisLabelStyle.OfficeDefault);
            (string? estilo, _) = AxisLabelStyle.Choose(null, "  ", Dibujo("75"), "Standard");
            Assert.Equal("75", estilo);
        }
    }
}
