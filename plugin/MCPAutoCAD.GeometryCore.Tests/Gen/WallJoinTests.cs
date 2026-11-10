using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>protocol.md §5G.2: unión de dos contornos de muro rectos.</summary>
    public class WallJoinTests
    {
        private const int P = 6;

        private const string PuntoMsg = "union: los muros solo se tocan en un punto; no hay encuentro que unir";

        // Rectángulos reales de MUROS en casa6x12_limpio.dwg (ODA + ezdxf, sin redondear).
        private static readonly double[] R95795 =
        {
            3311.853226773471, 3983.194007335784, 3313.163226773472, 3983.194007335784,
            3313.163226773472, 3983.074007335784, 3311.853226773471, 3983.074007335784,
        };

        private static readonly double[] R95821 =
        {
            3311.853226773471, 3983.194007335785, 3311.703226773471, 3983.194007335785,
            3311.703226773471, 3985.594007335784, 3311.853226773471, 3985.594007335784,
        };

        private static readonly double[] R958E9 =
        {
            3314.663226773471, 3988.594007335785, 3314.513226773471, 3988.594007335785,
            3314.513226773471, 3990.994007335785, 3314.663226773471, 3990.994007335785,
        };

        private static readonly double[] R958ED =
        {
            3313.263226773471, 3991.114007335784, 3314.663226773471, 3991.114007335784,
            3314.663226773471, 3990.994007335785, 3313.263226773471, 3990.994007335785,
        };

        private static readonly double[] R958EB =
        {
            3313.413226773471, 3988.594007335786, 3313.263226773471, 3988.594007335786,
            3313.263226773471, 3990.994007335786, 3313.413226773471, 3990.994007335786,
        };

        private static readonly double[] R95C47 =
        {
            3282.697199753939, 3990.674007335785, 3282.54719975394, 3990.674007335785,
            3282.54719975394, 3990.994007335786, 3282.697199753939, 3990.994007335786,
        };

        private static readonly double[] R95C4E =
        {
            3282.54719975394, 3991.114007335786, 3287.917199753939, 3991.114007335784,
            3287.917199753939, 3990.994007335785, 3282.54719975394, 3990.994007335787,
        };

        private static List<(double x, double y)> Pts(params double[] xy)
        {
            var r = new List<(double x, double y)>();
            for (int i = 0; i < xy.Length; i += 2)
            {
                r.Add((xy[i], xy[i + 1]));
            }

            return r;
        }

        private static List<(double x, double y)> Rect(double x0, double y0, double x1, double y1) =>
            Pts(x0, y0, x1, y0, x1, y1, x0, y1);

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

        private static WallGeometryException Throws(Action f) => Assert.Throws<WallGeometryException>(() => f());

        private static void AssertGeom(string detalle, Action f)
        {
            WallGeometryException e = Throws(f);
            Assert.Equal(detalle, e.Message);
            Assert.False(e.EsParametro);
        }

        private static void AssertValido(WallJoin.JoinResult r)
        {
            Assert.Empty(PolylineValidator.Validate(r.Contorno, true, null, 1e-6));
            Assert.True(PolygonMetrics.SignedArea(r.Contorno) > 0, "el contorno unido debe ser antihorario");
            Assert.Equal(r.Contorno.Count, r.NVertices);
        }

        [Fact]
        public void PlanoReal_95795_95821_SoloCompartenUnaEsquina()
        {
            // El reparto describía este par como T con 8 vértices; las coordenadas reales del plano dicen otra cosa:
            // 95795 ocupa x ≥ 3311,853 e y ≤ 3983,194 y 95821 x ≤ 3311,853 e y ≥ 3983,194: comparten solo ese vértice.
            AssertGeom(PuntoMsg, () => WallJoin.Union(Pts(R95795), Pts(R95821)));
        }

        [Fact]
        public void PlanoReal_958E9_958ED_LConCaraComun()
        {
            WallJoin.JoinResult r = WallJoin.Union(Pts(R958E9), Pts(R958ED));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(6, r.NVertices);
            Assert.Equal(0, r.AreaSolape, P);
            Assert.Equal((0.15 * 2.4) + (1.4 * 0.12), r.AreaUnion, P);
            AssertValido(r);
        }

        [Fact]
        public void PlanoReal_958EB_958ED_L()
        {
            WallJoin.JoinResult r = WallJoin.Union(Pts(R958EB), Pts(R958ED));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            AssertValido(r);
        }

        [Fact]
        public void PlanoReal_95C47_95C4E_LPeseAlRedondeoDelPlano()
        {
            WallJoin.JoinResult r = WallJoin.Union(Pts(R95C47), Pts(R95C4E));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(6, r.NVertices);
            AssertValido(r);
        }

        [Fact]
        public void T_ConLasMedidasDelPlano_TapaApoyadaEnLaCara()
        {
            // Muro de 2,4 × 0,15 cuya tapa se apoya en mitad de la cara de un muro de 1,31 × 0,12.
            List<(double x, double y)> a = Rect(3311.853, 3983.074, 3313.163, 3983.194);
            List<(double x, double y)> b = Rect(3312.4, 3983.194, 3312.55, 3985.594);
            WallJoin.JoinResult r = WallJoin.Union(a, b);
            Assert.Equal(WallJoin.TipoT, r.TipoDetectado);
            Assert.Equal(8, r.NVertices);
            Assert.Equal(0, r.AreaSolape, P);
            Assert.Equal((1.31 * 0.12) + (0.15 * 2.4), r.AreaUnion, P);
            AssertValido(r);
        }

        [Fact]
        public void T_ConLaTapaEnA_ResultadoIgualQueConLaTapaEnB()
        {
            List<(double x, double y)> cara = Rect(0, 0, 3, 0.15);
            List<(double x, double y)> tapa = Rect(1, 0.15, 1.15, 2);
            WallJoin.JoinResult ab = WallJoin.Union(cara, tapa);
            WallJoin.JoinResult ba = WallJoin.Union(tapa, cara);
            Assert.Equal(8, ab.NVertices);
            Assert.Equal(8, ba.NVertices);
            Assert.Equal(ab.AreaUnion, ba.AreaUnion, P);
            AssertValido(ba);
        }

        [Fact]
        public void T_ConSolape_ElMuroEntraEnElOtro()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(1, 0.05, 1.15, 2));
            Assert.Equal(WallJoin.TipoT, r.TipoDetectado);
            Assert.Equal(0.15 * 0.1, r.AreaSolape, P);
            Assert.Equal(0.45 + (0.15 * 1.95) - (0.15 * 0.1), r.AreaUnion, P);
            AssertValido(r);
        }

        [Fact]
        public void L_ConSolapeDeEsquina_6Vertices()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(2.85, 0, 3, 2));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(6, r.NVertices);
            Assert.Equal(0.15 * 0.15, r.AreaSolape, P);
            AssertValido(r);
        }

        [Fact]
        public void L_ALaInversa_MismoTipo()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(2.85, 0, 3, 2), Rect(0, 0, 3, 0.15));
            Assert.Equal(6, r.NVertices);
            AssertValido(r);
        }

        [Fact]
        public void L_ATope_TapaEnLaCaraYRasante()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(0, 0.15, 0.15, 2));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(0, r.AreaSolape, P);
            AssertValido(r);
        }

        [Fact]
        public void Cruce_12Vertices()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 1, 3, 1.15), Rect(1.5, 0, 1.65, 3));
            Assert.Equal(WallJoin.TipoCruce, r.TipoDetectado);
            Assert.Equal(12, r.NVertices);
            Assert.Equal(0.15 * 0.15, r.AreaSolape, P);
            AssertValido(r);
        }

        [Fact]
        public void Colineales_Solapados_4Vertices()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(2, 0, 5, 0.15));
            Assert.Equal(WallJoin.TipoColineal, r.TipoDetectado);
            Assert.Equal(4, r.NVertices);
            Assert.Equal(0.15, r.AreaSolape, P);
            Assert.Equal(0.75, r.AreaUnion, P);
            AssertValido(r);
        }

        [Fact]
        public void Colineales_ATope_4Vertices()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(3, 0, 5, 0.15));
            Assert.Equal(WallJoin.TipoColineal, r.TipoDetectado);
            Assert.Equal(0, r.AreaSolape, P);
            AssertValido(r);
        }

        [Fact]
        public void EspesorDistinto_ATope_NoEsColineal()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(3, 0, 5, 0.12));
            Assert.Equal(6, r.NVertices);
            AssertValido(r);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(30)]
        [InlineData(45)]
        [InlineData(137)]
        public void T_ACualquierAngulo(double grados)
        {
            WallJoin.JoinResult r = WallJoin.Union(
                Girado(Rect(0, 0, 3, 0.15), grados, 3300, 3900),
                Girado(Rect(1, 0.15, 1.15, 2), grados, 3300, 3900));
            Assert.Equal(WallJoin.TipoT, r.TipoDetectado);
            Assert.Equal(0.45 + (0.15 * 1.85), r.AreaUnion, P);
            AssertValido(r);
        }

        [Theory]
        [InlineData(30)]
        [InlineData(75)]
        public void Cruce_ACualquierAngulo(double grados)
        {
            WallJoin.JoinResult r = WallJoin.Union(
                Girado(Rect(0, 1, 3, 1.15), grados, 10, -4),
                Girado(Rect(1.5, 0, 1.65, 3), grados, 10, -4));
            Assert.Equal(WallJoin.TipoCruce, r.TipoDetectado);
            AssertValido(r);
        }

        [Fact]
        public void Paralelogramos_EncuentroOblicuoA30Grados_T()
        {
            // B sube a 60° desde la cara de A: sus tapas son paralelas a esa cara.
            double dx = 2 / Math.Tan(Math.PI / 3);
            List<(double x, double y)> a = Rect(0, 0, 3, 0.15);
            List<(double x, double y)> b = Pts(1, 0.15, 1.2, 0.15, 1.2 + dx, 2.15, 1 + dx, 2.15);
            WallJoin.JoinResult r = WallJoin.Union(a, b);
            Assert.Equal(WallJoin.TipoT, r.TipoDetectado);
            Assert.Equal(0.45 + (0.2 * 2), r.AreaUnion, P);
            AssertValido(r);
        }

        [Fact]
        public void Paralelogramos_LRasante()
        {
            // La cara exterior de B prolonga la tapa inclinada de A: queda una L limpia.
            List<(double x, double y)> a = Pts(0, 0, 3, 0, 3.1, 0.15, 0.1, 0.15);
            List<(double x, double y)> b = Pts(0.1, 0.15, 0.3, 0.15, 1.3, 1.65, 1.1, 1.65);
            WallJoin.JoinResult r = WallJoin.Union(a, b);
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(6, r.NVertices);
            AssertValido(r);
        }

        [Fact]
        public void Paralelogramos_LOblicuaConCodoExterior_7Vertices_EsL()
        {
            // La tapa de B se apoya en la cara de A enrasada con su esquina; el codo de la cara exterior deja 7 vértices.
            WallJoin.JoinResult r = WallJoin.Union(Pts(0, 0, 3, 0, 3.1, 0.15, 0.1, 0.15), Pts(0.1, 0.15, 0.3, 0.15, 0.4, 2, 0.2, 2));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(7, r.NVertices);
            AssertValido(r);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(17)]
        [InlineData(62)]
        [InlineData(200)]
        public void LOblicuaConCodoExterior_ACualquierAngulo(double grados)
        {
            WallJoin.JoinResult r = WallJoin.Union(
                Girado(Pts(0, 0, 3, 0, 3.1, 0.15, 0.1, 0.15), grados, 3300, 3900),
                Girado(Pts(0.1, 0.15, 0.3, 0.15, 0.4, 2, 0.2, 2), grados, 3300, 3900));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(7, r.NVertices);
            AssertValido(r);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(33)]
        [InlineData(115)]
        public void LDesplazadaConSaliente_8Vertices_EsL(double grados)
        {
            // B sobresale 0,05 por debajo de A: menos que su ancho, así que es un saliente y no una pata de T.
            WallJoin.JoinResult r = WallJoin.Union(
                Girado(Rect(0, 0, 3, 0.15), grados, 3300, 3900),
                Girado(Rect(2.9, -0.05, 3.05, 2), grados, 3300, 3900));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(8, r.NVertices);
            AssertValido(r);
        }

        [Fact]
        public void LDesplazadaConSaliente_ALaInversa_EsL()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(2.9, -0.05, 3.05, 2), Rect(0, 0, 3, 0.15));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            AssertValido(r);
        }

        [Fact]
        public void LConEsquinasCruzadas_8Vertices_EsL()
        {
            // Cada muro mete una sola esquina en el otro: ninguno termina en una cara ni la cruza entera.
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(2.9, 0.1, 3.1, 2));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(8, r.NVertices);
            AssertValido(r);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(30)]
        [InlineData(90)]
        [InlineData(137)]
        public void T_ConPataApenasMayorQueElAncho_SigueSiendoT(double grados)
        {
            // Pata de 0,16 con un muro pasante de 0,15.
            WallJoin.JoinResult r = WallJoin.Union(
                Girado(Rect(0, 0, 3, 0.15), grados, 3300, 3900),
                Girado(Rect(2.69, 0.1, 2.84, 2), grados, 3300, 3900));
            Assert.Equal(WallJoin.TipoT, r.TipoDetectado);
            AssertValido(r);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(30)]
        [InlineData(90)]
        [InlineData(137)]
        public void PataIgualAlAncho_EsSaliente_LACualquierAngulo(double grados)
        {
            // Sin tolerancia, el redondeo de la rotación decidía el tipo.
            WallJoin.JoinResult r = WallJoin.Union(
                Girado(Rect(0, 0, 3, 0.15), grados, 3300, 3900),
                Girado(Rect(2.7, 0.1, 2.85, 2), grados, 3300, 3900));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            AssertValido(r);
        }

        [Fact]
        public void T_ElMuroAtraviesaYAcabaEnrasadoConLaCaraDelFondo()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(1, 0, 1.15, 2));
            Assert.Equal(WallJoin.TipoT, r.TipoDetectado);
            Assert.Equal(8, r.NVertices);
            AssertValido(r);
        }

        [Fact]
        public void T_OblicuaConLaEsquinaDentro_9Vertices()
        {
            // La tapa del muro inclinado no es paralela a la cara: solo su esquina entra en el otro, y quedan 9.
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Pts(1.5, 0.1, 1.7, 0.3, 0.7, 1.3, 0.5, 1.1));
            Assert.Equal(WallJoin.TipoT, r.TipoDetectado);
            Assert.Equal(9, r.NVertices);
            AssertValido(r);
        }

        [Theory]
        [InlineData(20)]
        [InlineData(45)]
        [InlineData(70)]
        public void Cruce_Oblicuo_MurosNoPerpendiculares(double grados)
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(-3, 0, 3, 0.15), Girado(Rect(-2, 0, 2, 0.15), grados));
            Assert.Equal(WallJoin.TipoCruce, r.TipoDetectado);
            Assert.Equal(12, r.NVertices);
            AssertValido(r);
        }

        [Fact]
        public void Cruce_ConSalienteCorto_SigueSiendoCruce()
        {
            // B parte a A en dos y A parte a B en dos: lo que sobresale no cambia la topología.
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(1, -0.05, 1.15, 2));
            Assert.Equal(WallJoin.TipoCruce, r.TipoDetectado);
            AssertValido(r);
        }

        [Fact]
        public void EspesorDistinto_EnProlongacion_EsL()
        {
            // No es colineal (el contorno no es un cuadrilátero) y ninguno sigue a los dos lados del otro.
            Assert.Equal(WallJoin.TipoL, WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(3, 0, 5, 0.12)).TipoDetectado);
            Assert.Equal(WallJoin.TipoL, WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(3, 0.01, 5, 0.13)).TipoDetectado);
        }

        [Fact]
        public void TipoPedido_T_EnUnaLConSaliente_SeRechaza()
        {
            AssertGeom(
                "tipo: los muros forman un encuentro en L, no en T",
                () => WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(2.9, -0.05, 3.05, 2), "T"));
        }

        [Fact]
        public void SentidoHorario_SeNormalizaACcw()
        {
            List<(double x, double y)> a = Rect(0, 0, 3, 0.15);
            a.Reverse();
            List<(double x, double y)> b = Rect(1, 0.15, 1.15, 2);
            b.Reverse();
            WallJoin.JoinResult r = WallJoin.Union(a, b);
            Assert.Equal(8, r.NVertices);
            AssertValido(r);
        }

        [Fact]
        public void Areas_DeCadaMuro()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(2.85, 0, 3, 2));
            Assert.Equal(0.45, r.AreaA, P);
            Assert.Equal(0.3, r.AreaB, P);
        }

        [Fact]
        public void ContactoConRedondeoDentroDeTolerancia_Encadena()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(1, 0.15 + 4e-7, 1.15, 2));
            Assert.Equal(8, r.NVertices);
            AssertValido(r);
        }

        [Fact]
        public void TipoPedidoCoincide()
        {
            Assert.Equal(WallJoin.TipoL, WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(2.85, 0, 3, 2), "L").TipoDetectado);
            Assert.Equal(WallJoin.TipoT, WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(1, 0.15, 1.15, 2), "T").TipoDetectado);
            Assert.Equal(WallJoin.TipoCruce, WallJoin.Union(Rect(0, 1, 3, 1.15), Rect(1.5, 0, 1.65, 3), "cruce").TipoDetectado);
        }

        [Fact]
        public void TipoNuloEsAuto()
        {
            Assert.Equal(WallJoin.TipoL, WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(2.85, 0, 3, 2), null!).TipoDetectado);
        }

        [Fact]
        public void TipoPedidoDistinto()
        {
            AssertGeom(
                "tipo: los muros forman un encuentro en T, no en L",
                () => WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(1, 0.15, 1.15, 2), "L"));
        }

        [Fact]
        public void TipoPedidoDistinto_Colineal()
        {
            AssertGeom(
                "tipo: los muros forman un encuentro en colineal, no en cruce",
                () => WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(2, 0, 5, 0.15), "cruce"));
        }

        [Theory]
        [InlineData("colineal")]
        [InlineData("l")]
        [InlineData("")]
        [InlineData("X")]
        public void TipoInvalido_EsParametro(string tipo)
        {
            WallGeometryException e = Throws(() => WallJoin.Union(Rect(0, 0, 1, 1), Rect(1, 0, 2, 1), tipo));
            Assert.Equal("tipo: debe ser L, T, cruce o auto", e.Message);
            Assert.True(e.EsParametro);
        }

        [Fact]
        public void NoSeTocan()
        {
            AssertGeom("union: los muros no se tocan ni se solapan", () => WallJoin.Union(Rect(0, 0, 1, 1), Rect(2, 0, 3, 1)));
        }

        [Fact]
        public void CasiSeTocan_FueraDeTolerancia()
        {
            AssertGeom(
                "union: los muros no se tocan ni se solapan",
                () => WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(1, 0.15 + 1e-5, 1.15, 2)));
        }

        [Fact]
        public void SoloUnVerticeComun()
        {
            AssertGeom(PuntoMsg, () => WallJoin.Union(Rect(0, 0, 1, 1), Rect(1, 1, 2, 2)));
        }

        [Fact]
        public void VerticeApoyadoEnLaCara_UnPunto()
        {
            AssertGeom(PuntoMsg, () => WallJoin.Union(Rect(0, 0, 3, 1), Pts(1.5, 1, 2, 1.5, 1.5, 2, 1, 1.5)));
        }

        [Fact]
        public void Contenido_BDentroDeA()
        {
            AssertGeom("union: un muro está contenido en el otro", () => WallJoin.Union(Rect(0, 0, 3, 1), Rect(1, 0.25, 2, 0.75)));
        }

        [Fact]
        public void Contenido_ADentroDeB()
        {
            AssertGeom("union: un muro está contenido en el otro", () => WallJoin.Union(Rect(1, 0.25, 2, 0.75), Rect(0, 0, 3, 1)));
        }

        [Fact]
        public void Contenido_ConCaraComun()
        {
            AssertGeom("union: un muro está contenido en el otro", () => WallJoin.Union(Rect(0, 0, 3, 1), Rect(0, 0, 2, 1)));
        }

        [Fact]
        public void Contenido_MurosIguales()
        {
            AssertGeom("union: un muro está contenido en el otro", () => WallJoin.Union(Rect(0, 0, 3, 1), Rect(0, 0, 3, 1)));
        }

        [Fact]
        public void RomboEnLaEsquina_7Vertices_EsL()
        {
            // Rombo centrado en la esquina del cuadrado: se encuentran por sus extremos, a 45°.
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 2, 2), Pts(2, 1, 3, 2, 2, 3, 1, 2));
            Assert.Equal(WallJoin.TipoL, r.TipoDetectado);
            Assert.Equal(7, r.NVertices);
            AssertValido(r);
        }

        [Fact]
        public void EncuentroNoReconocido_CuadradoYRomboEnEstrella()
        {
            // Las cuatro esquinas de cada uno asoman fuera del otro: más de dos tramos fuera no es un encuentro de muros.
            AssertGeom(
                "union: encuentro no reconocido (16 vértices)",
                () => WallJoin.Union(Rect(0, 0, 2, 2), Pts(2.3, 1, 1, 2.3, -0.3, 1, 1, -0.3)));
        }

        [Fact]
        public void OverlapArea_RectangulosConValorAnalitico()
        {
            Assert.Equal(0.15 * 0.1, WallJoin.OverlapArea(Rect(0, 0, 3, 0.15), Rect(1, 0.05, 1.15, 2)), 12);
            Assert.Equal(0, WallJoin.OverlapArea(Rect(0, 0, 3, 0.15), Rect(1, 0.15, 1.15, 2)), 12);
            Assert.Equal(0, WallJoin.OverlapArea(Rect(0, 0, 1, 1), Rect(5, 5, 6, 6)), 12);
        }

        [Fact]
        public void OverlapArea_NoDependeDelSentidoNiDeLasCoordenadasGrandes()
        {
            List<(double x, double y)> a = Rect(3300, 3900, 3303, 3900.15);
            a.Reverse();
            Assert.Equal(0.15 * 0.1, WallJoin.OverlapArea(a, Rect(3301, 3900.05, 3301.15, 3902)), 9);
            Assert.Equal(0.15 * 0.1, WallJoin.OverlapArea(Rect(3301, 3900.05, 3301.15, 3902), a), 9);
        }

        [Fact]
        public void OverlapArea_Oblicua_DosPorDosGirado45()
        {
            // Cuadrado de lado 2 centrado y su copia girada 45°: octógono regular de área 8(√2 − 1).
            List<(double x, double y)> a = Rect(-1, -1, 1, 1);
            Assert.Equal(8 * (Math.Sqrt(2) - 1), WallJoin.OverlapArea(a, Girado(a, 45)), 9);
        }

        [Fact]
        public void AreaSolape_NoSeDerivaDelContorno_CoincideConElValorAnalitico()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(1, 0.05, 1.15, 2));
            Assert.Equal(0.15 * 0.1, r.AreaSolape, 12);
            Assert.Equal(r.AreaA + r.AreaB - r.AreaSolape, r.AreaUnion, 9);
        }

        [Fact]
        public void CheckAreaBalance_CuadraDentroDeTolerancia()
        {
            WallJoin.CheckAreaBalance(0.45, 0.3, 0.0225, 0.7275 + 5e-7, 0);
        }

        [Fact]
        public void CheckAreaBalance_NoCuadra_Rechaza()
        {
            AssertGeom(
                "union: el contorno unido no es un polígono simple",
                () => WallJoin.CheckAreaBalance(0.45, 0.3, 0.0225, 0.72, 0));
            AssertGeom(
                "union: el contorno unido no es un polígono simple",
                () => WallJoin.CheckAreaBalance(0.45, 0.3, 0.0225, 0.7275 + 2e-6, 0));
        }

        [Fact]
        public void CheckAreaBalance_ElMargenDeFusionDeNodosAdmiteLaDiferencia()
        {
            WallJoin.CheckAreaBalance(0.45, 0.3, 0.0225, 0.7275 + 2e-6, 1e-5);
        }

        [Fact]
        public void ContactoConHolguraDeRedondeoEnUnaCaraLarga_NoSeRechazaPorAreas()
        {
            // Holgura 9e-7 (dentro de tol) a lo largo de 3 m: la fusión de nodos mueve 2,7e-6 de área, más que el 1e-6 relativo.
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(0, 0.15 + 9e-7, 3, 0.3));
            Assert.Equal(WallJoin.TipoColineal, r.TipoDetectado);
            AssertValido(r);
        }

        [Fact]
        public void MuroA_NoConvexo()
        {
            AssertGeom(
                "muro_a: el contorno debe ser un cuadrilátero convexo (muro recto)",
                () => WallJoin.Union(Pts(0, 0, 3, 0, 3, 0.15, 0.15, 0.15, 0.15, 3, 0, 3), Rect(0, 0, 1, 1)));
        }

        [Fact]
        public void MuroB_NoConvexo()
        {
            AssertGeom(
                "muro_b: el contorno debe ser un cuadrilátero convexo (muro recto)",
                () => WallJoin.Union(Rect(0, 0, 1, 1), Pts(0, 0, 2, 0, 0.5, 0.5, 0, 2)));
        }

        [Fact]
        public void MuroA_Nulo()
        {
            AssertGeom(
                "muro_a: el contorno debe ser un cuadrilátero convexo (muro recto)",
                () => WallJoin.Union(null!, Rect(0, 0, 1, 1)));
        }

        [Fact]
        public void ResultadoDeUnaUnion_NoEsEntradaValida()
        {
            WallJoin.JoinResult r = WallJoin.Union(Rect(0, 0, 3, 0.15), Rect(2.85, 0, 3, 2));
            AssertGeom(
                "muro_a: el contorno debe ser un cuadrilátero convexo (muro recto)",
                () => WallJoin.Union(r.Contorno, Rect(0, 0, 1, 1)));
        }

        [Fact]
        public void TipoInvalido_SeCompruebaAntesQueLaGeometria()
        {
            WallGeometryException e = Throws(() => WallJoin.Union(null!, null!, "Y"));
            Assert.True(e.EsParametro);
        }

        /// <summary>Solape de dos convexos CCW por recorte de Sutherland-Hodgman: comprobación independiente de Union.</summary>
        private static double AreaSolapeConvexos(List<(double x, double y)> a, List<(double x, double y)> b)
        {
            List<(double x, double y)> salida = a;
            for (int i = 0; i < b.Count && salida.Count > 0; i++)
            {
                (double x, double y) p = b[i];
                (double x, double y) q = b[(i + 1) % b.Count];
                double Lado((double x, double y) c) => ((q.x - p.x) * (c.y - p.y)) - ((q.y - p.y) * (c.x - p.x));
                var entrada = salida;
                salida = new List<(double x, double y)>();
                for (int k = 0; k < entrada.Count; k++)
                {
                    (double x, double y) s = entrada[k];
                    (double x, double y) e = entrada[(k + 1) % entrada.Count];
                    double ls = Lado(s);
                    double le = Lado(e);
                    if (ls >= 0)
                    {
                        salida.Add(s);
                    }

                    if ((ls >= 0) != (le >= 0))
                    {
                        double t = ls / (ls - le);
                        salida.Add((s.x + (t * (e.x - s.x)), s.y + (t * (e.y - s.y))));
                    }
                }
            }

            return salida.Count < 3 ? 0 : Math.Abs(PolygonMetrics.SignedArea(salida));
        }

        /// <summary>
        /// Rectángulos en una rejilla de 0,05 (aristas y vértices coincidentes a menudo) a 0° y 30°: toda unión que se
        /// construye tiene el área que da el recorte independiente, y nunca se llega a "no es un polígono simple" ni a un
        /// encuentro sin tipo.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(30)]
        public void Fuzz_AreaDeLaUnion_CoincideConElRecorteIndependiente(double grados)
        {
            var azar = new Random(20261031);
            int construidas = 0;
            for (int n = 0; n < 4000; n++)
            {
                double Paso(int max) => azar.Next(max) * 0.05;
                double ax = Paso(20);
                double ay = Paso(20);
                List<(double x, double y)> a = Girado(Rect(ax, ay, ax + 0.05 + Paso(30), ay + 0.05 + Paso(30)), grados, 3300, 3900);
                double bx = Paso(20);
                double by = Paso(20);
                List<(double x, double y)> b = Girado(Rect(bx, by, bx + 0.05 + Paso(30), by + 0.05 + Paso(30)), grados, 3300, 3900);
                try
                {
                    WallJoin.JoinResult r = WallJoin.Union(a, b);
                    construidas++;
                    AssertValido(r);
                    Assert.Equal(AreaSolapeConvexos(a, b), r.AreaSolape, 5);
                    Assert.Equal(r.AreaA + r.AreaB - r.AreaSolape, r.AreaUnion, 5);
                }
                catch (WallGeometryException e)
                {
                    Assert.DoesNotContain("polígono simple", e.Message);
                    Assert.DoesNotContain("no reconocido", e.Message);
                }
            }

            Assert.True(construidas > 500, "construidas: " + construidas);
        }

        [Fact]
        public void IsConvexQuad_Rectangulo()
        {
            Assert.True(WallJoin.IsConvexQuad(Rect(0, 0, 3, 0.15)));
        }

        [Fact]
        public void IsConvexQuad_HorarioTambien()
        {
            List<(double x, double y)> r = Rect(0, 0, 3, 0.15);
            r.Reverse();
            Assert.True(WallJoin.IsConvexQuad(r));
        }

        [Fact]
        public void IsConvexQuad_Paralelogramo()
        {
            Assert.True(WallJoin.IsConvexQuad(Pts(0, 0, 3, 0, 3.2, 0.15, 0.2, 0.15)));
        }

        [Fact]
        public void IsConvexQuad_ConVerticeColinealYDuplicado()
        {
            Assert.True(WallJoin.IsConvexQuad(Pts(0, 0, 1.5, 0, 1.5, 0, 3, 0, 3, 0.15, 0, 0.15)));
        }

        [Fact]
        public void IsConvexQuad_Concavo()
        {
            Assert.False(WallJoin.IsConvexQuad(Pts(0, 0, 2, 0, 0.5, 0.5, 0, 2)));
        }

        [Fact]
        public void IsConvexQuad_Lazo()
        {
            Assert.False(WallJoin.IsConvexQuad(Pts(0, 0, 3, 1, 3, 0, 0, 1)));
        }

        [Fact]
        public void IsConvexQuad_Triangulo()
        {
            Assert.False(WallJoin.IsConvexQuad(Pts(0, 0, 3, 0, 0, 1)));
        }

        [Fact]
        public void IsConvexQuad_Nulo()
        {
            Assert.False(WallJoin.IsConvexQuad(null!));
        }

        [Fact]
        public void IsConvexQuad_NoFinito()
        {
            Assert.False(WallJoin.IsConvexQuad(Pts(0, 0, double.NaN, 0, 3, 1, 0, 1)));
        }

        [Fact]
        public void SimplifyLoop_QuitaColinealesYDuplicados()
        {
            List<(double x, double y)> r = WallJoin.SimplifyLoop(Pts(0, 0, 1, 0, 1, 0, 2, 0, 2, 1, 1, 1, 0, 1));
            Assert.Equal(4, r.Count);
            Assert.DoesNotContain(r, p => p.x == 1);
        }

        [Fact]
        public void SimplifyLoop_ColinealEnElCierre()
        {
            List<(double x, double y)> r = WallJoin.SimplifyLoop(Pts(1, 0, 2, 0, 2, 1, 0, 1, 0, 0));
            Assert.Equal(4, r.Count);
            Assert.DoesNotContain(r, p => p.x == 1 && p.y == 0);
        }

        [Fact]
        public void SimplifyLoop_UltimoIgualAlPrimero()
        {
            Assert.Equal(4, WallJoin.SimplifyLoop(Pts(0, 0, 2, 0, 2, 1, 0, 1, 0, 0)).Count);
        }

        [Fact]
        public void SimplifyLoop_ContornoSinCambios()
        {
            List<(double x, double y)> v = Rect(0, 0, 2, 1);
            Assert.Equal(v, WallJoin.SimplifyLoop(v));
        }

        [Fact]
        public void SimplifyLoop_Nulo()
        {
            Assert.Throws<ArgumentNullException>(() => WallJoin.SimplifyLoop(null!));
        }
    }
}
