using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>protocol.md §5D.2: pruebas obligatorias y casos límite de PolylineValidator.</summary>
    public class PolylineValidatorTests
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
        public void CuadradoCerrado_EsValido()
        {
            Assert.Empty(PolylineValidator.Validate(V(0, 0, 5, 0, 5, 3, 0, 3), true, null));
        }

        [Fact]
        public void Pajarita_Cerrada_SeAutointersecaEnTramos0y2()
        {
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 1, 1, 1, 0, 0, 1), true, null);
            Assert.Equal(new[] { "vertices: la polilínea cerrada se autointerseca (tramos 0 y 2)" }, e);
        }

        [Fact]
        public void Pajarita_Abierta_SinError()
        {
            Assert.Empty(PolylineValidator.Validate(V(0, 0, 1, 1, 1, 0, 0, 1), false, null));
        }

        [Fact]
        public void DuplicadoConsecutivo_CitaElIndice()
        {
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 1, 0, 1, 0, 2, 2), false, null);
            Assert.Equal(new[] { "vertices[2]: vértice duplicado con el anterior" }, e);
        }

        [Fact]
        public void DuplicadoDentroDeTolerancia()
        {
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 1e-10, 0, 3, 0), false, null);
            Assert.Equal("vertices[1]: vértice duplicado con el anterior", e[0]);
        }

        [Fact]
        public void CerradaDeDosVertices_Rechazada()
        {
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 1, 0), true, null);
            Assert.Equal(PolylineValidator.ClosedNeedsThree, e[0]);
            Assert.Equal("vertices: una polilínea cerrada necesita al menos 3 vértices", e[0]);
        }

        [Fact]
        public void AbiertaDeDosVertices_Valida()
        {
            Assert.Empty(PolylineValidator.Validate(V(0, 0, 1, 0), false, null));
        }

        [Fact]
        public void BulgesDeLongitudDistinta_Rechazados()
        {
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 1, 0, 1, 1), false, new[] { 0.0, 0.5 });
            Assert.Equal(new[] { "bulges: debe tener tantos elementos como vertices" }, e);
        }

        [Fact]
        public void BulgeNoFinito_CitaElIndice()
        {
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 1, 0, 1, 1), false, new[] { 0.0, double.NaN, 0.0 });
            Assert.Equal(new[] { "bulges[1]: debe ser un número finito" }, e);
        }

        [Fact]
        public void BulgesCorrectos_Validos()
        {
            Assert.Empty(PolylineValidator.Validate(V(0, 0, 2, 0, 2, 2), true, new[] { 1.0, 0.0, -0.5 }));
            Assert.Empty(PolylineValidator.Validate(V(0, 0, 2, 0), false, new[] { 1.0, 0.0 }));
        }

        [Fact]
        public void CerradaQueRepiteElPrimero_Rechazada()
        {
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 5, 0, 5, 3, 0, 0), true, null);
            Assert.Equal(
                new[] { "vertices[3]: coincide con el primero (la polilínea cerrada no debe repetir el vértice inicial)" }, e);
        }

        [Fact]
        public void AbiertaQueVuelveAlPrimero_Valida()
        {
            Assert.Empty(PolylineValidator.Validate(V(0, 0, 5, 0, 5, 3, 0, 0), false, null));
        }

        [Fact]
        public void Triangulo_Valido()
        {
            Assert.Empty(PolylineValidator.Validate(V(0, 0, 4, 0, 2, 3), true, null));
        }

        [Fact]
        public void EnL_Concava_Valida()
        {
            Assert.Empty(PolylineValidator.Validate(V(0, 0, 4, 0, 4, 1, 1, 1, 1, 4, 0, 4), true, null));
        }

        [Fact]
        public void TramosQueSeTocanEnUnVertice_Autointerseccion()
        {
            // El vértice 3 (2,0) cae sobre el tramo 0 [(0,0)-(4,0)].
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 4, 0, 4, 2, 2, 0, 0, 2), true, null);
            Assert.Single(e);
            Assert.StartsWith("vertices: la polilínea cerrada se autointerseca (tramos 0 y ", e[0]);
        }

        [Fact]
        public void TramosColinealesSolapados_Autointerseccion()
        {
            // Tramo 0 (0,0)-(4,0) y tramo 4 (3,0)-(1,0) solapan sobre el eje X.
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 4, 0, 4, 1, 3, 1, 3, 0, 1, 0, 1, -1, 0, -1), true, null);
            Assert.Single(e);
            Assert.Contains("se autointerseca", e[0]);
        }

        [Fact]
        public void CoordenadaNoFinita_Rechazada()
        {
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, double.PositiveInfinity, 0), false, null);
            Assert.Equal(new[] { "vertices[1]: coordenada no finita" }, e);
        }

        [Fact]
        public void SegmentsTouch_CasosBasicos()
        {
            Assert.True(PolylineValidator.SegmentsTouch((0, 0), (2, 2), (0, 2), (2, 0)));
            Assert.False(PolylineValidator.SegmentsTouch((0, 0), (1, 0), (0, 1), (1, 1)));
            Assert.True(PolylineValidator.SegmentsTouch((0, 0), (2, 0), (2, 0), (3, 1)));
            Assert.False(PolylineValidator.SegmentsTouch((0, 0), (1, 0), (2, 0), (3, 0)));
            Assert.True(PolylineValidator.SegmentsTouch((0, 0), (2, 0), (1, 0), (3, 0)));
        }

        [Fact]
        public void Detallado_Pajarita_CitaTramosYPuntoDelCruce()
        {
            IReadOnlyList<string> e = PolylineValidator.ValidateDetailed(V(0, 0, 1, 1, 1, 0, 0, 1), true, null);
            Assert.Equal(
                new[] { "vertices: la polilínea cerrada se autointerseca (tramos 0 y 2, punto aproximado [0.5, 0.5])" }, e);
        }

        [Fact]
        public void Detallado_SinAutointerseccion_SigueSiendoValido()
        {
            Assert.Empty(PolylineValidator.ValidateDetailed(V(0, 0, 5, 0, 5, 3, 0, 3), true, null));
        }

        [Fact]
        public void Validate_ConservaElTextoAnterior()
        {
            IReadOnlyList<string> e = PolylineValidator.Validate(V(0, 0, 1, 1, 1, 0, 0, 1), true, null);
            Assert.Equal(new[] { "vertices: la polilínea cerrada se autointerseca (tramos 0 y 2)" }, e);
        }

        [Fact]
        public void ApproxIntersection_CruceEnUnPunto()
        {
            (double x, double y) p = PolylineValidator.ApproxIntersection((0, 0), (2, 2), (0, 2), (2, 0));
            Assert.Equal(1.0, p.x, 9);
            Assert.Equal(1.0, p.y, 9);
        }

        [Fact]
        public void ApproxIntersection_SegmentosParalelosSeparados_DevuelveElPuntoMasCercano()
        {
            // Los dos puntos más cercanos son (0,0) y (0,1): el punto medio es (0, 0.5).
            (double x, double y) p = PolylineValidator.ApproxIntersection((0, 0), (2, 0), (0, 1), (2, 1));
            Assert.Equal(0.0, p.x, 9);
            Assert.Equal(0.5, p.y, 9);
        }

        [Fact]
        public void ApproxIntersection_TramoQueTocaUnVertice()
        {
            // El vértice (2,0) del tramo [(2,0)-(3,1)] cae sobre el tramo [(0,0)-(4,0)].
            (double x, double y) p = PolylineValidator.ApproxIntersection((0, 0), (4, 0), (2, 0), (3, 1));
            Assert.Equal(2.0, p.x, 9);
            Assert.Equal(0.0, p.y, 9);
        }

        [Fact]
        public void Detallado_TramosSolapadosColineales_CitaUnPuntoDelSolape()
        {
            IReadOnlyList<string> e = PolylineValidator.ValidateDetailed(V(0, 0, 4, 0, 4, 1, 3, 1, 3, 0, 1, 0, 1, -1, 0, -1), true, null);
            Assert.Single(e);
            Assert.StartsWith("vertices: la polilínea cerrada se autointerseca (tramos ", e[0]);
            Assert.Contains("punto aproximado [", e[0]);
        }

        [Fact]
        public void Fallo33_cerrada_de_dos_vertices_con_arcos_es_valida()
        {
            var v = new List<(double x, double y)> { (0, 0), (2, 0) };
            Assert.Empty(PolylineValidator.Validate(v, true, new[] { 1.0, 1.0 }));   // DONUT
            Assert.Empty(PolylineValidator.Validate(v, true, new[] { 0.0, 0.5 }));   // "D": recta + arco
            Assert.Empty(PolylineValidator.Validate(v, true, new[] { -1.0, -1.0 }));
        }

        [Fact]
        public void Fallo33_cerrada_de_dos_vertices_sin_arco_o_con_arcos_opuestos_se_rechaza()
        {
            var v = new List<(double x, double y)> { (0, 0), (2, 0) };
            Assert.Equal(PolylineValidator.ClosedNeedsThree, PolylineValidator.Validate(v, true, null)[0]);
            Assert.Equal(PolylineValidator.ClosedNeedsThreeOrArc, PolylineValidator.Validate(v, true, new[] { 0.0, 0.0 })[0]);
            Assert.Equal(PolylineValidator.TwoVertexRetrace, PolylineValidator.Validate(v, true, new[] { 1.0, -1.0 })[0]);
            Assert.Equal(PolylineValidator.BulgesLength, PolylineValidator.Validate(v, true, new[] { 1.0 })[0]);
            Assert.Equal(PolylineValidator.BulgeNotFinite(0), PolylineValidator.Validate(v, true, new[] { double.PositiveInfinity, 1.0 })[0]);
            Assert.Equal(PolylineValidator.ClosedNeedsThreeOrArc, PolylineValidator.Validate(new List<(double x, double y)> { (0, 0) }, true, new[] { 1.0 })[0]);
        }
    }
}
