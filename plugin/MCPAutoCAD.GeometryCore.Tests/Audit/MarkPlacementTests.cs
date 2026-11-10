using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>Comprobación previa de marcar_hallazgos (protocol.md §5C.9, F9 de 0.3.2).</summary>
    public class MarkPlacementTests
    {
        // Caso real del gate 0.3.1: papel de Layout1 ≈ x 319…321, y 59…60; marca con coordenadas de modelo.
        private static readonly BBox2 PapelReal = BBox2.FromCorners(319, 59, 321, 60);
        private static readonly BBox2 MarcaModelo = BBox2.FromCorners(3273, 3989, 3278, 3994);

        [Fact]
        public void CasoReal_MarcaDeModeloEnPapel_Fuera()
        {
            Assert.False(MarkPlacement.Fits(MarcaModelo, PapelReal));
            Assert.Equal(new List<int> { 0 }, MarkPlacement.OutsideIndices(new[] { MarcaModelo }, PapelReal));
        }

        [Fact]
        public void CasoReal_MarcaDeModeloEnModelo_Dentro()
        {
            BBox2 modelo = BBox2.FromCorners(-2300, 3500, 3800, 4100);
            Assert.True(MarkPlacement.Fits(MarcaModelo, modelo));
        }

        [Fact]
        public void SinExtension_NoBloquea()
        {
            Assert.True(MarkPlacement.Fits(MarcaModelo, null));
            Assert.Empty(MarkPlacement.OutsideIndices(new[] { MarcaModelo }, null));
        }

        [Fact]
        public void Expand_DiezPorCientoDelLadoMayor()
        {
            BBox2 e = MarkPlacement.Expand(BBox2.FromCorners(0, 0, 100, 20), 0.10);
            Assert.Equal(BBox2.FromCorners(-10, -10, 110, 30), e);
        }

        [Fact]
        public void Expand_FraccionInvalida_Lanza()
        {
            Assert.Throws<ArgumentException>(() => MarkPlacement.Expand(PapelReal, -0.1));
            Assert.Throws<ArgumentException>(() => MarkPlacement.Expand(PapelReal, double.NaN));
        }

        [Fact]
        public void Margen_MarcaJustoDentroYJustoFuera()
        {
            BBox2 ext = BBox2.FromCorners(0, 0, 100, 100); // margen 10
            Assert.True(MarkPlacement.Fits(BBox2.FromCorners(109, 50, 120, 60), ext));
            Assert.True(MarkPlacement.Fits(BBox2.FromCorners(110, 50, 120, 60), ext)); // toca el borde ampliado
            Assert.False(MarkPlacement.Fits(BBox2.FromCorners(110.001, 50, 120, 60), ext));
        }

        [Fact]
        public void MarcaPunto_Y_ExtensionDegenerada()
        {
            BBox2 linea = BBox2.FromCorners(0, 5, 100, 5); // una línea horizontal: margen 10 también en y
            Assert.True(MarkPlacement.Fits(BBox2.FromCorners(50, 14, 50, 14), linea));
            Assert.False(MarkPlacement.Fits(BBox2.FromCorners(50, 16, 50, 16), linea));
        }

        [Fact]
        public void MarcaQueEnvuelveLaExtension_Interseca()
        {
            Assert.True(MarkPlacement.Fits(BBox2.FromCorners(-1000, -1000, 1000, 1000), PapelReal));
        }

        [Fact]
        public void OutsideIndices_SoloLasDeFuera()
        {
            var marcas = new[] { BBox2.FromCorners(319.5, 59.2, 320, 59.5), MarcaModelo, BBox2.FromCorners(320, 59, 320, 59) };
            Assert.Equal(new List<int> { 1 }, MarkPlacement.OutsideIndices(marcas, PapelReal));
        }

        [Fact]
        public void Mensaje_Papel_SugiereModelo()
        {
            string msg = MarkPlacement.OutsideMessage("Layout1", false, PapelReal, new[] { 0 }, new[] { MarcaModelo });
            Assert.Equal(
                "marcas[0] [[3273, 3989], [3278, 3994]] fuera de la extensión del espacio destino 'Layout1' [[319, 59], [321, 60]] "
                + "(ampliada un 10 %): probablemente las coordenadas son del modelo: use espacio:\"modelo\" (o el layout correcto). "
                + "No se escribió nada; pase forzar: true para escribir igualmente.",
                msg);
        }

        [Fact]
        public void Mensaje_Modelo_SugiereLayout_YCortaEnCinco()
        {
            var marcas = new List<BBox2>();
            var fuera = new List<int>();
            for (int i = 0; i < 7; i++)
            {
                marcas.Add(BBox2.FromCorners(1e6, 1e6, 1e6 + 1, 1e6 + 1));
                fuera.Add(i);
            }

            string msg = MarkPlacement.OutsideMessage("modelo", true, BBox2.FromCorners(0, 0, 10, 10.12345), fuera, marcas);
            Assert.Contains("marcas[4]", msg);
            Assert.DoesNotContain("marcas[5]", msg);
            Assert.Contains(" y 2 más", msg);
            Assert.Contains("[[0, 0], [10, 10.123]]", msg);
            Assert.Contains("indique en 'espacio' el layout correcto", msg);
        }

        [Fact]
        public void Mensaje_SinMarcasFuera_Lanza()
        {
            Assert.Throws<ArgumentException>(() => MarkPlacement.OutsideMessage("modelo", true, PapelReal, new int[0], new BBox2[0]));
        }
    }
}
