using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>Factor de escala implícito entre unidades (protocol.md §5C.3 a y tabla §8).</summary>
    public class UnitsScaleTests
    {
        [Fact]
        public void MilimetrosEnMetros_Es0001()
        {
            Assert.Equal(0.001, UnitsScale.Factor(4, 6)!.Value, 15);
        }

        [Fact]
        public void PulgadasEnMetros_Es00254()
        {
            Assert.Equal(0.0254, UnitsScale.Factor(1, 6)!.Value, 15);
        }

        [Fact]
        public void MetrosEnMilimetros_Es1000()
        {
            Assert.Equal(1000.0, UnitsScale.Factor(6, 4)!.Value, 9);
        }

        [Fact]
        public void MismaUnidad_Es1()
        {
            Assert.Equal(1.0, UnitsScale.Factor(6, 6)!.Value, 15);
        }

        [Theory]
        [InlineData(18, 6)]
        [InlineData(19, 6)]
        [InlineData(20, 6)]
        [InlineData(6, 18)]
        [InlineData(0, 6)]
        [InlineData(6, 0)]
        [InlineData(25, 6)]
        [InlineData(-1, 6)]
        public void SinEquivalencia_EsNull(int from, int to)
        {
            Assert.Null(UnitsScale.Factor(from, to));
        }

        [Theory]
        [InlineData(1, 0.0254)]
        [InlineData(2, 0.3048)]
        [InlineData(3, 1609.344)]
        [InlineData(4, 0.001)]
        [InlineData(5, 0.01)]
        [InlineData(6, 1.0)]
        [InlineData(7, 1000.0)]
        [InlineData(10, 0.9144)]
        [InlineData(14, 0.1)]
        [InlineData(15, 10.0)]
        [InlineData(16, 100.0)]
        public void MetrosPorUnidad(int insunits, double meters)
        {
            Assert.Equal(meters, UnitsScale.MetersPerUnit(insunits)!.Value, 12);
        }

        [Fact]
        public void PieTopograficoUs()
        {
            Assert.Equal(1200.0 / 3937.0, UnitsScale.MetersPerUnit(21)!.Value, 15);
            Assert.Null(UnitsScale.MetersPerUnit(0));
        }

        [Fact]
        public void CentimetrosEnPulgadas()
        {
            Assert.Equal(0.01 / 0.0254, UnitsScale.Factor(5, 1)!.Value, 12);
        }

        // ---- Compensación por inserción (§5C.3, corrección F4 del gate de Fase 2) ----

        [Fact]
        public void Compensada_EscalaIgualAlFactor()
        {
            Assert.True(UnitsScale.IsCompensated(0.001, 0.001));
        }

        [Fact]
        public void Compensada_DentroDel1PorCiento()
        {
            Assert.True(UnitsScale.IsCompensated(0.00101, 0.001));
        }

        [Fact]
        public void NoCompensada_FueraDel1PorCiento()
        {
            Assert.False(UnitsScale.IsCompensated(0.0011, 0.001));
        }

        [Fact]
        public void NoCompensada_Escala1ConFactorMilimetros()
        {
            Assert.False(UnitsScale.IsCompensated(1.0, 0.001));
        }

        [Fact]
        public void Compensada_PulgadasEnMetros()
        {
            Assert.True(UnitsScale.IsCompensated(0.0254, UnitsScale.Factor(1, 6)!.Value));
        }

        [Fact]
        public void EscalaNegativa_EspejoSeEvaluaPorValorAbsoluto()
        {
            // El llamador pasa |ScaleFactors.X|; aun así un valor negativo (espejo) se evalúa por valor absoluto.
            Assert.True(UnitsScale.IsCompensated(-0.001, 0.001));
            Assert.True(UnitsScale.IsCompensated(System.Math.Abs(-0.001), 0.001));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.001)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void FactorInvalido_NoCompensada(double factor)
        {
            Assert.False(UnitsScale.IsCompensated(0.001, factor));
        }

        [Fact]
        public void EscalaNoFinita_NoCompensada()
        {
            Assert.False(UnitsScale.IsCompensated(double.NaN, 0.001));
        }

        [Fact]
        public void Clasificar_FactorNull_TodasNoCompensadas()
        {
            var ins = new List<(string, double)> { ("1A", 1.0), ("1B", 0.001) };
            var (comp, noComp) = UnitsScale.ClassifyInsertions(ins, null);
            Assert.Empty(comp);
            Assert.Equal(new[] { "1A", "1B" }, noComp.Select(x => x.handle));
        }

        [Fact]
        public void Clasificar_CasoRealLav_CuatroAEscala1_NingunaCompensada()
        {
            // Datos reales (copia de EXP 26-XXXX, dibujo en m): bloque 'lav' en mm, 4 inserciones a escala 1.
            var ins = new List<(string, double)> { ("1DE9E", 1.0), ("1DEA0", 1.0), ("1DEA2", 1.0), ("1DEA4", 1.0) };
            var (comp, noComp) = UnitsScale.ClassifyInsertions(ins, UnitsScale.Factor(4, 6));
            Assert.Empty(comp);
            Assert.Equal(4, noComp.Count);
            Assert.Equal("1DE9E", noComp[0].handle);
            Assert.Equal(1.0, noComp[0].escala);
        }

        [Fact]
        public void Clasificar_Mezcla_ConservaOrden()
        {
            // PuertaBaño (pulgadas) real: escalas 1.076923 y 1.22257 → ninguna compensada (factor 0.0254);
            // se añade una compensada sintética para comprobar el reparto y el orden.
            var ins = new List<(string, double)> { ("A1", 1.076923), ("A2", 0.0254), ("A3", 1.22257), ("A4", 0.02541) };
            var (comp, noComp) = UnitsScale.ClassifyInsertions(ins, UnitsScale.Factor(1, 6));
            Assert.Equal(new[] { "A2", "A4" }, comp.Select(x => x.handle));
            Assert.Equal(new[] { "A1", "A3" }, noComp.Select(x => x.handle));
        }

        [Fact]
        public void Clasificar_CarroYBloqueC_Reales_NoCompensadas()
        {
            // CARRO (mm) a 0.88128 y C (mm, anidado en *D…) a 0.045: ninguna es 0.001 → no compensadas.
            var ins = new List<(string, double)> { ("C1", 0.88128), ("C2", 0.045) };
            var (comp, noComp) = UnitsScale.ClassifyInsertions(ins, 0.001);
            Assert.Empty(comp);
            Assert.Equal(2, noComp.Count);
        }

        [Fact]
        public void Clasificar_TodasCompensadas()
        {
            var ins = new List<(string, double)> { ("B1", 0.001), ("B2", 0.000995), ("B3", 0.001005) };
            var (comp, noComp) = UnitsScale.ClassifyInsertions(ins, 0.001);
            Assert.Equal(3, comp.Count);
            Assert.Empty(noComp);
        }

        // ---------------------------------------------------------------- F4: tamaño real y metadato de unidades

        [Fact]
        public void TamanoReal_DibujoEnMetros_EsLaCaja()
        {
            double[]? t = UnitsScale.RealSizeMeters(0.45, 0.51, 6);
            Assert.NotNull(t);
            Assert.Equal(0.45, t![0], 12);
            Assert.Equal(0.51, t[1], 12);
        }

        [Fact]
        public void TamanoReal_DibujoEnMilimetros_SeConvierte()
        {
            double[]? t = UnitsScale.RealSizeMeters(450, 510, 4);
            Assert.Equal(0.45, t![0], 12);
            Assert.Equal(0.51, t[1], 12);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(18)]
        public void TamanoReal_SinEquivalencia_EsNull(int insunits)
        {
            Assert.Null(UnitsScale.RealSizeMeters(1, 1, insunits));
        }

        [Fact]
        public void TamanoReal_NoFinitoONegativo_EsNull()
        {
            Assert.Null(UnitsScale.RealSizeMeters(double.NaN, 1, 6));
            Assert.Null(UnitsScale.RealSizeMeters(-1, 1, 6));
        }

        [Theory]
        [InlineData(0.45, 0.51, true)]     // lav real
        [InlineData(0.02, 0.01, true)]     // límite inferior inclusive
        [InlineData(200, 3, true)]         // límite superior inclusive
        [InlineData(0.019, 0.019, false)]  // demasiado pequeño
        [InlineData(450, 510, false)]      // geometría en mm leída como m: escala realmente incorrecta
        [InlineData(0, 0, false)]
        public void TamanoPlausible(double ancho, double alto, bool esperado)
        {
            Assert.Equal(esperado, UnitsScale.IsPlausibleRealSize(new[] { ancho, alto }));
        }

        [Fact]
        public void TamanoPlausible_NullONoFinito_False()
        {
            Assert.False(UnitsScale.IsPlausibleRealSize(null));
            Assert.False(UnitsScale.IsPlausibleRealSize(new[] { double.PositiveInfinity, 1 }));
        }

        [Fact]
        public void SeveridadNoCompensada_PlausibleMedia_FueraDeRangoAlta_NoMedibleMedia()
        {
            Assert.Equal(Severity.Media, UnitsScale.NonCompensatedSeverity(new[] { 0.45, 0.51 }));
            Assert.Equal(Severity.Alta, UnitsScale.NonCompensatedSeverity(new[] { 450.0, 510.0 }));
            Assert.Equal(Severity.Alta, UnitsScale.NonCompensatedSeverity(new[] { 0.0004, 0.0005 }));
            // 0.3.2: "alta" solo con un tamaño MEDIDO implausible; sin medida → media (no medible).
            Assert.Equal(Severity.Media, UnitsScale.NonCompensatedSeverity(null));
        }

        [Fact]
        public void BloqueC_Real_AnidadaEnContenedorMedidaPlausible()
        {
            // 'C' (mm) a escala 0.045 dentro de un contenedor insertado a escala 1 en el modelo (m): definición de 1×1
            // → caja local 0.045×0.045 en el contenedor → en el modelo 0.045×0.045 m → plausible → media.
            BBox2 local = Affine2.FromInsert(10, 20, 0.045, 0.045, 0).TransformBox(BBox2.FromCorners(-0.5, -0.5, 0.5, 0.5));
            BBox2 enModelo = Affine2.BoxInSpace(local, new[] { Affine2.FromInsert(3300, 3990, 1, 1, 0) });
            double[]? real = UnitsScale.RealSizeMeters(enModelo.Width, enModelo.Height, 6);
            Assert.NotNull(real);
            Assert.Equal(0.045, real![0], 9);
            Assert.True(UnitsScale.IsPlausibleRealSize(real));
            Assert.Equal(Severity.Media, UnitsScale.NonCompensatedSeverity(real));
        }

        [Fact]
        public void Anidada_ContenedorConEscalaGrande_MedidaImplausible_Alta()
        {
            BBox2 local = BBox2.FromCorners(0, 0, 1, 1);
            BBox2 enModelo = Affine2.BoxInSpace(local, new[] { Affine2.FromInsert(0, 0, 500, 500, 0) });
            Assert.Equal(Severity.Alta, UnitsScale.NonCompensatedSeverity(UnitsScale.RealSizeMeters(enModelo.Width, enModelo.Height, 6)));
        }

        [Fact]
        public void MensajeNoMedible_Anidadas()
        {
            Assert.Equal(
                "El bloque 'C' declara unidades mm y 500 de sus inserciones evaluadas no están compensadas (escala 0.045), "
                + "pero su tamaño real es no medible (inserciones anidadas): revise el metadato de unidades del bloque.",
                UnitsScale.UnmeasurableMessage("C", "mm", 500, "escala 0.045", true));
            Assert.DoesNotContain("anidadas", UnitsScale.UnmeasurableMessage("C", "mm", 1, "escala 1", false));
        }

        [Theory]
        [InlineData(0.001, "1000× más pequeño")]
        [InlineData(0.0254, "39.3701× más pequeño")]
        [InlineData(1000.0, "1000× más grande")]
        [InlineData(1.0, "del mismo tamaño")]
        public void EfectoReinsercion(double factor, string esperado)
        {
            Assert.Equal(esperado, UnitsScale.ReinsertionEffect(factor));
        }

        [Fact]
        public void MensajeMetadato_Lav()
        {
            string m = UnitsScale.MetadataMessage("lav", "mm", new[] { 0.45, 0.51 }, 0.001);
            Assert.Equal(
                "El bloque 'lav' declara unidades mm pero su geometría ya está en las unidades del dibujo (tamaño real ≈ "
                + "0.45×0.51 m): el metadato de unidades es incorrecto; si se reinserta con escala automática quedará 1000× más pequeño.",
                m);
        }

        [Fact]
        public void MensajeMetadato_PulgadasRedondea()
        {
            string m = UnitsScale.MetadataMessage("PuertaBaño", "in", new[] { 0.8761, 2.1049 }, 0.0254);
            Assert.Contains("declara unidades in", m);
            Assert.Contains("0.88×2.1 m", m);
            Assert.EndsWith("quedará 39.3701× más pequeño.", m);
        }
    }
}
