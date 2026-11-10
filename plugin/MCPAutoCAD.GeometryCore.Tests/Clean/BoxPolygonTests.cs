using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Clean;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Clean
{
    /// <summary>
    /// Criterio dentro/fuera de borrar_fuera_de (protocol.md §5F.1) con el contorno REAL del plano limpio (39D46 en
    /// casa6x12_limpio.dwg, leído con ODA + ezdxf el 2026-10-27) y las cajas reales de §5F.7.
    /// </summary>
    public class BoxPolygonTests
    {
        /// <summary>Vértices de 39D46 en el plano limpio, en el orden del archivo (horario).</summary>
        private static readonly IReadOnlyList<(double x, double y)> ContornoReal = new[]
        {
            (3255.1938, 4034.1727),
            (3341.0294, 4034.1727),
            (3341.0294, 3950.8152),
            (3255.1938, 3950.8152),
        };

        private static IReadOnlyList<(double x, double y)> Antihorario => ContornoReal.Reverse().ToArray();

        /// <summary>Polígono en U (no convexo): muesca abierta por arriba entre x = 4 y x = 6, desde y = 4.</summary>
        private static readonly IReadOnlyList<(double x, double y)> U = new[]
        {
            (0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (6.0, 10.0), (6.0, 4.0), (4.0, 4.0), (4.0, 10.0), (0.0, 10.0),
        };

        /// <summary>Polígono en L (no convexo): quita el cuadrante superior derecho (x &gt; 4, y &gt; 4).</summary>
        private static readonly IReadOnlyList<(double x, double y)> L = new[]
        {
            (0.0, 0.0), (10.0, 0.0), (10.0, 4.0), (4.0, 4.0), (4.0, 10.0), (0.0, 10.0),
        };

        private static BBox2 B(double x0, double y0, double x1, double y1) => BBox2.FromCorners(x0, y0, x1, y1);

        private static void BothOrientations(BBox2 box, BoxRelation expected)
        {
            Assert.Equal(expected, BoxPolygon.Classify(ContornoReal, box));
            Assert.Equal(expected, BoxPolygon.Classify(Antihorario, box));
        }

        [Fact]
        public void Real_Linea6D136_FueraDelContorno()
        {
            // Línea horizontal (caja de alto 0) del plano madre que está fuera del contorno.
            BothOrientations(B(3375.163, 3986.452, 3381.163, 3986.452), BoxRelation.Fuera);
        }

        [Fact]
        public void Real_B7142_PolilineaDegeneradaPunto_Dentro()
        {
            BothOrientations(B(3308.88, 3998.78, 3308.88, 3998.78), BoxRelation.Dentro);
        }

        [Fact]
        public void Real_ExtensionConservada_Dentro()
        {
            // Extensión de lo conservado (§5F.7): x 3272.99…3316.10, y 3978.55…4011.30.
            BothOrientations(B(3272.99, 3978.55, 3316.10, 4011.30), BoxRelation.Dentro);
        }

        [Fact]
        public void Real_CajaSobreEsquinaInferiorIzquierda_Cruza()
        {
            BothOrientations(B(3250, 3960, 3260, 3970), BoxRelation.Cruza);
        }

        [Fact]
        public void Real_CajaQueContieneElContorno_Cruza()
        {
            BothOrientations(B(3200, 3900, 3400, 4100), BoxRelation.Cruza);
        }

        [Fact]
        public void Real_CajaQueTocaElBordeExterior_Cruza()
        {
            BothOrientations(B(3341.0294, 3990, 3350, 3995), BoxRelation.Cruza);
        }

        [Fact]
        public void Real_CajaIgualAlContorno_Dentro()
        {
            // El borde cuenta como dentro: una caja que coincide con el contorno está dentro.
            BothOrientations(B(3255.1938, 3950.8152, 3341.0294, 4034.1727), BoxRelation.Dentro);
        }

        [Fact]
        public void Real_ExtensionDelModeloSucio_Cruza()
        {
            // Extensión del modelo sucio (§5F.7): contiene el contorno.
            BothOrientations(B(-2539.26, -0.85, 6574.72, 4207.85), BoxRelation.Cruza);
        }

        [Fact]
        public void Real_CajaLejanaALaIzquierda_Fuera()
        {
            BothOrientations(B(-2539.26, -0.85, -2000, 100), BoxRelation.Fuera);
        }

        [Fact]
        public void PuntoSobreElBorde_Dentro()
        {
            BothOrientations(B(3341.0294, 4000, 3341.0294, 4000), BoxRelation.Dentro);
        }

        [Fact]
        public void LineaVerticalQueSaleDelContorno_Cruza()
        {
            BothOrientations(B(3300, 4000, 3300, 4100), BoxRelation.Cruza);
        }

        [Fact]
        public void NoConvexo_EnU_CuatroEsquinasDentroPeroLaMuescaAtraviesa_Cruza()
        {
            // Esquinas (2,6), (8,6), (8,8), (2,8): las cuatro dentro de los brazos; la muesca cruza la caja.
            Assert.True(BoxPolygon.PointInPolygon(U, 2, 6));
            Assert.True(BoxPolygon.PointInPolygon(U, 8, 8));
            Assert.Equal(BoxRelation.Cruza, BoxPolygon.Classify(U, B(2, 6, 8, 8)));
            Assert.Equal(BoxRelation.Cruza, BoxPolygon.Classify(U.Reverse().ToArray(), B(2, 6, 8, 8)));
        }

        [Fact]
        public void NoConvexo_EnU_LineaDegeneradaQueAtraviesaLaMuesca_Cruza()
        {
            // Caja de alto 0 con los dos extremos dentro: la muesca la corta (segmento degenerado).
            Assert.Equal(BoxRelation.Cruza, BoxPolygon.Classify(U, B(2, 7, 8, 7)));
        }

        [Fact]
        public void NoConvexo_EnU_DentroDeUnBrazoYDentroDeLaMuesca()
        {
            Assert.Equal(BoxRelation.Dentro, BoxPolygon.Classify(U, B(1, 5, 3, 9)));
            Assert.Equal(BoxRelation.Dentro, BoxPolygon.Classify(U, B(1, 1, 9, 3)));
            Assert.Equal(BoxRelation.Fuera, BoxPolygon.Classify(U, B(4.5, 5, 5.5, 9)));

            // Caja que llena la muesca exactamente: comparte bordes con el polígono pero su interior está fuera.
            Assert.Equal(BoxRelation.Cruza, BoxPolygon.Classify(U, B(4, 4, 6, 10)));
        }

        [Fact]
        public void NoConvexo_EnL_CajaEnElCuadranteQuitado_FueraYSobreElVerticeReflejo_Cruza()
        {
            Assert.Equal(BoxRelation.Fuera, BoxPolygon.Classify(L, B(5, 5, 9, 9)));
            Assert.Equal(BoxRelation.Cruza, BoxPolygon.Classify(L, B(3, 3, 5, 5)));
            Assert.Equal(BoxRelation.Dentro, BoxPolygon.Classify(L, B(1, 1, 4, 4)));

            // Segmento degenerado de (2, 4) a (8, 4): recorre el borde inferior del cuadrante quitado (borde = dentro).
            Assert.Equal(BoxRelation.Dentro, BoxPolygon.Classify(L, B(2, 4, 8, 4)));
        }

        [Fact]
        public void PoligonoDentroDeLaCaja_SinEsquinasDentro_Cruza()
        {
            var tri = new[] { (1.0, 1.0), (2.0, 1.0), (1.5, 2.0) };
            Assert.Equal(BoxRelation.Cruza, BoxPolygon.Classify(tri, B(0, 0, 3, 3)));
        }

        [Fact]
        public void MenosDeTresVertices_ArgumentException()
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                BoxPolygon.Classify(new[] { (0.0, 0.0), (1.0, 1.0) }, B(0, 0, 1, 1)));
            Assert.Equal("contorno: se necesitan al menos 3 vértices", ex.Message);
        }

        [Theory]
        [InlineData(BoxRelation.Fuera, OutsideCriterion.CajaCruza, true)]
        [InlineData(BoxRelation.Fuera, OutsideCriterion.CajaDentro, true)]
        [InlineData(BoxRelation.Cruza, OutsideCriterion.CajaCruza, false)]
        [InlineData(BoxRelation.Cruza, OutsideCriterion.CajaDentro, true)]
        [InlineData(BoxRelation.Dentro, OutsideCriterion.CajaCruza, false)]
        [InlineData(BoxRelation.Dentro, OutsideCriterion.CajaDentro, false)]
        public void ShouldDelete_SeisCombinaciones(BoxRelation rel, OutsideCriterion criterio, bool esperado)
        {
            Assert.Equal(esperado, BoxPolygon.ShouldDelete(rel, criterio));
        }

        [Theory]
        [InlineData("caja_cruza", OutsideCriterion.CajaCruza)]
        [InlineData("CAJA_CRUZA", OutsideCriterion.CajaCruza)]
        [InlineData("caja_dentro", OutsideCriterion.CajaDentro)]
        [InlineData("Caja_Dentro", OutsideCriterion.CajaDentro)]
        [InlineData(null, OutsideCriterion.CajaCruza)]
        public void ParseCriterion_Valores(string? texto, OutsideCriterion esperado)
        {
            Assert.Equal(esperado, BoxPolygon.ParseCriterion(texto));
        }

        [Theory]
        [InlineData("")]
        [InlineData("dentro")]
        [InlineData(" caja_cruza")]
        public void ParseCriterion_Invalido_ArgumentException(string texto)
        {
            var ex = Assert.Throws<ArgumentException>(() => BoxPolygon.ParseCriterion(texto));
            Assert.Equal("criterio: debe ser caja_cruza o caja_dentro", ex.Message);
        }

        [Fact]
        public void CirclePolygon_CuatroLados_AntihorarioDesdeMasX()
        {
            List<(double x, double y)> p = BoxPolygon.CirclePolygon(0, 0, 1, 4);
            var esperado = new[] { (1.0, 0.0), (0.0, 1.0), (-1.0, 0.0), (0.0, -1.0) };
            Assert.Equal(4, p.Count);
            for (int i = 0; i < 4; i++)
            {
                Assert.True(Math.Abs(p[i].x - esperado[i].Item1) <= 1e-12, "x[" + i + "]");
                Assert.True(Math.Abs(p[i].y - esperado[i].Item2) <= 1e-12, "y[" + i + "]");
            }
        }

        [Fact]
        public void CirclePolygon_PorDefecto64Lados_InscritoYClasifica()
        {
            List<(double x, double y)> p = BoxPolygon.CirclePolygon(10, 20, 5);
            Assert.Equal(BoxPolygon.CircleSides, p.Count);
            Assert.All(p, v => Assert.Equal(5.0, Math.Sqrt(((v.x - 10) * (v.x - 10)) + ((v.y - 20) * (v.y - 20))), 9));
            Assert.Equal(BoxRelation.Dentro, BoxPolygon.Classify(p, B(8, 18, 12, 22)));
            Assert.Equal(BoxRelation.Fuera, BoxPolygon.Classify(p, B(16, 26, 17, 27)));
            Assert.Equal(BoxRelation.Cruza, BoxPolygon.Classify(p, B(14, 19, 16, 21)));
        }
    }
}
