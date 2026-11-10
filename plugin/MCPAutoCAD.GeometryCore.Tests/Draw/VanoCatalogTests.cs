using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>protocol.md §5G.1: catálogo biblioteca_vanos, con el lps.json real del repositorio (respaldo embebido si no está).</summary>
    public class VanoCatalogTests
    {
        private static IReadOnlyList<VanoBlockSpec> ParseCatalogo(string entrada, List<string> avisos)
        {
            using JsonDocument d = JsonDocument.Parse("{\"biblioteca_vanos\":{\"B\":{\"tipo\":\"ventana\",\"eje_ancho\":\"y\",\"direccion_espesor\":\"-x\",\"ancho_nominal\":0.3,\"espesor_nominal\":0.12" + entrada + "}}}");
            return VanoCatalog.Parse(d.RootElement.Clone(), avisos);
        }

        [Fact]
        public void AjusteAncho_PorDefectoCero()
        {
            VanoBlockSpec s = ParseCatalogo("", new List<string>())[0];
            Assert.Equal(0.0, s.AjusteAncho);
            Assert.Equal(0.8, s.ValorPropiedad(0.8));
        }

        [Fact]
        public void AjusteAncho_SeSumaAlValorDeLaPropiedadYAlNominal()
        {
            VanoBlockSpec s = ParseCatalogo(",\"ajuste_ancho\":0.0045", new List<string>())[0];
            Assert.Equal(1.2045, s.ValorPropiedad(1.2), 12);
            Assert.Equal(0.3045, s.ValorNominal, 12);
            Assert.Equal(0.3, s.AnchoNominal);
        }

        [Fact]
        public void AjusteAncho_NoNumerico_EntradaIgnorada()
        {
            var avisos = new List<string>();
            Assert.Empty(ParseCatalogo(",\"ajuste_ancho\":\"x\"", avisos));
            Assert.Equal("biblioteca_vanos['B']: entrada ignorada (ajuste_ancho debe ser un número)", Assert.Single(avisos));
        }

        private const string FallbackJson = @"{ ""biblioteca_vanos"": {
  ""Puerta Dinamica LM"": { ""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 0.9, ""espesor_nominal"": 0.13, ""propiedad_ancho"": null, ""capa"": ""puertas"" },
  ""PuertaBaño"": { ""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 0.65, ""espesor_nominal"": 0.1, ""propiedad_ancho"": null, ""capa"": ""puertas"" },
  ""VENTANA01"": { ""tipo"": ""ventana"", ""eje_ancho"": ""y"", ""direccion_espesor"": ""-x"", ""ancho_nominal"": 0.3, ""espesor_nominal"": 0.12, ""propiedad_ancho"": null, ""capa"": ""ventanas"" }
} }";

        private const string Valida =
            @"""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 0.9, ""espesor_nominal"": 0.13";

        private static readonly Lazy<IReadOnlyList<VanoBlockSpec>> Real = new Lazy<IReadOnlyList<VanoBlockSpec>>(LoadReal);

        private static IReadOnlyList<VanoBlockSpec> LoadReal()
        {
            string? path = null;
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && path == null)
            {
                string candidate = Path.Combine(dir.FullName, "standards", "lps.json");
                path = File.Exists(candidate) ? candidate : null;
                dir = dir.Parent;
            }

            using JsonDocument doc = JsonDocument.Parse(path != null ? File.ReadAllText(path) : FallbackJson);
            var avisos = new List<string>();
            IReadOnlyList<VanoBlockSpec> specs = VanoCatalog.Parse(doc.RootElement, avisos);
            Assert.Empty(avisos);
            return specs;
        }

        private static (IReadOnlyList<VanoBlockSpec> specs, List<string> avisos) Parse(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var avisos = new List<string>();
            return (VanoCatalog.Parse(doc.RootElement, avisos), avisos);
        }

        /// <summary>Catálogo con una entrada "B" de campos dados.</summary>
        private static (IReadOnlyList<VanoBlockSpec> specs, List<string> avisos) One(string campos) =>
            Parse(@"{ ""biblioteca_vanos"": { ""B"": { " + campos + " } } }");

        [Fact]
        public void Real_TresEntradasEnOrden()
        {
            Assert.Equal(new[] { "Puerta Dinamica LM", "PuertaBaño", "VENTANA01" }, Real.Value.Select(s => s.Bloque));
        }

        [Fact]
        public void Real_PuertaDinamica()
        {
            VanoBlockSpec s = Real.Value[0];
            Assert.Equal("puerta", s.Tipo);
            Assert.Equal("x", s.EjeAncho);
            Assert.Equal("+y", s.DireccionEspesor);
            Assert.Equal(0.9, s.AnchoNominal);
            Assert.Equal(0.13, s.EspesorNominal);
            Assert.Equal("DistanceGeneralVano", s.PropiedadAncho);
            Assert.Equal(0.0, s.AjusteAncho);
            Assert.Equal("puertas", s.Concepto);
        }

        [Fact]
        public void Real_PuertaBano()
        {
            VanoBlockSpec s = Real.Value[1];
            Assert.Equal("x", s.EjeAncho);
            Assert.Equal("+y", s.DireccionEspesor);
            Assert.Equal(0.65, s.AnchoNominal);
            Assert.Equal(0.1, s.EspesorNominal);
        }

        [Fact]
        public void Real_Ventana()
        {
            VanoBlockSpec s = Real.Value[2];
            Assert.Equal("ventana", s.Tipo);
            Assert.Equal("y", s.EjeAncho);
            Assert.Equal("-x", s.DireccionEspesor);
            Assert.Equal(0.3, s.AnchoNominal);
            Assert.Equal(0.12, s.EspesorNominal);
            Assert.Equal("ventanas", s.Concepto);
        }

        [Fact]
        public void Real_DefaultFor()
        {
            Assert.Equal("Puerta Dinamica LM", VanoCatalog.DefaultFor(Real.Value, "puerta")!.Bloque);
            Assert.Equal("VENTANA01", VanoCatalog.DefaultFor(Real.Value, "ventana")!.Bloque);
        }

        [Fact]
        public void DefaultFor_Hueco_Null()
        {
            Assert.Null(VanoCatalog.DefaultFor(Real.Value, "hueco"));
        }

        [Theory]
        [InlineData("ventana01", "VENTANA01")]
        [InlineData("PUERTABAÑO", "PuertaBaño")]
        [InlineData("puerta dinamica lm", "Puerta Dinamica LM")]
        public void Find_SinMayusculas(string nombre, string esperado)
        {
            Assert.Equal(esperado, VanoCatalog.Find(Real.Value, nombre)!.Bloque);
        }

        [Fact]
        public void Find_Inexistente()
        {
            Assert.Null(VanoCatalog.Find(Real.Value, "TANQUE 1000 LTS"));
        }

        [Fact]
        public void SinRaiz_ListaVaciaSinAdvertencia()
        {
            var avisos = new List<string>();
            Assert.Empty(VanoCatalog.Parse(null, avisos));
            Assert.Empty(avisos);
        }

        [Fact]
        public void SinClave_ListaVaciaSinAdvertencia()
        {
            (IReadOnlyList<VanoBlockSpec> specs, List<string> avisos) = Parse("{}");
            Assert.Empty(specs);
            Assert.Empty(avisos);
        }

        [Fact]
        public void ClaveQueNoEsObjeto_NoLanza()
        {
            (IReadOnlyList<VanoBlockSpec> specs, List<string> avisos) = Parse(@"{ ""biblioteca_vanos"": [1] }");
            Assert.Empty(specs);
            Assert.Single(avisos);
        }

        [Fact]
        public void EntradaValida_CapaPorDefecto_Puerta()
        {
            (IReadOnlyList<VanoBlockSpec> specs, List<string> avisos) = One(Valida);
            Assert.Empty(avisos);
            Assert.Equal("puertas", specs[0].Concepto);
            Assert.Null(specs[0].PropiedadAncho);
        }

        [Fact]
        public void EntradaValida_CapaPorDefecto_Ventana()
        {
            (IReadOnlyList<VanoBlockSpec> specs, _) = One(
                @"""tipo"": ""ventana"", ""eje_ancho"": ""y"", ""direccion_espesor"": ""-x"", ""ancho_nominal"": 0.3, ""espesor_nominal"": 0.12");
            Assert.Equal("ventanas", specs[0].Concepto);
        }

        [Fact]
        public void EntradaValida_PropiedadYCapa()
        {
            (IReadOnlyList<VanoBlockSpec> specs, _) = One(Valida + @", ""propiedad_ancho"": ""Ancho"", ""capa"": ""muebles""");
            Assert.Equal("Ancho", specs[0].PropiedadAncho);
            Assert.Equal("muebles", specs[0].Concepto);
        }

        [Fact]
        public void EntradaNoObjeto_SeIgnora()
        {
            (IReadOnlyList<VanoBlockSpec> specs, List<string> avisos) = Parse(@"{ ""biblioteca_vanos"": { ""B"": 3 } }");
            Assert.Empty(specs);
            Assert.Equal("biblioteca_vanos['B']: entrada ignorada (no es un objeto)", avisos.Single());
        }

        [Theory]
        [InlineData(@"""tipo"": ""hueco"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 1, ""espesor_nominal"": 1", "tipo debe ser puerta o ventana")]
        [InlineData(@"""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 1, ""espesor_nominal"": 1", "tipo debe ser puerta o ventana")]
        [InlineData(@"""tipo"": ""puerta"", ""eje_ancho"": ""z"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 1, ""espesor_nominal"": 1", "eje_ancho debe ser x o y")]
        [InlineData(@"""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""y"", ""ancho_nominal"": 1, ""espesor_nominal"": 1", "direccion_espesor debe ser +x, -x, +y o -y")]
        [InlineData(@"""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""-x"", ""ancho_nominal"": 1, ""espesor_nominal"": 1", "eje_ancho y direccion_espesor son colineales")]
        [InlineData(@"""tipo"": ""puerta"", ""eje_ancho"": ""y"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 1, ""espesor_nominal"": 1", "eje_ancho y direccion_espesor son colineales")]
        [InlineData(@"""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 0, ""espesor_nominal"": 1", "ancho_nominal debe ser un número mayor que 0")]
        [InlineData(@"""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": ""0.9"", ""espesor_nominal"": 1", "ancho_nominal debe ser un número mayor que 0")]
        [InlineData(@"""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 1, ""espesor_nominal"": -1", "espesor_nominal debe ser un número mayor que 0")]
        [InlineData(@"""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 1, ""espesor_nominal"": 1, ""propiedad_ancho"": 5", "propiedad_ancho debe ser una cadena o null")]
        [InlineData(@"""tipo"": ""puerta"", ""eje_ancho"": ""x"", ""direccion_espesor"": ""+y"", ""ancho_nominal"": 1, ""espesor_nominal"": 1, ""capa"": 5", "capa debe ser una cadena")]
        public void EntradaInvalida_SeIgnoraConMotivo(string campos, string motivo)
        {
            (IReadOnlyList<VanoBlockSpec> specs, List<string> avisos) = One(campos);
            Assert.Empty(specs);
            Assert.Equal("biblioteca_vanos['B']: entrada ignorada (" + motivo + ")", avisos.Single());
        }

        [Fact]
        public void EntradaInvalida_NoImpideLasDemas()
        {
            (IReadOnlyList<VanoBlockSpec> specs, List<string> avisos) = Parse(
                @"{ ""biblioteca_vanos"": { ""Mala"": { ""tipo"": ""x"" }, ""Buena"": { " + Valida + " } } }");
            Assert.Equal("Buena", specs.Single().Bloque);
            Assert.Single(avisos);
        }

        [Theory]
        [InlineData("x", 1, 0)]
        [InlineData("y", 0, 1)]
        public void AxisVector_Valores(string eje, double x, double y)
        {
            Assert.Equal((x, y), VanoCatalog.AxisVector(eje));
        }

        [Fact]
        public void AxisVector_Invalido()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => VanoCatalog.AxisVector("z"));
            Assert.Equal("eje_ancho: debe ser x o y", e.Message);
        }

        [Theory]
        [InlineData("+x", 1, 0)]
        [InlineData("-x", -1, 0)]
        [InlineData("+y", 0, 1)]
        [InlineData("-y", 0, -1)]
        public void ThicknessVector_Valores(string d, double x, double y)
        {
            Assert.Equal((x, y), VanoCatalog.ThicknessVector(d));
        }

        [Fact]
        public void ThicknessVector_Invalido()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => VanoCatalog.ThicknessVector("x"));
            Assert.Equal("direccion_espesor: debe ser +x, -x, +y o -y", e.Message);
        }
    }
}
