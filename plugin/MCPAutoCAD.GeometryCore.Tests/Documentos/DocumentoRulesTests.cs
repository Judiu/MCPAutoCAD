using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Documentos;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Documentos
{
    /// <summary>Reglas puras de guardar / abrir_documento / activar_documento (búsqueda entre abiertos, carpetas de solo lectura).</summary>
    public class DocumentoRulesTests
    {
        private static readonly IReadOnlyList<DocumentoRef> Abiertos = new List<DocumentoRef>
        {
            new DocumentoRef("PLANTA.dwg", @"C:\obras\a\PLANTA.dwg", true),
            new DocumentoRef("ALZADO.dwg", @"C:\obras\a\ALZADO.dwg", false),
            new DocumentoRef("Drawing1.dwg", null, false),
            new DocumentoRef("PLANTA.dwg", @"O:\otra\PLANTA.dwg", false),
        };

        [Fact]
        public void Buscar_PorNombreUnico_Encuentra()
        {
            ResultadoConsulta r = DocumentoRules.Buscar(Abiertos, "alzado.dwg");
            Assert.Equal(ResultadoBusqueda.Encontrado, r.Estado);
            Assert.Equal(1, r.Indice);
        }

        [Fact]
        public void Buscar_SinExtension_Encuentra()
        {
            ResultadoConsulta r = DocumentoRules.Buscar(Abiertos, "Drawing1");
            Assert.Equal(ResultadoBusqueda.Encontrado, r.Estado);
            Assert.Equal(2, r.Indice);
        }

        [Fact]
        public void Buscar_NombreRepetido_EsAmbiguoConCandidatos()
        {
            ResultadoConsulta r = DocumentoRules.Buscar(Abiertos, "PLANTA");
            Assert.Equal(ResultadoBusqueda.Ambiguo, r.Estado);
            Assert.Null(r.Indice);
            Assert.Equal(new[] { 0, 3 }, r.Candidatos);
        }

        [Theory]
        [InlineData(@"C:\obras\a\PLANTA.dwg")]
        [InlineData("C:/obras/a/planta.DWG")]
        public void Buscar_PorRuta_EligeElDocumentoExacto(string ruta)
        {
            ResultadoConsulta r = DocumentoRules.Buscar(Abiertos, ruta);
            Assert.Equal(ResultadoBusqueda.Encontrado, r.Estado);
            Assert.Equal(0, r.Indice);
        }

        [Fact]
        public void Buscar_RutaQueNoEstaAbierta_NoEncuentra()
        {
            ResultadoConsulta r = DocumentoRules.Buscar(Abiertos, @"D:\nada\PLANTA.dwg");
            Assert.Equal(ResultadoBusqueda.NoEncontrado, r.Estado);
            Assert.Empty(r.Candidatos);
        }

        [Fact]
        public void Buscar_ConsultaVacia_NoEncuentra()
        {
            Assert.Equal(ResultadoBusqueda.NoEncontrado, DocumentoRules.Buscar(Abiertos, "   ").Estado);
        }

        [Fact]
        public void Buscar_DibujoSinGuardar_NoCoincideConRuta()
        {
            ResultadoConsulta r = DocumentoRules.Buscar(Abiertos, @"C:\Drawing1.dwg");
            Assert.Equal(ResultadoBusqueda.NoEncontrado, r.Estado);
        }

        [Theory]
        [InlineData(@"O:\Planos\x\DIBUJO.dwg", true)]
        [InlineData(@"o:\planos\DIBUJO.dwg", true)]
        [InlineData(@"O:\Planos\DIBUJO.dwg", true)]
        [InlineData(@"O:\Planos2\DIBUJO.dwg", false)]
        [InlineData(@"O:\Otra\DIBUJO.dwg", false)]
        [InlineData(@"C:\Planos\DIBUJO.dwg", false)]
        public void EstaEnCarpeta_RespetaLimitesDeCarpeta(string ruta, bool esperado)
        {
            Assert.Equal(esperado, DocumentoRules.EstaEnCarpeta(ruta, new[] { @"O:\Planos" }));
        }

        [Fact]
        public void EstaEnCarpeta_IgnoraEntradasVaciasYListaVacia()
        {
            Assert.False(DocumentoRules.EstaEnCarpeta(@"O:\Planos\DIBUJO.dwg", new[] { "", "  " }));
            Assert.False(DocumentoRules.EstaEnCarpeta(@"O:\Planos\DIBUJO.dwg", new string[0]));
            Assert.True(DocumentoRules.EstaEnCarpeta(@"O:\Planos\DIBUJO.dwg", new[] { "", @"O:\Planos" }));
        }

        [Theory]
        [InlineData(@"O:\Planos\", @"O:\Planos\x\D.dwg", true)]
        [InlineData(@"O:\Planos", @"O:\Planos\x\D.dwg", true)]
        [InlineData("O:/Planos/", @"O:\Planos\D.dwg", true)]
        [InlineData(@"O:\Planos\\", @"O:\Planos\D.dwg", true)]
        [InlineData(@"O:\", @"O:\cualquiera\D.dwg", true)]
        [InlineData(@"O:", @"O:\D.dwg", true)]
        [InlineData(@"O:\", @"C:\D.dwg", false)]
        [InlineData(@"\\srv\obras", @"\\srv\obras\D.dwg", true)]
        [InlineData(@"\\srv\obras", @"\\srv\obras2\D.dwg", false)]
        [InlineData(@"\\srv\obras", @"\\otro\obras\D.dwg", false)]
        [InlineData(@"O:\Planos", @"O:\Planos", false)]
        [InlineData(@"O:\Planos", @"O:\Planos\..\Otra\D.dwg", false)]
        [InlineData(@"O:\Planos", @"O:\Planos\..\Planos\D.dwg", true)]
        [InlineData(@"O:\Planos", @"O:\Planos\.\D.dwg", true)]
        public void EstaEnCarpeta_CasosDeFormaDeRuta(string carpeta, string ruta, bool esperado)
        {
            Assert.Equal(esperado, DocumentoRules.EstaEnCarpeta(ruta, new[] { carpeta }));
        }

        [Theory]
        [InlineData(@"C:\obras\a\..\a\PLANTA.dwg")]
        [InlineData(@"C:\obras\.\a\PLANTA.dwg")]
        [InlineData(@"C:\obras\\a\PLANTA.dwg")]
        [InlineData(@"c:/obras//a/planta.dwg")]
        public void Buscar_RutaNoCanonica_EncuentraElDocumento(string consulta)
        {
            var docs = new List<DocumentoRef> { new DocumentoRef("PLANTA.dwg", @"C:\obras\a\PLANTA.dwg", true) };
            ResultadoConsulta r = DocumentoRules.Buscar(docs, consulta);
            Assert.Equal(ResultadoBusqueda.Encontrado, r.Estado);
            Assert.Equal(0, r.Indice);
        }

        [Theory]
        [InlineData(@"O:\PLANOS~1\D.dwg", true)]
        [InlineData(@"O:\Planos\D~1.dwg", true)]
        [InlineData(@"O:\Planos\Obra~\D.dwg", false)]
        [InlineData(@"O:\Planos\Diseño Final\D.dwg", false)]
        [InlineData(@"O:\Planos\D.dwg", false)]
        public void TieneNombreCorto_DetectaNombresDe8Punto3(string ruta, bool esperado)
        {
            Assert.Equal(esperado, DocumentoRules.TieneNombreCorto(ruta));
        }

        [Fact]
        public void TieneNombreCorto_NuloNoEsCorto()
        {
            Assert.False(DocumentoRules.TieneNombreCorto(null));
        }

        [Fact]
        public void NombresAutoCarga_IncluyeAcadYElMnlDelDibujo()
        {
            var nombres = DocumentoRules.NombresAutoCarga("PLANTA");
            Assert.Equal(8, nombres.Count);
            Assert.Contains("acaddoc.lsp", nombres);
            Assert.Contains("acaddoc.fas", nombres);
            Assert.Contains("acaddoc.vlx", nombres);
            Assert.Contains("acad.lsp", nombres);
            Assert.Contains("acad.fas", nombres);
            Assert.Contains("acad.vlx", nombres);
            Assert.Contains("acad.mnl", nombres);
            Assert.Contains("PLANTA.mnl", nombres);
        }

        [Fact]
        public void Canonica_ResuelveDotYDotDotSinSubirDeLaRaiz()
        {
            Assert.Equal(@"C:\a\c", DocumentoRules.Canonica(@"C:\a\b\..\.\c"));
            Assert.Equal(@"C:\x", DocumentoRules.Canonica(@"C:\..\..\x"));
            Assert.Equal(@"\\srv\r\f", DocumentoRules.Canonica(@"\\srv\r\\.\f\"));
            Assert.Equal(@"C:\", DocumentoRules.Canonica("C:"));
        }
    }
}
