using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class PlotMediaSelectorTests
    {
        // Lista representativa del PC3 "PublishToWeb PNG" (nombres locales tal como los muestra AutoCAD).
        private static readonly (string name, double a, double b)[] Pc3 =
        {
            ("VGA (480.00 x 640.00 Pixels)", 480, 640),
            ("SVGA (600.00 x 800.00 Pixels)", 600, 800),
            ("XGA (768.00 x 1024.00 Pixels)", 768, 1024),
            ("Sun Hi-Res (1600.00 x 1280.00 Pixels)", 1600, 1280),
            ("ultra (2048.00 x 1536.00 Pixels)", 2048, 1536),
        };

        [Theory]
        [InlineData(640, 480, "Sun Hi-Res (1600.00 x 1280.00 Pixels)")]
        [InlineData(64, 64, "VGA (480.00 x 640.00 Pixels)")]
        [InlineData(800, 600, "Sun Hi-Res (1600.00 x 1280.00 Pixels)")]
        [InlineData(700, 500, "Sun Hi-Res (1600.00 x 1280.00 Pixels)")]
        [InlineData(1024, 768, "Sun Hi-Res (1600.00 x 1280.00 Pixels)")]
        [InlineData(768, 1024, "XGA (768.00 x 1024.00 Pixels)")]
        [InlineData(1280, 1024, "Sun Hi-Res (1600.00 x 1280.00 Pixels)")]
        [InlineData(1600, 1280, "Sun Hi-Res (1600.00 x 1280.00 Pixels)")]
        [InlineData(2048, 1536, "ultra (2048.00 x 1536.00 Pixels)")]
        [InlineData(1700, 1000, "ultra (2048.00 x 1536.00 Pixels)")]
        // Una petición horizontal elige un medio horizontal aunque haya uno vertical más pequeño (ver abajo).
        public void EligeElMasPequenoQueCubre(int ancho, int alto, string esperado)
        {
            Assert.Equal(esperado, PlotMediaSelector.Choose(Pc3, ancho, alto));
        }

        // Gate Fase 1: el PC3 real trae cada tamaño en las dos orientaciones; el primer número es el ancho
        // de la imagen. Con 640x480 se elegía "480 x 640" (vertical) y la ventana salía comprimida.
        private static readonly (string, double, double)[] Pc3AmbasOrientaciones =
        {
            ("VGA_(480.00_x_640.00_Pixels)", 480, 640),
            ("VGA_(640.00_x_480.00_Pixels)", 640, 480),
            ("XGA_(768.00_x_1024.00_Pixels)", 768, 1024),
            ("XGA_(1024.00_x_768.00_Pixels)", 1024, 768),
        };

        [Theory]
        [InlineData(640, 480, "VGA_(640.00_x_480.00_Pixels)")]
        [InlineData(480, 640, "VGA_(480.00_x_640.00_Pixels)")]
        [InlineData(1024, 768, "XGA_(1024.00_x_768.00_Pixels)")]
        [InlineData(256, 1024, "XGA_(768.00_x_1024.00_Pixels)")]
        public void Choose_PrefiereLaMismaOrientacion(int ancho, int alto, string esperado)
        {
            Assert.Equal(esperado, PlotMediaSelector.Choose(Pc3AmbasOrientaciones, ancho, alto));
        }

        [Theory]
        [InlineData(2048, 2048)]
        [InlineData(2000, 1600)]
        public void NingunoCubre_EligeElDeMayorArea(int ancho, int alto)
        {
            Assert.Equal("ultra (2048.00 x 1536.00 Pixels)", PlotMediaSelector.Choose(Pc3, ancho, alto));
        }

        [Fact]
        public void ElOrdenDeLaListaNoImporta()
        {
            var reversed = new List<(string, double, double)>(Pc3);
            reversed.Reverse();
            Assert.Equal("Sun Hi-Res (1600.00 x 1280.00 Pixels)", PlotMediaSelector.Choose(reversed, 1000, 700));
        }

        [Fact]
        public void ListaVacia_Null()
        {
            Assert.Null(PlotMediaSelector.Choose(Array.Empty<(string, double, double)>(), 100, 100));
        }

        [Fact]
        public void DimensionesInvalidas_SeIgnoran()
        {
            var media = new[] { ("raro", 0.0, 0.0), ("nan", double.NaN, 5.0), ("SVGA", 600.0, 800.0) };
            Assert.Equal("SVGA", PlotMediaSelector.Choose(media, 100, 100));
        }

        [Fact]
        public void ListaNula_Lanza()
        {
            Assert.Throws<ArgumentNullException>(() => PlotMediaSelector.Choose(null!, 1, 1));
        }

        [Theory]
        [InlineData("XGA (768.00 x 1024.00 Pixels)", 768, 1024)]
        [InlineData("Sun Hi-Res (1600.00 x 1280.00 Pixels)", 1600, 1280)]
        [InlineData("VGA_(480.00_x_640.00_Pixels)", 480, 640)]
        [InlineData("UserDefinedRaster (1024.00 x 768.00Pixels)", 1024, 768)]
        [InlineData("1024x768", 1024, 768)]
        [InlineData("SVGA (600,00 x 800,00 Píxeles)", 600, 800)]
        public void ParseCanonical_ExtraeLosDosNumeros(string nombre, double a, double b)
        {
            (double a, double b)? parsed = PlotMediaSelector.ParseCanonical(nombre);
            Assert.NotNull(parsed);
            Assert.Equal(a, parsed!.Value.a);
            Assert.Equal(b, parsed.Value.b);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Letter")]
        [InlineData("0.00 x 640.00")]
        public void ParseCanonical_SinPar_Null(string? nombre)
        {
            Assert.Null(PlotMediaSelector.ParseCanonical(nombre));
        }

        [Fact]
        public void ParseCanonical_NoFiltraPorUnidad()
        {
            Assert.Equal((8.5, 11.0), PlotMediaSelector.ParseCanonical("ANSI_A_(8.50_x_11.00_Inches)"));
        }
    }
}
