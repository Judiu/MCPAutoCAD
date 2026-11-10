using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>protocol.md §5G.1: colocación y orientación del bloque en el hueco.</summary>
    public class OpeningPlacementTests
    {
        private const int P = 9;

        private static readonly VanoBlockSpec Puerta =
            new VanoBlockSpec("Puerta Dinamica LM", "puerta", "x", "+y", 0.9, 0.13, null, "puertas");

        private static readonly VanoBlockSpec Ventana =
            new VanoBlockSpec("VENTANA01", "ventana", "y", "-x", 0.3, 0.12, null, "ventanas");

        /// <summary>Muro horizontal 3 × 0,15: hueco 0,8–1,6 (Direccion +x; izquierda = +y).</summary>
        private static WallOpening.OpeningResult HuecoH() =>
            WallOpening.Cut(new List<(double x, double y)> { (0, 0), (3, 0), (3, 0.15), (0, 0.15) }, (1.2, 0.075), 0.8);

        /// <summary>Muro vertical 0,15 × 3: hueco y 0,8–1,6 (Direccion +y; izquierda = −x).</summary>
        private static WallOpening.OpeningResult HuecoV() =>
            WallOpening.Cut(new List<(double x, double y)> { (0, 0), (0.15, 0), (0.15, 3), (0, 3) }, (0.075, 1.2), 0.8);

        private static VanoBlockSpec SpecDe(string tipo) => tipo == "puerta" ? Puerta : Ventana;

        /// <summary>Punto local del bloque → mundo: S, luego R, luego traslación (como una BlockReference).</summary>
        private static (double x, double y) Mundo(OpeningPlacement.Placement c, (double x, double y) local)
        {
            double a = c.RotacionGrados * Math.PI / 180.0;
            double lx = local.x * c.Escala[0];
            double ly = local.y * c.Escala[1];
            return (
                c.Posicion.x + (lx * Math.Cos(a)) - (ly * Math.Sin(a)),
                c.Posicion.y + (lx * Math.Sin(a)) + (ly * Math.Cos(a)));
        }

        private static void AssertPt((double x, double y) esperado, (double x, double y) real)
        {
            Assert.Equal(esperado.x, real.x, P);
            Assert.Equal(esperado.y, real.y, P);
        }

        [Theory]
        [InlineData(1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1)]
        [InlineData(1, 0, 0, 1, 0, 1, -1, 0, 90, 1, 1)]
        [InlineData(1, 0, 0, 1, -1, 0, 0, -1, 180, 1, 1)]
        [InlineData(1, 0, 0, 1, 0, -1, 1, 0, 270, 1, 1)]
        [InlineData(1, 0, 0, 1, 1, 0, 0, -1, 0, 1, -1)]
        [InlineData(1, 0, 0, 1, -1, 0, 0, 1, 180, 1, -1)]
        [InlineData(0, 1, -1, 0, 1, 0, 0, 1, 270, 1, 1)]
        [InlineData(0, 1, -1, 0, 0, 1, -1, 0, 0, 1, 1)]
        public void Solve_RotacionYSignos(
            double ux, double uy, double vx, double vy, double lx, double ly, double dx, double dy, double rot, double sx, double sy)
        {
            (double r, double a, double b) = OpeningPlacement.Solve((ux, uy), (vx, vy), (lx, ly), (dx, dy));
            Assert.Equal(rot, r, P);
            Assert.Equal(sx, a);
            Assert.Equal(sy, b);
        }

        [Fact]
        public void Solve_PrefiereSinEspejo()
        {
            (_, double sx, double sy) = OpeningPlacement.Solve((1, 0), (0, 1), (0.6, 0.8), (-0.8, 0.6));
            Assert.Equal(1, sx);
            Assert.Equal(1, sy);
        }

        [Fact]
        public void Solve_AnguloCualquiera()
        {
            double c = Math.Cos(Math.PI / 6);
            double s = Math.Sin(Math.PI / 6);
            (double rot, _, _) = OpeningPlacement.Solve((1, 0), (0, 1), (c, s), (-s, c));
            Assert.Equal(30, rot, P);
        }

        [Fact]
        public void Solve_RotacionEnRangoCeroA360()
        {
            (double rot, _, _) = OpeningPlacement.Solve((0, 1), (-1, 0), (0.6, -0.8), (0.8, 0.6));
            Assert.InRange(rot, 0, 360);
            Assert.True(rot < 360);
        }

        [Fact]
        public void Solve_UYVNoPerpendiculares()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => OpeningPlacement.Solve((1, 0), (1, 1), (1, 0), (0, 1)));
            Assert.Equal("colocacion: u y v deben ser perpendiculares", e.Message);
        }

        [Fact]
        public void Solve_DestinoNoPerpendicular_SinSolucion()
        {
            Assert.Throws<ArgumentException>(() => OpeningPlacement.Solve((1, 0), (0, 1), (1, 0), (0.6, 0.8)));
        }

        [Theory]
        [InlineData("izquierda", "inicio", 0.8, 0)]
        [InlineData("izquierda", "fin", 1.6, 0)]
        [InlineData("derecha", "inicio", 0.8, 0.15)]
        [InlineData("derecha", "fin", 1.6, 0.15)]
        public void Puerta_MuroHorizontal_PuntoBaseEnLaEsquinaCorrecta(string cara, string bisagra, double x, double y)
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Puerta, cara, bisagra, true);
            AssertPt((x, y), c.Posicion);
        }

        [Theory]
        [InlineData("izquierda", "inicio", 0.15, 0.8)]
        [InlineData("izquierda", "fin", 0.15, 1.6)]
        [InlineData("derecha", "inicio", 0, 0.8)]
        [InlineData("derecha", "fin", 0, 1.6)]
        public void Ventana_MuroVertical_PuntoBaseEnLaEsquinaCorrecta(string cara, string bisagra, double x, double y)
        {
            // Con Direccion +y la izquierda es −x: abrir a la izquierda deja el punto base en la cara x = 0,15.
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoV(), Ventana, cara, bisagra, true);
            AssertPt((x, y), c.Posicion);
        }

        /// <summary>u cae a lo largo del hueco, de la bisagra a la otra esquina, y v entra hasta la otra cara.</summary>
        [Theory]
        [InlineData("puerta", "H", "izquierda", "inicio")]
        [InlineData("puerta", "H", "izquierda", "fin")]
        [InlineData("puerta", "H", "derecha", "inicio")]
        [InlineData("puerta", "H", "derecha", "fin")]
        [InlineData("puerta", "V", "izquierda", "inicio")]
        [InlineData("puerta", "V", "izquierda", "fin")]
        [InlineData("puerta", "V", "derecha", "inicio")]
        [InlineData("puerta", "V", "derecha", "fin")]
        [InlineData("ventana", "H", "izquierda", "inicio")]
        [InlineData("ventana", "H", "izquierda", "fin")]
        [InlineData("ventana", "H", "derecha", "inicio")]
        [InlineData("ventana", "H", "derecha", "fin")]
        [InlineData("ventana", "V", "izquierda", "inicio")]
        [InlineData("ventana", "V", "izquierda", "fin")]
        [InlineData("ventana", "V", "derecha", "inicio")]
        [InlineData("ventana", "V", "derecha", "fin")]
        public void BloqueEscalado_CubreElHuecoYEntraEnElMuro(string tipo, string muro, string cara, string bisagra)
        {
            WallOpening.OpeningResult h = muro == "H" ? HuecoH() : HuecoV();
            VanoBlockSpec spec = SpecDe(tipo);
            OpeningPlacement.Placement c = OpeningPlacement.Place(h, spec, cara, bisagra, false);

            (double x, double y) u = VanoCatalog.AxisVector(spec.EjeAncho);
            (double x, double y) v = VanoCatalog.ThicknessVector(spec.DireccionEspesor);

            // Otra esquina del hueco en la misma cara.
            bool baseDerecha = cara == "izquierda";
            bool inicio = bisagra == "inicio";
            (double x, double y) otra = baseDerecha ? (inicio ? h.B2 : h.A2) : (inicio ? h.B1 : h.A1);
            AssertPt(otra, Mundo(c, (u.x * spec.AnchoNominal, u.y * spec.AnchoNominal)));

            // v (de longitud el espesor del muro) llega a la cara opuesta en la misma sección.
            (double x, double y) opuesta = baseDerecha ? (inicio ? h.A1 : h.B1) : (inicio ? h.A2 : h.B2);
            AssertPt(opuesta, Mundo(c, (v.x * h.Espesor, v.y * h.Espesor)));
        }

        [Fact]
        public void Puerta_SinEspejo_RotacionCero_IzquierdaInicio()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Puerta, "izquierda", "inicio", true);
            Assert.Equal(0, c.RotacionGrados, P);
            Assert.False(c.Espejo);
            Assert.Equal(new[] { 1.0, 1.0, 1.0 }, c.Escala);
            Assert.Null(c.FactorAncho);
        }

        [Fact]
        public void Puerta_BisagraFin_EsEspejo()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Puerta, "izquierda", "fin", true);
            Assert.True(c.Espejo);
            Assert.Equal(180, c.RotacionGrados, P);
            Assert.Equal(new[] { 1.0, -1.0, 1.0 }, c.Escala);
        }

        [Fact]
        public void Puerta_Derecha_Inicio_EsEspejo()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Puerta, "derecha", "inicio", true);
            Assert.True(c.Espejo);
            Assert.Equal(0, c.RotacionGrados, P);
        }

        [Fact]
        public void Puerta_Derecha_Fin_Rotacion180SinEspejo()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Puerta, "derecha", "fin", true);
            Assert.False(c.Espejo);
            Assert.Equal(180, c.RotacionGrados, P);
        }

        [Fact]
        public void Ventana_MuroHorizontal_Rot270ComoEnElPlano()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Ventana, "izquierda", "inicio", true);
            Assert.Equal(270, c.RotacionGrados, P);
            Assert.False(c.Espejo);
        }

        [Fact]
        public void Puerta_MuroVertical_Izquierda_Rotacion90()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoV(), Puerta, "izquierda", "inicio", true);
            Assert.Equal(90, c.RotacionGrados, P);
            Assert.False(c.Espejo);
        }

        [Fact]
        public void Escala_EjeX_FactorDeAncho()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Puerta, "izquierda", "inicio", false);
            Assert.Equal(0.8 / 0.9, c.FactorAncho!.Value, P);
            Assert.Equal(0.8 / 0.9, c.Escala[0], P);
            Assert.Equal(1, c.Escala[1], P);
            Assert.Equal(1, c.Escala[2], P);
        }

        [Fact]
        public void Escala_EjeY_FactorDeAncho()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Ventana, "izquierda", "inicio", false);
            Assert.Equal(0.8 / 0.3, c.FactorAncho!.Value, P);
            Assert.Equal(1, c.Escala[0], P);
            Assert.Equal(0.8 / 0.3, c.Escala[1], P);
        }

        [Fact]
        public void Escala_ConEspejo_ConservaElSigno()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Puerta, "izquierda", "fin", false);
            Assert.Equal(0.8 / 0.9, c.Escala[0], P);
            Assert.Equal(-1, c.Escala[1], P);
        }

        [Fact]
        public void PorPropiedad_SinFactor()
        {
            OpeningPlacement.Placement c = OpeningPlacement.Place(HuecoH(), Ventana, "izquierda", "fin", true);
            Assert.Null(c.FactorAncho);
            Assert.Equal(1, Math.Abs(c.Escala[0]), P);
            Assert.Equal(1, Math.Abs(c.Escala[1]), P);
        }

        [Fact]
        public void MuroEn30Grados_OrientacionConElMuro()
        {
            double a = Math.PI / 6;
            (double x, double y) R(double x, double y) => ((x * Math.Cos(a)) - (y * Math.Sin(a)), (x * Math.Sin(a)) + (y * Math.Cos(a)));
            var muro = new List<(double x, double y)> { R(0, 0), R(3, 0), R(3, 0.15), R(0, 0.15) };
            WallOpening.OpeningResult h = WallOpening.Cut(muro, R(1.2, 0.075), 0.8);
            OpeningPlacement.Placement c = OpeningPlacement.Place(h, Puerta, "izquierda", "inicio", true);
            Assert.Equal(30, c.RotacionGrados, 7);
            Assert.False(c.Espejo);
        }

        [Theory]
        [InlineData("arriba")]
        [InlineData("")]
        [InlineData("IZQUIERDA")]
        public void Place_CaraInvalida(string cara)
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => OpeningPlacement.Place(HuecoH(), Puerta, cara, "inicio", true));
            Assert.Equal("cara_apertura: debe ser izquierda o derecha", e.Message);
        }

        [Fact]
        public void Place_BisagraInvalida()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => OpeningPlacement.Place(HuecoH(), Puerta, "izquierda", "centro", true));
            Assert.Equal("bisagra: debe ser inicio o fin", e.Message);
        }

        [Fact]
        public void EstimatedBox_Izquierda_AmpliaHaciaArriba()
        {
            BBox2 b = OpeningPlacement.EstimatedBox(HuecoH(), "izquierda", 0.8);
            Assert.Equal(0.8, b.Xmin, P);
            Assert.Equal(1.6, b.Xmax, P);
            Assert.Equal(0, b.Ymin, P);
            Assert.Equal(0.95, b.Ymax, P);
        }

        [Fact]
        public void EstimatedBox_Derecha_AmpliaHaciaAbajo()
        {
            BBox2 b = OpeningPlacement.EstimatedBox(HuecoH(), "derecha", 0.8);
            Assert.Equal(-0.8, b.Ymin, P);
            Assert.Equal(0.15, b.Ymax, P);
        }

        [Fact]
        public void EstimatedBox_MuroVertical_Izquierda_AmpliaHaciaMenosX()
        {
            BBox2 b = OpeningPlacement.EstimatedBox(HuecoV(), "izquierda", 0.8);
            Assert.Equal(-0.8, b.Xmin, P);
            Assert.Equal(0.15, b.Xmax, P);
        }

        [Fact]
        public void EstimatedBox_AlcanceCero_EsElHueco()
        {
            BBox2 b = OpeningPlacement.EstimatedBox(HuecoH(), "derecha", 0);
            Assert.Equal(0, b.Ymin, P);
            Assert.Equal(0.15, b.Ymax, P);
        }

        [Fact]
        public void EstimatedBox_CaraInvalida()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => OpeningPlacement.EstimatedBox(HuecoH(), "x", 0));
            Assert.Equal("cara_apertura: debe ser izquierda o derecha", e.Message);
        }
    }
}
