using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>muros_desde_lineas: emparejado de caras paralelas (<see cref="WallPairing"/>) y selección de borrables.</summary>
    public class WallFromLinesTests
    {
        private const double Tol = 0.012;
        private const double Eps = 1e-9;

        private static WallPairOptions Opt(double min = 0.075, double max = 0.62, double tol = Tol) =>
            new WallPairOptions(min, max, tol);

        private static WallLine L(string id, double x1, double y1, double x2, double y2) => new WallLine(id, x1, y1, x2, y2);

        private static double Area(IReadOnlyList<(double x, double y)> v)
        {
            double a = 0;
            for (int i = 0; i < v.Count; i++)
            {
                (double x1, double y1) = v[i];
                (double x2, double y2) = v[(i + 1) % v.Count];
                a += (x1 * y2) - (x2 * y1);
            }

            return Math.Abs(a) / 2.0;
        }

        // ---------------------------------------------------------------- emparejado

        [Fact]
        public void DosCarasParalelas_FormanUnRectangulo()
        {
            WallPairingResult r = WallPairing.Pair(new[] { L("a", 0, 0, 4, 0), L("b", 0, 0.15, 4, 0.15) }, Opt());

            Assert.Single(r.Rects);
            Assert.Equal(1, r.Pares);
            Assert.Empty(r.SinPareja);
            Assert.Equal(2, r.Usadas.Count);
            Assert.Equal(0.15, r.Rects[0].Espesor, 9);
            Assert.Equal(4.0, r.Rects[0].Solape, 9);
            Assert.Equal(0.6, Area(r.Rects[0].Vertices), 9);
        }

        [Fact]
        public void SentidoDeLosExtremosNoImporta()
        {
            WallPairingResult r = WallPairing.Pair(new[] { L("a", 0, 0, 4, 0), L("b", 4, 0.15, 0, 0.15) }, Opt());

            Assert.Single(r.Rects);
            Assert.Equal(0.6, Area(r.Rects[0].Vertices), 9);
        }

        [Fact]
        public void MasFinaQueElMinimo_NoEmpareja()
        {
            WallPairingResult r = WallPairing.Pair(new[] { L("a", 0, 0, 4, 0), L("b", 0, 0.05, 4, 0.05) }, Opt());

            Assert.Empty(r.Rects);
            Assert.Equal(new[] { "a", "b" }, r.SinPareja);
        }

        [Fact]
        public void MasGruesaQueElMaximo_NoEmpareja()
        {
            WallPairingResult r = WallPairing.Pair(new[] { L("a", 0, 0, 4, 0), L("b", 0, 0.7, 4, 0.7) }, Opt());

            Assert.Empty(r.Rects);
            Assert.Equal(2, r.SinPareja.Count);
        }

        [Fact]
        public void NoParalelas_NoEmpareja()
        {
            double c = Math.Cos(10 * Math.PI / 180);
            double s = Math.Sin(10 * Math.PI / 180);
            WallPairingResult r = WallPairing.Pair(new[] { L("a", 0, 0, 4, 0), L("b", 0, 0.15, 4 * c, 0.15 + (4 * s)) }, Opt());

            Assert.Empty(r.Rects);
        }

        [Fact]
        public void SolapeMenorQueTolerancia_NoEmpareja()
        {
            WallPairingResult r = WallPairing.Pair(new[] { L("a", 0, 0, 4, 0), L("b", 3.995, 0.15, 8, 0.15) }, Opt());

            Assert.Empty(r.Rects);
        }

        [Fact]
        public void CaraPartidaEnDosTramos_DosRectangulos()
        {
            WallPairingResult r = WallPairing.Pair(
                new[] { L("a", 0, 0, 4, 0), L("b1", 0, 0.15, 2, 0.15), L("b2", 2, 0.15, 4, 0.15) }, Opt());

            Assert.Equal(2, r.Rects.Count);
            Assert.Empty(r.SinPareja);
            Assert.Equal(3, r.Usadas.Count);
            Assert.Equal(0.6, r.Rects.Sum(q => Area(q.Vertices)), 9);
        }

        [Fact]
        public void LaCaraMasCercanaGana()
        {
            // a-c a 0,15 y c-d a 0,20: a-c se forma primero; d (a 0,35 de a) queda sin pareja.
            WallPairingResult r = WallPairing.Pair(
                new[] { L("a", 0, 0, 4, 0), L("c", 0, 0.15, 4, 0.15), L("d", 0, 0.35, 4, 0.35) }, Opt());

            Assert.Single(r.Rects);
            Assert.Equal(new[] { "d" }, r.SinPareja);
            Assert.Equal(1, r.Pares);
        }

        [Fact]
        public void SegmentosDegeneradosSeIgnoran()
        {
            WallPairingResult r = WallPairing.Pair(new[] { L("p", 1, 1, 1.005, 1), L("a", 0, 0, 4, 0) }, Opt());

            Assert.Equal(new[] { "p" }, r.Ignoradas);
            Assert.Equal(new[] { "a" }, r.SinPareja);
        }

        [Fact]
        public void SinEntradas_ResultadoVacio()
        {
            WallPairingResult r = WallPairing.Pair(new List<WallLine>(), Opt());

            Assert.Empty(r.Rects);
            Assert.Equal(0, r.Pares);
        }

        [Fact]
        public void OpcionesInvalidas_Lanzan()
        {
            Assert.Throws<ArgumentException>(() => new WallPairOptions(0, 0.62, 0.012));
            Assert.Throws<ArgumentException>(() => new WallPairOptions(0.5, 0.2, 0.012));
            Assert.Throws<ArgumentException>(() => new WallPairOptions(0.075, 0.62, -1));
            Assert.Throws<ArgumentException>(() => new WallPairOptions(0.075, 0.62, 0.0));
            Assert.Throws<ArgumentException>(() => new WallPairOptions(0.075, 0.62, 0.012, 0));
            Assert.Throws<ArgumentException>(() => new WallPairOptions(0.075, 0.62, 0.012, 0.5, new[] { 0.0 }));
        }

        [Fact]
        public void HuecoDeAireEntreMurosVecinos_NoSeEmparejaAntesQueLasTapas()
        {
            // Muro 1: caras y=0 / y=0,15 cerradas por tapas. Muro 2: caras y=0,25 / y=0,40, también cerradas.
            // El hueco de aire (0,15–0,25 = 0,10) cabe en el rango pero NO está cerrado: no debe ganar.
            var lines = new[]
            {
                L("a", 0, 0, 4, 0), L("b", 0, 0.15, 4, 0.15), L("c", 0, 0.25, 4, 0.25), L("d", 0, 0.40, 4, 0.40),
                L("t1", 0, 0, 0, 0.15), L("t2", 4, 0, 4, 0.15), L("t3", 0, 0.25, 0, 0.40), L("t4", 4, 0.25, 4, 0.40),
            };

            WallPairingResult r = WallPairing.Pair(lines, Opt());

            Assert.Equal(2, r.Rects.Count);
            Assert.All(r.Rects, q => Assert.Equal(0.15, q.Espesor, 9));
            Assert.Equal(new[] { "t1", "t2", "t3", "t4" }, r.SinPareja);
            WallBuildResult b = WallFromLines.Build(lines, Opt());
            Assert.Equal(new[] { "t1", "t2", "t3", "t4" }, b.Tapas);
        }

        [Fact]
        public void DistanciaSeComruebaEnAmbosExtremos_NoSoloEnLaMedia()
        {
            // Cara con deriva de 0,3° sobre 10 m: 0,15 en un extremo y 0,2024 en el otro. Media 0,176 (dentro), extremo 0,2024 (fuera de 0,20).
            double drift = 10 * Math.Tan(0.3 * Math.PI / 180);
            WallPairingResult r = WallPairing.Pair(
                new[] { L("a", 0, 0, 10, 0), L("b", 0, 0.15, 10, 0.15 + drift) },
                new WallPairOptions(0.075, 0.20, 0.012));

            Assert.Empty(r.Rects);
        }

        [Fact]
        public void EspesoresDiscretos_SoloEmparejanLosValoresDeLaLista()
        {
            var lines = new[] { L("a", 0, 0, 4, 0), L("d", 0, 0.40, 4, 0.40) };

            WallPairingResult sin = WallPairing.Pair(lines, new WallPairOptions(0.075, 0.62, 0.012, 0.5, new[] { 0.15 }, 0.01));
            WallPairingResult con = WallPairing.Pair(lines, new WallPairOptions(0.075, 0.62, 0.012, 0.5, new[] { 0.40 }, 0.01));

            Assert.Empty(sin.Rects);
            Assert.Single(con.Rects);
        }

        [Fact]
        public void CaraAntiparalelaPartida_NoPierdeElSolape()
        {
            // B va al revés (k < 0) y su tramo libre queda en dos piezas: el solape de A debe encontrarse en ambas.
            var lines = new[]
            {
                L("c", 1.5, 0.3, 2.5, 0.3),        // consume el centro de B primero (mismo solape que A-B, índice menor)
                L("a", 0, 0, 1, 0),
                L("b", 4, 0.15, 0, 0.15),
            };

            WallPairingResult r = WallPairing.Pair(lines, Opt());

            Assert.Equal(2, r.Rects.Count);
            Assert.Equal(0.30, r.Rects.Sum(q => Area(q.Vertices)), 9);
        }

        [Fact]
        public void HuecoPuenteadoEntreTramosConsumidos_NoDejaMuesca()
        {
            // Un muro perpendicular que llega por la cara CONTINUA (a, hacia abajo) no es T: no puentea el hueco de la cara b.
            var lines = new[]
            {
                L("a", 0, 0, 5, 0), L("b1", 0, 0.15, 2, 0.15), L("b2", 2.15, 0.15, 5, 0.15),
                L("p1", 2, 0, 2, -3), L("p2", 2.15, 0, 2.15, -3),
            };

            WallPairingResult r = WallPairing.Pair(lines, Opt());
            WallBuildResult b = WallFromLines.Build(lines, Opt());

            // La muesca no se puentea (3 caras): la línea a queda sobre la arista del muro perpendicular, cubierta, y es borrable
            // sin perder superficie; el hueco de la cara b sigue abierto.
            Assert.Equal(3, r.Rects.Count);
            Assert.True(b.Regions.Count > 1);
        }

        [Fact]
        public void A7_VentanaAlineadaConMuroQueLlegaPorLaCaraContinua_NoSePuentea()
        {
            var lines = new[]
            {
                L("a", 0, 0, 5, 0), L("b1", 0, 0.15, 2, 0.15), L("b2", 2.6, 0.15, 5, 0.15),
                L("p1", 2, 0, 2, -3), L("p2", 2.6, 0, 2.6, -3), L("j1", 2, 0, 2, 0.15), L("j2", 2.6, 0, 2.6, 0.15),
            };

            WallPairingResult r = WallPairing.Pair(lines, Opt());
            WallBuildResult b = WallFromLines.Build(lines, Opt());

            // Sin puente: 3 caras. Las jambas abarcan el espesor de la cara b y son tapas del vano.
            Assert.Equal(3, r.Rects.Count);
            Assert.Equal(new[] { "j1", "j2" }, b.Tapas);
        }

        [Fact]
        public void A12_AireConOrdenDeLineas_EmparejaLosMurosNoElAire()
        {
            // Solo espesor 0,15: los pares de muro (a-b, c-d) tienen un candidato menos que el aire (b-c). Da igual el orden.
            var lines = new[] { L("b", 0, 0.15, 4, 0.15), L("c", 0, 0.30, 4, 0.30), L("a", 0, 0, 4, 0), L("d", 0, 0.45, 4, 0.45) };

            WallPairingResult r = WallPairing.Pair(lines, new WallPairOptions(0.075, 0.62, 0.01, 0.5, new[] { 0.15 }, 0.01));

            Assert.Equal(2, r.Rects.Count);
            Assert.DoesNotContain(r.Rects, q => (q.IdA == "b" && q.IdB == "c") || (q.IdA == "c" && q.IdB == "b"));
        }

        [Fact]
        public void A3_TramosCortosDentroDelMuro_NoSonTapas()
        {
            // Trozos que no unen las dos caras (sello, mobiliario): no son tapas aunque estén dentro del muro.
            var lines = new[]
            {
                L("a", 0, 0, 5, 0), L("b", 0, 0.15, 5, 0.15),
                L("s1", 2, 0, 2.1, 0), L("s2", 3, 0.02, 3, 0.13), L("s3", 5, 0, 5, 0.1), L("s4", 4, 0, 4, 0.1),
            };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Empty(b.Tapas);
            Assert.Contains("a", b.Borrables);
            Assert.Contains("b", b.Borrables);
        }


        [Fact]
        public void JambasDeVano_NoSeEmparejanEntreSi()
        {
            // Vano de 0,6 con jambas de 0,15 (solape 0,15 < espesor 0,6): no son un muro y no tapan el vano.
            var lines = new[]
            {
                L("a1", 0, 0, 2, 0), L("a2", 2.6, 0, 5, 0), L("b1", 0, 0.15, 2, 0.15), L("b2", 2.6, 0.15, 5, 0.15),
                L("j1", 2, 0, 2, 0.15), L("j2", 2.6, 0, 2.6, 0.15),
            };

            WallPairingResult r = WallPairing.Pair(lines, Opt());
            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.DoesNotContain(r.Rects, q => q.IdA.StartsWith("j", StringComparison.Ordinal) || q.IdB.StartsWith("j", StringComparison.Ordinal));
            Assert.Equal(new[] { "j1", "j2" }, r.SinPareja);
            Assert.Equal(new[] { "j1", "j2" }, b.Tapas);
            Assert.Equal(2, b.Regions.Count);
        }

        [Fact]
        public void VanoEnUnaSolaCara_LaCaraEsParcial()
        {
            // Vano de 0,6 cortado solo en la cara B: el hueco de A no tiene muro enfrente y A no es borrable.
            var lines = new[] { L("a", 0, 0, 5, 0), L("b1", 0, 0.15, 2, 0.15), L("b2", 2.6, 0.15, 5, 0.15), L("j1", 2, 0, 2, 0.15), L("j2", 2.6, 0, 2.6, 0.15) };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Contains("a", b.Parciales);
            Assert.DoesNotContain("a", b.Borrables);
        }

        [Fact]
        public void Recinto_EsquinasAInglete_UnaRegionConAgujeroYCaraBorrables()
        {
            // Recinto 5 x 4: caras exteriores (hasta las esquinas) e interiores (a 0,15). Las esquinas a inglete se rellenan.
            var lines = new[]
            {
                L("o1", 0, 0, 5, 0), L("o2", 5, 0, 5, 4), L("o3", 5, 4, 0, 4), L("o4", 0, 4, 0, 0),
                L("i1", 0.15, 0.15, 4.85, 0.15), L("i2", 4.85, 0.15, 4.85, 3.85), L("i3", 4.85, 3.85, 0.15, 3.85), L("i4", 0.15, 3.85, 0.15, 0.15),
            };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Single(b.Regions);
            Assert.Single(b.Regions[0].Holes);
            Assert.Equal(20.0 - (4.7 * 3.7), b.Regions[0].Area, 6);
            Assert.Equal(8, b.Borrables.Count);
            Assert.Empty(b.Parciales);
        }

        [Fact]
        public void CaraPasanteEnAmbosExtremos_SinEspesores_AvisaYConEspesoresNoConfunde()
        {
            // Dos muros con caras pasantes en los extremos: el aire entre ellos (0,10) puntúa igual que un muro.
            var lines = new[]
            {
                L("a", 0, 0, 4, 0), L("b", 0, 0.15, 4, 0.15), L("c", 0, 0.25, 4, 0.25), L("d", 0, 0.40, 4, 0.40),
                L("w1", 0, -1, 0, 1.4), L("w2", 4, -1, 4, 1.4),
            };

            WallPairingResult sin = WallPairing.Pair(lines, Opt());
            WallPairingResult con = WallPairing.Pair(lines, new WallPairOptions(0.075, 0.62, 0.012, 0.5, new[] { 0.15, 0.40 }, 0.01));

            Assert.NotEmpty(sin.Advertencias);
            Assert.All(con.Rects, q => Assert.True(Math.Abs(q.Espesor - 0.15) < 1e-9 || Math.Abs(q.Espesor - 0.40) < 1e-9));
            Assert.Equal(2, con.Rects.Count);
            // Con espesores también se avisa de los pares sin tapa (las caras pasantes no cierran el aire).
            Assert.NotEmpty(con.Advertencias);
        }

        [Fact]
        public void AireSinCierreSinEspesores_AvisaEnLaPrevisualizacion()
        {
            var lines = new[] { L("a", 0, 0, 4, 0), L("b", 0, 0.15, 4, 0.15), L("c", 0, 0.25, 4, 0.25), L("d", 0, 0.40, 4, 0.40) };

            WallPairingResult r = WallPairing.Pair(lines, Opt());

            Assert.NotEmpty(r.Advertencias);
        }

        [Fact]
        public void TRealDeRevit_PerpendicularEmpiezaEnLaCaraVecina_SePuentea()
        {
            // p1/p2 empiezan EN la línea de la cara b (y = 0,15) y se alejan: la T real también es puente.
            var lines = new[]
            {
                L("a", 0, 0, 5, 0), L("b1", 0, 0.15, 2, 0.15), L("b2", 2.15, 0.15, 5, 0.15),
                L("p1", 2, 0.15, 2, 3), L("p2", 2.15, 0.15, 2.15, 3),
            };

            WallPairingResult r = WallPairing.Pair(lines, Opt());
            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Equal(4, r.Rects.Count);
            Assert.Single(b.Regions);
            Assert.Equal(1.1775, b.Regions[0].Area, 9);
            Assert.Equal(5, b.Borrables.Count);
        }

        [Fact]
        public void Vano_TramoLibreBajoMuroCruzado_NoEsBorrable()
        {
            // Tramo libre de a de 3 m; en medio lo cruza un muro de 0,15: el metro libre de a no está representado.
            var lines = new[] { L("a", 0, 0, 5, 0), L("b1", 0, 0.15, 1, 0.15), L("b2", 4, 0.15, 5, 0.15), L("v1", 2.425, -1, 2.425, 1), L("v2", 2.575, -1, 2.575, 1) };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Contains("a", b.Parciales);
            Assert.DoesNotContain("a", b.Borrables);
        }

        [Fact]
        public void EsquinaConPuertaCorta_NoSeTapaSegunElOrdenDeLasLineas()
        {
            // Puerta de 0,15 en la esquina: con b2 primero, b2 conserva su tramo libre y queda parcial.
            var lines = new[] { L("b2", 5, 0, 5, 3), L("b1", 4.85, 0.5, 4.85, 3), L("a1", 0, 0, 5, 0), L("a2", 0, 0.15, 4.85, 0.15) };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Contains("b2", b.Parciales);
        }

        [Fact]
        public void T_YVentanaEnLaMismaCara_SoloPuenteaLaT()
        {
            // Ventana (vano 0,6) en la misma cara que la T: la T se puentea; el vano queda parcial y sus jambas no son tapas.
            var lines = new[]
            {
                L("a", 0, 0, 5, 0), L("b1", 0, 0.15, 2, 0.15), L("b2", 2.15, 0.15, 3, 0.15), L("b3", 3.6, 0.15, 5, 0.15),
                L("p1", 2, 0.15, 2, 3), L("p2", 2.15, 0.15, 2.15, 3), L("j1", 3, 0, 3, 0.15), L("j2", 3.6, 0, 3.6, 0.15),
            };

            WallPairingResult r = WallPairing.Pair(lines, Opt());
            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Equal(5, r.Rects.Count);
            Assert.Contains("a", b.Parciales);
            Assert.Empty(b.Tapas);
        }

        [Fact]
        public void PuertaDe09_UnaSolaCara_NoSePuentea()
        {
            var lines = new[] { L("a", 0, 0, 5, 0), L("b1", 0, 0.15, 2, 0.15), L("b2", 2.9, 0.15, 5, 0.15) };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Contains("a", b.Parciales);
            Assert.Equal(2, b.Regions.Count);
        }

        [Fact]
        public void HuecoEntreTramosDeCarasOpuestas_NoSePuentea()
        {
            // Tramos vecinos en lados opuestos (y = 0,15 y y = -0,15): no están en la misma línea, no es puente.
            var lines = new[] { L("x", 0, 0, 5, 0), L("y", 0, 0.15, 2, 0.15), L("z", 2.15, -0.15, 5, -0.15) };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Contains("x", b.Parciales);
            Assert.Equal(2, b.Regions.Count);
        }

        [Fact]
        public void TapaParaleloAlMuro_NoEsTapa()
        {
            // Línea corta sobre la cara del muro, paralela a él: no es tapa.
            var lines = new[] { L("a", 0, 0, 5, 0), L("b", 0, 0.15, 5, 0.15), L("cara", 1, 0, 1.5, 0) };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Empty(b.Tapas);
        }

        // ---------------------------------------------------------------- Build (muros + borrables)

        [Fact]
        public void Build_HuecoLibreEnMedioDeLaCara_NoEsBorrable()
        {
            // La cara de A tiene un tramo libre [1; 1,8] de 0,8 (> espesor máximo: no es un puente) con el punto medio cubierto.
            var lines = new[] { L("a", 0, 0, 6, 0), L("b1", 0, 0.15, 1, 0.15), L("b2", 1.8, 0.15, 6, 0.15) };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Contains("a", b.Parciales);
            Assert.DoesNotContain("a", b.Borrables);
        }

        [Fact]
        public void Build_TapaDePeriferia_SeReportaComoTapa()
        {
            var lines = new[] { L("a", 0, 0, 5, 0), L("b", 0, 0.15, 5, 0.15), L("tapa", 0, 0, 0, 0.15) };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Equal(new[] { "tapa" }, b.Tapas);
            Assert.Contains("a", b.Borrables);
            Assert.Contains("b", b.Borrables);
        }

        [Fact]
        public void Build_EsquinaEnL_UnMuroDeSeisVertices()
        {
            // Muro A horizontal (caras y=-0.1 hasta x=5.1 e y=0.1 hasta x=4.9) y muro B vertical (caras x=4.9 y x=5.1).
            var lines = new[]
            {
                L("a1", 0, -0.1, 5.1, -0.1),
                L("a2", 0, 0.1, 4.9, 0.1),
                L("b1", 4.9, -0.1, 4.9, 3),
                L("b2", 5.1, -0.1, 5.1, 3),
            };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Equal(2, b.Pairing.Rects.Count);
            Assert.Single(b.Regions);
            Assert.Equal(6, b.Regions[0].Outer.Count);
            Assert.Empty(b.Regions[0].Holes);
            Assert.Equal(1.6, b.Regions[0].Area, 6);
            // a1 se usa en [0, 4.9] con a2; su tramo [4.9, 5.1] queda sobre el borde de B: cubierto, es borrable.
            Assert.Equal(4, b.Borrables.Count);
            Assert.Empty(b.Parciales);
        }

        [Fact]
        public void Build_TramoNoCubiertoQuedaComoParcial()
        {
            // Hueco de puerta en la cara interior: el tramo [2; 2,9] de la cara exterior no tiene muro enfrente.
            var lines = new[]
            {
                L("ext", 0, 0, 5, 0),
                L("i1", 0, 0.15, 2, 0.15),
                L("i2", 2.9, 0.15, 5, 0.15),
            };

            WallBuildResult b = WallFromLines.Build(lines, Opt());

            Assert.Equal(2, b.Regions.Count);
            Assert.Contains("ext", b.Parciales);
            Assert.DoesNotContain("ext", b.Borrables);
        }

        [Fact]
        public void Build_LimiteDeLineas_Lanza()
        {
            var lines = Enumerable.Range(0, WallFromLines.MaxLineas + 1)
                .Select(i => L("h" + i, 0, i * 10.0, 1, i * 10.0))
                .ToList();

            Assert.Throws<ArgumentException>(() => WallFromLines.Build(lines, Opt()));
        }
    }
}
