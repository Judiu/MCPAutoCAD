using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class XrefRulesTests
    {
        private const string Host = @"C:\Proyectos\Maestro\MAESTRO.dwg";

        [Theory]
        [InlineData(@"C:\Proyectos\Maestro\PLANTA_01.dwg", @".\PLANTA_01.dwg")]
        [InlineData(@"C:\Proyectos\Maestro\sub\PLANTA_01.dwg", @".\sub\PLANTA_01.dwg")]
        [InlineData(@"C:\Proyectos\Maestro\sub\deep\PLANTA_01.dwg", @".\sub\deep\PLANTA_01.dwg")]
        [InlineData(@"C:\Proyectos\Planos\PLANTA_01.dwg", @"..\Planos\PLANTA_01.dwg")]
        [InlineData(@"C:\Proyectos\PLANTA_01.dwg", @"..\PLANTA_01.dwg")]
        [InlineData(@"C:\PLANTA_01.dwg", @"..\..\PLANTA_01.dwg")]
        [InlineData(@"C:\Otro\x\PLANTA_01.dwg", @"..\..\Otro\x\PLANTA_01.dwg")]
        public void RutaRelativa_CasosDeCarpeta(string destino, string esperada)
        {
            Assert.Equal(esperada, XrefRules.RelativePath(Host, destino));
        }

        [Fact]
        public void RutaRelativa_IgnoraMayusculasYAceptaBarrasDeUnix()
        {
            Assert.Equal(@".\MAESTRO\Sub\x.dwg", XrefRules.RelativePath(@"c:\proyectos\maestro.dwg", @"C:/Proyectos/MAESTRO/Sub/x.dwg"));
        }

        [Fact]
        public void RutaRelativa_HostEnRaiz_UsaPrefijoPunto()
        {
            Assert.Equal(@".\p.dwg", XrefRules.RelativePath(@"C:\host.dwg", @"C:\p.dwg"));
            Assert.Equal(@".\x\p.dwg", XrefRules.RelativePath(@"C:\host.dwg", @"C:\x\p.dwg"));
        }

        [Fact]
        public void RutaRelativa_NuncaSeEscribeConPrefijoEnSubida()
        {
            Assert.Equal(@"..\p.dwg", XrefRules.RelativePath(Host, @"C:\Proyectos\p.dwg"));
        }

        [Fact]
        public void RutaRelativa_OtraUnidad_EsNula()
        {
            Assert.Null(XrefRules.RelativePath(Host, @"D:\Planos\PLANTA_01.dwg"));
        }

        [Fact]
        public void RutaRelativa_Red_EsNula()
        {
            Assert.Null(XrefRules.RelativePath(Host, @"\\servidor\planos\PLANTA_01.dwg"));
            Assert.Null(XrefRules.RelativePath(@"\\servidor\maestro\MAESTRO.dwg", @"C:\Planos\PLANTA_01.dwg"));
        }

        [Fact]
        public void RutaRelativa_HostNoAbsoluto_EsNula()
        {
            Assert.Null(XrefRules.RelativePath("MAESTRO.dwg", @"C:\Planos\PLANTA_01.dwg"));
        }

        [Fact]
        public void RutaRelativa_ConPuntoPunto_EsNula()
        {
            Assert.Null(XrefRules.RelativePath(Host, @"C:\Proyectos\..\x.dwg"));
        }

        [Theory]
        [InlineData(@"C:\Planos\PLANTA.dwg", true)]
        [InlineData(@"Z:\planos\PLANTA.DWG", true)]
        [InlineData(@"C:/Planos/PLANTA.dwg", true)]
        [InlineData(@"\\servidor\planos\PLANTA.dwg", false)]
        [InlineData(@"PLANTA.dwg", false)]
        [InlineData(@"C:\Planos\PLANTA.dxf", false)]
        [InlineData(@"C:\Planos\..\PLANTA.dwg", false)]
        [InlineData(@"C:\Planos\*.dwg", false)]
        [InlineData(@"C:\Planos\PLANTA?.dwg", false)]
        [InlineData(@"C:\Planos\PLANTA|1.dwg", false)]
        [InlineData(@"C:\Planos\PLANTA 1.dwg", true)]
        [InlineData(@"C:\Planos\a:b.dwg", false)]
        [InlineData(@"C:\Planos\a" + "\u0001" + ".dwg", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        public void ValidarRuta(string ruta, bool valida)
        {
            string? error = XrefRules.ValidateRuta(ruta);
            if (valida)
            {
                Assert.Null(error);
            }
            else
            {
                Assert.StartsWith("ruta: ", error);
            }
        }

        [Fact]
        public void ValidarRuta_MensajesConcretos()
        {
            Assert.Equal("ruta: no se admiten rutas UNC (\\\\servidor\\...): use una unidad (local o asignada)", XrefRules.ValidateRuta(@"\\servidor\planos\x.dwg"));
            Assert.Equal("ruta: debe ser absoluta en una unidad (por ejemplo C:\\... o Z:\\...)", XrefRules.ValidateRuta(@"planos\x.dwg"));
            Assert.Equal("ruta: no se admiten '..'", XrefRules.ValidateRuta(@"C:\a\..\x.dwg"));
            Assert.Equal("ruta: solo se admiten archivos .dwg", XrefRules.ValidateRuta(@"C:\a\x.dxf"));
        }

        [Theory]
        [InlineData(@"C:\Planos\PLANTA 01.dwg", "PLANTA 01")]
        [InlineData(@"C:\Planos\PLANTA.V2.DWG", "PLANTA.V2")]
        [InlineData(@"C:/Planos/x.dwg", "x")]
        [InlineData(@"C:\Planos\.dwg", "")]
        public void NombrePorDefecto_EsElArchivoSinExtension(string ruta, string esperado)
        {
            Assert.Equal(esperado, XrefRules.DefaultName(ruta));
        }

        [Theory]
        [InlineData("PLANTA_01")]
        [InlineData("PLANTA 01")]
        [InlineData("PLANTA.V2")]
        [InlineData("PuertaBaño")]
        public void NombreValido(string nombre)
        {
            Assert.Null(XrefRules.ValidateNombre(nombre));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("a|b")]
        [InlineData("..\\x")]
        [InlineData("*U12")]
        [InlineData("x.dwg")]
        [InlineData("CON")]
        [InlineData("a,b")]
        [InlineData("a=b")]
        [InlineData("a;b")]
        [InlineData("a`b")]
        public void NombreInvalido_NombreMensaje(string? nombre)
        {
            string? error = XrefRules.ValidateNombre(nombre);
            Assert.NotNull(error);
            Assert.StartsWith("nombre: ", error);
        }

        [Fact]
        public void NombreDemasiadoLargo_Rechazado()
        {
            string largo = new string('A', XrefRules.MaxNombre + 1);
            Assert.Equal("nombre: debe tener entre 1 y 200 caracteres", XrefRules.ValidateNombre(largo));
            Assert.Null(XrefRules.ValidateNombre(new string('A', XrefRules.MaxNombre)));
        }

        [Fact]
        public void MismoArchivo_IgnoraMayusculasYBarras()
        {
            Assert.True(XrefRules.SamePath(@"C:\Planos\A.dwg", @"c:/planos/a.DWG"));
            Assert.False(XrefRules.SamePath(@"C:\Planos\A.dwg", @"C:\Planos\B.dwg"));
            Assert.False(XrefRules.SamePath(null, @"C:\Planos\A.dwg"));
            Assert.False(XrefRules.SamePath(@"A.dwg", @"C:\Planos\A.dwg"));
        }

        [Theory]
        [InlineData(@"..\Planos\A.dwg", @"C:\Proyectos\Planos\A.dwg")]
        [InlineData(@"sub\A.dwg", @"C:\Proyectos\Maestro\sub\A.dwg")]
        [InlineData(@".\sub\A.dwg", @"C:\Proyectos\Maestro\sub\A.dwg")]
        [InlineData(@".\A.dwg", @"C:\Proyectos\Maestro\A.dwg")]
        [InlineData(@"..\..\x.dwg", @"C:\x.dwg")]
        [InlineData(@"C:\Otro\A.dwg", @"C:\Otro\A.dwg")]
        public void RutaGuardadaAbsoluta(string guardada, string esperada)
        {
            Assert.Equal(esperada, XrefRules.AbsoluteFrom(Host, guardada));
        }

        [Theory]
        [InlineData(@"..\..\..\x.dwg")]
        [InlineData(@"..\..\..\..\x.dwg")]
        [InlineData(@"C:\..\x.dwg")]
        public void RutaGuardada_SubeMasAllaDeLaUnidad_EsNula(string guardada)
        {
            Assert.Null(XrefRules.AbsoluteFrom(Host, guardada));
        }

        [Fact]
        public void MismoArchivo_ConSubidaExcesiva_NoCoincide()
        {
            Assert.False(XrefRules.SamePath(@"C:\a\..\..\..\x.dwg", @"C:\x.dwg"));
        }

        [Fact]
        public void RutaGuardadaRelativa_SinHostGuardado_EsNula()
        {
            Assert.Null(XrefRules.AbsoluteFrom(null, @"sub\A.dwg"));
            Assert.Null(XrefRules.AbsoluteFrom(Host, ""));
        }

        [Fact]
        public void DecidirNombre_LibreAdjunta()
        {
            Assert.Equal(XrefRules.Decision.Adjuntar, XrefRules.DecideName(false, false, null, @"C:\a\x.dwg", false, out string? error));
            Assert.Null(error);
        }

        [Fact]
        public void DecidirNombre_SoloInsertarSinReferencia_Rechaza()
        {
            Assert.Equal(XrefRules.Decision.Rechazar, XrefRules.DecideName(false, false, null, @"C:\a\x.dwg", true, out string? error));
            Assert.StartsWith("solo_insertar: ", error);
        }

        [Fact]
        public void DecidirNombre_BloqueNoReferencia_Rechaza()
        {
            Assert.Equal(XrefRules.Decision.Rechazar, XrefRules.DecideName(true, false, null, @"C:\a\x.dwg", false, out string? error));
            Assert.Equal("nombre: ya existe un bloque con ese nombre que no es una referencia externa", error);
        }

        [Fact]
        public void DecidirNombre_ReferenciaOtroArchivo_Rechaza()
        {
            Assert.Equal(XrefRules.Decision.Rechazar, XrefRules.DecideName(true, true, @"C:\b\y.dwg", @"C:\a\x.dwg", false, out string? error));
            Assert.Equal("nombre: ya existe una referencia con ese nombre que no apunta a este archivo", error);
            Assert.Equal(XrefRules.Decision.Rechazar, XrefRules.DecideName(true, true, null, @"C:\a\x.dwg", true, out _));
        }

        [Fact]
        public void DecidirNombre_MismoArchivoSinSoloInsertar_RechazaConAviso()
        {
            Assert.Equal(XrefRules.Decision.Rechazar, XrefRules.DecideName(true, true, @"C:\A\X.dwg", @"c:\a\x.dwg", false, out string? error));
            Assert.Contains("solo_insertar: true", error);
        }

        [Fact]
        public void DecidirNombre_MismoArchivoConSoloInsertar_Reutiliza()
        {
            Assert.Equal(XrefRules.Decision.Reutilizar, XrefRules.DecideName(true, true, @"C:\a\x.dwg", @"C:\a\x.dwg", true, out string? error));
            Assert.Null(error);
        }

        [Theory]
        [InlineData("recargar", "resuelta", null)]
        [InlineData("recargar", "descargada", null)]
        [InlineData("descargar", "resuelta", null)]
        [InlineData("descargar", "descargada", "accion: la referencia ya está descargada")]
        [InlineData("separar", "no_encontrada", null)]
        [InlineData("borrar", "resuelta", "accion: debe ser recargar, descargar o separar")]
        public void ErrorDeAccion(string accion, string estado, string? esperado)
        {
            Assert.Equal(esperado, XrefRules.ErrorAccion(accion, estado));
        }

        [Fact]
        public void UnidadesDistintas_AvisaSoloSiAmbasDeclaradasYDiferentes()
        {
            Assert.Null(XrefRules.UnidadesDistintas(6, 6));
            Assert.Null(XrefRules.UnidadesDistintas(0, 6));
            Assert.Null(XrefRules.UnidadesDistintas(6, 0));
            Assert.Null(XrefRules.UnidadesDistintas(null, 6));
            string? aviso = XrefRules.UnidadesDistintas(4, 6);
            Assert.NotNull(aviso);
            Assert.StartsWith("La referencia declara INSUNITS 4", aviso);
        }
    }
}
