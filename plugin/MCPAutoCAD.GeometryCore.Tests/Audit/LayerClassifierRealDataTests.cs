using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>
    /// auditar_capas con el estándar v2 (standards/lps.json, decisión del usuario del 2026-10-28) sobre las capas REALES
    /// de dos planos. Datos extraídos con ODA File Converter 27.1 + ezdxf de copias de tests/fixtures (nombre, color ACI,
    /// tipo de línea, grosor): (1) copia_real.dwg, plano madre 6X12 sin limpiar (161 capas); (2) el 6X12 normalizado = las
    /// 21 capas de docs/mapa_capas_6x12.json "resultado_copia", con las propiedades que tienen en casa6x12_limpio.dwg
    /// (normalizar_capas no cambia las propiedades de las capas destino). Las cifras esperadas se calcularon aparte con
    /// una reimplementación en Python de §5C.1 y deben coincidir con <see cref="LayerClassifier"/>. Con el estándar v1
    /// (las 161 capas del plano madre) las 161 capas de copia_real salían "estandar".
    /// </summary>
    public class LayerClassifierRealDataTests
    {
        private static LpsLayerRules Rules => LayerClassifierTests.RealRules;

        /// <summary>Las 161 capas de copia_real.dwg (orden ordinal sin mayúsculas).</summary>
        internal static readonly (string nombre, int color, string tipoLinea, int grosor)[] CopiaReal =
        {
            ("0", 7, "Continuous", -3),
            ("0.4", 4, "Continuous", 40),
            ("01-NIVELES", 82, "Continuous", -3),
            ("1. Muros", 160, "Continuous", 35),
            ("10. LINEA PROYECCIÓN", 251, "Continuous", -3),
            ("12. ESTRUCTURA", 72, "Continuous", 20),
            ("13. TECHO ESTRUCTURA", 1, "Continuous", 20),
            ("14. TECHO LÁMINA", 7, "Continuous", 20),
            ("15. EJES", 2, "Punto&Rayas", 35),
            ("17. SUPERBOARD", 251, "Continuous", -3),
            ("2. PISO", 251, "Continuous", 9),
            ("2D Drafting - General_Pen_No__1", 7, "Continuous", 15),
            ("2D Drafting - General_Pen_No__2", 8, "Continuous", 15),
            ("2D Drafting - General_Pen_No__62", 253, "Continuous", 15),
            ("2D Drafting - General_Pen_No__82", 253, "Continuous", 15),
            ("3. MOBILIARIO", 104, "Continuous", 0),
            ("3D EJES ESTRUCTURALES_Pen_No__19", 255, "Continuous", 15),
            ("3D EJES ESTRUCTURALES_Pen_No__7", 7, "Continuous", 15),
            ("3D LINEAS DE PLANTA_Pen_No__7", 7, "Continuous", 15),
            ("4. VENTANAS", 44, "Continuous", 0),
            ("5. PUERTAS", 2, "Continuous", 0),
            ("5_P_VIVIENDA", 7, "Continuous", 20),
            ("6. ACHURADOS", 252, "Continuous", 0),
            ("6. ESTRUCTURA", 5, "Continuous", 70),
            ("7. LINEA CORTE", 7, "Continuous", 0),
            ("8. TEXTO", 53, "Continuous", -3),
            ("9. Cotas", 2, "Continuous", -3),
            ("_Above_ Riser Lines", 7, "Trazo pequeño", 18),
            ("A-DETL-GENF", 92, "Continuous", 18),
            ("A-DH TEXTOS", 250, "Continuous", 13),
            ("A-DOOR-____-OTLN", 7, "Continuous", 18),
            ("A-FLOR", 192, "Continuous", 25),
            ("A-GENM", 13, "Continuous", 18),
            ("A-GLAZ-____-OTLN", 7, "Continuous", 18),
            ("A-GLAZ-____-OTLN-1", 55, "Continuous", 18),
            ("A-GLAZ-CWMG", 51, "Continuous", 18),
            ("A-MURO-ACHU", 252, "Continuous", 30),
            ("A-ROOF", 132, "Continuous", 18),
            ("A-WALL", 113, "Continuous", 18),
            ("AC", 7, "Continuous", -3),
            ("ACHU", 8, "Continuous", 20),
            ("ACHURADO", 253, "Continuous", 0),
            ("Achurados", 151, "Continuous", 9),
            ("ACOTACIONES", 2, "Continuous", 20),
            ("ADICIONES", 8, "Continuous", 5),
            ("AGUAS N", 95, "Continuous", 20),
            ("AMOBLA", 9, "Continuous", -3),
            ("Antepecho", 201, "Continuous", 20),
            ("ATRÁS", 251, "Continuous", 9),
            ("BARRIOS", 7, "Continuous", 5),
            ("BARRIOS_Pen_No__7", 7, "Continuous", 15),
            ("Capa1", 30, "Continuous", -3),
            ("Capa2", 150, "Continuous", -3),
            ("CICLOP", 6, "Continuous", -3),
            ("CODIGOMANZANA", 7, "Continuous", 15),
            ("CODPREDIO", 8, "Continuous", 0),
            ("CODPREDIO_Pen_No__8", 14, "Continuous", 15),
            ("columnas", 1, "Continuous", -3),
            ("CONCRETO", 254, "Continuous", -3),
            ("CORTE", 220, "Continuous", 80),
            ("COTA", 4, "Continuous", -3),
            ("COTACOL", 161, "Continuous", -3),
            ("Cotas", 8, "Continuous", 13),
            ("COTAS-VIGAS", 7, "Continuous", -3),
            ("CT", 20, "Continuous", -3),
            ("DEFPOINTS", 7, "Continuous", -3),
            ("DELGADA", 1, "Continuous", 9),
            ("DEMOLICIÓN", 1, "Continuous", 0),
            ("DESAGUES", 181, "Continuous", 25),
            ("DETALLES", 7, "Continuous", -3),
            ("Dibujo 2D - General_Pen_No__102", 254, "Continuous", 15),
            ("Dibujo 2D - General_Pen_No__142", 8, "Continuous", 15),
            ("DINTEL", 1, "Continuous", -3),
            ("DISENO", 152, "Continuous", -3),
            ("E-ELEC-EQPM-OTLN", 7, "Continuous", 18),
            ("EDIFICACION", 9, "Continuous", 0),
            ("EDIFICACION_Pen_No__9", 135, "Continuous", 15),
            ("EJES", 1, "CENTER", 5),
            ("Electrical Equipment", 7, "Continuous", 18),
            ("Elevation Swing", 7, "Dash 1_16_", 18),
            ("ESC", 6, "Continuous", -3),
            ("ESCALERA", 6, "Continuous", 5),
            ("ESCALERAS", 232, "Continuous", 15),
            ("ESTRUCTURA", 250, "Continuous", 60),
            ("Filos", 3, "Continuous", 15),
            ("Furniture", 7, "Continuous", 18),
            ("G-IMPT", 8, "Continuous", 18),
            ("GRUESA", 2, "Continuous", -3),
            ("GUIA P ILUMINACION", 2, "Continuous", 0),
            ("hatch 1", 253, "Continuous", 18),
            ("Hidden P-V", 252, "HIDDEN", 0),
            ("HIDRAULICO sifones sanitarios", 5, "Continuous", 5),
            ("HIERRO", 1, "Continuous", -3),
            ("I-FURN", 30, "Continuous", 18),
            ("I-FURN-____-OTLN", 7, "Continuous", 18),
            ("LADRILLO", 30, "Continuous", -3),
            ("Layer5", 6, "Continuous", -3),
            ("Layer6", 2, "Continuous", -3),
            ("Layer7", 3, "Continuous", -3),
            ("LCOR", 52, "Continuous", -3),
            ("LINEA DE EVACUACION", 80, "JIS_02_0.7", 30),
            ("LINEAS SUAVES", 250, "Continuous", 5),
            ("LOGO1", 7, "Continuous", -3),
            ("m fijos", 2, "Continuous", 0),
            ("MAD", 55, "Continuous", 0),
            ("MAMPOSTERIA", 30, "Continuous", -3),
            ("MEDIO", 181, "Continuous", 15),
            ("Mobiliario", 251, "Continuous", 0),
            ("MU", 6, "Continuous", -3),
            ("MUEB", 43, "Continuous", -3),
            ("MUEBLE", 33, "Continuous", 13),
            ("Mueble Electro", 122, "Continuous", 5),
            ("muebles", 253, "Continuous", 5),
            ("muebles fijos", 2, "Continuous", -3),
            ("MUEBLES2", 251, "Continuous", 5),
            ("MUR BAAAJO", 241, "Continuous", 20),
            ("MURALZADA", 250, "Continuous", 20),
            ("MURO", 44, "Continuous", 60),
            ("MUROESTRUCTURAL", 20, "Continuous", 5),
            ("MUROS", 155, "Continuous", 50),
            ("New_3D MUEBLES_Pen_No__4", 104, "Continuous", 13),
            ("New_3D MUEBLES_Pen_No__86", 173, "Continuous", 13),
            ("niveles", 8, "Continuous", -3),
            ("NOMENCLATURA", 7, "Continuous", 20),
            ("P-SANR-FIXT-OTLN", 7, "Continuous", 18),
            ("PDF2_Text", 7, "Continuous", -3),
            ("piso", 11, "Continuous", -3),
            ("PREDIOURBANO", 8, "Continuous", 9),
            ("PREDIOURBANO_Pen_No__8", 14, "Continuous", 15),
            ("Proyeccion", 220, "CONTINUA", 5),
            ("Proyeccion 1", 161, "Continuous", -3),
            ("Puerta -Ventana", 6, "Continuous", 0),
            ("PUERTAS", 9, "Continuous", -3),
            ("PUERTAS VENTANAS", 251, "Continuous", 0),
            ("punteadas", 150, "ACAD_ISO02W100", 15),
            ("Q-SPCQ-____-OTLN", 7, "Continuous", 18),
            ("RE", 3, "Continuous", -3),
            ("REFUERZO", 3, "Continuous", 30),
            ("Rotulo", 150, "Continuous", 80),
            ("S-BEAM", 12, "Continuous", 18),
            ("S-COLS", 52, "Continuous", 18),
            ("Sanitarios", 6, "Continuous", 5),
            ("Specialty Equipment", 7, "Continuous", 18),
            ("T3", 20, "Continuous", -3),
            ("TEJA", 250, "Continuous", 20),
            ("TEJALI2", 250, "Continuous", 13),
            ("TEXT", 250, "Continuous", 20),
            ("TEXTO", 253, "Continuous", 5),
            ("TEXTO 0.4", 4, "Continuous", 5),
            ("TEXTOS", 152, "Continuous", 15),
            ("TEXTOS 2", 6, "Continuous", -3),
            ("TITULO-TEXTOS", 3, "Continuous", -3),
            ("VEN1ALZADA", 250, "Continuous", 13),
            ("VEN3ALZADA", 250, "Continuous", 5),
            ("VENT", 7, "Continuous", -3),
            ("VENTANA PUERTA", 250, "Continuous", 13),
            ("ventanas", 6, "Continuous", 9),
            ("VIGA", 2, "Continuous", -3),
            ("Vigas", 2, "Continuous", 30),
            ("Walls", 7, "Continuous", 18),
            ("Zapatas", 30, "Continuous", 50),
        };

        /// <summary>Las 21 capas del 6X12 normalizado (docs/mapa_capas_6x12.json, resultado_copia).</summary>
        internal static readonly (string nombre, int color, string tipoLinea, int grosor)[] SeisX12Normalizado =
        {
            ("0", 7, "Continuous", -3),
            ("6. ESTRUCTURA", 5, "Continuous", 70),
            ("AC", 7, "Continuous", -3),
            ("Achurados", 151, "Continuous", 9),
            ("Antepecho", 201, "Continuous", 20),
            ("columnas", 1, "Continuous", -3),
            ("Cotas", 8, "Continuous", 13),
            ("DEFPOINTS", 7, "Continuous", -3),
            ("EJES", 1, "CENTER", 5),
            ("Filos", 3, "Continuous", 15),
            ("I-FURN-____-OTLN", 7, "Continuous", 18),
            ("m fijos", 2, "Continuous", 0),
            ("muebles fijos", 2, "Continuous", -3),
            ("MUR BAAAJO", 241, "Continuous", 20),
            ("MUROS", 155, "Continuous", 50),
            ("Proyeccion", 220, "CONTINUA", 5),
            ("PUERTAS", 9, "Continuous", -3),
            ("Rotulo", 150, "Continuous", 80),
            ("Sanitarios", 6, "Continuous", 5),
            ("TEXTOS", 152, "Continuous", 15),
            ("ventanas", 6, "Continuous", 9),
        };

        private static Dictionary<string, int> Count((string nombre, int color, string tipoLinea, int grosor)[] capas)
        {
            var counts = new Dictionary<string, int>();
            foreach (string c in LayerClassifier.All)
            {
                counts[c] = 0;
            }

            foreach (var c in capas)
            {
                counts[LayerClassifier.Classify(c.nombre, c.color, c.tipoLinea, c.grosor, Rules).Clasificacion]++;
            }

            return counts;
        }

        private static LayerClassification Of((string nombre, int color, string tipoLinea, int grosor)[] capas, string nombre)
        {
            foreach (var c in capas)
            {
                if (c.nombre == nombre)
                {
                    return LayerClassifier.Classify(c.nombre, c.color, c.tipoLinea, c.grosor, Rules);
                }
            }

            throw new KeyNotFoundException(nombre);
        }

        [Fact]
        public void CopiaReal_PorClasificacion()
        {
            Assert.Equal(161, CopiaReal.Length);
            Assert.Equal(161, CopiaReal.Select(c => c.nombre.ToUpperInvariant()).Distinct().Count());
            Dictionary<string, int> n = Count(CopiaReal);
            Assert.Equal(0, n[LayerClassifier.Mcp]);
            Assert.Equal(24, n[LayerClassifier.Estandar]);
            Assert.Equal(0, n[LayerClassifier.EstandarConDiferencias]);
            Assert.Equal(0, n[LayerClassifier.Cype]);
            Assert.Equal(18, n[LayerClassifier.AjenaRevitAia]);
            Assert.Equal(16, n[LayerClassifier.AjenaExportacion]);
            Assert.Equal(5, n[LayerClassifier.Generica]);
            Assert.Equal(98, n[LayerClassifier.FueraDeEstandar]);
        }

        [Fact]
        public void CopiaReal_LasEstandarSonExactamenteLasPermitidasDeLaV2()
        {
            // Las 24 capas de capas_permitidas existen en el plano madre con las mismas propiedades.
            List<string> estandar = CopiaReal
                .Where(c => LayerClassifier.Classify(c.nombre, c.color, c.tipoLinea, c.grosor, Rules).Clasificacion == LayerClassifier.Estandar)
                .Select(c => c.nombre.ToUpperInvariant())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            List<string> permitidas = Rules.CapasPermitidas.Keys
                .Select(k => k.ToUpperInvariant())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(permitidas, estandar);
        }

        [Theory]
        // nombre, clasificación, sugerencia, patron_ajeno, sugerencia_patron
        [InlineData("MUROS", "estandar", null, null, null)]
        [InlineData("AC", "estandar", null, null, null)]
        [InlineData("S-COLS", "ajena_revit_aia", "6. ESTRUCTURA", "revit_aia", "6. ESTRUCTURA")] // el mapa gana a COLS → columnas
        [InlineData("Q-SPCQ-____-OTLN", "ajena_revit_aia", "muebles fijos", "revit_aia", "muebles fijos")]
        [InlineData("P-SANR-FIXT-OTLN", "ajena_revit_aia", "muebles fijos", "revit_aia", "muebles fijos")] // el mapa gana a SANR → Sanitarios
        [InlineData("A-DOOR-____-OTLN", "ajena_revit_aia", "PUERTAS", "revit_aia", "PUERTAS")]
        [InlineData("I-FURN-____-OTLN", "ajena_revit_aia", null, "revit_aia", "muebles fijos")] // no está en el mapa decidido
        [InlineData("A-WALL", "ajena_revit_aia", null, "revit_aia", "MUROS")]
        [InlineData("S-BEAM", "ajena_revit_aia", null, "revit_aia", "VIGA")]
        [InlineData("3D EJES ESTRUCTURALES_Pen_No__7", "ajena_exportacion", "TEXTOS", "exportacion", "TEXTOS")] // el mapa gana a EJE → EJES
        [InlineData("3D EJES ESTRUCTURALES_Pen_No__19", "ajena_exportacion", "EJES", "exportacion", "EJES")]
        [InlineData("Dibujo 2D - General_Pen_No__142", "ajena_exportacion", "Rotulo", "exportacion", "Rotulo")]
        [InlineData("2D Drafting - General_Pen_No__1", "ajena_exportacion", null, "exportacion", null)]
        [InlineData("PDF2_Text", "ajena_exportacion", "TEXTOS", "exportacion", "TEXTOS")]
        [InlineData("Layer5", "generica", null, "generica", null)]
        [InlineData("Capa2", "generica", null, "generica", null)]
        [InlineData("TEXT", "fuera_de_estandar", "EJES", null, null)] // el mapa gana a TEXT → TEXTOS
        [InlineData("ACHU", "fuera_de_estandar", "6. ESTRUCTURA", null, null)]
        [InlineData("Furniture", "fuera_de_estandar", "muebles fijos", null, null)]
        [InlineData("MUEBLE", "fuera_de_estandar", "muebles fijos", null, null)]
        [InlineData("1. Muros", "fuera_de_estandar", "MUROS", null, null)]
        [InlineData("ESC", "fuera_de_estandar", null, null, null)]
        public void CopiaReal_CapasConcretas(string nombre, string clasif, string? sugerencia, string? patron, string? sugerenciaPatron)
        {
            LayerClassification r = Of(CopiaReal, nombre);
            Assert.Equal(clasif, r.Clasificacion);
            Assert.Empty(r.Diferencias);
            Assert.Equal(sugerencia, r.Sugerencia);
            Assert.Equal(patron, r.PatronAjeno);
            Assert.Equal(sugerenciaPatron, r.SugerenciaPatron);
        }

        [Fact]
        public void SeisX12Normalizado_VeinteEstandarYSoloIFurnAjena()
        {
            Assert.Equal(21, SeisX12Normalizado.Length);
            Dictionary<string, int> n = Count(SeisX12Normalizado);
            Assert.Equal(20, n[LayerClassifier.Estandar]);
            Assert.Equal(1, n[LayerClassifier.AjenaRevitAia]);
            Assert.Equal(21, n.Values.Sum());

            foreach (var c in SeisX12Normalizado)
            {
                LayerClassification r = LayerClassifier.Classify(c.nombre, c.color, c.tipoLinea, c.grosor, Rules);
                if (c.nombre == "I-FURN-____-OTLN")
                {
                    Assert.Equal(LayerClassifier.AjenaRevitAia, r.Clasificacion);
                    Assert.Null(r.Sugerencia);
                    Assert.Equal("revit_aia", r.PatronAjeno);
                    Assert.Equal("muebles fijos", r.SugerenciaPatron);
                }
                else
                {
                    Assert.True(r.Clasificacion == LayerClassifier.Estandar, c.nombre + " → " + r.Clasificacion);
                    Assert.Empty(r.Diferencias);
                    Assert.Null(r.PatronAjeno);
                }
            }
        }
    }
}
