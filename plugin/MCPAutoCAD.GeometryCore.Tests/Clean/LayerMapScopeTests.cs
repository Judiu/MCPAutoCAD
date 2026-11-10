using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Clean;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Clean
{
    /// <summary>
    /// Ámbito de normalizar_capas (protocol.md §5F.3, correcciones del gate en vivo 0.6.1): parámetro espacios y
    /// exclusión de los VIEWPORT de layout. Caso real del gate (2026-10-27): el mapa {"0": "Cotas"} incluía 4 VIEWPORT
    /// (3 en Layout1, 1 en Layout2) y el usuario quería mover la capa 0 solo en el modelo.
    /// </summary>
    public class LayerMapScopeTests
    {
        private static readonly string[] Layouts = { "Layout1", "Layout2" };

        [Fact]
        public void SinEspacios_Todos()
        {
            Assert.True(LayerMapScope.TryResolve(null, Layouts, out IReadOnlyList<string>? r, out IReadOnlyList<string> faltan));
            Assert.Null(r);
            Assert.Empty(faltan);
            Assert.True(LayerMapScope.Includes(null, "modelo"));
            Assert.True(LayerMapScope.Includes(null, "Layout2"));
        }

        [Fact]
        public void Modelo_SinDistinguirMayusculas_YNombreRealDelLayout()
        {
            Assert.True(LayerMapScope.TryResolve(new[] { "MODELO", "layout2" }, Layouts, out IReadOnlyList<string>? r, out _));
            Assert.Equal(new[] { "modelo", "Layout2" }, r);
            Assert.True(LayerMapScope.Includes(r, "modelo"));
            Assert.True(LayerMapScope.Includes(r, "LAYOUT2"));
            Assert.False(LayerMapScope.Includes(r, "Layout1"));
        }

        [Fact]
        public void SoloModelo_ExcluyeLosLayouts()
        {
            Assert.True(LayerMapScope.TryResolve(new[] { "modelo" }, Layouts, out IReadOnlyList<string>? r, out _));
            Assert.True(LayerMapScope.Includes(r, "modelo"));
            Assert.False(LayerMapScope.Includes(r, "Layout1"));
            Assert.False(LayerMapScope.Includes(r, "Layout2"));
        }

        [Fact]
        public void LayoutLlamadoModelo_NoEsDireccionable()
        {
            // O2: "modelo" siempre es el espacio modelo; un layout llamado "Modelo" no entra con espacios ["modelo"].
            var layouts = new[] { "Modelo", "Layout1" };
            Assert.True(LayerMapScope.TryResolve(new[] { "MODELO" }, layouts, out IReadOnlyList<string>? r, out _));
            Assert.Equal(new[] { "modelo" }, r);
            Assert.True(LayerMapScope.Includes(r, "modelo", esModelo: true));
            Assert.False(LayerMapScope.Includes(r, "Modelo", esModelo: false));
            Assert.True(LayerMapScope.Includes(null, "Modelo", esModelo: false));
            Assert.True(LayerMapScope.TryResolve(new[] { "layout1" }, layouts, out r, out _));
            Assert.False(LayerMapScope.Includes(r, "modelo", esModelo: true));
            Assert.True(LayerMapScope.Includes(r, "Layout1", esModelo: false));
        }

        [Fact]
        public void LayoutsInexistentes_Todos_EnElOrdenDelCliente()
        {
            Assert.False(LayerMapScope.TryResolve(new[] { "Lamina9", "modelo", "Layout1", "X" }, Layouts, out IReadOnlyList<string>? r, out IReadOnlyList<string> faltan));
            Assert.Null(r);
            Assert.Equal(new[] { "Lamina9", "X" }, faltan);
            Assert.Equal(
                "espacios: layouts inexistentes: Lamina9, X (espacios del dibujo: modelo, Layout1, Layout2)",
                LayerMapScope.MissingDetail(faltan, Layouts));
        }

        [Fact]
        public void SinLayouts_SoloModeloExiste()
        {
            Assert.True(LayerMapScope.TryResolve(new[] { "modelo" }, Array.Empty<string>(), out _, out _));
            Assert.False(LayerMapScope.TryResolve(new[] { "Layout1" }, Array.Empty<string>(), out _, out IReadOnlyList<string> faltan));
            Assert.Equal(
                "espacios: layouts inexistentes: Layout1 (espacios del dibujo: modelo)",
                LayerMapScope.MissingDetail(faltan, Array.Empty<string>()));
        }

        [Theory]
        [InlineData(new[] { "modelo", "Modelo" }, "espacios: espacio repetido 'Modelo'")]
        [InlineData(new[] { "Layout1", "modelo", "LAYOUT1" }, "espacios: espacio repetido 'LAYOUT1'")]
        public void Repetidos_SinDistinguirMayusculas(string[] espacios, string detalle)
        {
            Assert.Equal(detalle, LayerMapScope.FindDuplicate(espacios));
        }

        [Fact]
        public void SinRepetidos_Null()
        {
            Assert.Null(LayerMapScope.FindDuplicate(new[] { "modelo", "Layout1", "Layout2" }));
        }

        [Fact]
        public void TextosDeForma()
        {
            Assert.Equal("espacios: debe ser un array de 1 a 100 nombres (\"modelo\" o nombres de layout)", LayerMapScope.ShapeError);
            Assert.Equal("espacios[2]: debe ser una cadena de 1 a 255 caracteres", LayerMapScope.ItemError(2));
        }

        [Theory]
        // espacio excluido: todo (también los viewports) cuenta como ExcluidaEspacio.
        [InlineData(false, true, true, true, ScopeDecision.ExcluidaEspacio)]
        [InlineData(false, true, false, true, ScopeDecision.ExcluidaEspacio)]
        [InlineData(false, false, false, false, ScopeDecision.ExcluidaEspacio)]
        // viewport n.º 1: nunca, ni con incluir_viewports.
        [InlineData(true, true, true, true, ScopeDecision.ViewportPapel)]
        [InlineData(true, true, true, false, ScopeDecision.ViewportPapel)]
        // otros viewports: solo con incluir_viewports.
        [InlineData(true, true, false, false, ScopeDecision.ExcluidaViewport)]
        [InlineData(true, true, false, true, ScopeDecision.Reasignar)]
        // entidades normales.
        [InlineData(true, false, false, false, ScopeDecision.Reasignar)]
        [InlineData(true, false, false, true, ScopeDecision.Reasignar)]
        public void Classify_Orden(bool incluido, bool viewport, bool papel, bool incluirViewports, ScopeDecision esperado)
        {
            Assert.Equal(esperado, LayerMapScope.Classify(incluido, viewport, papel, incluirViewports));
        }

        [Fact]
        public void AdvertenciaViewports_Literal_YOmiteCeros()
        {
            var porLayout = new List<KeyValuePair<string, int>>
            {
                new KeyValuePair<string, int>("Layout1", 2),
                new KeyValuePair<string, int>("Layout2", 0),
                new KeyValuePair<string, int>("Lamina", 1),
            };
            Assert.Equal(
                "3 viewport(s) de layout en capas origen no se reasignaron (Layout1 2, Lamina 1); pase incluir_viewports: true para reasignarlos.",
                LayerMapScope.ExcludedViewportsWarning(porLayout));
        }

        [Fact]
        public void AdvertenciaViewports_SinNinguno_Null()
        {
            Assert.Null(LayerMapScope.ExcludedViewportsWarning(new List<KeyValuePair<string, int>>()));
            Assert.Null(LayerMapScope.ExcludedViewportsWarning(new[] { new KeyValuePair<string, int>("Layout1", 0) }));
        }

        [Theory]
        [InlineData(1, false, true)]
        [InlineData(1, true, true)]
        // Number sin inicializar (layout no activado en la sesión): el primero del layout es el de papel.
        [InlineData(0, true, true)]
        [InlineData(-1, true, true)]
        [InlineData(-1, false, false)]
        [InlineData(2, true, false)]
        [InlineData(3, false, false)]
        public void ViewportPapel_Numero1OPrimeroSinNumero(int number, bool primero, bool esperado)
        {
            Assert.Equal(esperado, ViewportWindow.IsPaperSpaceViewport(number, primero));
        }

        private static KeyValuePair<long, int> Vp(long id, int number) => new KeyValuePair<long, int>(id, number);

        [Fact]
        public void PapelResuelto_GetViewportsEsLaFuenteDeVerdad()
        {
            // Layout no actual pero inicializado: Number = −1 en todos y el de papel NO es el primero del registro.
            var btr = new[] { Vp(0x3B5B9, -1), Vp(0x12D40, -1), Vp(0x12D44, -1) };
            Assert.True(ViewportWindow.TryResolvePaperViewport(new long[] { 0x12D40, 0x3B5B9, 0x12D44 }, btr, out long papel));
            Assert.Equal(0x12D40, papel);
        }

        [Fact]
        public void PapelResuelto_SinGetViewports_Number1()
        {
            var btr = new[] { Vp(0x3B5B9, 3), Vp(0x12D40, 1), Vp(0x12D44, 2) };
            Assert.True(ViewportWindow.TryResolvePaperViewport(Array.Empty<long>(), btr, out long papel));
            Assert.Equal(0x12D40, papel);
        }

        [Fact]
        public void PapelResuelto_Respaldo_PrimeroDelRegistro()
        {
            // Layout no actual sin inicializar: GetViewports vacío y Number = −1 en todos.
            var btr = new[] { Vp(0x17899, -1), Vp(0x1789D, -1) };
            Assert.True(ViewportWindow.TryResolvePaperViewport(Array.Empty<long>(), btr, out long papel));
            Assert.Equal(0x17899, papel);
        }

        [Fact]
        public void PapelResuelto_GetViewportsAjenoAlRegistro_SeIgnora()
        {
            var btr = new[] { Vp(0x20, -1), Vp(0x21, 1) };
            Assert.True(ViewportWindow.TryResolvePaperViewport(new long[] { 0x99 }, btr, out long papel));
            Assert.Equal(0x21, papel);
        }

        [Fact]
        public void PapelResuelto_SinViewports_False()
        {
            Assert.False(ViewportWindow.TryResolvePaperViewport(new long[] { 0x99 }, Array.Empty<KeyValuePair<long, int>>(), out _));
        }

        [Fact]
        public void PapelResuelto_UnoComoMaximo_ConIncluirViewports()
        {
            // O1: con incluir_viewports el papel real de un layout no actual sigue siendo ViewportPapel aunque no sea el
            // primero del registro; el primero del registro (ventana de modelo) sí se reasigna.
            var btr = new[] { Vp(0x3B5B9, -1), Vp(0x12D40, -1) };
            Assert.True(ViewportWindow.TryResolvePaperViewport(new long[] { 0x12D40, 0x3B5B9 }, btr, out long papel));
            Assert.Equal(ScopeDecision.ViewportPapel, LayerMapScope.Classify(true, true, 0x12D40 == papel, true));
            Assert.Equal(ScopeDecision.Reasignar, LayerMapScope.Classify(true, true, 0x3B5B9 == papel, true));
        }

        [Fact]
        public void CasoRealDelGate_Layout1_12D40_Papel_3B5B9_NoPapel()
        {
            // Layout1: 12D40 (n.º 1, capa 0) es el papel; 3B5B9 (n.º 3, capa 0) es una ventana de modelo → excluida por
            // defecto (aviso) y reasignable con incluir_viewports; con espacios ["modelo"] ambas son ExcluidaEspacio.
            bool papel = ViewportWindow.IsPaperSpaceViewport(1, true);
            bool otro = ViewportWindow.IsPaperSpaceViewport(3, false);
            Assert.Equal(ScopeDecision.ViewportPapel, LayerMapScope.Classify(true, true, papel, false));
            Assert.Equal(ScopeDecision.ExcluidaViewport, LayerMapScope.Classify(true, true, otro, false));
            Assert.Equal(ScopeDecision.Reasignar, LayerMapScope.Classify(true, true, otro, true));
            Assert.True(LayerMapScope.TryResolve(new[] { "modelo" }, Layouts, out IReadOnlyList<string>? r, out _));
            Assert.Equal(ScopeDecision.ExcluidaEspacio, LayerMapScope.Classify(LayerMapScope.Includes(r, "Layout1"), true, papel, true));
        }
    }
}
