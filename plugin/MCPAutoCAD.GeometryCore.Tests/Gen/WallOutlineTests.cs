using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>protocol.md §5E.3.1: contorno de muro con ingletes.</summary>
    public class WallOutlineTests
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

        private static (double xmin, double ymin, double xmax, double ymax) Box(IReadOnlyList<(double x, double y)> v) =>
            (v.Min(p => p.x), v.Min(p => p.y), v.Max(p => p.x), v.Max(p => p.y));

        private static WallGeometryException Throws(Func<WallResult> f) => Assert.Throws<WallGeometryException>(() => f());

        [Fact]
        public void MuroRectoCentrado_RectanguloYArea()
        {
            WallResult r = WallOutline.Build(Pts(0, 0, 4, 0), 0.15, WallJustification.Centro, false);
            Assert.Equal(4, r.Contorno.Count);
            (double xmin, double ymin, double xmax, double ymax) = Box(r.Contorno);
            Assert.Equal(0, xmin, P);
            Assert.Equal(-0.075, ymin, P);
            Assert.Equal(4, xmax, P);
            Assert.Equal(0.075, ymax, P);
            Assert.Equal(0.6, r.Area, P);
            Assert.Equal(4, r.LongitudEje, P);
            Assert.Equal(8.3, r.LongitudContorno, P);
            Assert.Equal(1, r.NTramos);
            Assert.Null(r.Interior);
            AssertPt((0, 0.075), r.CaraIzquierda[0]);
            AssertPt((4, 0.075), r.CaraIzquierda[1]);
            AssertPt((0, -0.075), r.CaraDerecha[0]);
            AssertPt((4, -0.075), r.CaraDerecha[1]);
            // Contorno = cara izquierda + cara derecha invertida.
            AssertPt((0, 0.075), r.Contorno[0]);
            AssertPt((4, 0.075), r.Contorno[1]);
            AssertPt((4, -0.075), r.Contorno[2]);
            AssertPt((0, -0.075), r.Contorno[3]);
        }

        [Fact]
        public void JustificacionIzquierda_MuroALaIzquierdaDelEje()
        {
            WallResult r = WallOutline.Build(Pts(0, 0, 4, 0), 0.15, WallJustification.Izquierda, false);
            (double _, double ymin, double _, double ymax) = Box(r.Contorno);
            Assert.Equal(0, ymin, P);
            Assert.Equal(0.15, ymax, P);
            Assert.Equal(0.6, r.Area, P);
        }

        [Fact]
        public void JustificacionDerecha_MuroALaDerechaDelEje()
        {
            WallResult r = WallOutline.Build(Pts(0, 0, 4, 0), 0.15, WallJustification.Derecha, false);
            (double _, double ymin, double _, double ymax) = Box(r.Contorno);
            Assert.Equal(-0.15, ymin, P);
            Assert.Equal(0, ymax, P);
        }

        [Fact]
        public void JustificacionIzquierda_SentidoInverso_CambiaElLado()
        {
            WallResult r = WallOutline.Build(Pts(4, 0, 0, 0), 0.15, WallJustification.Izquierda, false);
            (double _, double ymin, double _, double ymax) = Box(r.Contorno);
            Assert.Equal(-0.15, ymin, P);
            Assert.Equal(0, ymax, P);
        }

        [Fact]
        public void EnL_EsquinaAInglete()
        {
            WallResult r = WallOutline.Build(Pts(0, 0, 4, 0, 4, 3), 0.15, WallJustification.Centro, false);
            Assert.Equal(2, r.NTramos);
            Assert.Equal(7, r.LongitudEje, P);
            // Cara izquierda (interior de la L) y derecha (exterior).
            AssertPt((0, 0.075), r.CaraIzquierda[0]);
            AssertPt((3.925, 0.075), r.CaraIzquierda[1]);
            AssertPt((3.925, 3), r.CaraIzquierda[2]);
            AssertPt((0, -0.075), r.CaraDerecha[0]);
            AssertPt((4.075, -0.075), r.CaraDerecha[1]);
            AssertPt((4.075, 3), r.CaraDerecha[2]);
            Assert.Equal(6, r.Contorno.Count);
            Assert.Contains(r.Contorno, p => Math.Abs(p.x - 4.075) < 1e-9 && Math.Abs(p.y + 0.075) < 1e-9);
            // Área = longitud de eje × espesor (los ingletes se compensan).
            Assert.Equal(7 * 0.15, r.Area, P);
        }

        [Fact]
        public void TramosColineales_ConservanElVertice()
        {
            WallResult r = WallOutline.Build(Pts(0, 0, 2, 0, 4, 0), 0.2, WallJustification.Centro, false);
            Assert.Equal(3, r.CaraIzquierda.Count);
            AssertPt((2, 0.1), r.CaraIzquierda[1]);
            AssertPt((2, -0.1), r.CaraDerecha[1]);
            Assert.Equal(0.8, r.Area, P);
        }

        [Fact]
        public void EjeCerradoCuadrado_Anillo()
        {
            WallResult r = WallOutline.Build(Pts(0, 0, 4, 0, 4, 4, 0, 4), 0.15, WallJustification.Centro, true);
            Assert.NotNull(r.Interior);
            Assert.Equal(4, r.NTramos);
            Assert.Equal(16, r.LongitudEje, P);
            Assert.Equal((4.15 * 4.15) - (3.85 * 3.85), r.Area, P);
            (double xmin, double ymin, double xmax, double ymax) = Box(r.Contorno);
            Assert.Equal(-0.075, xmin, P);
            Assert.Equal(-0.075, ymin, P);
            Assert.Equal(4.075, xmax, P);
            Assert.Equal(4.075, ymax, P);
            (double ixmin, double _, double ixmax, double _) = Box(r.Interior!);
            Assert.Equal(0.075, ixmin, P);
            Assert.Equal(3.925, ixmax, P);
            Assert.Equal((4 * 4.15) + (4 * 3.85), r.LongitudContorno, P);
            // Eje antihorario: la cara izquierda es la interior.
            Assert.Same(r.CaraDerecha, r.Contorno);
        }

        [Fact]
        public void EjeCerradoHorario_ContornoSigueSiendoElExterior()
        {
            WallResult r = WallOutline.Build(Pts(0, 0, 0, 4, 4, 4, 4, 0), 0.15, WallJustification.Centro, true);
            Assert.Same(r.CaraIzquierda, r.Contorno);
            Assert.Equal((4.15 * 4.15) - (3.85 * 3.85), r.Area, P);
        }

        [Fact]
        public void AnguloDe5Grados_ExcepcionDeInglete()
        {
            double a = 5 * Math.PI / 180;
            var eje = Pts(0, 0, 4, 0, 4 - (4 * Math.Cos(a)), 4 * Math.Sin(a));
            WallGeometryException e = Throws(() => WallOutline.Build(eje, 0.15, WallJustification.Centro, false));
            Assert.False(e.EsParametro);
            Assert.Equal("eje: el ángulo en el vértice 1 (5.0°) es demasiado agudo para el inglete (límite 4× el espesor)", e.Message);
        }

        [Fact]
        public void EjeQueVuelveSobreSiMismo_ExcepcionDeInglete()
        {
            WallGeometryException e = Throws(() => WallOutline.Build(Pts(0, 0, 4, 0, 1, 0), 0.15, WallJustification.Centro, false));
            Assert.StartsWith("eje: el ángulo en el vértice 1 (0.0°)", e.Message);
        }

        [Fact]
        public void TramoMasCortoQueEspesor_Excepcion()
        {
            WallGeometryException e = Throws(() => WallOutline.Build(Pts(0, 0, 4, 0, 4, 0.1), 0.15, WallJustification.Centro, false));
            Assert.False(e.EsParametro);
            Assert.Equal("espesor: el tramo 1 (longitud 0.1) es más corto que el espesor 0.15", e.Message);
        }

        [Fact]
        public void EjeAutointersecado_Pajarita()
        {
            WallGeometryException e = Throws(() => WallOutline.Build(Pts(0, 0, 4, 4, 4, 0, 0, 4), 0.15, WallJustification.Centro, true));
            Assert.False(e.EsParametro);
            Assert.Equal("eje: el eje se autointerseca (tramos 0 y 2)", e.Message);
        }

        [Fact]
        public void EjeAbiertoAutointersecado()
        {
            WallGeometryException e = Throws(() => WallOutline.Build(Pts(0, 0, 4, 4, 4, 0, 0, 4), 0.15, WallJustification.Centro, false));
            Assert.Equal("eje: el eje se autointerseca (tramos 0 y 2)", e.Message);
        }

        [Fact]
        public void VerticeDuplicado_EsParametro()
        {
            WallGeometryException e = Throws(() => WallOutline.Build(Pts(0, 0, 4, 0, 4, 0, 4, 3), 0.15, WallJustification.Centro, false));
            Assert.True(e.EsParametro);
            Assert.Equal("eje[2]: vértice duplicado con el anterior", e.Message);
        }

        [Fact]
        public void CerradoRepitiendoElPrimero_EsParametro()
        {
            WallGeometryException e = Throws(() => WallOutline.Build(Pts(0, 0, 4, 0, 4, 4, 0, 0), 0.15, WallJustification.Centro, true));
            Assert.True(e.EsParametro);
            Assert.Equal("eje[3]: vértice duplicado con el anterior", e.Message);
        }

        [Fact]
        public void ErroresDeParametros()
        {
            WallGeometryException e1 = Throws(() => WallOutline.Build(Pts(0, 0), 0.15, WallJustification.Centro, false));
            Assert.Equal("eje: se necesitan al menos 2 vértices", e1.Message);
            Assert.True(e1.EsParametro);
            WallGeometryException e2 = Throws(() => WallOutline.Build(Pts(0, 0, 4, 0), 0.15, WallJustification.Centro, true));
            Assert.Equal("eje: un muro cerrado necesita al menos 3 vértices", e2.Message);
            Assert.True(e2.EsParametro);
            WallGeometryException e3 = Throws(() => WallOutline.Build(Pts(0, 0, 4, 0), 0, WallJustification.Centro, false));
            Assert.Equal("espesor: debe ser mayor que 0", e3.Message);
            Assert.True(e3.EsParametro);
            WallGeometryException e4 = Throws(() => WallOutline.Build(Pts(0, 0, 4, 0), -1, WallJustification.Centro, false));
            Assert.Equal("espesor: debe ser mayor que 0", e4.Message);
        }

        [Fact]
        public void ContornoAutointersecado_TramoComidoPorElInglete()
        {
            // U justificada a la izquierda (muro dentro de la U): el tramo central (0,2) es menor que 2 × 0,15.
            WallGeometryException e = Throws(() =>
                WallOutline.Build(Pts(0, 0, 2, 0, 2, 0.2, 0, 0.2), 0.15, WallJustification.Izquierda, false));
            Assert.False(e.EsParametro);
            Assert.StartsWith("eje: el contorno resultante se autointerseca (tramos ", e.Message);
        }

        [Fact]
        public void AnilloTriangularDemasiadoGrueso_CaraInteriorInvertida()
        {
            double h = Math.Sqrt(3) / 2;
            WallGeometryException e = Throws(() =>
                WallOutline.Build(Pts(0, 0, 1, 0, 0.5, h), 0.9, WallJustification.Centro, true));
            Assert.False(e.EsParametro);
            Assert.StartsWith("eje: el contorno resultante se autointerseca (tramos ", e.Message);
        }

        [Fact]
        public void LimiteDeIngleteConfigurable()
        {
            // 30° interior: 1/cos(75°) ≈ 3,86 < 4 → válido con el límite por defecto, rechazado con límite 2.
            double a = 30 * Math.PI / 180;
            var eje = Pts(0, 0, 4, 0, 4 - (4 * Math.Cos(a)), 4 * Math.Sin(a));
            WallOutline.Build(eje, 0.15, WallJustification.Centro, false);
            WallGeometryException e = Throws(() => WallOutline.Build(eje, 0.15, WallJustification.Centro, false, 2));
            Assert.Equal("eje: el ángulo en el vértice 1 (30.0°) es demasiado agudo para el inglete (límite 2× el espesor)", e.Message);
        }

        [Theory]
        [InlineData(WallJustification.Centro)]
        [InlineData(WallJustification.Izquierda)]
        [InlineData(WallJustification.Derecha)]
        public void Angulo45_AceptadoIgualConCualquierJustificacion(WallJustification j)
        {
            // 45° interior: factor 1/sin(22,5°) ≈ 2,61 ≤ 4. El límite se mide desde la línea media del muro, así que la
            // justificación no cambia el veredicto (antes un muro justificado se rechazaba por debajo de 60°).
            double a = 45 * Math.PI / 180;
            var eje = Pts(0, 0, 4, 0, 4 - (4 * Math.Cos(a)), 4 * Math.Sin(a));
            WallResult r = WallOutline.Build(eje, 0.15, j, false);
            Assert.Equal(3, r.CaraIzquierda.Count);
            Assert.True(r.Area > 0);
        }

        [Theory]
        [InlineData(WallJustification.Centro)]
        [InlineData(WallJustification.Izquierda)]
        [InlineData(WallJustification.Derecha)]
        public void Angulo20_RechazadoIgualConCualquierJustificacion(WallJustification j)
        {
            // 20° interior: factor 1/sin(10°) ≈ 5,76 > 4 para las tres justificaciones.
            double a = 20 * Math.PI / 180;
            var eje = Pts(0, 0, 4, 0, 4 - (4 * Math.Cos(a)), 4 * Math.Sin(a));
            WallGeometryException e = Throws(() => WallOutline.Build(eje, 0.15, j, false));
            Assert.Equal("eje: el ángulo en el vértice 1 (20.0°) es demasiado agudo para el inglete (límite 4× el espesor)", e.Message);
        }

        [Fact]
        public void LimiteDeInglete_IndependienteDeLaJustificacion()
        {
            // 30° interior (factor ≈ 3,86): mismo veredicto con límite 4 (aceptado) y con límite 3,5 (rechazado).
            double a = 30 * Math.PI / 180;
            var eje = Pts(0, 0, 4, 0, 4 - (4 * Math.Cos(a)), 4 * Math.Sin(a));
            foreach (WallJustification j in new[] { WallJustification.Centro, WallJustification.Izquierda, WallJustification.Derecha })
            {
                WallOutline.Build(eje, 0.15, j, false, 4);
                Throws(() => WallOutline.Build(eje, 0.15, j, false, 3.5));
            }
        }

        [Fact]
        public void ContornoValidoParaPolylineValidator()
        {
            WallResult r = WallOutline.Build(Pts(0, 0, 4, 0, 4, 3, 8, 3, 8, 0), 0.25, WallJustification.Derecha, false);
            Assert.Empty(MCPAutoCAD.GeometryCore.Draw.PolylineValidator.Validate(r.Contorno, true, null));
            Assert.Equal(14, r.LongitudEje, P);
            Assert.True(r.Area > 0);
        }
    }
}
