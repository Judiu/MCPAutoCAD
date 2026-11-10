using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>protocol.md §5D.7: criterio de cierre del contorno y validación por vértices.</summary>
    public class HatchBoundaryTests
    {
        private static List<(double x, double y)> V(params double[] xy)
        {
            var list = new List<(double x, double y)>();
            for (int i = 0; i < xy.Length; i += 2)
            {
                list.Add((xy[i], xy[i + 1]));
            }

            return list;
        }

        [Fact]
        public void IsClosed_ConBanderaClosed()
        {
            Assert.True(HatchBoundary.IsClosedPolyline(true, V(0, 0, 1, 0, 1, 1)));
            Assert.True(HatchBoundary.IsClosedPolyline(true, V(0, 0, 1, 0)));
            Assert.False(HatchBoundary.IsClosedPolyline(true, V(0, 0)));
        }

        [Fact]
        public void IsClosed_PrimeroIgualAlUltimoConTresOMas()
        {
            Assert.True(HatchBoundary.IsClosedPolyline(false, V(0, 0, 1, 0, 1, 1, 0, 0)));
            Assert.True(HatchBoundary.IsClosedPolyline(false, V(0, 0, 1, 0, 1, 1, 1e-10, 0)));
        }

        [Fact]
        public void IsClosed_AbiertaNoCuenta()
        {
            Assert.False(HatchBoundary.IsClosedPolyline(false, V(0, 0, 1, 0, 1, 1)));
            Assert.False(HatchBoundary.IsClosedPolyline(false, V(0, 0, 1, 0, 1, 1, 0, 0.001)));
        }

        [Fact]
        public void IsClosed_DosVerticesIgualesSinBandera_NoCuenta()
        {
            Assert.False(HatchBoundary.IsClosedPolyline(false, V(0, 0, 0, 0)));
        }

        [Fact]
        public void ValidateVertices_PoligonoValido()
        {
            Assert.Empty(HatchBoundary.ValidateVertices(V(0, 0, 5, 0, 5, 3, 0, 3)));
        }

        [Fact]
        public void ValidateVertices_Pajarita_ConEtiquetaContorno()
        {
            Assert.Equal(
                new[] { "contorno.vertices: la polilínea cerrada se autointerseca (tramos 0 y 2)" },
                HatchBoundary.ValidateVertices(V(0, 0, 1, 1, 1, 0, 0, 1)));
        }

        [Fact]
        public void ValidateVertices_Duplicado_ConEtiquetaContorno()
        {
            Assert.Equal(
                "contorno.vertices[2]: vértice duplicado con el anterior",
                HatchBoundary.ValidateVertices(V(0, 0, 1, 0, 1, 0, 0, 1))[0]);
        }

        [Fact]
        public void ValidateVertices_RepiteElPrimero()
        {
            Assert.Equal(
                "contorno.vertices[3]: coincide con el primero (la polilínea cerrada no debe repetir el vértice inicial)",
                HatchBoundary.ValidateVertices(V(0, 0, 1, 0, 1, 1, 0, 0))[0]);
        }

        [Fact]
        public void Mensajes_DelContrato()
        {
            Assert.Equal("contorno: indique handles o vertices (solo uno)", HatchBoundary.ContourExclusive);
            Assert.Equal("contorno.handles[1]: la entidad no es un contorno cerrado (LINE)", HatchBoundary.NotClosed(1, "LINE"));
            Assert.Equal("contorno.handles[0]: la entidad no está en el espacio 'Layout1'", HatchBoundary.OtherSpace(0, "Layout1"));
        }

        [Fact]
        public void Mensajes_CapaDelContorno()
        {
            Assert.Equal(
                "contorno.handles[2]: la entidad está en una capa bloqueada; desbloquéela o use contorno.vertices",
                HatchBoundary.LockedLayer(2));
            Assert.Equal("contorno.handles[0]: la entidad está en una capa dependiente de xref", HatchBoundary.XrefLayer(0));
        }

        [Fact]
        public void Relabel_SoloPrefijo()
        {
            Assert.Equal("contorno.vertices: x vertices", HatchBoundary.Relabel("vertices: x vertices"));
            Assert.Equal("bulges: y", HatchBoundary.Relabel("bulges: y"));
        }
    }
}
