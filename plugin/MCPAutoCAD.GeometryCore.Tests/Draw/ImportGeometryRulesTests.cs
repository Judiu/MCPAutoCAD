using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class ImportGeometryRulesTests
    {
        private static string Unidad(int n) => "unidad" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ---- ruta ----

        [Theory]
        [InlineData(@"C:\datos\plano.dxf")]
        [InlineData("D:/datos/plano.DXF")]
        [InlineData(@"c:\a b\c.dxf")]
        public void RutaValidaSeAcepta(string ruta)
        {
            Assert.Null(ImportGeometryRules.ValidatePath(ruta));
        }

        [Theory]
        [InlineData(@"\\servidor\share\plano.dxf", "UNC")]
        [InlineData(@"//servidor/share/plano.dxf", "UNC")]
        [InlineData(@"\\?\C:\plano.dxf", "UNC")]
        [InlineData(@"plano.dxf", "absoluta")]
        [InlineData(@"..\plano.dxf", "absoluta")]
        [InlineData(@"C:\datos\..\secreto\plano.dxf", "'..'")]
        [InlineData(@"C:\datos\*.dxf", "comodines")]
        [InlineData(@"C:\datos\a?.dxf", "comodines")]
        [InlineData(@"C:\datos\plano.dxf:stream", "NTFS")]
        [InlineData(@"C:\datos\plano.dwg", ".dxf")]
        [InlineData(@"C:\datos\plano", ".dxf")]
        [InlineData("C:\\datos\\pl\tano.dxf", "control")]
        public void RutaInvalidaSeRechaza(string ruta, string fragmento)
        {
            string? error = ImportGeometryRules.ValidatePath(ruta);
            Assert.NotNull(error);
            Assert.StartsWith("ruta:", error);
            Assert.Contains(fragmento, error, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void RutaVaciaSeRechaza(string ruta)
        {
            Assert.Equal("ruta: no puede estar vacía", ImportGeometryRules.ValidatePath(ruta));
        }

        [Fact]
        public void TamanoDentroDelLimiteSeAcepta()
        {
            Assert.Null(ImportGeometryRules.ValidateSize(ImportGeometryRules.MaxFileBytes));
            Assert.Null(ImportGeometryRules.ValidateSize(0));
        }

        [Fact]
        public void TamanoSobreElLimiteDiceCuantoPesa()
        {
            string? error = ImportGeometryRules.ValidateSize(ImportGeometryRules.MaxFileBytes + 1024L * 1024L);
            Assert.NotNull(error);
            Assert.Contains("201.0 MB", error);
            Assert.Contains("máximo es 200 MB", error);
        }

        // ---- tipos ----

        [Theory]
        [InlineData("LINE", true)]
        [InlineData("LWPOLYLINE", true)]
        [InlineData("POLYLINE", true)]
        [InlineData("CIRCLE", true)]
        [InlineData("ARC", true)]
        [InlineData("ELLIPSE", true)]
        [InlineData("SPLINE", true)]
        [InlineData("POINT", true)]
        [InlineData("TEXT", true)]
        [InlineData("MTEXT", true)]
        [InlineData("HATCH", true)]
        [InlineData("SOLID", true)]
        [InlineData("line", true)]
        [InlineData("INSERT", true)]
        [InlineData("XREF", false)]
        [InlineData("DIMENSION", false)]
        [InlineData("POLYLINE3D", false)]
        [InlineData("3DFACE", false)]
        [InlineData("IMAGE", false)]
        [InlineData("ACAD_PROXY_ENTITY", false)]
        [InlineData("", false)]
        public void TiposImportables(string tipo, bool esperado)
        {
            Assert.Equal(esperado, ImportGeometryRules.IsImportable(tipo));
        }

        [Fact]
        public void SelecionaCombinaTipoYFiltroSinDistinguirMayusculas()
        {
            var filtro = new HashSet<string>(new[] { "MAXI-VIA" }, StringComparer.OrdinalIgnoreCase);
            Assert.True(ImportGeometryRules.Selecciona("LWPOLYLINE", "maxi-via", filtro));
            Assert.False(ImportGeometryRules.Selecciona("LWPOLYLINE", "MAXI-TEXTO", filtro));
            Assert.True(ImportGeometryRules.Selecciona("INSERT", "MAXI-VIA", filtro));
            Assert.False(ImportGeometryRules.Selecciona("XREF", "MAXI-VIA", filtro));
            Assert.True(ImportGeometryRules.Selecciona("CIRCLE", "0", null));
            Assert.False(ImportGeometryRules.Selecciona("DIMENSION", "0", null));
        }

        // ---- filtro ----

        [Fact]
        public void FiltroAusenteNoRestringe()
        {
            Assert.Null(ImportGeometryRules.NormalizeFilter(null, out string? error));
            Assert.Null(error);
        }

        [Fact]
        public void FiltroNormalizaYDeduplicaSinMayusculas()
        {
            HashSet<string>? f = ImportGeometryRules.NormalizeFilter(new[] { " MAXI-VIA ", "maxi-via", "0" }, out string? error);
            Assert.Null(error);
            Assert.NotNull(f);
            Assert.Equal(2, f!.Count);
            Assert.Contains("maxi-via", f);
            Assert.Contains("0", f);
        }

        [Fact]
        public void FiltroVacioSeRechaza()
        {
            Assert.Null(ImportGeometryRules.NormalizeFilter(Array.Empty<string>(), out string? error));
            Assert.Contains("vacía", error);
        }

        [Fact]
        public void FiltroConNombreEnBlancoSeRechaza()
        {
            Assert.Null(ImportGeometryRules.NormalizeFilter(new[] { "MAXI-VIA", "  " }, out string? error));
            Assert.Contains("vacío", error);
        }

        [Fact]
        public void FiltroDemasiadoLargoSeRechaza()
        {
            var muchas = Enumerable.Range(0, ImportGeometryRules.MaxFilterLayers + 1).Select(i => "C" + i).ToList();
            Assert.Null(ImportGeometryRules.NormalizeFilter(muchas, out string? error));
            Assert.Contains("máximo", error);
        }

        // ---- plan ----

        private static List<ImportGeometryRules.EntidadArchivo> Archivo() => new List<ImportGeometryRules.EntidadArchivo>
        {
            new("LWPOLYLINE", "MAXI-VIA"),
            new("LWPOLYLINE", "MAXI-VIA"),
            new("CIRCLE", "MAXI-PUNTOS"),
            new("MTEXT", "MAXI-TEXTO-TRAZO"),
            new("IMAGE", "MAXI-VIA"),
            new("DIMENSION", "MAXI-COTAS"),
            new("HATCH", "MAXI-ACHURADO"),
            new("LWPOLYLINE", "0"),
        };

        [Fact]
        public void PlanSinFiltroCuentaTodoYOmitidasPorTipo()
        {
            ImportGeometryRules.ImportPlan plan = ImportGeometryRules.Plan(Archivo(), null, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal(8, plan.Total);
            Assert.Equal(6, plan.Seleccionadas);
            Assert.Equal(3, plan.PorTipo["LWPOLYLINE"]);
            Assert.Equal(1, plan.PorTipo["CIRCLE"]);
            Assert.Equal(1, plan.PorTipo["MTEXT"]);
            Assert.Equal(1, plan.PorTipo["HATCH"]);
            Assert.False(plan.PorTipo.ContainsKey("IMAGE"));
            Assert.Equal(1, plan.Omitidas["IMAGE"]);
            Assert.Equal(1, plan.Omitidas["DIMENSION"]);
            Assert.Equal(0, plan.ExcluidasPorCapa);
            Assert.Equal(2, plan.PorCapa["MAXI-VIA"]);
            Assert.False(plan.Excede);
        }

        [Fact]
        public void PlanConFiltroExcluyeYCuentaLoQueQuedaFuera()
        {
            var filtro = new HashSet<string>(new[] { "maxi-via", "MAXI-NO-EXISTE" }, StringComparer.OrdinalIgnoreCase);
            ImportGeometryRules.ImportPlan plan = ImportGeometryRules.Plan(Archivo(), filtro, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.Equal(8, plan.Total);
            Assert.Equal(2, plan.Seleccionadas);
            Assert.Equal(1, plan.Omitidas["IMAGE"]);
            Assert.False(plan.Omitidas.ContainsKey("DIMENSION"));
            Assert.Equal(5, plan.ExcluidasPorCapa);
            Assert.Equal(new[] { "MAXI-NO-EXISTE" }, plan.FiltroSinCoincidencia);
            Assert.Equal(new[] { "MAXI-VIA" }, plan.CapasNuevas);
        }

        [Fact]
        public void PlanSeparaCapasNuevasDeExistentes()
        {
            var destino = new HashSet<string>(new[] { "maxi-via", "0" }, StringComparer.OrdinalIgnoreCase);
            ImportGeometryRules.ImportPlan plan = ImportGeometryRules.Plan(Archivo(), null, destino);

            Assert.Equal(new[] { "MAXI-VIA", "0" }, plan.CapasExistentes);
            Assert.Equal(new[] { "MAXI-PUNTOS", "MAXI-TEXTO-TRAZO", "MAXI-ACHURADO" }, plan.CapasNuevas);
        }

        [Fact]
        public void PlanSinEntidadesImportablesQuedaVacio()
        {
            ImportGeometryRules.ImportPlan plan = ImportGeometryRules.Plan(
                new[] { new ImportGeometryRules.EntidadArchivo("IMAGE", "X") }, null, new HashSet<string>());
            Assert.Equal(0, plan.Seleccionadas);
            Assert.Empty(plan.PorTipo);
            Assert.Empty(plan.CapasNuevas);
            Assert.Equal(1, plan.Omitidas["IMAGE"]);
        }

        [Fact]
        public void TotalEsSumaDeSeleccionadasOmitidasYExcluidas()
        {
            var filtro = new HashSet<string>(new[] { "MAXI-VIA" }, StringComparer.OrdinalIgnoreCase);
            ImportGeometryRules.ImportPlan plan = ImportGeometryRules.Plan(Archivo(), filtro, new HashSet<string>());
            Assert.Equal(plan.Total, plan.Seleccionadas + plan.Omitidas.Values.Sum() + plan.ExcluidasPorCapa);
        }

        [Fact]
        public void TopeDeEntidadesSeDetecta()
        {
            var muchas = Enumerable.Range(0, ImportGeometryRules.MaxEntities + 1)
                .Select(_ => new ImportGeometryRules.EntidadArchivo("LINE", "C"));
            ImportGeometryRules.ImportPlan plan = ImportGeometryRules.Plan(muchas, null, new HashSet<string>());
            Assert.True(plan.Excede);
            Assert.Contains("10000", ImportGeometryRules.TooManyEntities(plan.Seleccionadas));
            Assert.Contains("desde", ImportGeometryRules.TooManyEntities(plan.Seleccionadas));
            Assert.Contains("filtrar_capas", ImportGeometryRules.TooManyEntities(plan.Seleccionadas));
        }

        [Fact]
        public void TopeExactoNoExcede()
        {
            var justo = Enumerable.Range(0, ImportGeometryRules.MaxEntities)
                .Select(_ => new ImportGeometryRules.EntidadArchivo("LINE", "C"));
            Assert.False(ImportGeometryRules.Plan(justo, null, new HashSet<string>()).Excede);
        }

        // ---- unidades ----

        [Fact]
        public void UnidadesIgualesSonCorrectas()
        {
            ImportGeometryRules.VeredictoUnidades v = ImportGeometryRules.CompareUnits(6, 6, Unidad);
            Assert.Equal(ImportGeometryRules.EstadoUnidades.Correcto, v.Estado);
            Assert.Contains("unidad6", v.Mensaje);
        }

        [Fact]
        public void UnidadesDistintasNoNulasSonIncompatibles()
        {
            ImportGeometryRules.VeredictoUnidades v = ImportGeometryRules.CompareUnits(4, 6, Unidad);
            Assert.Equal(ImportGeometryRules.EstadoUnidades.Incompatible, v.Estado);
            Assert.Contains("unidad4", v.Mensaje);
            Assert.Contains("unidad6", v.Mensaje);
            Assert.Contains("no escala automáticamente", v.Mensaje);
        }

        [Fact]
        public void OrigenSinUnidadesEsAdvertencia()
        {
            ImportGeometryRules.VeredictoUnidades v = ImportGeometryRules.CompareUnits(0, 6, Unidad);
            Assert.Equal(ImportGeometryRules.EstadoUnidades.Advertencia, v.Estado);
            Assert.Contains("INSUNITS=0", v.Mensaje);
            Assert.Contains("unidad6", v.Mensaje);
        }

        [Fact]
        public void DestinoSinUnidadesEsAdvertencia()
        {
            ImportGeometryRules.VeredictoUnidades v = ImportGeometryRules.CompareUnits(6, 0, Unidad);
            Assert.Equal(ImportGeometryRules.EstadoUnidades.Advertencia, v.Estado);
            Assert.Contains("unidad6", v.Mensaje);
        }

        [Fact]
        public void AmbosSinUnidadesEsAdvertencia()
        {
            Assert.Equal(ImportGeometryRules.EstadoUnidades.Advertencia, ImportGeometryRules.CompareUnits(0, 0, Unidad).Estado);
        }

        // ---- capas ----

        [Theory]
        [InlineData(false, false, false, null)]
        [InlineData(true, false, false, "capa bloqueada")]
        [InlineData(false, true, false, "capa congelada")]
        [InlineData(false, false, true, "capa dependiente de xref")]
        [InlineData(true, true, true, "capa dependiente de xref")]
        public void MotivoDeBloqueoDeCapa(bool bloqueada, bool congelada, bool xref, string? esperado)
        {
            Assert.Equal(esperado, ImportGeometryRules.LayerBlockReason(bloqueada, congelada, xref));
        }

        [Fact]
        public void MensajeDeCapasBloqueadasNombraCadaUna()
        {
            string msg = ImportGeometryRules.BlockedLayersMessage(new[] { ("MAXI-VIA", "capa bloqueada"), ("MAXI-X", "capa congelada") });
            Assert.Contains("'MAXI-VIA' (capa bloqueada)", msg);
            Assert.Contains("'MAXI-X' (capa congelada)", msg);
            Assert.Contains("filtrar_capas", msg);
        }

        [Fact]
        public void MallasPoligonalesNoSonImportables()
        {
            // POLYMESH lo clasifica el plugin para PolygonMesh y PolyFaceMesh; no son polilíneas 2D.
            Assert.False(ImportGeometryRules.IsImportable("POLYMESH"));
        }

        [Fact]
        public void AvisoFueraDePlanoSoloCuandoHayEntidades()
        {
            Assert.Null(ImportGeometryRules.AvisoFueraDePlanoXY(0));
            string? aviso = ImportGeometryRules.AvisoFueraDePlanoXY(3);
            Assert.NotNull(aviso);
            Assert.Contains("3 entidades", aviso);
            Assert.Contains("sin transformar", aviso);
        }

        [Fact]
        public void AvisoCapasNuevasNombraSusEstados()
        {
            Assert.Null(ImportGeometryRules.AvisoCapasNuevasConEstado(new[] { new ImportGeometryRules.EstadoCapaNueva("A", false, false, false) }));
            string? aviso = ImportGeometryRules.AvisoCapasNuevasConEstado(new[]
            {
                new ImportGeometryRules.EstadoCapaNueva("MURO", true, false, false),
                new ImportGeometryRules.EstadoCapaNueva("COTAS", false, true, true),
                new ImportGeometryRules.EstadoCapaNueva("OK", false, false, false),
            });
            Assert.NotNull(aviso);
            Assert.Contains("'MURO' (congelada)", aviso);
            Assert.Contains("'COTAS' (apagada, bloqueada)", aviso);
            Assert.DoesNotContain("'OK'", aviso);
        }

        [Fact]
        public void AvisoDefinicionesExistentesIgnoraLasEstandar()
        {
            string? aviso = ImportGeometryRules.AvisoDefinicionesExistentes(
                "estilos de texto",
                new[] { "Standard", "ROMANS", "romans", "NUEVO" },
                new[] { "STANDARD", "ROMANS" });
            Assert.NotNull(aviso);
            Assert.Contains("'ROMANS'", aviso);
            Assert.False(aviso.Contains("standard", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("NUEVO", aviso);
            Assert.Equal(2, aviso.Split("'ROMANS'").Length);   // aparece una sola vez
        }

        [Fact]
        public void AvisoDefinicionesExistentesSinCoincidenciasEsNulo()
        {
            Assert.Null(ImportGeometryRules.AvisoDefinicionesExistentes(
                "tipos de línea", new[] { "Continuous", "DASH" }, new[] { "Continuous", "ACAD_ISO" }));
        }

        [Fact]
        public void AvisoDefinicionesExistentesRecortaLaLista()
        {
            var dxf = Enumerable.Range(1, 12).Select(i => "T" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).ToList();
            string? aviso = ImportGeometryRules.AvisoDefinicionesExistentes("tipos de línea", dxf, dxf);
            Assert.NotNull(aviso);
            Assert.Contains("y 2 más", aviso);
        }

        [Fact]
        public void AvisosDeHatchYDeExtensionSonNulosSinDatos()
        {
            Assert.Null(ImportGeometryRules.AvisoOrdenDeDibujoHatch(0));
            Assert.Null(ImportGeometryRules.AvisoHatchNoAsociativos(0));
            Assert.Contains("SORTENTS", ImportGeometryRules.AvisoOrdenDeDibujoHatch(2));
            Assert.Contains("no asociativos", ImportGeometryRules.AvisoHatchNoAsociativos(1));
            Assert.Contains("(R4)", ImportGeometryRules.AvisoSinExtension);
        }

        // ---- 0.10.3: páginas, lotes, capas no válidas, importación incompleta (fallos 36, 38, 41) ----

        [Fact]
        public void TopeBajaA10000PorFallo38()
        {
            Assert.Equal(10000, ImportGeometryRules.MaxEntities);
            Assert.True(ImportGeometryRules.BatchSize <= ImportGeometryRules.MaxEntities);
        }

        [Fact]
        public void PaginarSinDesdeAceptaHastaElTope()
        {
            ImportGeometryRules.Pagina? pg = ImportGeometryRules.Paginar(ImportGeometryRules.MaxEntities, null, out string? error);
            Assert.Null(error);
            Assert.Equal(new ImportGeometryRules.Pagina(0, ImportGeometryRules.MaxEntities, null), pg);
        }

        [Fact]
        public void PaginarSinDesdeRechazaPorEncimaDelTope()
        {
            Assert.Null(ImportGeometryRules.Paginar(ImportGeometryRules.MaxEntities + 1, null, out string? error));
            Assert.Contains("desde", error);
            Assert.Contains("filtrar_capas", error);
        }

        [Fact]
        public void PaginarConDesdeRecorreTodo()
        {
            int total = 33675, vistos = 0, paginas = 0;
            int? desde = 0;
            while (desde != null)
            {
                ImportGeometryRules.Pagina? pg = ImportGeometryRules.Paginar(total, desde, out string? error);
                Assert.Null(error);
                Assert.Equal(vistos, pg!.Inicio);
                Assert.True(pg.Cantidad <= ImportGeometryRules.MaxEntities);
                vistos += pg.Cantidad;
                paginas++;
                desde = pg.Siguiente;
            }

            Assert.Equal(total, vistos);
            Assert.Equal(4, paginas);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(500)]
        [InlineData(501)]
        public void PaginarRechazaDesdeFueraDeRango(int desde)
        {
            Assert.Null(ImportGeometryRules.Paginar(500, desde, out string? error));
            Assert.StartsWith("desde:", error);
        }

        [Fact]
        public void PaginarConDesdeCeroYSeleccionVaciaNoFalla()
        {
            ImportGeometryRules.Pagina? pg = ImportGeometryRules.Paginar(0, 0, out string? error);
            Assert.Null(error);
            Assert.Equal(0, pg!.Cantidad);
            Assert.Null(pg.Siguiente);
        }

        [Fact]
        public void LotesPartenEnOrdenYCubrenTodo()
        {
            List<List<int>> lotes = ImportGeometryRules.Lotes(4500, null, 2000);
            Assert.Equal(new[] { 2000, 2000, 500 }, lotes.Select(l => l.Count));
            Assert.Equal(Enumerable.Range(0, 4500), lotes.SelectMany(l => l));
        }

        [Fact]
        public void LotesNoSeparanUnGrupo()
        {
            // HATCH en 1999 con su contorno en 1998 y 2000: los tres en el mismo lote.
            var grupos = new List<IReadOnlyCollection<int>> { new[] { 1999, 1998, 2000 } };
            List<List<int>> lotes = ImportGeometryRules.Lotes(4000, grupos, 2000);
            Assert.Single(lotes, l => l.Contains(1998) && l.Contains(1999) && l.Contains(2000));
            Assert.Equal(4000, lotes.Sum(l => l.Count));
            Assert.Equal(4000, lotes.SelectMany(l => l).Distinct().Count());
            Assert.All(lotes, l => Assert.True(l.Count <= 2000));
        }

        [Fact]
        public void LotesGrupoMayorQueElTamanoVaSolo()
        {
            var grupos = new List<IReadOnlyCollection<int>> { new[] { 0, 1, 2, 3, 4 } };
            List<List<int>> lotes = ImportGeometryRules.Lotes(7, grupos, 3);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, lotes[0]);
            Assert.Equal(new[] { 5, 6 }, lotes[1]);
        }

        [Fact]
        public void LotesUnenGruposEncadenadosEIgnoranIndicesMalos()
        {
            var grupos = new List<IReadOnlyCollection<int>> { new[] { 0, 5 }, new[] { 5, 9, 99, -1 } };
            List<List<int>> lotes = ImportGeometryRules.Lotes(10, grupos, 2);
            Assert.Contains(lotes, l => l.Contains(0) && l.Contains(5) && l.Contains(9));
            Assert.Equal(10, lotes.Sum(l => l.Count));
        }

        [Fact]
        public void LotesVacioYTamanoInvalido()
        {
            Assert.Empty(ImportGeometryRules.Lotes(0, null, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImportGeometryRules.Lotes(3, null, 0));
        }

        [Theory]
        [InlineData("A23|A-WALL", '|')]
        [InlineData("A<B", '<')]
        [InlineData("a=b", '=')]
        [InlineData("C:D", ':')]
        public void CaracterInvalidoEnCapaLoDetecta(string nombre, char esperado)
        {
            Assert.Equal(esperado, ImportGeometryRules.CaracterInvalidoEnCapa(nombre));
        }

        [Theory]
        [InlineData("A23_A-WALL")]
        [InlineData("S-GRID-IDEN")]
        [InlineData("Planta 1 (muros)")]
        [InlineData("")]
        public void CaracterInvalidoEnCapaAceptaNombresValidos(string nombre)
        {
            Assert.Null(ImportGeometryRules.CaracterInvalidoEnCapa(nombre));
        }

        [Fact]
        public void CapasInvalidasEnDxfLeeTablaYEntidades()
        {
            string[] dxf =
            {
                "  0", "SECTION", "  2", "TABLES",
                "  0", "TABLE", "  2", "LAYER",
                "  0", "LAYER", "  2", "A23|A-WALL", " 70", "0",
                "  0", "LAYER", "  2", "BIEN", " 70", "0",
                "  0", "ENDTAB", "  0", "ENDSEC",
                "  0", "SECTION", "  2", "ENTITIES",
                "  0", "LINE", "  8", "A23|A-WALL",
                "  0", "LINE", "  8", "OTRA<MAL",
                "  0", "TEXT", "  8", "BIEN", "  2", "no|es-capa",
                "  0", "ENDSEC", "  0", "EOF",
            };
            List<(string Nombre, char Caracter)> malas = ImportGeometryRules.CapasInvalidasEnDxf(dxf);
            Assert.Equal(new[] { ("A23|A-WALL", '|'), ("OTRA<MAL", '<') }, malas);
        }

        [Fact]
        public void CapasInvalidasEnDxfRespetaElMaximoYElBinario()
        {
            var dxf = new List<string>();
            for (int i = 0; i < 30; i++)
            {
                dxf.Add("  8");
                dxf.Add("C|" + i);
            }

            Assert.Equal(10, ImportGeometryRules.CapasInvalidasEnDxf(dxf).Count);
            Assert.Empty(ImportGeometryRules.CapasInvalidasEnDxf(new[] { "AutoCAD Binary DXF", "  8", "C|1" }));
        }

        [Fact]
        public void MensajeCapasInvalidasNombraCapaYCaracter()
        {
            string m = ImportGeometryRules.MensajeCapasInvalidas("x.dxf", new List<(string, char)> { ("A23|A-WALL", '|') });
            Assert.Contains("'A23|A-WALL'", m);
            Assert.Contains("'|'", m);
            Assert.Contains("x.dxf", m);
        }

        [Fact]
        public void AvisoIncompletaDiceQueFaltaYComoDeshacer()
        {
            var faltan = new Dictionary<string, int> { ["MTEXT"] = 10, ["LWPOLYLINE"] = 2213 };
            string m = ImportGeometryRules.AvisoIncompleta(781, 3004, faltan, "LWPOLYLINE", "A12-MUROS", "2F1");
            Assert.Contains("781 de 3004", m);
            Assert.Contains("LWPOLYLINE 2213, MTEXT 10", m);
            Assert.Contains("2F1", m);
            Assert.Contains("CTRL+Z", m);
        }

        [Fact]
        public void AvisoIncompletaSinReintentoYConDesde()
        {
            var faltan = new Dictionary<string, int> { ["LINE"] = 3 };
            string m = ImportGeometryRules.AvisoIncompleta(7, 10, faltan, "LINE", "C", "1A", reintentado: false, desde: 20000);
            Assert.Contains("no se reintentó", m);
            Assert.DoesNotContain("aun después de reintentar", m);
            Assert.Contains("mismo desde (20000)", m);
        }

        [Fact]
        public void MensajeCapasInvalidasNoAfirmaLaCausa()
        {
            string m = ImportGeometryRules.MensajeCapasInvalidas("x.dxf", new List<(string, char)> { ("A|B", '|') });
            Assert.Contains("Causa probable", m);
        }
    }
}
