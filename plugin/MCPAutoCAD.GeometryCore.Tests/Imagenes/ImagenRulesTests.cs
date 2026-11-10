using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MCPAutoCAD.GeometryCore.Imagenes;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Imagenes
{
    /// <summary>Reglas puras de insertar_imagen (fallo 24): ruta, tamaño, copia, clave de diccionario, marco.</summary>
    public class ImagenRulesTests
    {
        [Theory]
        [InlineData(@"C:\imagenes\logo.png")]
        [InlineData(@"O:\LPS\LOGO.PNG")]
        [InlineData("D:/logos/logo.jpeg")]
        [InlineData(@"C:\a b\logo.tif")]
        [InlineData(@"C:\a\logo.TIFF")]
        [InlineData(@"C:\a\logo.bmp")]
        [InlineData(@"C:\a\logo.jpg")]
        public void ValidarRutaOrigen_AceptaRutasAbsolutasConExtensionPermitida(string ruta)
        {
            Assert.Null(ImagenRules.ValidarRutaOrigen(ruta));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("logo.png")]
        [InlineData(@"imagenes\logo.png")]
        [InlineData(@"C:logo.png")]
        [InlineData(@"\logo.png")]
        [InlineData(@"C:\a\..\logo.png")]
        [InlineData(@"C:\a\*.png")]
        [InlineData(@"C:\a\logo?.png")]
        [InlineData(@"C:\a\logo|x.png")]
        [InlineData(@"C:\a\logo<x>.png")]
        [InlineData("C:\\a\\logo\x01.png")]
        [InlineData(@"C:\a\logo:x.png")]
        [InlineData(@"C:\a\logo.gif")]
        [InlineData(@"C:\a\logo.pdf")]
        [InlineData(@"C:\a\logo")]
        [InlineData(@"C:\a\carpeta\")]
        public void ValidarRutaOrigen_RechazaRutasNoValidas(string? ruta)
        {
            string? detalle = ImagenRules.ValidarRutaOrigen(ruta);
            Assert.NotNull(detalle);
            Assert.StartsWith("ruta: ", detalle);
        }

        [Theory]
        [InlineData(@"\\servidor\obras\logo.png")]
        [InlineData(@"\\?\C:\logo.png")]
        [InlineData("//servidor/obras/logo.png")]
        public void ValidarRutaOrigen_RechazaRedUnc(string ruta)
        {
            Assert.Contains("UNC", ImagenRules.ValidarRutaOrigen(ruta));
        }

        [Theory]
        [InlineData(".png", true)]
        [InlineData(".PNG", true)]
        [InlineData(".Jpeg", true)]
        [InlineData(".tif", true)]
        [InlineData(".gif", false)]
        [InlineData(".dwg", false)]
        [InlineData("", false)]
        public void ExtensionPermitida_SinDistinguirMayusculas(string ext, bool esperado)
        {
            Assert.Equal(esperado, ImagenRules.ExtensionPermitida("logo" + ext));
        }

        [Fact]
        public void ValidarTamano_LimitaA50MB()
        {
            Assert.NotNull(ImagenRules.ValidarTamano(0));
            Assert.NotNull(ImagenRules.ValidarTamano(-1));
            Assert.Null(ImagenRules.ValidarTamano(1));
            Assert.Null(ImagenRules.ValidarTamano(ImagenRules.TamanoMaximoBytes));
            Assert.NotNull(ImagenRules.ValidarTamano(ImagenRules.TamanoMaximoBytes + 1));
        }

        [Theory]
        [InlineData(@"C:\a\logo.png", "logo.png")]
        [InlineData("C:/a/logo.png", "logo.png")]
        [InlineData("logo.png", "logo.png")]
        public void NombreArchivo_AceptaAmbosSeparadores(string ruta, string esperado)
        {
            Assert.Equal(esperado, ImagenRules.NombreArchivo(ruta));
        }

        [Fact]
        public void ResolverCopia_NombreLibre_NoReutiliza()
        {
            DecisionCopia? d = ImagenRules.ResolverCopia("logo.png", _ => false, _ => false);
            Assert.Equal(new DecisionCopia("logo.png", false), d);
        }

        [Fact]
        public void ResolverCopia_MismoContenido_Reutiliza()
        {
            var existentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "logo.png" };
            DecisionCopia? d = ImagenRules.ResolverCopia("logo.png", existentes.Contains, _ => true);
            Assert.Equal(new DecisionCopia("logo.png", true), d);
        }

        [Fact]
        public void ResolverCopia_ContenidoDistinto_UsaSufijo()
        {
            var existentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "logo.png" };
            DecisionCopia? d = ImagenRules.ResolverCopia("logo.png", existentes.Contains, _ => false);
            Assert.Equal(new DecisionCopia("logo_2.png", false), d);
        }

        [Fact]
        public void ResolverCopia_SufijoOcupadoConMismoContenido_ReutilizaElSufijo()
        {
            var existentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "logo.png", "logo_2.png", "logo_3.png" };
            // logo.png y logo_2.png difieren; logo_3.png es idéntico: se reutiliza y no se crea logo_4.
            DecisionCopia? d = ImagenRules.ResolverCopia("logo.png", existentes.Contains, c => c.Equals("logo_3.png", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(new DecisionCopia("logo_3.png", true), d);
        }

        [Fact]
        public void ResolverCopia_NuncaSobrescribe_ConsultaCadaCandidato()
        {
            var existentes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "logo.png", "logo_2.png" };
            var consultados = new List<string>();
            DecisionCopia? d = ImagenRules.ResolverCopia(
                "logo.png",
                c =>
                {
                    consultados.Add(c);
                    return existentes.Contains(c);
                },
                _ => false);
            Assert.Equal(new DecisionCopia("logo_3.png", false), d);
            Assert.Equal(new[] { "logo.png", "logo_2.png", "logo_3.png" }, consultados);
        }

        [Fact]
        public void ResolverCopia_AgotaSufijos_DevuelveNull()
        {
            Assert.Null(ImagenRules.ResolverCopia("logo.png", _ => true, _ => false));
        }

        [Fact]
        public void RutaRelativa_ApuntaAImagenesJuntoAlDwg()
        {
            Assert.Equal(@".\imagenes\logo.png", ImagenRules.RutaRelativa("logo.png"));
        }

        [Fact]
        public void ClaveDiccionario_SustituyeCaracteresNoAdmitidos()
        {
            Assert.Equal("MCP_IMG_logo_empresa", ImagenRules.ClaveDiccionario("logo empresa.png", Array.Empty<string>()));
            Assert.Equal("MCP_IMG_logo_v2", ImagenRules.ClaveDiccionario("logo.v2.png", Array.Empty<string>()));
        }

        [Fact]
        public void ClaveDiccionario_AnadeSufijoSiYaExisteSinDistinguirMayusculas()
        {
            Assert.Equal("MCP_IMG_logo_2", ImagenRules.ClaveDiccionario("logo.png", new[] { "MCP_IMG_LOGO" }));
            Assert.Equal("MCP_IMG_logo_3", ImagenRules.ClaveDiccionario("logo.png", new[] { "MCP_IMG_logo", "mcp_img_logo_2" }));
        }

        [Fact]
        public void ClaveDiccionario_TruncaNombresLargos()
        {
            string clave = ImagenRules.ClaveDiccionario(new string('x', 300) + ".png", Array.Empty<string>());
            Assert.True(clave.Length <= 120);
            Assert.StartsWith("MCP_IMG_", clave);
        }

        [Fact]
        public void Marco_Rotacion0_AlturaPorProporcion()
        {
            MarcoImagen m = ImagenRules.Marco(200, 0, 400, 100);
            Assert.Equal(50, m.Alto, 9);
            Assert.Equal(200, m.UX, 9);
            Assert.Equal(0, m.UY, 9);
            Assert.Equal(0, m.VX, 9);
            Assert.Equal(50, m.VY, 9);
            Assert.Equal((10.0, 20.0, 210.0, 70.0), Redondea(m.Caja(10, 20)));
        }

        [Fact]
        public void Marco_Rotacion90_EjesPerpendicularesExactos()
        {
            MarcoImagen m = ImagenRules.Marco(200, 90, 400, 100);
            Assert.Equal(0, m.UX);
            Assert.Equal(200, m.UY);
            Assert.Equal(-50, m.VX);
            Assert.Equal(0, m.VY);
            Assert.Equal((-50.0, 0.0, 0.0, 200.0), Redondea(m.Caja(0, 0)));
        }

        [Fact]
        public void Marco_Rotacion180_NoDejaResiduos()
        {
            MarcoImagen m = ImagenRules.Marco(10, 180, 1, 1);
            Assert.Equal(-10, m.UX);
            Assert.Equal(0, m.UY);
            Assert.Equal(0, m.VX);
            Assert.Equal(-10, m.VY);
        }

        [Theory]
        [InlineData(0, 10, 10)]
        [InlineData(-1, 10, 10)]
        [InlineData(double.NaN, 10, 10)]
        [InlineData(double.PositiveInfinity, 10, 10)]
        [InlineData(5, 0, 10)]
        [InlineData(5, 10, -1)]
        public void Marco_RechazaDatosInvalidos(double ancho, int px, int py)
        {
            Assert.ThrowsAny<ArgumentOutOfRangeException>(() => ImagenRules.Marco(ancho, 0, px, py));
        }

        [Fact]
        public void MismaRuta_IgnoraMayusculasYBarras()
        {
            Assert.True(ImagenRules.MismaRuta(@"C:\a\Logo.png", "c:/a/logo.PNG"));
            Assert.False(ImagenRules.MismaRuta(@"C:\a\logo.png", @"C:\b\logo.png"));
            Assert.False(ImagenRules.MismaRuta(null, @"C:\a\logo.png"));
        }

        [Fact]
        public void Sha256Hex_VectorConocido()
        {
            Assert.Equal(
                "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                ImagenRules.Sha256Hex(Encoding.ASCII.GetBytes("abc")));
        }

        private static (double, double, double, double) Redondea((double a, double b, double c, double d) t) =>
            (Math.Round(t.a, 9), Math.Round(t.b, 9), Math.Round(t.c, 9), Math.Round(t.d, 9));
    }
}
