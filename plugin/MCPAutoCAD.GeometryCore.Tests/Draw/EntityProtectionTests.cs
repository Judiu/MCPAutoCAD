using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class EntityProtectionTests
    {
        private static EntityInfo E(
            string handle,
            string capa,
            bool bloqueada = false,
            bool xref = false,
            bool congelada = false,
            bool atributo = false,
            bool viewport = false,
            bool enBloque = false,
            string espacio = "modelo") =>
            new EntityInfo(handle, capa, bloqueada, xref, congelada, atributo, viewport, enBloque, espacio);

        [Fact]
        public void TodasMcp_SinErrorNiConfirmacion()
        {
            var list = new List<EntityInfo> { E("2A3F", "MCP-DIBUJO"), E("2A40", "mcp-observaciones") };
            Assert.Null(EntityProtection.Classify(list));
            Assert.Equal(0, EntityProtection.CountUser(list));
            Assert.Null(EntityProtection.RequireConfirmation(list, confirmar: false));
        }

        [Fact]
        public void Mezcla_PideConfirmacionConEjemplosYConteos()
        {
            var list = new List<EntityInfo> { E("2A3F", "MUROS"), E("2A41", "MCP-DIBUJO"), E("2A40", "Cotas") };
            Assert.Null(EntityProtection.Classify(list));
            Assert.Equal(2, EntityProtection.CountUser(list));
            Assert.Equal(
                "confirmar: 2 de las 3 entidades no están en capas MCP-* (p. ej. 2A3F en MUROS, 2A40 en Cotas); pase confirmar: true para modificarlas",
                EntityProtection.RequireConfirmation(list, confirmar: false));
        }

        [Fact]
        public void Mezcla_ConConfirmar_NoPideNada()
        {
            var list = new List<EntityInfo> { E("2A3F", "MUROS"), E("2A41", "MCP-DIBUJO") };
            Assert.Null(EntityProtection.RequireConfirmation(list, confirmar: true));
        }

        [Fact]
        public void ConfirmMessage_CitaComoMucho5Ejemplos()
        {
            var list = new List<EntityInfo>();
            for (int i = 0; i < 8; i++)
            {
                list.Add(E((0x100 + i).ToString("X"), "MUROS"));
            }

            List<(string handle, string capa)> ejemplos = EntityProtection.UserExamples(list);
            Assert.Equal(5, ejemplos.Count);
            string msg = EntityProtection.ConfirmMessage(8, 8, ejemplos);
            Assert.StartsWith("confirmar: 8 de las 8 entidades no están en capas MCP-* (p. ej. 100 en MUROS, ", msg);
            Assert.Contains("104 en MUROS)", msg);
            Assert.DoesNotContain("105", msg);
        }

        [Fact]
        public void ConfirmMessage_MasDe5EjemplosRecibidos_SeRecorta()
        {
            var ejemplos = new List<(string handle, string capa)>
            {
                ("A", "X"), ("B", "X"), ("C", "X"), ("D", "X"), ("E", "X"), ("F", "X"),
            };
            Assert.DoesNotContain("F en X", EntityProtection.ConfirmMessage(6, 6, ejemplos));
        }

        [Fact]
        public void CapaQueContieneMcpPeroNoEmpiezaPorMcp_EsDelUsuario()
        {
            Assert.False(EntityProtection.IsMcpLayer("X-MCP-DIBUJO"));
            Assert.False(EntityProtection.IsMcpLayer("MCP"));
            Assert.True(EntityProtection.IsMcpLayer("Mcp-Algo"));
            Assert.False(EntityProtection.IsMcpLayer(null));
        }

        [Fact]
        public void Bloqueada_RechazoAunqueConfirmar()
        {
            var list = new List<EntityInfo> { E("10", "MCP-DIBUJO"), E("2A3F", "MUROS", bloqueada: true) };
            ProtectionError? err = EntityProtection.Classify(list);
            Assert.NotNull(err);
            Assert.Equal("handles[1]: la entidad está en una capa bloqueada", err!.Detalle);
            Assert.Equal("2A3F", err.Handle);
            Assert.Equal("MUROS", err.Capa);
            // Classify no depende de confirmar: el handler rechaza antes de mirar la confirmación.
            Assert.Null(EntityProtection.RequireConfirmation(list, confirmar: true));
        }

        [Fact]
        public void Xref_Rechazo()
        {
            ProtectionError? err = EntityProtection.Classify(new[] { E("2B", "BASE|MUROS", xref: true) });
            Assert.Equal("handles[0]: la entidad está en una capa dependiente de xref", err!.Detalle);
            Assert.Equal("BASE|MUROS", err.Capa);
        }

        [Fact]
        public void Atributo_Rechazo()
        {
            ProtectionError? err = EntityProtection.Classify(new[] { E("1", "MCP-DIBUJO"), E("2C", "0", atributo: true) });
            Assert.Equal("handles[1]: es un atributo; use el handle del INSERT", err!.Detalle);
            Assert.Equal("2C", err.Handle);
            Assert.Null(err.Capa);
        }

        [Fact]
        public void EnBloque_Rechazo()
        {
            ProtectionError? err = EntityProtection.Classify(new[] { E("2D", "0", enBloque: true, espacio: "bloque:PUERTA") });
            Assert.Equal("handles[0]: la entidad está dentro de una definición de bloque", err!.Detalle);
        }

        [Fact]
        public void ViewportPapel_Rechazo()
        {
            ProtectionError? err = EntityProtection.Classify(new[] { E("2E", "VP", viewport: true, espacio: "Layout1") });
            Assert.Equal("handles[0]: es un viewport de papel", err!.Detalle);
        }

        [Fact]
        public void Orden_Paso2AntesQuePaso3_SobreTodosLosHandles()
        {
            // El handle 0 tiene capa bloqueada (paso 3) y el 1 es un atributo (paso 2): manda el paso 2.
            var list = new[] { E("A1", "MUROS", bloqueada: true), E("A2", "0", atributo: true) };
            Assert.Equal("handles[1]: es un atributo; use el handle del INSERT", EntityProtection.Classify(list)!.Detalle);
        }

        [Fact]
        public void Orden_Paso3AntesQuePaso4()
        {
            var list = new[] { E("A1", "MUROS"), E("A2", "MUROS", bloqueada: true, espacio: "Layout1") };
            Assert.Equal("handles[1]: la entidad está en una capa bloqueada", EntityProtection.Classify(list)!.Detalle);
        }

        [Fact]
        public void Orden_BloqueadaAntesQueXrefEnLaMismaEntidad()
        {
            var list = new[] { E("A1", "X|MUROS", bloqueada: true, xref: true) };
            Assert.Equal("handles[0]: la entidad está en una capa bloqueada", EntityProtection.Classify(list)!.Detalle);
        }

        [Fact]
        public void EspaciosDistintos_MensajeConLosDosHandles()
        {
            var list = new[] { E("2A3F", "MCP-DIBUJO"), E("2A40", "MCP-DIBUJO"), E("2B00", "MCP-DIBUJO", espacio: "Layout1") };
            ProtectionError? err = EntityProtection.Classify(list);
            Assert.Equal("handles: las entidades deben estar en el mismo espacio (2A3F en modelo, 2B00 en Layout1)", err!.Detalle);
            Assert.Equal(new[] { "2A3F", "2B00" }, err.Handles);
        }

        [Fact]
        public void MismoLayoutConDistintaCaja_EsElMismoEspacio()
        {
            var list = new[] { E("1", "MCP-A", espacio: "Layout1"), E("2", "MCP-A", espacio: "LAYOUT1") };
            Assert.Null(EntityProtection.Classify(list));
        }

        [Fact]
        public void CongeladaOApagada_NoBloquea_YCuentaParaLaAdvertencia()
        {
            var list = new[] { E("1", "MCP-A", congelada: true), E("2", "MCP-A"), E("3", "MCP-B", congelada: true) };
            Assert.Null(EntityProtection.Classify(list));
            Assert.Equal(2, EntityProtection.CountFrozenOrOff(list));
            Assert.Equal("2 entidad(es) en capas congeladas o apagadas: el cambio no será visible.", EntityProtection.FrozenOrOffWarning(2));
        }

        [Fact]
        public void UnaSolaEntidadDelUsuario()
        {
            var list = new[] { E("2A3F", "MUROS") };
            Assert.Equal(
                "confirmar: 1 de las 1 entidades no están en capas MCP-* (p. ej. 2A3F en MUROS); pase confirmar: true para modificarlas",
                EntityProtection.RequireConfirmation(list, false));
        }

        [Fact]
        public void ArgumentosNulos_Lanzan()
        {
            Assert.Throws<ArgumentNullException>(() => EntityProtection.Classify(null!));
            Assert.Throws<ArgumentNullException>(() => EntityProtection.CountUser(null!));
            Assert.Throws<ArgumentNullException>(() => EntityProtection.ConfirmMessage(1, 1, null!));
        }
    }
}
