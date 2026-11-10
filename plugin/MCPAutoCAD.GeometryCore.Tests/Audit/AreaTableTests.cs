using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>
    /// Cuadros de áreas (protocol.md §5C.5), con el caso obligatorio de los dos cuadros reales del plano madre
    /// (EXP 26-XXXX): cada cuadro es coherente por dentro y difieren LOTE, LIBRE, OCUPACION y CONSTRUCCION.
    /// </summary>
    public class AreaTableTests
    {
        private const double H = 0.18;

        /// <summary>Etiquetas tal como aparecen en el plano (con acentos y ceros a la izquierda).</summary>
        private static readonly string[] Labels =
        {
            "ÁREA LOTE", "ÁREA PISO 01", "ÁREA PISO 02", "ÁREA PISO 03", "REMATE PUNTO FIJO",
            "TOTAL CONSTRUIDO", "ÁREA LIBRE", "ÍNDICE DE OCUPACIÓN", "ÍNDICE DE CONSTRUCCIÓN",
        };

        private static readonly string[] ValuesA = { "72.00", "52.50", "56.10", "56.10", "7.52", "172.22", "19.5", "0.73", "2.39" };

        private static readonly string[] ValuesB = { "73.11", "52.50", "56.10", "56.10", "7.52", "172.22", "20.61", "0.72", "2.36" };

        /// <summary>
        /// Cuadro sintético: etiquetas en x = <paramref name="xLabel"/>, valores en x = <paramref name="xValue"/>,
        /// filas desde <paramref name="yTop"/> cada 1.0 hacia abajo; título una fila por encima. Handles "&lt;p&gt;L&lt;i&gt;" y
        /// "&lt;p&gt;V&lt;i&gt;".
        /// </summary>
        private static List<TextItem> Box(string prefix, double xLabel, double xValue, double yTop, string[] values, bool title = true)
        {
            var items = new List<TextItem>();
            if (title)
            {
                items.Add(new TextItem(prefix + "T", "ÁREAS DE PROYECTO", xLabel, yTop + 1.0, H));
            }

            for (int i = 0; i < Labels.Length; i++)
            {
                double y = yTop - i;
                items.Add(new TextItem(prefix + "L" + i, Labels[i], xLabel, y, H));
                items.Add(new TextItem(prefix + "V" + i, values[i], xValue, y, H));
            }

            return items;
        }

        private static List<TextItem> DosCuadros()
        {
            var items = new List<TextItem>();
            items.AddRange(Box("A", 3273, 3277, 3994, ValuesA));
            items.AddRange(Box("B", 3356, 3360, 3992, ValuesB));
            // Ruido: textos que no son del cuadro.
            items.Add(new TextItem("N1", "COCINA", 3100, 3900, 0.2));
            items.Add(new TextItem("N2", "3.50", 3105, 3900, 0.2));
            items.Add(new TextItem("N3", "PLANTA PRIMER PISO", 3000, 3800, 0.5));
            return items;
        }

        private static IEnumerable<Finding> ByRule(AreaTableResult r, string rule) =>
            r.Hallazgos.Where(f => Equals(f.Datos["regla"], rule));

        [Fact]
        public void DosCuadros_DatosReales_CuatroDiscrepantesYCeroInternos()
        {
            AreaTableResult r = AreaTable.Parse(DosCuadros());

            Assert.Equal(2, r.Cuadros.Count);
            Assert.Equal("cuadro-1", r.Cuadros[0].Id);
            Assert.Equal(72.0, r.Cuadros[0].Valores["LOTE"], 9);
            Assert.Equal(73.11, r.Cuadros[1].Valores["LOTE"], 9);
            Assert.Equal(9, r.Cuadros[0].Valores.Count);
            Assert.Equal(9, r.Cuadros[1].Valores.Count);
            Assert.Equal("ÁREAS DE PROYECTO", r.Cuadros[0].Titulo);

            // Cero hallazgos internos: ambos cuadros son coherentes por dentro.
            foreach (string rule in new[] { "suma_pisos", "libre", "ocupacion", "construccion" })
            {
                Assert.Empty(ByRule(r, rule));
            }

            List<Finding> disc = ByRule(r, "cuadros_discrepantes").ToList();
            Assert.Equal(4, disc.Count);
            Assert.Equal(4, r.Hallazgos.Count);
            Assert.Equal(new[] { "LOTE", "LIBRE", "OCUPACION", "CONSTRUCCION" }, disc.Select(f => (string)f.Datos["etiqueta"]!));
            Assert.All(disc, f =>
            {
                Assert.Equal(Severity.Alta, f.Severidad);
                Assert.Equal("verificar_cuadro_areas", f.Regla);
                Assert.Equal("", f.Id);
                Assert.Equal(4, f.Handles.Count);
                Assert.NotNull(f.Bbox);
            });

            Finding lote = disc[0];
            Assert.Equal(new[] { "AL0", "AV0", "BL0", "BV0" }, lote.Handles);
            Assert.Equal("LOTE difiere entre cuadros: cuadro-1 = 72.00, cuadro-2 = 73.11.", lote.Mensaje);
            var valores = (List<Dictionary<string, object?>>)lote.Datos["valores"]!;
            Assert.Equal("cuadro-1", valores[0]["cuadro"]);
            Assert.Equal(72.0, (double)valores[0]["valor"]!, 9);
            Assert.Equal("cuadro-2", valores[1]["cuadro"]);

            // Caja = unión de las posiciones (más la altura del texto en y).
            Assert.Equal(3273.0, lote.Bbox!.Value.Xmin, 9);
            Assert.Equal(3360.0, lote.Bbox.Value.Xmax, 9);
            Assert.Equal(3992.0, lote.Bbox.Value.Ymin, 9);
            Assert.Equal(3994.0 + H, lote.Bbox.Value.Ymax, 9);
        }

        [Fact]
        public void DosCuadros_SinTitulo_TambienSeSeparan()
        {
            var items = new List<TextItem>();
            items.AddRange(Box("A", 3273, 3277, 3994, ValuesA, title: false));
            items.AddRange(Box("B", 3356, 3360, 3992, ValuesB, title: false));
            AreaTableResult r = AreaTable.Parse(items);
            Assert.Equal(2, r.Cuadros.Count);
            Assert.Equal(4, r.Hallazgos.Count);
        }

        [Fact]
        public void DosCuadrosApilados_SeDividenPorAnclas()
        {
            // Mismo x, separados solo 9.5 filas: la componente conexa se parte por los títulos.
            var items = new List<TextItem>();
            items.AddRange(Box("A", 0, 4, 20, ValuesA));
            items.AddRange(Box("B", 0, 4, 9.5, ValuesB));
            AreaTableResult r = AreaTable.Parse(items);
            Assert.Equal(2, r.Cuadros.Count);
            Assert.Equal(72.0, r.Cuadros[0].Valores["LOTE"], 9);
            Assert.Equal(73.11, r.Cuadros[1].Valores["LOTE"], 9);
            Assert.Equal(4, ByRule(r, "cuadros_discrepantes").Count());
        }

        [Fact]
        public void TotalCambiado_SumaPisosYConstruccion()
        {
            string[] values = (string[])ValuesA.Clone();
            values[5] = "170.00";
            AreaTableResult r = AreaTable.Parse(Box("A", 3273, 3277, 3994, values));

            Assert.Single(r.Cuadros);
            Assert.Equal(2, r.Hallazgos.Count);

            Finding suma = Assert.Single(ByRule(r, "suma_pisos"));
            Assert.Equal(Severity.Alta, suma.Severidad);
            Assert.Equal(172.22, (double)suma.Datos["esperado"]!, 9);
            Assert.Equal(170.0, (double)suma.Datos["declarado"]!, 9);
            Assert.Equal(-2.22, (double)suma.Datos["diferencia"]!, 9);
            Assert.Equal("cuadro-1", suma.Datos["cuadro"]);
            Assert.Equal("TOTAL CONSTRUIDO", suma.Datos["etiqueta"]);
            // Etiqueta y valor de los 3 pisos, el remate y el total.
            Assert.Equal(10, suma.Handles.Count);
            Assert.Contains("AL5", suma.Handles);
            Assert.Contains("AV5", suma.Handles);

            Finding cons = Assert.Single(ByRule(r, "construccion"));
            Assert.Equal(Severity.Media, cons.Severidad);
            Assert.Equal(2.36, (double)cons.Datos["esperado"]!, 9);
            Assert.Equal(2.39, (double)cons.Datos["declarado"]!, 9);
        }

        [Fact]
        public void Libre_YOcupacion_Incoherentes()
        {
            string[] values = (string[])ValuesA.Clone();
            values[6] = "21.00"; // LOTE − PISO 1 = 19.50
            values[7] = "0.80";  // 52.5 / 72 = 0.73
            AreaTableResult r = AreaTable.Parse(Box("A", 0, 4, 10, values));
            Finding libre = Assert.Single(ByRule(r, "libre"));
            Assert.Equal(Severity.Media, libre.Severidad);
            Assert.Equal(19.5, (double)libre.Datos["esperado"]!, 9);
            Finding ocup = Assert.Single(ByRule(r, "ocupacion"));
            Assert.Equal(0.73, (double)ocup.Datos["esperado"]!, 9);
            Assert.Equal(2, r.Hallazgos.Count);
        }

        [Fact]
        public void EtiquetaConValorEnElMismoTexto()
        {
            var items = new List<TextItem>
            {
                new TextItem("C1", "LOTE: 72.00 m2", 0, 10, H),
                new TextItem("C2", "ÁREA PISO 1 = 52,50 m²", 0, 9, H),
                new TextItem("C3", "AREA LIBRE 19.50", 0, 8, H),
            };

            AreaTableResult r = AreaTable.Parse(items);
            AreaTableBox box = Assert.Single(r.Cuadros);
            Assert.Equal(72.0, box.Valores["LOTE"], 9);
            Assert.Equal(52.5, box.Valores["AREA PISO 1"], 9);
            Assert.Equal(19.5, box.Valores["LIBRE"], 9);
            Assert.Equal(("C1", (string?)null), box.Handles["LOTE"]);
            Assert.Empty(r.Hallazgos);
        }

        [Fact]
        public void EtiquetaSinValor_SeDevuelvePeroNoParticipa()
        {
            var items = new List<TextItem>
            {
                new TextItem("L1", "ÁREA LOTE", 0, 10, H),
                new TextItem("V1", "72.00", 4, 10, H),
                new TextItem("L2", "ÁREA LIBRE", 0, 9, H),
                new TextItem("L3", "ÁREA PISO 1", 0, 8, H),
                new TextItem("V3", "52.50", 4, 8, H),
            };

            AreaTableResult r = AreaTable.Parse(items);
            AreaTableBox box = Assert.Single(r.Cuadros);
            Assert.Equal(new[] { "LIBRE" }, box.EtiquetasSinValor);
            Assert.False(box.Valores.ContainsKey("LIBRE"));
            Assert.Empty(r.Hallazgos);
        }

        [Fact]
        public void CuadroSinNingunValor_SeDescarta()
        {
            var items = new List<TextItem>
            {
                new TextItem("L1", "ÁREAS DE PROYECTO", 0, 11, H),
                new TextItem("L2", "ÁREA LOTE", 0, 10, H),
            };

            Assert.Empty(AreaTable.Parse(items).Cuadros);
        }

        [Theory]
        [InlineData("ÁREA PISO 01", "AREA PISO 1")]
        [InlineData("Área piso 1:", "AREA PISO 1")]
        [InlineData("AREAS PISO 012", "AREA PISO 12")]
        [InlineData("ÁREAS DEL PROYECTO", "TITULO")]
        [InlineData("TOTAL ÁREA CONSTRUIDA", "TOTAL CONSTRUIDO")]
        [InlineData("REMATE PUNTO FIJO m2", "REMATE PUNTO FIJO")]
        [InlineData("Área lote", "LOTE")]
        [InlineData("LOTE", "LOTE")]
        [InlineData("ÁREA DEL LOTE", "LOTE")]
        [InlineData("ÁREA DE LOTE:", "LOTE")]
        [InlineData("ÁREA TOTAL LOTE", "LOTE")]
        [InlineData("ÍNDICE DE OCUPACIÓN", "OCUPACION")]
        [InlineData("Í. CONSTRUCCIÓN", "CONSTRUCCION")]
        public void NormalizacionDeEtiquetas(string text, string expected)
        {
            Assert.Equal(expected, AreaTable.CanonicalLabel(text));
        }

        [Theory]
        [InlineData("COCINA")]
        [InlineData("AREA PISO 0")]
        [InlineData("")]
        [InlineData("AISLAMIENTO LOTE")] // B5, plugin 0.8.1: texto de un export de Revit, no la etiqueta del área
        [InlineData("LINDERO LOTE VECINO")]
        [InlineData("MANZANA U LOTE")]
        public void EtiquetasNoReconocidas(string text)
        {
            Assert.Null(AreaTable.CanonicalLabel(text));
        }

        [Fact]
        public void NormalizeLabel_QuitaAcentosEspaciosYSufijo()
        {
            Assert.Equal("AREAPISO01", AreaTable.NormalizeLabel("Área piso 01 m²"));
        }

        [Theory]
        [InlineData("72.00", 72.0)]
        [InlineData("52,50", 52.5)]
        [InlineData(" 7.52 m2", 7.52)]
        [InlineData("-3", -3.0)]
        [InlineData("172.22M²", 172.22)]
        public void ValoresNumericos(string text, double expected)
        {
            Assert.True(AreaTable.TryParseValue(text, out double v));
            Assert.Equal(expected, v, 9);
        }

        [Theory]
        [InlineData("1.234,5")]
        [InlineData("72.")]
        [InlineData("abc")]
        [InlineData("")]
        public void ValoresNoNumericos(string text)
        {
            Assert.False(AreaTable.TryParseValue(text, out _));
        }

        [Fact]
        public void Emparejamiento_CadaValorUnaSolaVez_YSoloALaDerecha()
        {
            // Dos etiquetas en la misma fila (x = 0 y x = 3) y un solo valor a la derecha de ambas (x = 5):
            // gana la de menor Δx (LIBRE); LOTE queda sin valor. El valor a la izquierda (x = -2) no se usa.
            var items = new List<TextItem>
            {
                new TextItem("L1", "ÁREA LOTE", 0, 10, H),
                new TextItem("L2", "ÁREA LIBRE", 3, 10, H),
                new TextItem("V1", "19.50", 5, 10.05, H),
                new TextItem("V0", "99.00", -2, 10, H),
            };

            AreaTableBox box = Assert.Single(AreaTable.Parse(items).Cuadros);
            Assert.Equal(19.5, box.Valores["LIBRE"], 9);
            Assert.Contains("LOTE", box.EtiquetasSinValor);
        }

        [Fact]
        public void Emparejamiento_ExigeMismaFila()
        {
            // |Δy| = 0.2 > 0.6·0.18 = 0.108: no se empareja.
            var items = new List<TextItem>
            {
                new TextItem("L1", "ÁREA LOTE", 0, 10, H),
                new TextItem("V1", "72.00", 4, 10.2, H),
            };

            Assert.Empty(AreaTable.Parse(items).Cuadros);
        }

        [Fact]
        public void TextosLargosOMultilinea_SeIgnoran()
        {
            var items = new List<TextItem>
            {
                new TextItem("L1", "ÁREA LOTE\nX", 0, 10, H),
                new TextItem("V1", "72.00", 4, 10, H),
                new TextItem("L2", new string('L', 121) + "LOTE", 0, 9, H),
            };

            Assert.Empty(AreaTable.Parse(items).Cuadros);
        }

        [Fact]
        public void MasDeVeinteCuadros_SeCortaYAdvierte()
        {
            var items = new List<TextItem>();
            for (int k = 0; k < 22; k++)
            {
                double x = k * 100;
                items.Add(new TextItem("L" + k, "LOTE", x, 0, H));
                items.Add(new TextItem("V" + k, "72", x + 4, 0, H));
            }

            AreaTableResult r = AreaTable.Parse(items);
            Assert.Equal(20, r.Cuadros.Count);
            Assert.Contains(AreaTable.WarningTooManyBoxes, r.Advertencias);
            Assert.Equal("cuadro-20", r.Cuadros[19].Id);
            // Ordenados por y del ancla desc y luego x asc.
            Assert.Equal("L0", r.Cuadros[0].Handles["LOTE"].etiqueta);
        }

        [Fact]
        public void CompareContour_ConTolerancia()
        {
            AreaTableResult r = AreaTable.Parse(DosCuadros());

            // 72.05 vs 72.00: |0.05| ≤ max(0.01, 0.072) → sin hallazgo en cuadro-1; vs 73.11 (cuadro-2) sí.
            List<Finding> f1 = AreaTable.CompareContour(r, "LOTE", "ABCD", 72.05);
            Finding only = Assert.Single(f1);
            Assert.Equal("cuadro-2", only.Datos["cuadro"]);
            Assert.Equal("contorno_vs_declarado", only.Datos["regla"]);
            Assert.Equal(Severity.Alta, only.Severidad);
            Assert.Equal(new[] { "BL0", "BV0", "ABCD" }, only.Handles);
            Assert.Equal(72.05, (double)only.Datos["esperado"]!, 9);
            Assert.Equal(73.11, (double)only.Datos["declarado"]!, 9);

            // 72.10 vs 72.00: 0.10 > 0.072 → hallazgo en ambos cuadros.
            Assert.Equal(2, AreaTable.CompareContour(r, "LOTE", "ABCD", 72.10).Count);

            // Etiqueta que ningún cuadro declara → lista vacía (el handler advierte).
            Assert.Empty(AreaTable.CompareContour(r, "AREA PISO 7", "ABCD", 10));
        }

        [Fact]
        public void CompareContour_UneLaCajaDelContorno()
        {
            AreaTableResult r = AreaTable.Parse(Box("A", 0, 4, 10, ValuesA));
            Finding f = Assert.Single(AreaTable.CompareContour(r, "LOTE", "C0", 80, BBox2.FromCorners(-50, -50, -40, -40)));
            Assert.Equal(-50.0, f.Bbox!.Value.Xmin, 9);
            Assert.Equal(10.0 + H, f.Bbox.Value.Ymax, 9);
        }

        [Theory]
        [InlineData("LOTE", true)]
        [InlineData("AREA PISO 3", true)]
        [InlineData("TOTAL CONSTRUIDO", true)]
        [InlineData("OCUPACION", false)]
        [InlineData("TITULO", false)]
        [InlineData("lote", false)]
        public void EtiquetasDeContorno(string label, bool ok)
        {
            Assert.Equal(ok, AreaTable.IsContourLabel(label));
        }

        [Fact]
        public void Mensajes_ConFormatoInvariante()
        {
            string[] values = (string[])ValuesA.Clone();
            values[5] = "170.00";
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("es-CO");
                AreaTableResult r = AreaTable.Parse(Box("A", 0, 4, 10, values));
                Assert.Contains("172.22", Assert.Single(ByRule(r, "suma_pisos")).Mensaje);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
