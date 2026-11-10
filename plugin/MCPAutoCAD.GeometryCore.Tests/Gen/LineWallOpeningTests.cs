using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>protocol.md §5G.1: hueco en un muro de dos caras LINE, con las medidas reales de 946C5 y 946C6.</summary>
    public class LineWallOpeningTests
    {
        private const int P = 6;

        private static readonly LineWallOpening.Face C5 = new LineWallOpening.Face("946C5", (3274.43, 4009.899), (3280.43, 4009.899));

        private static readonly LineWallOpening.Face C6 = new LineWallOpening.Face("946C6", (3274.58, 4009.749), (3280.28, 4009.749));

        private static LineWallOpening.Face F(string h, double x1, double y1, double x2, double y2) =>
            new LineWallOpening.Face(h, (x1, y1), (x2, y2));

        private static LineWallOpening.Face Girada(LineWallOpening.Face f, double grados)
        {
            double a = grados * Math.PI / 180.0;
            (double x, double y) R((double x, double y) p) => ((p.x * Math.Cos(a)) - (p.y * Math.Sin(a)), (p.x * Math.Sin(a)) + (p.y * Math.Cos(a)));
            return new LineWallOpening.Face(f.Handle, R(f.P1), R(f.P2));
        }

        private static WallGeometryException Throws(Action f) => Assert.Throws<WallGeometryException>(() => f());

        private static double Len(LineWallOpening.Piece p) => PolygonMetrics.Dist(p.Desde, p.Hasta);

        [Fact]
        public void PlanoReal_PuertaDe120EntreEjes()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(C5, C6, (3278.0, 4009.82), 1.2);
            Assert.Equal(0.15, c.Hueco.Espesor, P);
            Assert.Equal(5.7, c.Hueco.LongitudTramo, P);
            Assert.Equal(1.0, c.Hueco.Direccion.x, P);
            Assert.Equal(3278.0 - 3274.58, c.Hueco.DistanciaDesdeInicio, P);
            Assert.Equal(4009.824, c.Hueco.Centro.y, P);
            Assert.Equal(3278.0 - 0.6 - 3274.43, Len(c.MuroInicio), P);
            Assert.Equal(3280.43 - 3278.6, Len(c.MuroFin), P);
            Assert.Equal(3278.0 - 0.6 - 3274.58, Len(c.OpuestaInicio), P);
            Assert.Equal(3280.28 - 3278.6, Len(c.OpuestaFin), P);
            Assert.Equal(0.15, Len(c.TapaInicio), P);
            Assert.Equal(0.15, Len(c.TapaFin), P);
        }

        [Fact]
        public void CarasDeLaIzquierdaYDerecha_ComoOpeningResult()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(C5, C6, (3278.0, 4009.82), 1.2);
            Assert.Equal(4009.899, c.Hueco.A1.y, P);
            Assert.Equal(4009.749, c.Hueco.A2.y, P);
            Assert.Equal(c.Hueco.A1.x + 1.2, c.Hueco.B1.x, P);
            Assert.Empty(c.Hueco.LadoInicio);
            Assert.Empty(c.Hueco.LadoFin);
        }

        [Fact]
        public void DarLaCaraDeAbajoComoMuro_EsElMismoHueco()
        {
            LineWallOpening.LineCut a = LineWallOpening.Cut(C5, C6, (3278.0, 4009.82), 1.2);
            LineWallOpening.LineCut b = LineWallOpening.Cut(C6, C5, (3278.0, 4009.82), 1.2);
            Assert.Equal(a.Hueco.A1.y, b.Hueco.A1.y, P);
            Assert.Equal(a.Hueco.A2.y, b.Hueco.A2.y, P);
            Assert.Equal(a.Hueco.DistanciaDesdeInicio, b.Hueco.DistanciaDesdeInicio, P);
        }

        [Fact]
        public void LineasDibujadasAlReves_MismoInicio()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(F("a", 6, 0.15, 0, 0.15), F("b", 0, 0, 6, 0), (2, 0.07), 0.8);
            Assert.Equal(1.0, c.Hueco.Direccion.x, P);
            Assert.Equal(0.0, c.MuroInicio.Desde.x, P);
            Assert.Equal(6.0, c.MuroFin.Hasta.x, P);
        }

        [Fact]
        public void Vertical_ElInicioEsElDeMenorY()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(F("a", 0, 6, 0, 0), F("b", 0.15, 0, 0.15, 6), (0.07, 2), 0.8);
            Assert.Equal(1.0, c.Hueco.Direccion.y, P);
            Assert.Equal(2.0, c.Hueco.DistanciaDesdeInicio, P);
        }

        [Theory]
        [InlineData(30)]
        [InlineData(137)]
        public void Oblicuo_LongitudesYEspesorIguales(double grados)
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(
                Girada(F("a", 0, 0.15, 6, 0.15), grados), Girada(F("b", 0.15, 0, 5.85, 0), grados), Giro((3, 0.075), grados), 1.2);
            Assert.Equal(0.15, c.Hueco.Espesor, P);
            Assert.Equal(5.7, c.Hueco.LongitudTramo, P);
            Assert.Equal(1.2, PolygonMetrics.Dist(c.Hueco.A1, c.Hueco.B1), P);
            Assert.Equal(2.4, Len(c.MuroInicio), P);
            Assert.Equal(2.25, Len(c.OpuestaInicio), P);
        }

        private static (double x, double y) Giro((double x, double y) p, double grados)
        {
            double a = grados * Math.PI / 180.0;
            return ((p.x * Math.Cos(a)) - (p.y * Math.Sin(a)), (p.x * Math.Sin(a)) + (p.y * Math.Cos(a)));
        }

        [Fact]
        public void HuecoFueraDelSolape_AunqueQuepaEnLaCaraLarga()
        {
            // 946C5 sigue hasta 3280,43, pero 946C6 acaba en 3280,28: el hueco no cae en las dos caras.
            WallGeometryException e = Throws(() => LineWallOpening.Cut(C5, C6, (3280.0, 4009.82), 0.8));
            Assert.Equal("ancho: el hueco (0.8) no cabe en el tramo (longitud 5.7): se sale del tramo o cruza una esquina", e.Message);
            Assert.False(e.EsParametro);
        }

        [Fact]
        public void AnchoMayorQueElSolape()
        {
            Assert.Equal("ancho: debe ser menor que la longitud del tramo", Throws(() => LineWallOpening.Cut(C5, C6, (3277.0, 4009.82), 5.7)).Message);
        }

        [Fact]
        public void AnchoNoPositivo()
        {
            WallGeometryException e = Throws(() => LineWallOpening.Cut(C5, C6, (3277.0, 4009.82), 0));
            Assert.Equal("ancho: debe ser un número mayor que 0", e.Message);
            Assert.True(e.EsParametro);
        }

        [Fact]
        public void NoParalelas()
        {
            WallGeometryException e = Throws(() => LineWallOpening.Cut(C5, F("x", 3274, 4009, 3280, 4010), (3277, 4009.8), 0.8));
            Assert.Equal("cara_opuesta: la línea x no es paralela a la cara del muro", e.Message);
            Assert.True(e.EsParametro);
        }

        [Fact]
        public void CasiParalelas_PorRedondeo_SeAdmiten()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(F("a", 0, 0.15, 6, 0.15), F("b", 0, 0, 6, 0.0001), (3, 0.07), 0.8);
            Assert.Equal(0.15, c.Hueco.Espesor, 3);
        }

        [Fact]
        public void SinSolape()
        {
            Assert.Equal(
                "cara_opuesta: las líneas 946C5 y x no se solapan a lo largo del tramo",
                Throws(() => LineWallOpening.Cut(C5, F("x", 3290, 4009.7, 3295, 4009.7), (3277, 4009.8), 0.8)).Message);
        }

        [Fact]
        public void Coincidentes()
        {
            Assert.Equal(
                "cara_opuesta: las líneas 946C5 y x coinciden",
                Throws(() => LineWallOpening.Cut(C5, F("x", 3275, 4009.899, 3279, 4009.899), (3277, 4009.8), 0.8)).Message);
        }

        [Fact]
        public void LineaDeLongitudCero()
        {
            Assert.Equal("muro: la línea tiene longitud 0", Throws(() => LineWallOpening.Cut(F("a", 1, 1, 1, 1), C6, (3277, 4009.8), 0.8)).Message);
        }

        [Fact]
        public void CenterFromDistance_DesdeElInicioDelSolape()
        {
            (double x, double y) = LineWallOpening.CenterFromDistance(C5, C6, 1.0, false);
            Assert.Equal(3275.58, x, P);
            Assert.Equal(4009.824, y, P);
        }

        [Fact]
        public void CenterFromDistance_DesdeElFin()
        {
            Assert.Equal(3279.28, LineWallOpening.CenterFromDistance(C5, C6, 1.0, true).x, P);
        }

        [Fact]
        public void CenterFromDistance_FueraDelTramo()
        {
            WallGeometryException e = Throws(() => LineWallOpening.CenterFromDistance(C5, C6, 6, false));
            Assert.Equal("posicion: la distancia 6 se sale del tramo (longitud 5.7)", e.Message);
            Assert.True(e.EsParametro);
        }

        [Fact]
        public void Busca_LaParalelaMasCercana()
        {
            LineWallOpening.OppositeSearch s = LineWallOpening.FindOpposite(
                C5, new[] { C5, C6, F("lejos", 3274, 4009.6, 3281, 4009.6) }, (3278.0, 4009.82), 1.2);
            Assert.Equal("946C6", s.Elegida!.Handle);
            Assert.Equal(2, s.Candidatas.Count);
        }

        [Fact]
        public void Busca_SinCandidatas()
        {
            LineWallOpening.OppositeSearch s = LineWallOpening.FindOpposite(
                C5, new[] { F("a40y1", 3274, 4009.4, 3281, 4009.4), F("otra", 3290, 4009.8, 3291, 4009.8), F("perp", 3277, 4009, 3277, 4011) }, null, 1.2);
            Assert.Null(s.Elegida);
            Assert.Empty(s.Candidatas);
            Assert.Equal(
                "cara_opuesta: ninguna LINE paralela de la capa 'MUROS' solapa el muro 946C5 a 0.4 o menos; indique cara_opuesta",
                LineWallOpening.NoOppositeDetail("946C5", "MUROS"));
        }

        [Fact]
        public void Busca_SeparacionJustoEnElLimite_Cuenta()
        {
            Assert.Equal("x", LineWallOpening.FindOpposite(F("m", 0, 0.4, 6, 0.4), new[] { F("x", 0, 0, 6, 0) }, null, 1).Elegida!.Handle);
        }

        [Fact]
        public void Busca_DosALaMismaDistancia_LasLista()
        {
            LineWallOpening.OppositeSearch s = LineWallOpening.FindOpposite(
                F("m", 0, 0, 6, 0), new[] { F("arriba", 0, 0.15, 6, 0.15), F("abajo", 0, -0.15, 6, -0.15) }, (3, 0.07), 1);
            Assert.Null(s.Elegida);
            Assert.Equal(
                "cara_opuesta: hay varias LINE candidatas en la capa 'MUROS': arriba (0.15), abajo (0.15); indique cara_opuesta",
                LineWallOpening.ManyOppositesDetail("MUROS", s.Candidatas));
        }

        [Fact]
        public void Busca_CaraPartidaPorOtroVano_ElHuecoElige()
        {
            LineWallOpening.OppositeSearch s = LineWallOpening.FindOpposite(
                F("m", 0, 0.15, 6, 0.15), new[] { F("izq", 0, 0, 2, 0), F("der", 3, 0, 6, 0) }, (4.5, 0.07), 0.8);
            Assert.Equal("der", s.Elegida!.Handle);
        }

        [Fact]
        public void Busca_CaraPartida_PorDistancia_NoHayCentroYSeRechaza()
        {
            LineWallOpening.OppositeSearch s = LineWallOpening.FindOpposite(
                F("m", 0, 0.15, 6, 0.15), new[] { F("izq", 0, 0, 2, 0), F("der", 3, 0, 6, 0) }, null, 0.8);
            Assert.Null(s.Elegida);
            Assert.Equal(2, s.Candidatas.Count);
        }

        [Fact]
        public void SplitHatchLoop_DosTapasDeExtremo()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(F("a", 0, 0.15, 6, 0.15), F("b", 0, 0, 6, 0), (3, 0.07), 0.8);
            (IReadOnlyList<string> Inicio, IReadOnlyList<string> Fin)? r = LineWallOpening.SplitHatchLoop(
                c, new[] { ("fin", (6.0, 0.0), (6.0, 0.15)), ("ini", (0.0, 0.0), (0.0, 0.15)) });
            Assert.Equal(new[] { "ini" }, r!.Value.Inicio);
            Assert.Equal(new[] { "fin" }, r.Value.Fin);
        }

        [Fact]
        public void SplitHatchLoop_CadenaDeVariasEntidades()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(F("a", 0, 0.15, 6, 0.15), F("b", 0, 0, 6, 0), (3, 0.07), 0.8);
            (IReadOnlyList<string> Inicio, IReadOnlyList<string> Fin)? r = LineWallOpening.SplitHatchLoop(c, new[]
            {
                ("i2", (-0.1, 0.05), (0.0, 0.15)), ("i1", (0.0, 0.0), (-0.1, 0.05)), ("fin", (6.0, 0.0), (6.0, 0.15)),
            });
            Assert.Equal(new[] { "i1", "i2" }, r!.Value.Inicio);
        }

        [Fact]
        public void SplitHatchLoop_NoCierra_Null()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(F("a", 0, 0.15, 6, 0.15), F("b", 0, 0, 6, 0), (3, 0.07), 0.8);
            Assert.Null(LineWallOpening.SplitHatchLoop(c, new[] { ("ini", (0.0, 0.0), (0.0, 0.15)) }));
        }

        [Fact]
        public void SplitHatchLoop_SobraUnaEntidad_Null()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(F("a", 0, 0.15, 6, 0.15), F("b", 0, 0, 6, 0), (3, 0.07), 0.8);
            Assert.Null(LineWallOpening.SplitHatchLoop(c, new[]
            {
                ("ini", (0.0, 0.0), (0.0, 0.15)), ("fin", (6.0, 0.0), (6.0, 0.15)), ("suelta", (9.0, 0.0), (9.0, 1.0)),
            }));
        }

        [Fact]
        public void Busca_PrefiereLaQueCubreElHuecoALaMasCercana()
        {
            LineWallOpening.OppositeSearch s = LineWallOpening.FindOpposite(
                F("m", 0, 0.15, 6, 0.15), new[] { F("trozo", 0, 0.05, 2, 0.05), F("entera", 0, 0, 6, 0) }, (4, 0.07), 0.8);
            Assert.Equal("entera", s.Elegida!.Handle);
        }

        [Fact]
        public void Busca_SiNingunaCubre_GanaLaMasCercana()
        {
            LineWallOpening.OppositeSearch s = LineWallOpening.FindOpposite(
                F("m", 0, 0.15, 6, 0.15), new[] { F("cerca", 0, 0.05, 2, 0.05), F("lejos", 0, 0, 3, 0) }, (4, 0.07), 0.8);
            Assert.Equal("cerca", s.Elegida!.Handle);
        }

        [Fact]
        public void Busca_DosQueCubrenEmpatadas_LasLista()
        {
            LineWallOpening.OppositeSearch s = LineWallOpening.FindOpposite(
                F("m", 0, 0.15, 6, 0.15), new[] { F("a", 0, 0, 6, 0), F("b", 0, 0.3, 6, 0.3), F("c", 0, 0.1, 1, 0.1) }, (4, 0.07), 0.8);
            Assert.Null(s.Elegida);
            Assert.Equal(2, s.Candidatas.Count);
        }

        [Theory]
        [InlineData(0.00001)]
        [InlineData(0.009)]
        public void PiezaCasiDegenerada_NoCabe(double pieza)
        {
            WallGeometryException e = Throws(() => LineWallOpening.Cut(F("a", 0, 0.15, 6, 0.15), F("b", 0, 0, 6, 0), (pieza + 0.4, 0.07), 0.8));
            Assert.Equal("ancho: el hueco (0.8) no cabe en el tramo (longitud 6): se sale del tramo o cruza una esquina", e.Message);
        }

        [Fact]
        public void PiezaCasiDegenerada_ElOtroExtremo()
        {
            Assert.Contains("no cabe en el tramo", Throws(() => LineWallOpening.Cut(F("a", 0, 0.15, 6, 0.15), F("b", 0, 0, 6, 0), (5.595, 0.07), 0.8)).Message);
        }

        [Fact]
        public void PiezaDeUnCentimetro_Cabe()
        {
            LineWallOpening.LineCut c = LineWallOpening.Cut(F("a", 0, 0.15, 6, 0.15), F("b", 0, 0, 6, 0), (0.42, 0.07), 0.8);
            Assert.Equal(0.02, Len(c.MuroInicio), P);
        }

        [Fact]
        public void HatchesCrossingWarning_TextoLiteral()
        {
            Assert.Equal(
                "vano: 2 sombreado(s) cruzan el hueco y no se rehacen (A, B): revíselos.",
                WallOpening.HatchesCrossingWarning(new[] { "A", "B" }));
        }

        [Fact]
        public void HoleTouches_Columna()
        {
            WallOpening.OpeningResult h = LineWallOpening.Cut(C5, C6, (3277.46, 4009.82), 1.2).Hueco;
            Assert.True(WallOpening.HoleTouches(h, BBox2.FromCorners(3277.3, 4009.7, 3277.6, 4010.0)));
            Assert.False(WallOpening.HoleTouches(h, BBox2.FromCorners(3280.1, 4009.7, 3280.4, 4010.0)));
            Assert.False(WallOpening.HoleTouches(h, BBox2.FromCorners(3277.3, 4010.5, 3277.6, 4011.0)));
        }

        [Fact]
        public void HoleTouches_HuecoGirado_LaCajaDelHuecoNoBasta()
        {
            WallOpening.OpeningResult h = LineWallOpening.Cut(
                Girada(F("a", 0, 0.15, 6, 0.15), 45), Girada(F("b", 0, 0, 6, 0), 45), Giro((3, 0.075), 45), 2.0).Hueco;
            (double x, double y) c = Giro((3, 0.075), 45);
            Assert.True(WallOpening.HoleTouches(h, BBox2.FromCorners(c.x - 0.05, c.y - 0.05, c.x + 0.05, c.y + 0.05)));
            Assert.False(WallOpening.HoleTouches(h, BBox2.FromCorners(c.x + 0.4, c.y - 0.6, c.x + 0.6, c.y - 0.4)));
        }

        [Fact]
        public void ColumnWarning_TextoLiteral()
        {
            Assert.Equal(
                "vano: el hueco cae sobre 2 entidad(es) de la capa 'columnas' (A, B): revise la estructura antes de abrirlo.",
                WallOpening.ColumnWarning("columnas", new[] { "A", "B" }));
        }

        [Fact]
        public void ColumnWarning_CitaCinco()
        {
            Assert.Contains("(A, B, C, D, E y 2 más)", WallOpening.ColumnWarning("c", new[] { "A", "B", "C", "D", "E", "F", "G" }));
        }
    }
}
