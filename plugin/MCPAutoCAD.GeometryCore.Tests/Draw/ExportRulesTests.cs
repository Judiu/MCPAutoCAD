using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>protocol.md §5G.4: reglas puras de insertar_export.</summary>
    public class ExportRulesTests
    {
        private const string NoAbsoluta = "ruta: debe ser una ruta absoluta";
        private const string NoDwg = "ruta: solo se admiten archivos .dwg";
        private const string Comodines = "ruta: no se admiten '..' ni comodines";
        private const string Fuera = "ruta: fuera de las carpetas permitidas de lps.json (carpetas_export_permitidas)";

        private static readonly string[] Patrones = { "ARM_*", "ALZ_*", "CIM_*", "Vigas" };

        [Theory]
        [InlineData(@"C:\obras\planta.dwg")]
        [InlineData(@"D:\planta.DWG")]
        [InlineData("C:/obras/planta.dwg")]
        [InlineData(@"\\servidor\obras\planta.dwg")]
        [InlineData(@"C:\obras\planta.final.dwg")]
        public void Ruta_Valida(string ruta)
        {
            Assert.Null(ExportRules.ValidatePath(ruta, null));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("planta.dwg")]
        [InlineData(@"obras\planta.dwg")]
        [InlineData(@"C:planta.dwg")]
        [InlineData(@"\planta.dwg")]
        [InlineData(@"..\planta.dwg")]
        [InlineData(@"\\servidor")]
        [InlineData(@"\\?\C:\obras\planta.dwg")]
        [InlineData(@"\\.\C:\obras\planta.dwg")]
        public void Ruta_NoAbsoluta(string ruta)
        {
            Assert.Equal(NoAbsoluta, ExportRules.ValidatePath(ruta, null));
        }

        [Fact]
        public void Ruta_Nula()
        {
            Assert.Equal(NoAbsoluta, ExportRules.ValidatePath(null!, null));
        }

        [Theory]
        [InlineData(@"C:\obras\planta.dxf")]
        [InlineData(@"C:\obras\planta")]
        [InlineData(@"C:\obras\planta.dwg.bak")]
        [InlineData(@"C:\obras\planta.dwg ")]
        public void Ruta_NoDwg(string ruta)
        {
            Assert.Equal(NoDwg, ExportRules.ValidatePath(ruta, null));
        }

        [Theory]
        [InlineData(@"C:\obras\..\planta.dwg")]
        [InlineData(@"C:\obras\.. \planta.dwg")]
        [InlineData(@"C:\obras\*.dwg")]
        [InlineData(@"C:\obras\plant?.dwg")]
        [InlineData(@"C:\obras\pl<nta.dwg")]
        [InlineData("C:\\obras\\pl\tnta.dwg")]
        [InlineData("C:\\obras\\pl\0nta.dwg")]
        [InlineData(@"C:\obras\planta:oculto.dwg")]
        public void Ruta_Insegura(string ruta)
        {
            Assert.Equal(Comodines, ExportRules.ValidatePath(ruta, null));
        }

        [Theory]
        [InlineData(@"C:\CYPE\planta.dwg", @"C:\CYPE")]
        [InlineData(@"C:\CYPE\planta.dwg", @"C:\CYPE\")]
        [InlineData(@"c:\cype\sub\planta.dwg", @"C:\CYPE")]
        [InlineData(@"C:/CYPE/planta.dwg", @"C:\CYPE")]
        [InlineData(@"C:\CYPE\planta.dwg", "C:/CYPE/")]
        [InlineData(@"\\srv\obras\planta.dwg", @"\\srv\obras")]
        [InlineData(@"C:\\CYPE\planta.dwg", @"C:\CYPE")]
        public void Carpetas_Dentro(string ruta, string carpeta)
        {
            Assert.Null(ExportRules.ValidatePath(ruta, new[] { carpeta }));
        }

        [Theory]
        [InlineData(@"C:\CYPE2\planta.dwg", @"C:\CYPE")]
        [InlineData(@"D:\CYPE\planta.dwg", @"C:\CYPE")]
        [InlineData(@"C:\planta.dwg", @"C:\CYPE")]
        [InlineData(@"\\srv\obras2\planta.dwg", @"\\srv\obras")]
        [InlineData(@"\\srv\obras\planta.dwg", @"\")]
        public void Carpetas_Fuera(string ruta, string carpeta)
        {
            Assert.Equal(Fuera, ExportRules.ValidatePath(ruta, new[] { carpeta }));
        }

        [Fact]
        public void Carpetas_VariasBastaUna()
        {
            Assert.Null(ExportRules.ValidatePath(@"D:\b\x.dwg", new[] { @"C:\a", @"D:\b" }));
        }

        [Fact]
        public void Carpetas_ListaVacia_SinLimite()
        {
            Assert.Null(ExportRules.ValidatePath(@"D:\b\x.dwg", Array.Empty<string>()));
        }

        [Fact]
        public void Carpetas_EntradaEnBlancoNoAdmiteNada()
        {
            Assert.Equal(Fuera, ExportRules.ValidatePath(@"\\srv\a\x.dwg", new[] { "  " }));
        }

        [Fact]
        public void Carpetas_ElEscapeConPuntosSeRechazaAntes()
        {
            Assert.Equal(Comodines, ExportRules.ValidatePath(@"C:\CYPE\..\Windows\x.dwg", new[] { @"C:\CYPE" }));
        }

        [Fact]
        public void BlockNameFor_Simple()
        {
            Assert.Equal("CYPE_planta baja", ExportRules.BlockNameFor(@"C:\obras\planta baja.dwg", Array.Empty<string>()));
        }

        [Fact]
        public void BlockNameFor_SoloExtensionFinal()
        {
            Assert.Equal("CYPE_planta.v2", ExportRules.BlockNameFor(@"C:\obras\planta.v2.dwg", Array.Empty<string>()));
        }

        [Fact]
        public void BlockNameFor_BarraInclinada()
        {
            Assert.Equal("CYPE_planta", ExportRules.BlockNameFor("C:/obras/planta.dwg", Array.Empty<string>()));
        }

        [Theory]
        [InlineData(@"C:\a;b.dwg", "CYPE_a_b")]
        [InlineData(@"C:\a,b=c.dwg", "CYPE_a_b_c")]
        [InlineData(@"C:\a`b.dwg", "CYPE_a_b")]
        [InlineData(@"C:\ a .dwg", "CYPE__a_")]
        [InlineData(@"C:\  a b  .dwg", "CYPE___a b__")]
        public void BlockNameFor_CaracteresSaneados(string ruta, string esperado)
        {
            Assert.Equal(esperado, ExportRules.BlockNameFor(ruta, Array.Empty<string>()));
        }

        [Fact]
        public void BlockNameFor_TodosLosProhibidos()
        {
            // Las barras son separadores de carpeta: no llegan al nombre.
            Assert.Equal("CYPE_" + new string('_', 11), ExportRules.BlockNameFor("<>\":;?*|,=`.dwg", Array.Empty<string>()));
        }

        [Fact]
        public void BlockNameFor_Vacio()
        {
            Assert.Equal("CYPE_EXPORT", ExportRules.BlockNameFor(@"C:\obras\.dwg", Array.Empty<string>()));
        }

        [Fact]
        public void BlockNameFor_RutaNula()
        {
            Assert.Equal("CYPE_EXPORT", ExportRules.BlockNameFor(null!, Array.Empty<string>()));
        }

        [Fact]
        public void BlockNameFor_RecorteA200()
        {
            string n = ExportRules.BlockNameFor(@"C:\" + new string('a', 300) + ".dwg", Array.Empty<string>());
            Assert.Equal(ExportRules.MaxBlockName, n.Length);
            Assert.StartsWith(ExportRules.Prefix, n);
        }

        [Fact]
        public void BlockNameFor_Existente_Sufijo2()
        {
            Assert.Equal("CYPE_p_2", ExportRules.BlockNameFor(@"C:\p.dwg", new[] { "CYPE_p" }));
        }

        [Fact]
        public void BlockNameFor_Existente_SinMayusculas()
        {
            Assert.Equal("CYPE_p_2", ExportRules.BlockNameFor(@"C:\p.dwg", new[] { "cype_P" }));
        }

        [Fact]
        public void BlockNameFor_Existente_Sufijo3()
        {
            Assert.Equal("CYPE_p_3", ExportRules.BlockNameFor(@"C:\p.dwg", new[] { "CYPE_p", "CYPE_P_2" }));
        }

        [Fact]
        public void BlockNameFor_SufijoCabeEn200()
        {
            string larga = @"C:\" + new string('a', 300) + ".dwg";
            string n1 = ExportRules.BlockNameFor(larga, Array.Empty<string>());
            string n2 = ExportRules.BlockNameFor(larga, new[] { n1 });
            Assert.Equal(ExportRules.MaxBlockName, n2.Length);
            Assert.EndsWith("_2", n2);
        }

        private static readonly (double, double)[] RecuadrosReales = { (59.4, 42.0), (42.0, 29.7), (84.0, 59.4), (29.7, 21.0), (53.4, 36.0) };

        [Fact]
        public void Lamina_RecuadrosRealesEnCm()
        {
            ExportRules.SheetDetection d = ExportRules.DetectSheet(RecuadrosReales, new[] { "Escala: 1:50", "Escala 1:50" });
            Assert.True(d.EsLamina);
            Assert.Equal(new[] { "A1", "A2", "A3", "A4" }, d.Formatos);
            Assert.Equal(new[] { 50 }, d.Escalas);
        }

        [Fact]
        public void Lamina_FormatoGirado()
        {
            Assert.Equal(new[] { "A3" }, ExportRules.DetectSheet(new[] { (29.7, 42.0) }, System.Array.Empty<string>()).Formatos);
        }

        [Fact]
        public void Lamina_RecuadroQueNoEsNormalizado_NoCuenta()
        {
            Assert.False(ExportRules.DetectSheet(new[] { (53.4, 36.0), (1500.0, 1500.0) }, new[] { "Escala 1:50" }).EsLamina);
        }

        [Fact]
        public void Lamina_EnMilimetrosNoSeDetecta()
        {
            Assert.False(ExportRules.DetectSheet(new[] { (841.0, 594.0) }, System.Array.Empty<string>()).EsLamina);
        }

        [Theory]
        [InlineData("Escala 1:50", 50)]
        [InlineData("Escala: 1:50", 50)]
        [InlineData("ESCALA 1/20", 20)]
        [InlineData("escala 1 : 100", 100)]
        [InlineData("Cimentación  Escala 1:75  (m)", 75)]
        public void Lamina_LecturaDeLaEscala(string texto, int n)
        {
            Assert.True(ExportRules.IsScaleText(texto));
            Assert.Equal(new[] { n }, ExportRules.DetectSheet(RecuadrosReales, new[] { texto }).Escalas);
        }

        [Theory]
        [InlineData("Escala")]
        [InlineData("1:50")]
        [InlineData("Escala gráfica")]
        [InlineData("")]
        public void Lamina_TextosSinEscala(string texto)
        {
            Assert.False(ExportRules.IsScaleText(texto));
        }

        [Fact]
        public void IsScaleText_Nulo()
        {
            Assert.False(ExportRules.IsScaleText(null));
        }

        [Fact]
        public void Lamina_EscalaCeroSeIgnora()
        {
            Assert.Empty(ExportRules.DetectSheet(RecuadrosReales, new[] { "Escala 1:0" }).Escalas);
        }

        [Fact]
        public void Lamina_CapasDeRecuadroSinMayusculas()
        {
            Assert.True(ExportRules.IsSheetLayer("recuad_limites"));
            Assert.True(ExportRules.IsSheetLayer("RECUAD_MARCO"));
            Assert.False(ExportRules.IsSheetLayer("RECUAD_OTRA"));
        }

        [Fact]
        public void Lamina_UnaEscala_RechazoLiteral()
        {
            var d = new ExportRules.SheetDetection(new[] { "A1", "A3" }, new[] { 50 });
            Assert.Equal(
                "escala_unidades: el export es una lámina de papel en centímetros (A1, A3) a escala 1:50, no un modelo a escala real: su INSUNITS no sirve y el factor automático la dejaría a una escala equivocada. Pase escala: 0.5 y escala_unidades: 'ninguna'",
                ExportRules.SheetRejection(d, 0.01));
        }

        [Fact]
        public void Lamina_VariasEscalas_LasLista()
        {
            string t = ExportRules.SheetRejection(new ExportRules.SheetDetection(new[] { "A2" }, new[] { 20, 50 }), 0.01);
            Assert.Contains("con varias escalas (1:20 → 0.2, 1:50 → 0.5)", t);
            Assert.EndsWith("Elija la del plano que vaya a usar y pase esa escala y escala_unidades: 'ninguna'", t);
        }

        [Fact]
        public void Lamina_SinEscala_LoDice()
        {
            string t = ExportRules.SheetRejection(new ExportRules.SheetDetection(new[] { "A2" }, System.Array.Empty<int>()), 0.01);
            Assert.Contains("sin ningún texto 'Escala 1:N' legible", t);
            Assert.EndsWith("Pase escala: N × 0.01 con la escala 1:N de la lámina y escala_unidades: 'ninguna'", t);
        }

        [Fact]
        public void Lamina_EscalaSugerida_DependeDeLaUnidadDelDibujo()
        {
            Assert.Equal(0.5, ExportRules.SuggestedScale(50, 0.01), 12);
            Assert.Equal(500.0, ExportRules.SuggestedScale(50, 10.0), 12);
        }

        [Fact]
        public void PlacedBox_EscalaYPosicion()
        {
            BBox2 b = ExportRules.PlacedBox(BBox2.FromCorners(0, 0, 84, 59.4), (0, 0), new[] { 0.5, 0.5, 0.5 }, 0, new Point3(10, 20, 0));
            Assert.Equal(new[] { 10.0, 20.0, 52.0, 49.7 }, new[] { b.Xmin, b.Ymin, b.Xmax, System.Math.Round(b.Ymax, 9) });
        }

        [Fact]
        public void PlacedBox_Rotacion()
        {
            BBox2 b = ExportRules.PlacedBox(BBox2.FromCorners(0, 0, 10, 2), (0, 0), new[] { 1.0, 1.0, 1.0 }, 90, new Point3(0, 0, 0));
            Assert.Equal(-2, b.Xmin, 9);
            Assert.Equal(0, b.Ymin, 9);
            Assert.Equal(0, b.Xmax, 9);
            Assert.Equal(10, b.Ymax, 9);
        }

        [Fact]
        public void PlacedBox_ConPuntoBaseYEspejo()
        {
            BBox2 b = ExportRules.PlacedBox(BBox2.FromCorners(5, 5, 7, 6), (5, 5), new[] { -1.0, 1.0, 1.0 }, 0, new Point3(100, 0, 0));
            Assert.Equal(98, b.Xmin, 9);
            Assert.Equal(100, b.Xmax, 9);
            Assert.Equal(1, b.Ymax, 9);
        }

        [Fact]
        public void Factor_MmAM()
        {
            (double? f, string? aviso) = ExportRules.Factor(4, 6);
            Assert.Equal(0.001, f!.Value, 12);
            Assert.Null(aviso);
        }

        [Fact]
        public void Factor_MismasUnidades()
        {
            Assert.Equal(1.0, ExportRules.Factor(6, 6).factor);
        }

        [Fact]
        public void Factor_SinUnidades_AdvertenciaExacta()
        {
            (double? f, string? aviso) = ExportRules.Factor(0, 6);
            Assert.Null(f);
            Assert.Equal("El export no declara unidades (INSUNITS=0): no se aplicó factor; pase escala explícita si el export está en mm.", aviso);
        }

        [Fact]
        public void Factor_SinEquivalencia_NullSinAdvertencia()
        {
            (double? f, string? aviso) = ExportRules.Factor(18, 6);
            Assert.Null(f);
            Assert.Null(aviso);
        }

        [Fact]
        public void Factor_DestinoSinEquivalencia()
        {
            Assert.Null(ExportRules.Factor(4, 0).factor);
        }

        [Fact]
        public void ClassifyLayers_Reparte()
        {
            (List<string> cype, List<string> otras) = ExportRules.ClassifyLayers(
                new[] { "ARM_1", "0", "Cotas", "alz_muro", "CIM_", "Vigas", "VIGAS2" }, Patrones);
            Assert.Equal(new[] { "ARM_1", "alz_muro", "CIM_", "Vigas" }, cype);
            Assert.Equal(new[] { "Cotas", "VIGAS2" }, otras);
        }

        [Fact]
        public void ClassifyLayers_ComodinSoloAlFinal()
        {
            (List<string> cype, _) = ExportRules.ClassifyLayers(new[] { "XARM_1" }, Patrones);
            Assert.Empty(cype);
        }

        [Fact]
        public void ClassifyLayers_SinPatrones_TodoEsOtra()
        {
            (List<string> cype, List<string> otras) = ExportRules.ClassifyLayers(new[] { "A", "0" }, Array.Empty<string>());
            Assert.Empty(cype);
            Assert.Equal(new[] { "A" }, otras);
        }

        [Fact]
        public void ClassifyLayers_PatronesNulos()
        {
            Assert.Equal(new[] { "A" }, ExportRules.ClassifyLayers(new[] { "A" }, null!).otras);
        }

        [Fact]
        public void ForeignLayersWarning_SinOtras_Null()
        {
            Assert.Null(ExportRules.ForeignLayersWarning(Array.Empty<string>()));
        }

        [Fact]
        public void ForeignLayersWarning_HastaCincoSinResto()
        {
            Assert.Equal(
                "El export trae 2 capa(s) fuera del grupo CYPE: A, B; pase filtrar_capas: \"cype\" para dejarlas fuera.",
                ExportRules.ForeignLayersWarning(new[] { "A", "B" }));
        }

        [Fact]
        public void ForeignLayersWarning_Exactamente5()
        {
            Assert.Equal(
                "El export trae 5 capa(s) fuera del grupo CYPE: A, B, C, D, E; pase filtrar_capas: \"cype\" para dejarlas fuera.",
                ExportRules.ForeignLayersWarning(new[] { "A", "B", "C", "D", "E" }));
        }

        [Fact]
        public void ForeignLayersWarning_ConResto()
        {
            Assert.Equal(
                "El export trae 7 capa(s) fuera del grupo CYPE: A, B, C, D, E (y 2 más); pase filtrar_capas: \"cype\" para dejarlas fuera.",
                ExportRules.ForeignLayersWarning(Enumerable.Range(0, 7).Select(i => ((char)('A' + i)).ToString()).ToList()));
        }

        [Fact]
        public void Constantes()
        {
            Assert.Equal("carpetas_export_permitidas", ExportRules.LpsKeyFolders);
            Assert.Equal("CYPE_", ExportRules.Prefix);
        }
    }
}
