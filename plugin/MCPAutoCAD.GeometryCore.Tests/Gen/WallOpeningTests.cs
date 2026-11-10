using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>protocol.md §5G.1: hueco en el contorno cerrado de un muro.</summary>
    public class WallOpeningTests
    {
        [Theory]
        [InlineData(0.00001)]
        [InlineData(0.009)]
        public void PiezaCasiDegenerada_NoCabe(double pieza)
        {
            var muro = new List<(double x, double y)> { (0, 0), (6, 0), (6, 0.15), (0, 0.15) };
            WallGeometryException e = Assert.Throws<WallGeometryException>(() => WallOpening.Cut(muro, (pieza + 0.4, 0.07), 0.8));
            Assert.Equal("ancho: el hueco (0.8) no cabe en el tramo (longitud 6): se sale del tramo o cruza una esquina", e.Message);
        }

        [Fact]
        public void PiezaDeUnCentimetro_Cabe()
        {
            var muro = new List<(double x, double y)> { (0, 0), (6, 0), (6, 0.15), (0, 0.15) };
            Assert.Equal(0.42, WallOpening.Cut(muro, (0.42, 0.07), 0.8).DistanciaDesdeInicio, 6);
        }

        private const int P = 9;

        private const string NoSimple = "muro: el contorno debe ser un polígono cerrado simple de al menos 4 vértices";
        private const string NoTramo = "muro: no se encontró un tramo recto (dos caras paralelas) cerca del punto indicado";
        private const string MenorQueTramo = "ancho: debe ser menor que la longitud del tramo";
        private const string CruzaVertice = "ancho: el hueco cruza un vértice del contorno (esquina u otro vano)";

        private static List<(double x, double y)> Pts(params double[] xy)
        {
            var r = new List<(double x, double y)>();
            for (int i = 0; i < xy.Length; i += 2)
            {
                r.Add((xy[i], xy[i + 1]));
            }

            return r;
        }

        /// <summary>Muro horizontal de 3 m y 0,15 de espesor, antihorario.</summary>
        private static List<(double x, double y)> Muro3m() => Pts(0, 0, 3, 0, 3, 0.15, 0, 0.15);

        private static List<(double x, double y)> Invertido(List<(double x, double y)> v)
        {
            var r = new List<(double x, double y)>(v);
            r.Reverse();
            return r;
        }

        private static List<(double x, double y)> Girado(List<(double x, double y)> v, double grados, double dx = 0, double dy = 0)
        {
            double a = grados * Math.PI / 180.0;
            var r = new List<(double x, double y)>();
            foreach ((double x, double y) p in v)
            {
                r.Add(((p.x * Math.Cos(a)) - (p.y * Math.Sin(a)) + dx, (p.x * Math.Sin(a)) + (p.y * Math.Cos(a)) + dy));
            }

            return r;
        }

        private static (double x, double y) GiradoPt((double x, double y) p, double grados, double dx = 0, double dy = 0) =>
            Girado(new List<(double x, double y)> { p }, grados, dx, dy)[0];

        private static void AssertPt((double x, double y) esperado, (double x, double y) real, int precision = P)
        {
            Assert.Equal(esperado.x, real.x, precision);
            Assert.Equal(esperado.y, real.y, precision);
        }

        private static double Area(IReadOnlyList<(double x, double y)> v) => PolygonMetrics.LengthAndArea(v, true).area ?? 0;

        private static WallGeometryException Throws(Action f) => Assert.Throws<WallGeometryException>(() => f());

        private static void AssertGeom(string detalle, Action f)
        {
            WallGeometryException e = Throws(f);
            Assert.Equal(detalle, e.Message);
            Assert.False(e.EsParametro);
        }

        [Fact]
        public void Muro3m_Hueco080A12DelInicio_DosContornosDe4Vertices()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (1.2, 0.075), 0.8);
            Assert.Equal(4, r.LadoInicio.Count);
            Assert.Equal(4, r.LadoFin.Count);
            Assert.Equal(0.15 * 2.2, Area(r.LadoInicio) + Area(r.LadoFin), P);
            Assert.Equal(0.15 * 0.8, Area(r.LadoInicio), P);
            Assert.Equal(0.15 * 1.4, Area(r.LadoFin), P);
        }

        [Fact]
        public void Muro3m_NumerosDelTramo()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (1.2, 0.075), 0.8);
            AssertPt((1, 0), r.Direccion);
            AssertPt((1.2, 0.075), r.Centro);
            Assert.Equal(0.15, r.Espesor, P);
            Assert.Equal(3, r.LongitudTramo, P);
            Assert.Equal(1.2, r.DistanciaDesdeInicio, P);
        }

        [Fact]
        public void Muro3m_EsquinasDelHueco_A1B1EnCaraIzquierda()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (1.2, 0.075), 0.8);
            AssertPt((0.8, 0.15), r.A1);
            AssertPt((0.8, 0), r.A2);
            AssertPt((1.6, 0.15), r.B1);
            AssertPt((1.6, 0), r.B2);
        }

        [Fact]
        public void Muro3m_LadoInicioYLadoFin_ContienenSusVertices()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (1.2, 0.075), 0.8);
            Assert.Contains(r.LadoInicio, p => p.x == 0 && p.y == 0);
            Assert.Contains(r.LadoInicio, p => p.x == 0 && p.y == 0.15);
            Assert.Contains(r.LadoFin, p => p.x == 3 && p.y == 0);
            Assert.Contains(r.LadoFin, p => p.x == 3 && p.y == 0.15);
        }

        [Fact]
        public void SentidoOriginal_AntihorarioSeConserva()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (1.2, 0.075), 0.8);
            Assert.True(PolygonMetrics.SignedArea(r.LadoInicio) > 0);
            Assert.True(PolygonMetrics.SignedArea(r.LadoFin) > 0);
        }

        [Fact]
        public void SentidoOriginal_HorarioSeConserva_YElResultadoEsElMismo()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Invertido(Muro3m()), (1.2, 0.075), 0.8);
            Assert.True(PolygonMetrics.SignedArea(r.LadoInicio) < 0);
            Assert.True(PolygonMetrics.SignedArea(r.LadoFin) < 0);
            AssertPt((0.8, 0.15), r.A1);
            AssertPt((1.6, 0), r.B2);
            Assert.Equal(0.15 * 0.8, Area(r.LadoInicio), P);
        }

        [Fact]
        public void PrimerVerticeDistinto_MismoResultado()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Pts(3, 0.15, 0, 0.15, 0, 0, 3, 0), (1.2, 0.075), 0.8);
            AssertPt((1, 0), r.Direccion);
            AssertPt((0.8, 0.15), r.A1);
            Assert.Equal(0.15 * 0.8, Area(r.LadoInicio), P);
            Assert.Equal(0.15 * 1.4, Area(r.LadoFin), P);
        }

        [Fact]
        public void DuplicadosConsecutivos_SeIgnoran()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Pts(0, 0, 0, 0, 3, 0, 3, 0.15, 0, 0.15, 0, 0), (1.2, 0.075), 0.8);
            Assert.Equal(4, r.LadoInicio.Count);
            Assert.Equal(4, r.LadoFin.Count);
        }

        [Fact]
        public void CentroFueraDelEje_SeProyectaSobreLaLineaMedia()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (1.2, 0.01), 0.8);
            AssertPt((1.2, 0.075), r.Centro);
        }

        [Fact]
        public void CentroFueraDelMuro_SeProyectaSobreLaLineaMedia()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (1.2, 2), 0.8);
            AssertPt((1.2, 0.075), r.Centro);
            Assert.Equal(0.15, r.Espesor, P);
        }

        [Fact]
        public void CentroBajoElMuro_SeProyectaSobreLaLineaMedia()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (1.2, -2), 0.8);
            AssertPt((1.2, 0.075), r.Centro);
        }

        [Fact]
        public void MuroVertical_InicioEnMenorY_A1EnCaraOeste()
        {
            List<(double x, double y)> muro = Pts(0, 0, 0.15, 0, 0.15, 3, 0, 3);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, (0.075, 1.2), 0.8);
            AssertPt((0, 1), r.Direccion);
            Assert.Equal(1.2, r.DistanciaDesdeInicio, P);
            AssertPt((0, 0.8), r.A1);
            AssertPt((0.15, 0.8), r.A2);
            AssertPt((0, 1.6), r.B1);
            AssertPt((0.15, 1.6), r.B2);
            Assert.Contains(r.LadoInicio, p => p.x == 0 && p.y == 0);
            Assert.Contains(r.LadoFin, p => p.x == 0 && p.y == 3);
        }

        [Theory]
        [InlineData(30)]
        [InlineData(60)]
        [InlineData(-30)]
        [InlineData(-75)]
        public void MuroGirado_DireccionHaciaXCreciente(double grados)
        {
            List<(double x, double y)> muro = Girado(Muro3m(), grados, 3300, 3900);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, GiradoPt((1.2, 0.075), grados, 3300, 3900), 0.8);
            Assert.True(r.Direccion.x > 0);
            Assert.Equal(1.2, r.DistanciaDesdeInicio, 7);
            Assert.Equal(0.15, r.Espesor, 7);
            Assert.Equal(3, r.LongitudTramo, 7);
            Assert.Equal(0.8, PolygonMetrics.Dist(r.A1, r.B1), 7);
            Assert.Equal(0.8, PolygonMetrics.Dist(r.A2, r.B2), 7);
            Assert.Equal(0.15, PolygonMetrics.Dist(r.A1, r.A2), 7);
            Assert.Equal(0.15 * 2.2, Area(r.LadoInicio) + Area(r.LadoFin), 6);
        }

        [Theory]
        [InlineData(120)]
        [InlineData(210)]
        public void MuroGirado_MasDe90_ElInicioEsElExtremoDeMenorX(double grados)
        {
            List<(double x, double y)> muro = Girado(Muro3m(), grados);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, GiradoPt((1.2, 0.075), grados), 0.8);
            Assert.True(r.Direccion.x > 0);
            Assert.Equal(1.8, r.DistanciaDesdeInicio, 7); // el extremo de x = 0 del muro sin girar es ahora el fin
        }

        [Fact]
        public void A1YB1_EstanALaIzquierdaDeLaDireccion_EnMuroQueCorreHaciaX()
        {
            List<(double x, double y)> muro = Girado(Muro3m(), 210);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, GiradoPt((1.2, 0.075), 210), 0.8);
            (double x, double y) n = (-r.Direccion.y, r.Direccion.x);
            double h1 = ((r.A1.x - r.Centro.x) * n.x) + ((r.A1.y - r.Centro.y) * n.y);
            double h2 = ((r.B2.x - r.Centro.x) * n.x) + ((r.B2.y - r.Centro.y) * n.y);
            Assert.Equal(0.075, h1, 7);
            Assert.Equal(-0.075, h2, 7);
        }

        [Fact]
        public void A1EstaEnElLadoDelInicio_B1EnElDelFin()
        {
            List<(double x, double y)> muro = Girado(Muro3m(), 150);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, GiradoPt((1.2, 0.075), 150), 0.8);
            double tA = ((r.A1.x - r.Centro.x) * r.Direccion.x) + ((r.A1.y - r.Centro.y) * r.Direccion.y);
            double tB = ((r.B1.x - r.Centro.x) * r.Direccion.x) + ((r.B1.y - r.Centro.y) * r.Direccion.y);
            Assert.Equal(-0.4, tA, 7);
            Assert.Equal(0.4, tB, 7);
        }

        [Fact]
        public void LadoInicio_EsElDeMenorX_AunqueLaAristaCercanaVayaAlReves()
        {
            // El punto está pegado a la cara superior, que en el lazo CCW corre hacia −x.
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (1.2, 0.149), 0.8);
            Assert.Contains(r.LadoInicio, p => p.x == 0);
            Assert.Contains(r.LadoFin, p => p.x == 3);
            AssertPt((0.8, 0.15), r.A1);
            AssertPt((1.6, 0), r.B2);
        }

        [Fact]
        public void Paralelogramo_CortesPerpendicularesAlTramo()
        {
            List<(double x, double y)> muro = Pts(0, 0, 3, 0, 3.2, 0.15, 0.2, 0.15);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, (1.5, 0.075), 0.8);
            Assert.Equal(2.8, r.LongitudTramo, P); // solape de las dos caras proyectado
            Assert.Equal(1.3, r.DistanciaDesdeInicio, P);
            AssertPt((1.1, 0.15), r.A1);
            AssertPt((1.1, 0), r.A2);
        }

        [Fact]
        public void IngleteEnL_HuecoEnPrimerTramo_LadoInicio4YLadoFin6()
        {
            WallResult w = WallOutline.Build(Pts(0, 0, 4, 0, 4, 3), 0.15, WallJustification.Centro, false);
            Assert.Equal(6, w.Contorno.Count);
            WallOpening.OpeningResult r = WallOpening.Cut(w.Contorno, (2, 0), 0.8);
            Assert.Equal(4, r.LadoInicio.Count);
            Assert.Equal(6, r.LadoFin.Count);
            Assert.Equal(w.Area - (0.8 * 0.15), Area(r.LadoInicio) + Area(r.LadoFin), P);
            AssertPt((1, 0), r.Direccion);
            Assert.Equal(0.15, r.Espesor, P);
            Assert.Equal(3.925, r.LongitudTramo, P); // el solape termina en el inglete de la cara interior
        }

        [Fact]
        public void IngleteEnL_HuecoEnSegundoTramo_LadoFin4YLadoInicio6()
        {
            WallResult w = WallOutline.Build(Pts(0, 0, 4, 0, 4, 3), 0.15, WallJustification.Centro, false);
            WallOpening.OpeningResult r = WallOpening.Cut(w.Contorno, (4, 1.5), 0.8);
            Assert.Equal(6, r.LadoInicio.Count);
            Assert.Equal(4, r.LadoFin.Count);
            AssertPt((0, 1), r.Direccion);
            Assert.Equal(w.Area - (0.8 * 0.15), Area(r.LadoInicio) + Area(r.LadoFin), P);
        }

        [Fact]
        public void IngleteEnL_ConservaElSentidoHorarioDelContorno()
        {
            WallResult w = WallOutline.Build(Pts(0, 0, 4, 0, 4, 3), 0.15, WallJustification.Centro, false);
            Assert.True(PolygonMetrics.SignedArea(w.Contorno) < 0);
            WallOpening.OpeningResult r = WallOpening.Cut(w.Contorno, (2, 0), 0.8);
            Assert.True(PolygonMetrics.SignedArea(r.LadoInicio) < 0);
            Assert.True(PolygonMetrics.SignedArea(r.LadoFin) < 0);
        }

        [Fact]
        public void IngleteEnL_HuecoQueCruzaLaEsquina_NoCabe()
        {
            WallResult w = WallOutline.Build(Pts(0, 0, 4, 0, 4, 3), 0.15, WallJustification.Centro, false);
            AssertGeom(
                "ancho: el hueco (0.8) no cabe en el tramo (longitud 3.925): se sale del tramo o cruza una esquina",
                () => WallOpening.Cut(w.Contorno, (3.7, 0), 0.8));
        }

        [Fact]
        public void HuecoSobreContornoYaPartido_NoCruzaElHuecoAnterior()
        {
            WallOpening.OpeningResult primero = WallOpening.Cut(Muro3m(), (1.2, 0.075), 0.8);
            AssertGeom(
                "ancho: el hueco (0.8) no cabe en el tramo (longitud 1.4): se sale del tramo o cruza una esquina",
                () => WallOpening.Cut(primero.LadoFin, (1.8, 0.075), 0.8));
        }

        [Fact]
        public void SegundoHuecoEnElLadoFin_ParteEseContorno()
        {
            WallOpening.OpeningResult primero = WallOpening.Cut(Muro3m(), (1.2, 0.075), 0.8);
            WallOpening.OpeningResult segundo = WallOpening.Cut(primero.LadoFin, (2.3, 0.075), 0.6);
            Assert.Equal(4, segundo.LadoInicio.Count);
            Assert.Equal(4, segundo.LadoFin.Count);
            Assert.Equal(0.15 * (1.4 - 0.6), Area(segundo.LadoInicio) + Area(segundo.LadoFin), P);
        }

        [Fact]
        public void PlanoReal_95795_CoordenadasGrandesYSentidoHorario()
        {
            List<(double x, double y)> muro = Pts(
                3311.853226773471, 3983.194007335784, 3313.163226773472, 3983.194007335784,
                3313.163226773472, 3983.074007335784, 3311.853226773471, 3983.074007335784);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, (3312.5, 3983.134), 0.4);
            Assert.Equal(0.12, r.Espesor, 9);
            Assert.Equal(1.31, r.LongitudTramo, 9);
            Assert.Equal(0.12 * 0.91, Area(r.LadoInicio) + Area(r.LadoFin), 7);
            Assert.True(PolygonMetrics.SignedArea(r.LadoInicio) < 0);
        }

        [Fact]
        public void Fuzz_ElHuecoConservaElArea_YLasCarasDelHueco()
        {
            var azar = new Random(20261031);
            for (int n = 0; n < 2000; n++)
            {
                double largo = 1 + (azar.NextDouble() * 6);
                double espesor = 0.08 + (azar.NextDouble() * 0.4);
                double angulo = azar.NextDouble() * 360;
                double ancho = 0.1 + (azar.NextDouble() * (largo - 0.3));
                double desde = 0.05 + (azar.NextDouble() * (largo - ancho - 0.1)) + (ancho / 2.0);
                List<(double x, double y)> muro = Girado(Pts(0, 0, largo, 0, largo, espesor, 0, espesor), angulo, 3300, 3900);
                if (n % 2 == 1)
                {
                    muro.Reverse();
                }

                WallOpening.OpeningResult r = WallOpening.Cut(muro, GiradoPt((desde, espesor / 2.0), angulo, 3300, 3900), ancho);
                Assert.Equal(largo * espesor, Area(r.LadoInicio) + Area(r.LadoFin) + (ancho * espesor), 5);
                Assert.Equal(espesor, r.Espesor, 6);
                Assert.Equal(ancho, PolygonMetrics.Dist(r.A1, r.B1), 6);
                Assert.Equal(ancho, PolygonMetrics.Dist(r.A2, r.B2), 6);
                Assert.Equal(espesor, PolygonMetrics.Dist(r.A1, r.A2), 6);
                Assert.Equal(espesor, PolygonMetrics.Dist(r.B1, r.B2), 6);
            }
        }

        [Fact]
        public void Ancho_Cero_EsParametro()
        {
            WallGeometryException e = Throws(() => WallOpening.Cut(Muro3m(), (1, 0), 0));
            Assert.Equal("ancho: debe ser un número mayor que 0", e.Message);
            Assert.True(e.EsParametro);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Ancho_NoValido_EsParametro(double ancho)
        {
            WallGeometryException e = Throws(() => WallOpening.Cut(Muro3m(), (1, 0), ancho));
            Assert.True(e.EsParametro);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(3.5)]
        public void Ancho_IgualOMayorQueElTramo(double ancho)
        {
            AssertGeom(MenorQueTramo, () => WallOpening.Cut(Muro3m(), (1.5, 0.075), ancho));
        }

        [Fact]
        public void HuecoQueSeSaleDelTramo_NoCabe()
        {
            AssertGeom(
                "ancho: el hueco (0.8) no cabe en el tramo (longitud 3): se sale del tramo o cruza una esquina",
                () => WallOpening.Cut(Muro3m(), (0.3, 0.075), 0.8));
        }

        [Fact]
        public void HuecoHastaElExtremoExacto_NoCabe_MargenMinimo()
        {
            AssertGeom(
                "ancho: el hueco (0.8) no cabe en el tramo (longitud 3): se sale del tramo o cruza una esquina",
                () => WallOpening.Cut(Muro3m(), (0.4, 0.075), 0.8));
        }

        [Fact]
        public void HuecoPorElLadoDelFin_NoCabe()
        {
            AssertGeom(
                "ancho: el hueco (0.8) no cabe en el tramo (longitud 3): se sale del tramo o cruza una esquina",
                () => WallOpening.Cut(Muro3m(), (2.7, 0.075), 0.8));
        }

        [Fact]
        public void HuecoJustoDentroDelMargen_SeAdmite()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Muro3m(), (0.4 + 0.01 + 1e-5, 0.075), 0.8);
            Assert.Equal(4, r.LadoInicio.Count);
        }

        [Fact]
        public void CentroMasAllaDelExtremo_NoCabe()
        {
            AssertGeom(
                "ancho: el hueco (0.5) no cabe en el tramo (longitud 3): se sale del tramo o cruza una esquina",
                () => WallOpening.Cut(Muro3m(), (5, 0.075), 0.5));
        }

        [Fact]
        public void PuntoJuntoALaTapa_NoParteElMuroALoLargo()
        {
            // La arista más cercana es la tapa, pero tapa con tapa no es un tramo: 3 de separación y 0,15 de solape.
            AssertGeom(
                "ancho: el hueco (0.1) no cabe en el tramo (longitud 3): se sale del tramo o cruza una esquina",
                () => WallOpening.Cut(Muro3m(), (2.99, 0.075), 0.1));
        }

        [Fact]
        public void MuroCorto_JuntoALaTapa_SigueElTramoLargo()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Pts(0, 0, 1, 0, 1, 0.4, 0, 0.4), (0.5, 0.1), 0.3);
            Assert.Equal(1, r.LongitudTramo, P);
            Assert.Equal(0.4, r.Espesor, P);
        }

        [Fact]
        public void CuadradoPerfecto_LongitudIgualAlEspesor_SeAdmite()
        {
            WallOpening.OpeningResult r = WallOpening.Cut(Pts(0, 0, 1, 0, 1, 1, 0, 1), (0.5, 0.1), 0.3);
            Assert.Equal(1, r.Espesor, P);
        }

        [Fact]
        public void PrimeraAristaSinCaraParalela_NoSeSaltaAOtra()
        {
            // La arista inclinada es la más cercana y no tiene pareja: §5G.1 no busca otra.
            AssertGeom(NoTramo, () => WallOpening.Cut(Pts(0, 0, 6, 0, 5, 1, 1, 1), (5.6, 0.5), 0.1));
        }

        [Fact]
        public void NumerosConTresDecimales_CulturaInvariante()
        {
            AssertGeom(
                "ancho: el hueco (0.812) no cabe en el tramo (longitud 3): se sale del tramo o cruza una esquina",
                () => WallOpening.Cut(Muro3m(), (0.1, 0.075), 0.81234));
        }

        [Fact]
        public void VerticeEnLaFranja_MuescaDesdeLaTapa()
        {
            // Ranura que entra por el extremo derecho hasta x = 5, entre las dos caras largas.
            List<(double x, double y)> muro = Pts(0, 0, 10, 0, 10, 0.4, 5, 0.4, 5, 0.6, 10, 0.6, 10, 1, 0, 1);
            AssertGeom(CruzaVertice, () => WallOpening.Cut(muro, (4.5, 0.05), 2));
        }

        [Fact]
        public void HuecoAntesDeLaMuesca_SeAdmite()
        {
            List<(double x, double y)> muro = Pts(0, 0, 10, 0, 10, 0.4, 5, 0.4, 5, 0.6, 10, 0.6, 10, 1, 0, 1);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, (2, 0.05), 1);
            Assert.Equal(1, r.Espesor, P);
            Assert.Equal(4, r.LadoInicio.Count);
            Assert.Equal(8, r.LadoFin.Count);
        }

        [Fact]
        public void CaraParalelaMasCercanaQueAbarcaElCentro_SePrefiere()
        {
            // Ante la ranura, la cara opuesta de la cara inferior es la propia ranura, a 0,4.
            List<(double x, double y)> muro = Pts(0, 0, 10, 0, 10, 0.4, 5, 0.4, 5, 0.6, 10, 0.6, 10, 1, 0, 1);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, (7.5, 0.05), 1);
            Assert.Equal(0.4, r.Espesor, P);
            Assert.Equal(5, r.LongitudTramo, P);
        }

        [Fact]
        public void ContornoResultanteQueSeAutointerseca_FormatoDeTramos()
        {
            // Dos aristas oblicuas cruzan la franja sin tener vértices dentro de ella.
            List<(double x, double y)> muro = Pts(0, 0, 10, 0, 10, 0.4, 2, 0.5, 10, 0.6, 10, 1, 0, 1);
            AssertGeom(
                "muro: el contorno resultante se autointerseca (tramos 2 y 6)",
                () => WallOpening.Cut(muro, (5, 0.05), 2));
        }

        [Fact]
        public void ContornoNulo()
        {
            AssertGeom(NoSimple, () => WallOpening.Cut(null!, (0, 0), 1));
        }

        [Fact]
        public void Triangulo_NoEsContornoDeMuro()
        {
            AssertGeom(NoSimple, () => WallOpening.Cut(Pts(0, 0, 3, 0, 0, 1), (1, 0), 0.5));
        }

        [Fact]
        public void Autointersecado_NoEsContornoDeMuro()
        {
            AssertGeom(NoSimple, () => WallOpening.Cut(Pts(0, 0, 3, 0.15, 3, 0, 0, 0.15), (1, 0), 0.5));
        }

        [Fact]
        public void AreaCero_NoEsContornoDeMuro()
        {
            AssertGeom(NoSimple, () => WallOpening.Cut(Pts(0, 0, 1, 0, 2, 0, 3, 0), (1, 0), 0.5));
        }

        [Fact]
        public void CoordenadaNoFinita_NoEsContornoDeMuro()
        {
            AssertGeom(NoSimple, () => WallOpening.Cut(Pts(0, 0, 3, 0, 3, double.NaN, 0, 1), (1, 0), 0.5));
        }

        [Fact]
        public void TresVerticesTrasColapsar_NoEsContornoDeMuro()
        {
            AssertGeom(NoSimple, () => WallOpening.Cut(Pts(0, 0, 3, 0, 3, 0, 0, 1, 0, 0), (1, 0), 0.5));
        }

        [Fact]
        public void SinCarasParalelas_NoHayTramo()
        {
            AssertGeom(NoTramo, () => WallOpening.Cut(Pts(0, 0, 4, 0, 3, 2, 0.5, 1), (2, 0), 0.5));
        }

        [Fact]
        public void CaraParalelaSinSolape_NoHayTramo()
        {
            // Escalón: las únicas aristas horizontales no se solapan a lo largo del tramo.
            List<(double x, double y)> muro = Pts(0, 0, 2, 0, 3, 1, 5, 1, 4, 3, 1.5, 2);
            AssertGeom(NoTramo, () => WallOpening.Cut(muro, (1, 0), 0.5));
        }

        [Fact]
        public void CentroNoFinito_NoHayTramo()
        {
            AssertGeom(NoTramo, () => WallOpening.Cut(Muro3m(), (double.NaN, 0), 0.5));
        }

        [Fact]
        public void Orden_AnchoInvalidoAntesQueContorno()
        {
            WallGeometryException e = Throws(() => WallOpening.Cut(Pts(0, 0), (0, 0), -1));
            Assert.True(e.EsParametro);
        }

        [Fact]
        public void CenterFromDistance_DesdeElInicio()
        {
            AssertPt((1.2, 0.075), WallOpening.CenterFromDistance(Muro3m(), 1.2, false));
        }

        [Fact]
        public void CenterFromDistance_DesdeElFin()
        {
            AssertPt((1.8, 0.075), WallOpening.CenterFromDistance(Muro3m(), 1.2, true));
        }

        [Fact]
        public void CenterFromDistance_MuroVertical_InicioEnMenorY()
        {
            AssertPt((0.075, 1.2), WallOpening.CenterFromDistance(Pts(0, 0, 0.15, 0, 0.15, 3, 0, 3), 1.2, false));
        }

        [Fact]
        public void CenterFromDistance_SentidoHorario()
        {
            AssertPt((1.2, 0.075), WallOpening.CenterFromDistance(Invertido(Muro3m()), 1.2, false));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        public void CenterFromDistance_ExtremosAdmitidos(double d)
        {
            AssertPt((d, 0.075), WallOpening.CenterFromDistance(Muro3m(), d, false));
        }

        [Fact]
        public void CenterFromDistance_CaraMasLargaEsLaVerticalDelContorno()
        {
            // El primer par de aristas del lazo son las tapas: el eje debe seguir las caras largas.
            AssertPt((0.075, 1), WallOpening.CenterFromDistance(Pts(0, 0, 0.15, 0, 0.15, 3, 0, 3), 1, false));
            AssertPt((0.075, 1), WallOpening.CenterFromDistance(Pts(0.15, 0, 0.15, 3, 0, 3, 0, 0), 1, false));
        }

        [Fact]
        public void CenterFromDistance_CoincideConDistanciaDesdeInicioDeCut()
        {
            List<(double x, double y)> muro = Girado(Muro3m(), 120, 10, 20);
            (double x, double y) c = WallOpening.CenterFromDistance(muro, 0.9, false);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, c, 0.5);
            Assert.Equal(0.9, r.DistanciaDesdeInicio, 7);
            AssertPt(c, r.Centro, 7);
        }

        [Fact]
        public void CenterFromDistance_ParalelogramoCoincideConCut()
        {
            List<(double x, double y)> muro = Pts(0, 0, 3, 0, 3.2, 0.15, 0.2, 0.15);
            (double x, double y) c = WallOpening.CenterFromDistance(muro, 1.1, false);
            WallOpening.OpeningResult r = WallOpening.Cut(muro, c, 0.5);
            Assert.Equal(1.1, r.DistanciaDesdeInicio, P);
        }

        [Fact]
        public void CenterFromDistance_MasDe4Vertices()
        {
            WallResult w = WallOutline.Build(Pts(0, 0, 4, 0, 4, 3), 0.15, WallJustification.Centro, false);
            AssertGeom(
                "muro: posicion por distancia solo se admite en contornos de 4 vértices",
                () => WallOpening.CenterFromDistance(w.Contorno, 1, false));
        }

        [Fact]
        public void CenterFromDistance_ContornoNulo()
        {
            AssertGeom(
                "muro: posicion por distancia solo se admite en contornos de 4 vértices",
                () => WallOpening.CenterFromDistance(null!, 1, false));
        }

        [Fact]
        public void CenterFromDistance_DuplicadosNoCuentan()
        {
            AssertPt((1, 0.075), WallOpening.CenterFromDistance(Pts(0, 0, 3, 0, 3, 0, 3, 0.15, 0, 0.15, 0, 0), 1, false));
        }

        [Theory]
        [InlineData(3.5, "posicion: la distancia 3.5 se sale del tramo (longitud 3)")]
        [InlineData(-0.2, "posicion: la distancia -0.2 se sale del tramo (longitud 3)")]
        public void CenterFromDistance_FueraDelTramo_EsParametro(double d, string texto)
        {
            WallGeometryException e = Throws(() => WallOpening.CenterFromDistance(Muro3m(), d, false));
            Assert.Equal(texto, e.Message);
            Assert.True(e.EsParametro);
        }

        [Fact]
        public void CenterFromDistance_Trapecio_SinCarasLargasParalelas_UsaLasParalelas()
        {
            // Solo las tapas son paralelas: se usa ese par porque las caras largas no lo son.
            List<(double x, double y)> muro = Pts(0, 0, 1, 0, 1, 2, 0, 1);
            (double x, double y) c = WallOpening.CenterFromDistance(muro, 0.5, false);
            Assert.Equal(0.5, c.x, P);
        }

        [Fact]
        public void CenterFromDistance_SinParalelas_NoHayTramo()
        {
            AssertGeom(NoTramo, () => WallOpening.CenterFromDistance(Pts(0, 0, 4, 0, 3, 2, 0.5, 1), 1, false));
        }
    }
}
