using System;
using System.IO;
using System.Text.Json;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>
    /// Casos obligatorios de protocol.md §5C.1 contra el lps.json real del repositorio (standards\lps.json, estándar v2
    /// del 2026-10-28: 24 capas permitidas del 6X12 normalizado + mapa_normalizacion). Si el archivo no se encuentra
    /// (ejecución fuera del repo), se usa un JSON mínimo embebido con los mismos valores.
    /// Los casos de ORDEN de evaluación que necesitan nombres con patrón ajeno dentro de capas_permitidas (Layer5,
    /// A-WALL, …_Pen_No__142, PDF2_Text, Capa2) se prueban contra el estándar v1 histórico
    /// (standards\historico\lps_v1_6x12_sucio.json, las 161 capas del plano madre sin limpiar), que sí los contenía.
    /// </summary>
    public class LayerClassifierTests
    {
        private const string FallbackJson = @"{
  ""version"": 2,
  ""unidades"": { ""insunits"": 6, ""nombre"": ""m"" },
  ""capas_preferidas"": { ""muros"": ""MUROS"", ""cotas"": ""Cotas"", ""textos"": ""TEXTOS"", ""ejes"": ""EJES"",
    ""columnas"": ""columnas"", ""vigas"": ""VIGA"", ""puertas"": ""PUERTAS"", ""ventanas"": ""ventanas"",
    ""achurados"": ""Achurados"", ""mobiliario_fijo"": ""muebles fijos"", ""sanitarios"": ""Sanitarios"",
    ""niveles"": ""niveles"", ""escaleras"": ""ESCALERA"", ""estructura"": ""6. ESTRUCTURA"", ""rotulo"": ""Rotulo"" },
  ""capas_mcp"": { ""MCP-OBSERVACIONES"": { ""color"": 1, ""tipo_linea"": ""Continuous"", ""grosor"": 35, ""plot"": 1 } },
  ""grupos_externos_permitidos"": { ""cype"": [""ARM_*"", ""ALZ_*"", ""CIM_*"", ""SEC_*"", ""VIG_*"", ""TAB_*""] },
  ""capas_permitidas"": {
    ""0"": { ""color"": 7, ""tipo_linea"": ""Continuous"", ""grosor"": -3, ""plot"": 1 },
    ""MUROS"": { ""color"": 155, ""tipo_linea"": ""Continuous"", ""grosor"": 50, ""plot"": 1 },
    ""Cotas"": { ""color"": 8, ""tipo_linea"": ""Continuous"", ""grosor"": 13, ""plot"": 1 }
  },
  ""mapa_normalizacion"": { ""S-COLS"": ""6. ESTRUCTURA"", ""TEXT"": ""EJES"", ""Q-SPCQ-____-OTLN"": ""muebles fijos"",
    ""P-SANR-FIXT-OTLN"": ""muebles fijos"", ""3D EJES ESTRUCTURALES_Pen_No__7"": ""TEXTOS"",
    ""Dibujo 2D - General_Pen_No__142"": ""Rotulo"" }
}";

        private const string FallbackJsonV1 = @"{
  ""unidades"": { ""insunits"": 6, ""nombre"": ""m"" },
  ""capas_preferidas"": { ""muros"": ""MUROS"", ""cotas"": ""Cotas"", ""textos"": ""TEXTOS"", ""ejes"": ""EJES"",
    ""columnas"": ""columnas"", ""vigas"": ""VIGA"", ""puertas"": ""PUERTAS"", ""ventanas"": ""ventanas"",
    ""achurados"": ""Achurados"", ""mobiliario_fijo"": ""muebles fijos"", ""sanitarios"": ""Sanitarios"",
    ""niveles"": ""niveles"", ""escaleras"": ""ESCALERA"" },
  ""capas_mcp"": { ""MCP-OBSERVACIONES"": { ""color"": 1, ""tipo_linea"": ""Continuous"", ""grosor"": 35, ""plot"": 1 } },
  ""grupos_externos_permitidos"": { ""cype"": [""ARM_*"", ""ALZ_*"", ""CIM_*"", ""SEC_*"", ""VIG_*"", ""TAB_*""] },
  ""capas_permitidas"": {
    ""0"": { ""color"": 7, ""tipo_linea"": ""Continuous"", ""grosor"": -3, ""plot"": 1 },
    ""MUROS"": { ""color"": 155, ""tipo_linea"": ""Continuous"", ""grosor"": 50, ""plot"": 1 },
    ""Cotas"": { ""color"": 8, ""tipo_linea"": ""Continuous"", ""grosor"": 13, ""plot"": 1 },
    ""Layer5"": { ""color"": 6, ""tipo_linea"": ""Continuous"", ""grosor"": -3, ""plot"": 1 },
    ""A-WALL"": { ""color"": 113, ""tipo_linea"": ""Continuous"", ""grosor"": 18, ""plot"": 1 },
    ""S-COLS"": { ""color"": 52, ""tipo_linea"": ""Continuous"", ""grosor"": 18, ""plot"": 1 },
    ""Q-SPCQ-____-OTLN"": { ""color"": 7, ""tipo_linea"": ""Continuous"", ""grosor"": 18, ""plot"": 1 },
    ""P-SANR-FIXT-OTLN"": { ""color"": 7, ""tipo_linea"": ""Continuous"", ""grosor"": 18, ""plot"": 1 },
    ""3D EJES ESTRUCTURALES_Pen_No__7"": { ""color"": 7, ""tipo_linea"": ""Continuous"", ""grosor"": 15, ""plot"": 1 },
    ""Dibujo 2D - General_Pen_No__142"": { ""color"": 8, ""tipo_linea"": ""Continuous"", ""grosor"": 15, ""plot"": 1 }
  }
}";

        private static readonly Lazy<(LpsLayerRules rules, bool real)> Loaded =
            new Lazy<(LpsLayerRules, bool)>(() => Load(Path.Combine("standards", "lps.json"), FallbackJson));

        private static readonly Lazy<(LpsLayerRules rules, bool real)> LoadedV1 =
            new Lazy<(LpsLayerRules, bool)>(() => Load(Path.Combine("standards", "historico", "lps_v1_6x12_sucio.json"), FallbackJsonV1));

        /// <summary>Reglas del lps.json vigente (v2). También las usa <see cref="LayerClassifierRealDataTests"/>.</summary>
        internal static LpsLayerRules RealRules => Loaded.Value.rules;

        private static LpsLayerRules Rules => Loaded.Value.rules;

        /// <summary>Reglas del estándar v1 histórico (161 capas del plano madre sin limpiar).</summary>
        private static LpsLayerRules RulesV1 => LoadedV1.Value.rules;

        private static (LpsLayerRules, bool) Load(string relative, string fallback)
        {
            string? path = FindStandard(relative);
            string json = path != null ? File.ReadAllText(path) : fallback;
            using JsonDocument doc = JsonDocument.Parse(json);
            return (LpsLayerRules.FromJson(doc.RootElement.Clone()), path != null);
        }

        /// <summary>Sube desde la carpeta de salida hasta encontrar el archivo (ruta relativa a la raíz del repo).</summary>
        private static string? FindStandard(string relative)
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return null;
        }

        private static LayerClassification C(string name, int color, string lt, int lw) =>
            LayerClassifier.Classify(name, color, lt, lw, Rules);

        private static LayerClassification V1(string name, int color, string lt, int lw) =>
            LayerClassifier.Classify(name, color, lt, lw, RulesV1);

        /// <summary>Las 24 capas permitidas del estándar v2 (2026-10-28).</summary>
        public static readonly string[] PermitidasV2 =
        {
            "0", "6. ESTRUCTURA", "AC", "Achurados", "Antepecho", "columnas", "Cotas", "DEFPOINTS", "EJES", "Filos",
            "m fijos", "muebles fijos", "MUR BAAAJO", "MUROS", "Proyeccion", "PUERTAS", "Rotulo", "Sanitarios", "TEXTOS",
            "ventanas", "VIGA", "MAMPOSTERIA", "niveles", "ESCALERA",
        };

        [Fact]
        public void SeUsaElEstandarRealDelRepositorio()
        {
            // Dentro del repo debe encontrarse standards\lps.json (v2: 24 capas permitidas, 11 pares de mapa_normalizacion).
            Assert.True(Loaded.Value.real, "No se encontró standards\\lps.json subiendo desde " + AppContext.BaseDirectory);
            Assert.True(Rules.Disponible);
            Assert.Equal(24, Rules.CapasPermitidas.Count);
            foreach (string n in PermitidasV2)
            {
                Assert.True(Rules.CapasPermitidas.ContainsKey(n), "falta " + n);
            }

            Assert.Equal(11, Rules.MapaNormalizacion.Count);
            Assert.Contains("ARM_*", Rules.PatronesExternos);
            Assert.Equal("MUROS", Rules.CapasPreferidas["muros"]);
        }

        [Fact]
        public void EstandarV2_CadaCapaPreferidaEsCreable()
        {
            // Las herramientas de dibujo (R3) solo crean capas de capas_permitidas/capas_mcp: todo destino de
            // capas_preferidas debe estar en una de las dos.
            foreach (var kv in Rules.CapasPreferidas)
            {
                Assert.True(Rules.CapasPermitidas.ContainsKey(kv.Value) || Rules.CapasMcp.ContainsKey(kv.Value),
                    kv.Key + " → " + kv.Value + " no es creable");
            }
        }

        [Theory]
        [InlineData("I-FURN-____-OTLN")]
        [InlineData("Layer5")]
        [InlineData("A-WALL")]
        [InlineData("S-COLS")]
        [InlineData("Q-SPCQ-____-OTLN")]
        [InlineData("P-SANR-FIXT-OTLN")]
        [InlineData("3D EJES ESTRUCTURALES_Pen_No__7")]
        [InlineData("Dibujo 2D - General_Pen_No__142")]
        [InlineData("2D Drafting - General_Pen_No__1")]
        [InlineData("PDF2_Text")]
        [InlineData("TEXT")]
        [InlineData("ESC")]
        public void EstandarV2_SinCapasDeRevitExportacionNiGenericas(string nombre)
        {
            Assert.False(Rules.CapasPermitidas.ContainsKey(nombre), nombre + " no debe estar en capas_permitidas v2");
        }

        [Fact]
        public void EstandarV2_MapaNormalizacion_OrigenNoPermitidoYDestinoPermitido()
        {
            foreach (var kv in Rules.MapaNormalizacion)
            {
                Assert.False(Rules.CapasPermitidas.ContainsKey(kv.Key), "origen permitido: " + kv.Key);
                Assert.True(Rules.CapasPermitidas.ContainsKey(kv.Value), "destino no permitido: " + kv.Key + " → " + kv.Value);
            }

            Assert.Equal("6. ESTRUCTURA", Rules.MapaNormalizacion["s-cols"]);
            Assert.Equal("EJES", Rules.MapaNormalizacion["TEXT"]);
            Assert.Equal("Rotulo", Rules.MapaNormalizacion["3D LINEAS DE PLANTA_Pen_No__7"]);
        }

        [Fact]
        public void EstandarV1Historico_SeConserva()
        {
            Assert.True(LoadedV1.Value.real, "No se encontró standards\\historico\\lps_v1_6x12_sucio.json");
            Assert.Equal(161, RulesV1.CapasPermitidas.Count);
            Assert.Empty(RulesV1.MapaNormalizacion);
        }

        [Fact]
        public void Muros_IgualAlEstandar_EsEstandar()
        {
            LayerClassification r = C("MUROS", 155, "Continuous", 50);
            Assert.Equal(LayerClassifier.Estandar, r.Clasificacion);
            Assert.Empty(r.Diferencias);
            Assert.Null(r.Sugerencia);
        }

        [Fact]
        public void Muros_ColorDistinto_EstandarConDiferencias()
        {
            LayerClassification r = C("MUROS", 46, "Continuous", 50);
            Assert.Equal(LayerClassifier.EstandarConDiferencias, r.Clasificacion);
            Assert.Equal(new[] { "color: 46 ≠ 155" }, r.Diferencias);
            Assert.Null(r.Sugerencia);
        }

        [Fact]
        public void Muros_TodasLasDiferencias_EnOrdenYFormatoExacto()
        {
            LayerClassification r = C("muros", 46, "HIDDEN", -3);
            Assert.Equal(LayerClassifier.EstandarConDiferencias, r.Clasificacion);
            Assert.Equal(new[] { "color: 46 ≠ 155", "tipo_linea: HIDDEN ≠ Continuous", "grosor: -3 ≠ 50" }, r.Diferencias);
        }

        [Fact]
        public void TipoLinea_SeComparaSinMayusculas()
        {
            Assert.Equal(LayerClassifier.Estandar, C("MUROS", 155, "CONTINUOUS", 50).Clasificacion);
        }

        [Fact]
        public void Layer5_EstaEnPermitidas_EsEstandarNoGenerica()
        {
            // Orden de evaluación (§5C.1): con un estándar que la contiene (v1), capas_permitidas gana a la heurística.
            Assert.Equal(LayerClassifier.Estandar, V1("Layer5", 6, "Continuous", -3).Clasificacion);
        }

        [Fact]
        public void Layer5_EnLaV2_EsGenerica()
        {
            LayerClassification r = C("Layer5", 6, "Continuous", -3);
            Assert.Equal(LayerClassifier.Generica, r.Clasificacion);
            Assert.Null(r.Sugerencia);
        }

        [Fact]
        public void Layer9_EsGenerica()
        {
            LayerClassification r = C("Layer9", 7, "Continuous", -3);
            Assert.Equal(LayerClassifier.Generica, r.Clasificacion);
            Assert.Null(r.Sugerencia);
        }

        [Fact]
        public void PermitidasConNombreDeExportacion_GanaElEstandar()
        {
            // PDF2_Text y Capa2 figuraban en capas_permitidas v1 (plano madre): el estándar se evalúa antes que las heurísticas.
            Assert.NotEqual(LayerClassifier.AjenaExportacion, V1("PDF2_Text", 7, "Continuous", -3).Clasificacion);
            Assert.NotEqual(LayerClassifier.Generica, V1("Capa2", 7, "Continuous", -3).Clasificacion);
        }

        [Fact]
        public void NombresDeExportacionYGenericos_EnLaV2_SonAjenos()
        {
            Assert.Equal(LayerClassifier.AjenaExportacion, C("PDF2_Text", 7, "Continuous", -3).Clasificacion);
            Assert.Equal(LayerClassifier.Generica, C("Capa2", 7, "Continuous", -3).Clasificacion);
        }

        [Fact]
        public void Capa27_EsGenerica()
        {
            Assert.Equal(LayerClassifier.Generica, C("CAPA27", 7, "Continuous", -3).Clasificacion);
        }

        [Fact]
        public void AWall_EnPermitidas_EsEstandar()
        {
            Assert.Equal(LayerClassifier.Estandar, V1("A-WALL", 113, "Continuous", 18).Clasificacion);
        }

        [Fact]
        public void AWall_EnLaV2_EsAjenaRevitAiaConSugerenciaPatron()
        {
            LayerClassification r = C("A-WALL", 113, "Continuous", 18);
            Assert.Equal(LayerClassifier.AjenaRevitAia, r.Clasificacion);
            Assert.Null(r.Sugerencia); // sin palabra clave en español ni par en mapa_normalizacion
            Assert.Equal("MUROS", r.SugerenciaPatron);
        }

        [Fact]
        public void AWallFull_EsAjenaRevitAia()
        {
            LayerClassification r = C("A-WALL-FULL", 113, "Continuous", 18);
            Assert.Equal(LayerClassifier.AjenaRevitAia, r.Clasificacion);
            Assert.Empty(r.Diferencias);
        }

        [Fact]
        public void ArmPortico_EsCype()
        {
            LayerClassification r = C("ARM_PORTICO", 3, "Continuous", -3);
            Assert.Equal(LayerClassifier.Cype, r.Clasificacion);
            Assert.Null(r.Sugerencia);
        }

        [Fact]
        public void PatronCype_SinMayusculasYAnclado()
        {
            Assert.Equal(LayerClassifier.Cype, C("vig_cotas", 3, "Continuous", -3).Clasificacion);
            // "XARM_1" no empieza por ARM_: el patrón está anclado.
            Assert.NotEqual(LayerClassifier.Cype, C("XARM_1", 3, "Continuous", -3).Clasificacion);
        }

        [Fact]
        public void PenNo142_EnPermitidas_EsEstandar()
        {
            Assert.Equal(LayerClassifier.Estandar, V1("Dibujo 2D - General_Pen_No__142", 8, "Continuous", 15).Clasificacion);
        }

        [Fact]
        public void PenNo142_EnLaV2_EsAjenaExportacionConDestinoDelMapa()
        {
            LayerClassification r = C("Dibujo 2D - General_Pen_No__142", 8, "Continuous", 15);
            Assert.Equal(LayerClassifier.AjenaExportacion, r.Clasificacion);
            Assert.Equal("Rotulo", r.Sugerencia);
            Assert.Equal("Rotulo", r.SugerenciaPatron);
        }

        [Fact]
        public void PenNo99_EsAjenaExportacion()
        {
            Assert.Equal(LayerClassifier.AjenaExportacion, C("Dibujo 2D - General_Pen_No__99", 8, "Continuous", 15).Clasificacion);
        }

        [Theory]
        [InlineData("PDF_0")]
        [InlineData("PDF3_Lineas")]
        [InlineData("Plano_Pluma_N3")]
        [InlineData("2D Drafting - General")]
        [InlineData("3D Solidos")]
        public void Exportaciones_SonAjenaExportacion(string name)
        {
            Assert.Equal(LayerClassifier.AjenaExportacion, C(name, 7, "Continuous", -3).Clasificacion);
        }

        [Fact]
        public void McpObservaciones_EsMcp()
        {
            LayerClassification r = C("MCP-OBSERVACIONES", 1, "Continuous", 35);
            Assert.Equal(LayerClassifier.Mcp, r.Clasificacion);
            Assert.Null(r.Sugerencia);
        }

        [Fact]
        public void McpConPropiedadesDistintas_SigueSiendoMcp()
        {
            Assert.Equal(LayerClassifier.Mcp, C("mcp-observaciones", 5, "HIDDEN", 0).Clasificacion);
        }

        [Fact]
        public void MuroBajo_SugiereMuros()
        {
            LayerClassification r = C("muro bajo", 7, "Continuous", -3);
            Assert.Equal(LayerClassifier.FueraDeEstandar, r.Clasificacion);
            Assert.Equal("MUROS", r.Sugerencia);
        }

        [Fact]
        public void Cotas_EnMayusculas_EsEstandarPorComparacionSinMayusculas()
        {
            // "COTAS" no figura tal cual; "Cotas" sí → misma capa para AutoCAD.
            Assert.Equal(LayerClassifier.Estandar, C("COTAS", 8, "Continuous", 13).Clasificacion);
        }

        [Fact]
        public void Capa0_EsEstandar()
        {
            Assert.Equal(LayerClassifier.Estandar, C("0", 7, "Continuous", -3).Clasificacion);
            Assert.Equal(LayerClassifier.EstandarConDiferencias, C("0", 1, "Continuous", -3).Clasificacion);
        }

        [Theory]
        [InlineData("muros_viejos_2", "MUROS")]
        [InlineData("ACOTADO-X", "Cotas")]
        [InlineData("textitos", "TEXTOS")]
        [InlineData("EJE-A", "EJES")]
        [InlineData("Puerta_Ventana", "PUERTAS")]
        [InlineData("VENTANAL", "ventanas")]
        [InlineData("relleno_hatch", "Achurados")]
        [InlineData("columna-x", "columnas")]
        [InlineData("VIGAS-AEREAS", "VIGA")]
        [InlineData("nivel_piso", "niveles")]
        [InlineData("escaleras_2", "ESCALERA")]
        [InlineData("Sanitario_bano", "Sanitarios")]
        [InlineData("Mobiliário", "muebles fijos")]
        [InlineData("MUEBLES-COCINA", "muebles fijos")]
        public void Sugerencia_PorPalabraClave(string name, string expected)
        {
            LayerClassification r = C(name, 7, "Continuous", -3);
            Assert.True(LayerClassifier.IsForeign(r.Clasificacion), name + " → " + r.Clasificacion);
            Assert.Equal(expected, r.Sugerencia);
        }

        [Fact]
        public void Sugerencia_SinAcentos()
        {
            // "TÉXTO" sin acento contiene TEXT.
            Assert.Equal("TEXTOS", C("TÉXTO_NOTAS", 7, "Continuous", -3).Sugerencia);
        }

        [Fact]
        public void Sugerencia_OrdenDeLaTabla_MuroAntesQueCota()
        {
            Assert.Equal("MUROS", C("cota_muro", 7, "Continuous", -3).Sugerencia);
        }

        [Fact]
        public void SinPalabraClave_SugerenciaNull()
        {
            Assert.Null(C("XYZ-RARO", 7, "Continuous", -3).Sugerencia);
            Assert.Equal(LayerClassifier.FueraDeEstandar, C("XYZ-RARO", 7, "Continuous", -3).Clasificacion);
        }

        [Fact]
        public void SinEstandar_ClasificaSoloLoQueNoDependeDelArchivo()
        {
            LpsLayerRules empty = LpsLayerRules.FromJson(null);
            Assert.False(empty.Disponible);
            Assert.Equal(LayerClassifier.FueraDeEstandar, LayerClassifier.Classify("MUROS", 155, "Continuous", 50, empty).Clasificacion);
            Assert.Equal(LayerClassifier.FueraDeEstandar, LayerClassifier.Classify("ARM_PORTICO", 3, "Continuous", -3, empty).Clasificacion);
            Assert.Equal(LayerClassifier.Mcp, LayerClassifier.Classify("MCP-OBSERVACIONES", 1, "Continuous", 35, empty).Clasificacion);
            Assert.Equal(LayerClassifier.AjenaRevitAia, LayerClassifier.Classify("A-WALL", 113, "Continuous", 18, empty).Clasificacion);
            Assert.Equal(LayerClassifier.Generica, LayerClassifier.Classify("Layer5", 6, "Continuous", -3, empty).Clasificacion);
            Assert.Equal(LayerClassifier.AjenaExportacion, LayerClassifier.Classify("PDF_0", 7, "Continuous", -3, empty).Clasificacion);
            // Sin capas_preferidas no hay sugerencia.
            Assert.Null(LayerClassifier.Classify("muro bajo", 7, "Continuous", -3, empty).Sugerencia);
        }

        [Fact]
        public void Reglas_ToleranSeccionesMalFormadas()
        {
            using JsonDocument doc = JsonDocument.Parse(@"{
  ""capas_permitidas"": { ""MUROS"": { ""color"": ""rojo"" }, ""EJES"": { ""color"": 1, ""tipo_linea"": ""CENTER"", ""grosor"": 5 }, ""X"": 3 },
  ""capas_mcp"": [],
  ""capas_preferidas"": { ""muros"": 5, ""ejes"": ""EJES"" },
  ""grupos_externos_permitidos"": { ""cype"": ""ARM_*"", ""otro"": [""Z_*"", 4] }
}");
            LpsLayerRules r = LpsLayerRules.FromJson(doc.RootElement.Clone());
            Assert.True(r.Disponible);
            Assert.Single(r.CapasPermitidas);
            Assert.True(r.CapasPermitidas["ejes"].Plot);
            Assert.Empty(r.CapasMcp);
            Assert.Single(r.CapasPreferidas);
            Assert.Equal(new[] { "Z_*" }, r.PatronesExternos);
        }

        [Fact]
        public void Reglas_RaizNoObjeto_EsVacia()
        {
            using JsonDocument doc = JsonDocument.Parse("[1, 2]");
            Assert.False(LpsLayerRules.FromJson(doc.RootElement.Clone()).Disponible);
        }

        [Theory]
        [InlineData("ARM_1", "ARM_*", true)]
        [InlineData("arm_", "ARM_*", true)]
        [InlineData("ARM", "ARM_*", false)]
        [InlineData("XARM_1", "ARM_*", false)]
        [InlineData("A_B_C", "A*C", true)]
        [InlineData("ABC", "*", true)]
        [InlineData("ABD", "A*C", false)]
        public void Comodin(string name, string pattern, bool expected)
        {
            Assert.Equal(expected, LpsLayerRules.WildcardMatch(name, pattern));
        }

        // ---- 0.6 (§5F.5): patron_ajeno y sugerencia_patron con la tabla AIA ----

        [Theory]
        [InlineData("Q-SPCQ-____-OTLN", 7, 18, "revit_aia", null)]
        [InlineData("P-SANR-FIXT-OTLN", 7, 18, "revit_aia", "Sanitarios")]
        [InlineData("S-COLS", 52, 18, "revit_aia", "columnas")]
        [InlineData("A-WALL", 113, 18, "revit_aia", "MUROS")]
        [InlineData("3D EJES ESTRUCTURALES_Pen_No__7", 7, 15, "exportacion", "EJES")]
        [InlineData("Layer5", 6, -3, "generica", null)]
        public void PatronAjeno_EnCapasDelEstandar(string nombre, int color, int grosor, string patron, string? sugerenciaPatron)
        {
            // Con el estándar v1, que legitimaba estos nombres (0.6, §5F.5).
            LayerClassification r = V1(nombre, color, "Continuous", grosor);
            Assert.Equal(LayerClassifier.Estandar, r.Clasificacion);
            Assert.Equal(patron, r.PatronAjeno);
            Assert.Equal(sugerenciaPatron, r.SugerenciaPatron);
            Assert.Null(r.Sugerencia);
        }

        [Theory]
        // nombre, color, grosor, clasificación v2, sugerencia, sugerencia_patron (los destinos salen de mapa_normalizacion)
        [InlineData("Q-SPCQ-____-OTLN", 7, 18, "ajena_revit_aia", "muebles fijos", "muebles fijos")]
        [InlineData("P-SANR-FIXT-OTLN", 7, 18, "ajena_revit_aia", "muebles fijos", "muebles fijos")]
        [InlineData("S-COLS", 52, 18, "ajena_revit_aia", "6. ESTRUCTURA", "6. ESTRUCTURA")]
        [InlineData("A-WALL", 113, 18, "ajena_revit_aia", null, "MUROS")]
        [InlineData("3D EJES ESTRUCTURALES_Pen_No__7", 7, 15, "ajena_exportacion", "TEXTOS", "TEXTOS")]
        [InlineData("Layer5", 6, -3, "generica", null, null)]
        public void PatronAjeno_EnLaV2_SonAjenasConDestinoDecidido(string nombre, int color, int grosor, string clasif, string? sugerencia, string? sugerenciaPatron)
        {
            LayerClassification r = C(nombre, color, "Continuous", grosor);
            Assert.Equal(clasif, r.Clasificacion);
            Assert.Equal(sugerencia, r.Sugerencia);
            Assert.Equal(sugerenciaPatron, r.SugerenciaPatron);
        }

        [Fact]
        public void MapaNormalizacion_GanaALaHeuristica()
        {
            // Heurística: TEXT → textos (TEXTOS); S-COLS → COLS → columnas; P-SANR-FIXT-OTLN → SANR → Sanitarios.
            Assert.Equal("TEXTOS", LayerClassifier.Suggest("TEXT", Rules));
            Assert.Equal("columnas", LayerClassifier.SuggestForPattern("S-COLS", Rules));
            Assert.Equal("Sanitarios", LayerClassifier.SuggestForPattern("P-SANR-FIXT-OTLN", Rules));

            // Decidido por el usuario (docs/mapa_capas_6x12.json): gana en sugerencia y sugerencia_patron.
            Assert.Equal("EJES", C("TEXT", 250, "Continuous", 20).Sugerencia);
            Assert.Equal("EJES", C("text", 250, "Continuous", 20).Sugerencia); // sin distinguir mayúsculas
            Assert.Null(C("TEXT", 250, "Continuous", 20).SugerenciaPatron); // sin patrón ajeno → null
            Assert.Equal("6. ESTRUCTURA", C("S-COLS", 52, "Continuous", 18).SugerenciaPatron);
            Assert.Equal("muebles fijos", C("P-SANR-FIXT-OTLN", 7, "Continuous", 18).SugerenciaPatron);
        }

        [Fact]
        public void MapaNormalizacion_Lectura_ToleranteYConConceptos()
        {
            using JsonDocument doc = JsonDocument.Parse(@"{
  ""capas_preferidas"": { ""muros"": ""MUROS"" },
  ""capas_permitidas"": { ""MUROS"": { ""color"": 155, ""tipo_linea"": ""Continuous"", ""grosor"": 50 },
                          ""AC"": { ""color"": 7, ""tipo_linea"": ""Continuous"", ""grosor"": -3 } },
  ""capas_mcp"": { ""MCP-DIBUJO"": { ""color"": 3, ""tipo_linea"": ""Continuous"", ""grosor"": -3 } },
  ""mapa_normalizacion"": { ""MURO VIEJO"": ""Muros"", ""X"": 5, ""Y"": """", ""ac"": ""MUROS"", ""MCP-DIBUJO"": ""MUROS"", ""Z"": ""OTRA"" }
}");
            LpsLayerRules r = LpsLayerRules.FromJson(doc.RootElement.Clone());

            // Concepto → su capa; no cadenas y vacías se ignoran; origen permitido o MCP se ignora; destino libre se conserva.
            Assert.Equal(2, r.MapaNormalizacion.Count);
            Assert.Equal("MUROS", r.MapaNormalizacion["muro viejo"]);
            Assert.Equal("OTRA", r.MapaNormalizacion["Z"]);
            Assert.Null(LayerClassifier.Classify("AC", 7, "Continuous", -3, r).Sugerencia);
            Assert.Equal("OTRA", LayerClassifier.Classify("Z", 7, "Continuous", -3, r).Sugerencia);
        }

        [Fact]
        public void MapaNormalizacion_Ausente_EsVacioYNoCambiaNada()
        {
            using JsonDocument doc = JsonDocument.Parse(@"{ ""capas_preferidas"": { ""textos"": ""TEXTOS"" } }");
            LpsLayerRules r = LpsLayerRules.FromJson(doc.RootElement.Clone());
            Assert.Empty(r.MapaNormalizacion);
            Assert.Empty(LpsLayerRules.Empty.MapaNormalizacion);
            Assert.Equal("TEXTOS", LayerClassifier.Classify("TEXT", 7, "Continuous", -3, r).Sugerencia);
        }

        [Fact]
        public void PatronAjeno_Muros_Null()
        {
            LayerClassification r = C("MUROS", 155, "Continuous", 50);
            Assert.Null(r.PatronAjeno);
            Assert.Null(r.SugerenciaPatron);
        }

        [Fact]
        public void PatronAjeno_AWallFull_AjenaConSugerencias()
        {
            LayerClassification r = C("A-WALL-FULL", 113, "Continuous", 18);
            Assert.Equal(LayerClassifier.AjenaRevitAia, r.Clasificacion);
            Assert.Equal("revit_aia", r.PatronAjeno);
            Assert.Equal("MUROS", r.SugerenciaPatron);

            // sugerencia NO cambia en 0.6 (§5F.5): sigue siendo solo por palabras clave en español.
            Assert.Null(r.Sugerencia);
        }

        [Fact]
        public void PatronAjeno_McpObservaciones_Null()
        {
            LayerClassification r = C("MCP-OBSERVACIONES", 1, "Continuous", 35);
            Assert.Equal(LayerClassifier.Mcp, r.Clasificacion);
            Assert.Null(r.PatronAjeno);
            Assert.Null(r.SugerenciaPatron);
        }

        [Theory]
        [InlineData("A-DOOR-____-OTLN", "PUERTAS")]
        [InlineData("I-FURN-____-OTLN", "muebles fijos")]
        [InlineData("A-GLAZ", "ventanas")]
        [InlineData("A-DIMS", "Cotas")]
        [InlineData("A-ANNO-DIMS", "TEXTOS")] // decide el primer campo de la tabla
        [InlineData("S-GRID", "EJES")]
        [InlineData("S-BEAM", "VIGA")]
        [InlineData("A-FLOR-STRS", "ESCALERA")]
        [InlineData("A-PATT", "Achurados")]
        [InlineData("A-FLOR-LEVL", "niveles")]
        [InlineData("P-PLMB-FIXT", "Sanitarios")]
        [InlineData("x_wall_y", "MUROS")]
        [InlineData("A-SITE", null)]
        [InlineData("", null)]
        public void SuggestForPattern_TablaAia(string nombre, string? esperado)
        {
            Assert.Equal(esperado, LayerClassifier.SuggestForPattern(nombre, Rules));
        }

        [Fact]
        public void SuggestForPattern_PalabraClaveEnEspanolGana()
        {
            // "MURO" (palabra clave) gana sobre el código AIA DOOR del segundo campo.
            Assert.Equal("MUROS", LayerClassifier.SuggestForPattern("MURO-DOOR", Rules));
        }

        [Fact]
        public void SuggestForPattern_SinReglas_Null()
        {
            Assert.Null(LayerClassifier.SuggestForPattern("A-WALL", LpsLayerRules.Empty));
        }

        [Fact]
        public void Constantes_OchoClasificacionesEnOrden()
        {
            Assert.Equal(
                new[] { "mcp", "estandar", "estandar_con_diferencias", "cype", "ajena_revit_aia", "ajena_exportacion", "generica", "fuera_de_estandar" },
                LayerClassifier.All);
        }
    }
}
