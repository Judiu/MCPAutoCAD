using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>protocol.md §5G.3: cadena de cotas.</summary>
    public class DimensionChainTests
    {
        private const int P = 9;

        private static List<(double x, double y)> Pts(params double[] xy)
        {
            var r = new List<(double x, double y)>();
            for (int i = 0; i < xy.Length; i += 2)
            {
                r.Add((xy[i], xy[i + 1]));
            }

            return r;
        }

        private static void AssertPt((double x, double y) esperado, (double x, double y) real)
        {
            Assert.Equal(esperado.x, real.x, P);
            Assert.Equal(esperado.y, real.y, P);
        }

        private static void AssertError(string detalle, Action f)
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => f());
            Assert.Equal(detalle, e.Message);
        }

        [Fact]
        public void Horizontal_Izquierda_CadenaConDosCotas()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 2, 0, 5, 0), 0, "izquierda", null, 0.2, true);
            Assert.Equal(2, r.Cotas.Count);
            Assert.Equal(2, r.Medidas[0], P);
            Assert.Equal(3, r.Medidas[1], P);
            Assert.Equal(5, r.Total, P);
            AssertPt((1, 0), r.Direccion);
            AssertPt((0, 1), r.NormalLado);
            AssertPt((0, 0.2), r.Cotas[0].Linea);
            AssertPt((2, 0.2), r.Cotas[1].Linea);
        }

        [Fact]
        public void Horizontal_Derecha_LineaBajoLosPuntos()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 2, 0.5, 5, 0), 0, "derecha", null, 0.2, true);
            AssertPt((0, -1), r.NormalLado);
            Assert.Equal(-0.2, r.Cotas[0].Linea.y, P);
            Assert.Equal(-0.2, r.Cotas[1].Linea.y, P);
        }

        [Fact]
        public void LaSeparacionSeMideDesdeElPuntoMasExtremo()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 2, 0.5, 5, 0), 0, "izquierda", null, 0.2, true);
            Assert.Equal(0.7, r.Cotas[0].Linea.y, P);
            Assert.Equal(0.7, r.Cotas[1].Linea.y, P);
        }

        [Fact]
        public void Vertical_Izquierda_LineaHaciaMenosX()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 0, 3, 1, 5), 90, "izquierda", null, 0.2, true);
            Assert.Equal(new[] { 3.0, 2.0 }, r.Medidas.Select(m => Math.Round(m, 9)));
            AssertPt((-1, 0), r.NormalLado);
            AssertPt((-0.2, 0), r.Cotas[0].Linea);
            AssertPt((-0.2, 3), r.Cotas[1].Linea);
        }

        [Fact]
        public void Vertical_Derecha_LineaMasAllaDelExtremo()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 0, 3, 1, 5), 90, "derecha", null, 0.2, true);
            AssertPt((1, 0), r.NormalLado);
            Assert.Equal(1.2, r.Cotas[0].Linea.x, P);
        }

        [Fact]
        public void A30Grados_MedidasProyectadas()
        {
            double c = Math.Cos(Math.PI / 6);
            double s = Math.Sin(Math.PI / 6);
            DimensionChain.ChainResult r = DimensionChain.Build(
                Pts(0, 0, 2 * c, 2 * s, 5 * c, 5 * s), 30, "izquierda", null, 0.3, true);
            Assert.Equal(2, r.Medidas[0], P);
            Assert.Equal(3, r.Medidas[1], P);
            AssertPt((-s, c), r.NormalLado);
            AssertPt((-0.3 * s, 0.3 * c), r.Cotas[0].Linea);
        }

        [Fact]
        public void A30Grados_PuntosFundidosEnLaPerpendicular()
        {
            double c = Math.Cos(Math.PI / 6);
            double s = Math.Sin(Math.PI / 6);
            // El segundo y el tercero solo se diferencian en la normal: misma proyección.
            DimensionChain.ChainResult r = DimensionChain.Build(
                Pts(0, 0, 2 * c, 2 * s, (2 * c) - (0.5 * s), (2 * s) + (0.5 * c), 5 * c, 5 * s), 30, "izquierda", null, 0.3, true);
            Assert.Equal(2, r.Cotas.Count);
            Assert.Equal(1, r.NPuntosFundidos);
            AssertPt(((2 * c) - (0.5 * s), (2 * s) + (0.5 * c)), r.Cotas[0].P2);
        }

        [Fact]
        public void PuntosDesordenados_SeOrdenanEnLaDireccion()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(5, 0, 0, 0, 2, 0), 0, "izquierda", null, 0.2, true);
            AssertPt((0, 0), r.Cotas[0].P1);
            AssertPt((2, 0), r.Cotas[0].P2);
            AssertPt((5, 0), r.Cotas[1].P2);
        }

        [Fact]
        public void Direccion180_InvierteElOrden()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 2, 0), 180, "izquierda", null, 0.2, true);
            AssertPt((2, 0), r.Cotas[0].P1);
            AssertPt((0, 0), r.Cotas[0].P2);
            Assert.Equal(2, r.Medidas[0], P);
            Assert.Equal(-1, r.NormalLado.y, P);
        }

        [Fact]
        public void Fundidos_DentroDeTolerancia_Representante_MasCercanoALaLinea()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 2, 0, 2.0005, 0.3, 5, 0), 0, "izquierda", null, 0.2, true);
            Assert.Equal(1, r.NPuntosFundidos);
            Assert.Equal(2, r.Cotas.Count);
            AssertPt((2.0005, 0.3), r.Cotas[0].P2);
            AssertPt((2.0005, 0.3), r.Cotas[1].P1);
            Assert.Equal(2.0005, r.Medidas[0], P);
            Assert.Equal(2.9995, r.Medidas[1], P);
        }

        [Fact]
        public void Fundidos_ConDerecha_RepresentanteEsElDeMenorY()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 2, 0, 2.0005, 0.3, 5, 0), 0, "derecha", null, 0.2, true);
            AssertPt((2, 0), r.Cotas[0].P2);
        }

        [Fact]
        public void Fundidos_PuntosRepetidos()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 0, 0, 3, 0, 3, 0), 0, "izquierda", null, 0.2, true);
            Assert.Equal(2, r.NPuntosFundidos);
            Assert.Single(r.Cotas);
        }

        [Fact]
        public void Fundidos_ToleranciaPropia()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 0.05, 0, 3, 0), 0, "izquierda", null, 0.2, true, 0.1);
            Assert.Equal(1, r.NPuntosFundidos);
            Assert.Single(r.Cotas);
        }

        [Fact]
        public void Fundidos_SinDeriva_SeMideDesdeElPrimeroDelGrupo()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 0.0008, 0, 0.0016, 0, 3, 0), 0, "izquierda", null, 0.2, true);
            Assert.Equal(1, r.NPuntosFundidos);
            Assert.Equal(2, r.Cotas.Count);
        }

        [Fact]
        public void CadenaFalse_UnaCotaTotal()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 2, 0, 5, 0), 0, "izquierda", null, 0.2, false);
            Assert.Single(r.Cotas);
            Assert.Equal(5, r.Total, P);
            AssertPt((0, 0), r.Cotas[0].P1);
            AssertPt((5, 0), r.Cotas[0].P2);
        }

        [Fact]
        public void LadoPunto_PorLaIzquierda()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 3, 0), 0, null, (1, 2), 0.2, true);
            AssertPt((0, 1), r.NormalLado);
        }

        [Fact]
        public void LadoPunto_PorLaDerecha()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 3, 0), 0, null, (1, -2), 0.2, true);
            AssertPt((0, -1), r.NormalLado);
            Assert.Equal(-0.2, r.Cotas[0].Linea.y, P);
        }

        [Fact]
        public void LadoPunto_RespectoAlCentroide()
        {
            // El centroide está en y = 2: un punto en y = 1 queda por debajo aunque sea positivo.
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 2, 3, 2), 0, null, (1, 1), 0.2, true);
            AssertPt((0, -1), r.NormalLado);
        }

        [Fact]
        public void LadoPunto_SobreElCentroide_VaALaIzquierda()
        {
            DimensionChain.ChainResult r = DimensionChain.Build(Pts(0, 0, 3, 0), 0, null, (1.5, 0), 0.2, true);
            AssertPt((0, 1), r.NormalLado);
        }

        [Fact]
        public void Lado_NiLadoNiPunto()
        {
            AssertError("lado: indique lado o lado_punto (solo uno)", () => DimensionChain.Build(Pts(0, 0, 3, 0), 0, null, null, 0.2, true));
        }

        [Fact]
        public void Lado_LosDos()
        {
            AssertError("lado: indique lado o lado_punto (solo uno)", () => DimensionChain.Build(Pts(0, 0, 3, 0), 0, "izquierda", (0, 1), 0.2, true));
        }

        [Theory]
        [InlineData("arriba")]
        [InlineData("IZQUIERDA")]
        [InlineData("")]
        public void Lado_Invalido(string lado)
        {
            AssertError("lado: debe ser izquierda o derecha", () => DimensionChain.Build(Pts(0, 0, 3, 0), 0, lado, null, 0.2, true));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-0.1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Separacion_Invalida(double s)
        {
            AssertError("separacion: debe ser un número mayor que 0", () => DimensionChain.Build(Pts(0, 0, 3, 0), 0, "izquierda", null, s, true));
        }

        [Fact]
        public void Puntos_TodosEnLaMismaProyeccion()
        {
            AssertError(
                "puntos: se necesitan al menos 2 puntos distintos en la dirección de la cota",
                () => DimensionChain.Build(Pts(1, 0, 1, 5, 1, 9), 0, "izquierda", null, 0.2, true));
        }

        [Fact]
        public void Puntos_UnoSolo()
        {
            AssertError(
                "puntos: se necesitan al menos 2 puntos distintos en la dirección de la cota",
                () => DimensionChain.Build(Pts(1, 0), 0, "izquierda", null, 0.2, true));
        }

        [Fact]
        public void Puntos_Nulo()
        {
            AssertError(
                "puntos: se necesitan al menos 2 puntos distintos en la dirección de la cota",
                () => DimensionChain.Build(null!, 0, "izquierda", null, 0.2, true));
        }

        [Fact]
        public void Direccion_NoFinita()
        {
            AssertError(
                "direccion: debe ser x, y, auto o un número en grados",
                () => DimensionChain.Build(Pts(0, 0, 3, 0), double.NaN, "izquierda", null, 0.2, true));
        }

        [Fact]
        public void DominantDirection_Horizontal()
        {
            Assert.Equal(0, DimensionChain.DominantDirection(Pts(0, 0, 4, 0)), P);
        }

        [Fact]
        public void DominantDirection_HorizontalInvertida()
        {
            Assert.Equal(0, DimensionChain.DominantDirection(Pts(4, 0, 0, 0)), P);
        }

        [Fact]
        public void DominantDirection_Vertical()
        {
            Assert.Equal(90, DimensionChain.DominantDirection(Pts(0, 0, 0, 4)), P);
        }

        [Fact]
        public void DominantDirection_VerticalInvertida()
        {
            Assert.Equal(90, DimensionChain.DominantDirection(Pts(0, 4, 0, 0)), P);
        }

        [Fact]
        public void DominantDirection_Pendiente45()
        {
            Assert.Equal(45, DimensionChain.DominantDirection(Pts(0, 0, 1, 1)), P);
        }

        [Fact]
        public void DominantDirection_PendienteNegativa_Queda135()
        {
            Assert.Equal(135, DimensionChain.DominantDirection(Pts(0, 0, 1, -1)), P);
        }

        [Fact]
        public void DominantDirection_ElSegmentoMasLargo()
        {
            Assert.Equal(90, DimensionChain.DominantDirection(Pts(0, 0, 1, 0, 1, 5, 2, 5)), P);
        }

        [Fact]
        public void DominantDirection_UnPunto()
        {
            Assert.Equal(0, DimensionChain.DominantDirection(Pts(3, 4)));
        }

        [Fact]
        public void DominantDirection_TodosIguales()
        {
            Assert.Equal(0, DimensionChain.DominantDirection(Pts(3, 4, 3, 4)));
        }

        [Fact]
        public void DominantDirection_Vacio()
        {
            Assert.Equal(0, DimensionChain.DominantDirection(Pts()));
        }

        [Fact]
        public void DominantDirection_Nulo()
        {
            Assert.Equal(0, DimensionChain.DominantDirection(null!));
        }

        [Fact]
        public void DominantDirection_SegmentoBajoTolerancia()
        {
            Assert.Equal(0, DimensionChain.DominantDirection(Pts(0, 0, 0, 0.0001)));
        }
    }
}
