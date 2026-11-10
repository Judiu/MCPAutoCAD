using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>OffsetChoice (protocol.md §5E.3.4; corrección del gate en vivo de Fase 4A, 2026-10-27). Puro.</summary>
    public class OffsetChoiceTests
    {
        [Fact]
        public void Choose_MenorDistancia()
        {
            Assert.Equal(1, OffsetChoice.Choose(new[] { 0.9, 0.1 }));
            Assert.Equal(0, OffsetChoice.Choose(new[] { 0.1, 0.9 }));
        }

        [Fact]
        public void Choose_EmpateEligeElPrimero()
        {
            Assert.Equal(0, OffsetChoice.Choose(new[] { 0.5, 0.5 }));
        }

        [Fact]
        public void Choose_CandidatoSinDistanciaNoDecide()
        {
            Assert.Equal(1, OffsetChoice.Choose(new[] { double.PositiveInfinity, 3.0 }));
            Assert.Equal(0, OffsetChoice.Choose(new[] { 3.0, double.NaN }));
            Assert.Equal(-1, OffsetChoice.Choose(new[] { double.PositiveInfinity, double.NaN }));
            Assert.Equal(-1, OffsetChoice.Choose(Array.Empty<double>()));
        }

        [Fact]
        public void LateralPoint_ADistanciaHaciaElLado()
        {
            Point3? p = OffsetChoice.LateralPoint(new Point3(2, 3, 0), new Point3(2, 0, 0), 0.5);
            Assert.NotNull(p);
            Assert.Equal(2.0, p!.Value.X, 9);
            Assert.Equal(0.5, p.Value.Y, 9);
            Assert.Null(OffsetChoice.LateralPoint(new Point3(2, 0, 0), new Point3(2, 0, 0), 0.5));
        }

        [Fact]
        public void IsOnRequestedSide_MitadDeLaDistancia()
        {
            Assert.True(OffsetChoice.IsOnRequestedSide(0.0, 0.5));
            Assert.True(OffsetChoice.IsOnRequestedSide(0.25, 0.5));
            Assert.False(OffsetChoice.IsOnRequestedSide(0.2501, 0.5));
            Assert.False(OffsetChoice.IsOnRequestedSide(double.NaN, 0.5));
        }

        /// <summary>
        /// O3 (2026-10-27): círculo r 1 en (0, 0), offset 1,5 hacia dentro (lado (0.5, 0)). El candidato interior no existe
        /// (r − d &lt; 0); el exterior (r 2,5) queda a 2 del punto lateral (−0.5, 0) → lado equivocado: error.
        /// </summary>
        [Fact]
        public void CirculoHaciaDentroConDistanciaMayorQueElRadio_EsLadoEquivocado()
        {
            const double r = 1.0, d = 1.5;
            var lado = new Point3(0.5, 0, 0);
            var proy = new Point3(r, 0, 0);
            Point3 lat = OffsetChoice.LateralPoint(lado, proy, d)!.Value;
            Assert.Equal(-0.5, lat.X, 9);
            double distExterior = Math.Abs(Math.Sqrt((lat.X * lat.X) + (lat.Y * lat.Y)) - (r + d));
            int elegido = OffsetChoice.Choose(new[] { Math.Abs(0.5 - (r + d)), double.PositiveInfinity });
            Assert.Equal(0, elegido);
            Assert.False(OffsetChoice.IsOnRequestedSide(distExterior, d));
            Assert.Equal(
                "lado: no se puede construir la paralela a 1.5 del lado pedido (la distancia supera o iguala el radio 1)",
                OffsetChoice.WrongSideDetail(d, OffsetChoice.RadiusReason(r)));
        }

        [Fact]
        public void LineaConPuntoDeLadoLejano_NoEsFalsoPositivo()
        {
            // Línea y = 0, lado (2, 3) lejos: la paralela y = 0.5 está a 2,5 del punto de lado pero a 0 del lateral.
            Point3 lat = OffsetChoice.LateralPoint(new Point3(2, 3, 0), new Point3(2, 0, 0), 0.5)!.Value;
            Assert.True(OffsetChoice.IsOnRequestedSide(Math.Abs(lat.Y - 0.5), 0.5));
        }

        [Fact]
        public void UnionOf_IgnoraNulasYUneLasPiezas()
        {
            BBox2? a = BBox2.FromCorners(0, 0, 1, 1);
            BBox2? b = BBox2.FromCorners(2, -1, 3, 0.5);
            Assert.Equal(BBox2.FromCorners(0, -1, 3, 1), OffsetChoice.UnionOf(new[] { a, null, b }));
            Assert.Null(OffsetChoice.UnionOf(new BBox2?[] { null }));
            Assert.Null(OffsetChoice.UnionOf(new List<BBox2?>()));
        }

        /// <summary>
        /// Caso del gate (6D136, LINE horizontal de 6 m, offset 0,15 hacia el punto (3378, 3986)): la caja es la de la
        /// paralela elegida (una línea en y 3986.3023), no la de la original ampliada 0,15 por los dos lados.
        /// </summary>
        [Fact]
        public void CajaDeLaElegida_NoAbarcaLasDosParalelas()
        {
            const double y0 = 3986.4523, d = 0.15;
            var arriba = BBox2.FromCorners(3375.1632, y0 + d, 3381.1632, y0 + d);
            var abajo = BBox2.FromCorners(3375.1632, y0 - d, 3381.1632, y0 - d);
            double distArriba = Math.Abs(3986 - (y0 + d));
            double distAbajo = Math.Abs(3986 - (y0 - d));
            var cajas = new[] { arriba, abajo };
            int elegido = OffsetChoice.Choose(new[] { distArriba, distAbajo });
            Assert.Equal(1, elegido);
            BBox2? caja = OffsetChoice.UnionOf(new BBox2?[] { cajas[elegido] });
            Assert.Equal(abajo, caja);
            Assert.Equal(0.0, caja!.Value.Height, 9);
        }
    }
}
