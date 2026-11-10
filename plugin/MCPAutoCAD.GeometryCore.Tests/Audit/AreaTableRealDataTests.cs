using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>
    /// Cuadros de áreas con posiciones, alturas, rotaciones y alineaciones REALES (protocol.md §5C.5, correcciones
    /// F1, F2, F3 y F6 de 0.3.1). Datos extraídos de tests/fixtures/copia_real.dwg (copia del plano de EXP 26-XXXX)
    /// con ODA File Converter + ezdxf y guardados aquí como constantes: la prueba no depende del DWG.
    /// Cajas de los TEXT (aproximan GeometricExtents): izquierda → [x, y]–[x + 0.6·h·len, y + h];
    /// derecha → [x, y]–[ax, y + h]; centro → [x, y]–[2·ax − x, y + h].
    /// </summary>
    public class AreaTableRealDataTests
    {
        private const double AxValA = 3277.8950;   // punto de alineación derecho de la columna de valores de A
        private const double AxValB = 3360.7557;   // ídem B
        private const double HV = 0.1815;          // altura de valores y m²

        private enum Al
        {
            Izq,
            Centro,
            Der,
        }

        private static TextItem T(string handle, string texto, double x, double y, double h, Al al = Al.Izq, double ax = double.NaN)
        {
            BBox2 bbox = al switch
            {
                Al.Der => BBox2.FromCorners(x, y, ax, y + h),
                Al.Centro => BBox2.FromCorners(x, y, 2 * ax - x, y + h),
                _ => BBox2.FromCorners(x, y, x + 0.6 * h * texto.Length, y + h),
            };
            return new TextItem(handle, texto, x, y, h, 0.0, bbox);
        }

        /// <summary>MTEXT rotado: y = Location.Y − altura (como CuadroAreasCheck), sin caja.</summary>
        private static TextItem M(string handle, string texto, double lx, double ly, double h, double rotGrados) =>
            new TextItem(handle, texto, lx, ly - h, h, rotGrados, null);

        // ------------------------------------------------------------------ datos reales (capa Rotulo, metros)

        private static List<TextItem> CuadroA() => new List<TextItem>
        {
            T("95CF3", "AREAS DE PROYECTO ARQUITECTONICO", 3273.6190, 3994.0796, 0.1650, Al.Centro, 3275.9519),
            T("95CF0", "LOTE :", 3273.5386, 3993.5140, 0.1430),
            T("95D38", "72.00", 3277.2605, 3993.4891, HV, Al.Der, AxValA),
            T("95D39", "m²", 3277.9880, 3993.4891, HV, Al.Der, 3278.2836),
            T("95CF7", "AREA PISO 1", 3273.5847, 3993.0617, 0.1430),
            T("95CF8", "52.50", 3277.2605, 3993.0257, HV, Al.Der, AxValA),
            T("95CF9", "m²", 3277.9880, 3993.0257, HV),
            T("95CF6", "AREA PISO 2", 3273.5920, 3992.5668, 0.1430),
            T("95D2D", "56.10", 3277.2605, 3992.5623, HV, Al.Der, AxValA),
            T("95D2E", "m²", 3277.9880, 3992.5623, HV),
            T("95CFA", "AREA PISO 3", 3273.5920, 3992.0718, 0.1430),
            T("95D2F", "56.10", 3277.2605, 3992.0989, HV, Al.Der, AxValA),
            T("95D30", "m²", 3277.9880, 3992.0989, HV),
            T("95CFB", "REMATE PUNTO FIJO", 3273.4918, 3991.6361, 0.1430),
            T("95D31", "7.52", 3277.4015, 3991.6354, HV, Al.Der, AxValA),
            T("95D32", "m²", 3277.9880, 3991.6354, HV),
            T("95CF1", "TOTAL CONSTRUIDO ", 3273.5075, 3991.1641, 0.1430),
            T("95CF2", "172.22", 3277.0674, 3991.1315, 0.2200, Al.Der, 3278.0073),
            T("95CFC", "m²", 3278.1151, 3991.1335, HV, Al.Der, 3278.4108),
            T("95D33", "LIBRE (aislamineto posterior) ", 3273.4861, 3990.6967, 0.1430),
            T("95D35", "19.5", 3277.4092, 3990.6681, 0.2200, Al.Der, 3278.0073),
            T("95D36", "m²", 3278.1151, 3990.6701, HV),
            T("95D21", "INDICES ", 3275.5460, 3989.9317, 0.1778, Al.Centro, 3276.0827),
            T("95D1D", "OCUPACION ", 3273.7041, 3989.4784, 0.1541),
            T("95D24", "0.73", 3277.4407, 3989.4854, 0.1541),
            T("95D4D", "CONSTRUCCION ", 3273.7041, 3989.0299, 0.1541),
            T("95D37", "2.39", 3277.4704, 3989.0292, 0.1541),
            // Filas auxiliares debajo.
            T("95D3E", "ESTACIONAMIENTOS  ", 3274.7332, 3988.1901, 0.1778),
            T("95D41", "PROPUESTO ", 3276.7884, 3987.7437, 0.1541),
            T("95D3A", "EXIGIDOS ", 3275.4025, 3987.7368, 0.1541),
            T("95D4A", "VIVIENDA ", 3273.6237, 3987.2876, 0.1541),
            T("95D4B", "1 X VIV", 3275.5931, 3987.2876, 0.1541),
            T("95D4C", "1", 3277.4704, 3987.2876, 0.1541),
            T("95D52", "CUADRO DE OCUPACION ", 3274.5208, 3986.4484, 0.1778),
            T("95D55", "NO. DE PERSONAS ", 3276.2695, 3986.0021, 0.1541),
            T("95D4E", "R1", 3274.3399, 3985.9952, 0.1541),
            T("95D5D", "UNIFAMILIAR ", 3273.6237, 3985.5459, 0.1541),
            T("95D5E", "6", 3277.1491, 3985.5459, 0.1541),
            T("95D5F", "GRUPO DE USO I: ESTRUCTURA DE OCUPACION NORMAL ", 3273.3967, 3984.8029, 0.1380),
        };

        private static List<TextItem> CuadroB() => new List<TextItem>
        {
            T("8BD45", "AREAS DE PROYECTO ARQUITECTONICO", 3356.4797, 3991.9380, 0.1650, Al.Centro, 3358.8126),
            T("8BD41", "LOTE :", 3356.3993, 3991.3724, 0.1430),
            T("8BD98", "73.11", 3360.1213, 3991.3474, HV, Al.Der, AxValB),
            T("8BD99", "m²", 3360.8487, 3991.3474, HV, Al.Der, 3361.1444),
            T("8BD4A", "AREA PISO 1", 3356.4454, 3990.9201, 0.1430),
            T("8BD4B", "52.50", 3360.1213, 3990.8840, HV, Al.Der, AxValB),
            T("8BD4C", "m²", 3360.8487, 3990.8840, HV),
            T("8BD49", "AREA PISO 2", 3356.4527, 3990.4251, 0.1430),
            T("8BD82", "56.10", 3360.1213, 3990.4206, HV, Al.Der, AxValB),
            T("8BD83", "m²", 3360.8487, 3990.4206, HV),
            T("8BD4D", "AREA PISO 3", 3356.4527, 3989.9301, 0.1430),
            T("8BD84", "56.10", 3360.1213, 3989.9572, HV, Al.Der, AxValB),
            T("8BD85", "m²", 3360.8487, 3989.9572, HV),
            T("8BD4E", "REMATE PUNTO FIJO", 3356.3526, 3989.4945, 0.1430),
            T("8BD86", "7.52", 3360.2623, 3989.4938, HV, Al.Der, AxValB),
            T("8BD87", "m²", 3360.8487, 3989.4938, HV),
            T("8BD42", "TOTAL CONSTRUIDO ", 3356.3683, 3989.0224, 0.1430),
            T("8BD44", "172.22", 3359.9281, 3988.9898, 0.2200, Al.Der, 3360.8681),
            T("8BD4F", "m²", 3360.9759, 3988.9918, HV),
            T("8BD88", "LIBRE (aislamineto posterior) ", 3356.3468, 3988.5550, 0.1430),
            T("8BD8D", "20.61", 3360.0990, 3988.5264, 0.2200, Al.Der, 3360.8681),
            T("8BD8E", "m²", 3360.9759, 3988.5284, HV),
            T("8BD75", "INDICES ", 3358.4067, 3987.7900, 0.1778),
            T("8BD71", "OCUPACION ", 3356.5649, 3987.3368, 0.1541),
            T("8BD78", "0.72", 3360.3014, 3987.3437, 0.1541),
            T("8BF77", "CONSTRUCCION ", 3356.5649, 3986.8882, 0.1541),
            T("8BD95", "2.36", 3360.3311, 3986.8875, 0.1541),
            T("8BF68", "ESTACIONAMIENTOS  ", 3357.5939, 3986.0484, 0.1778),
            T("8BF6B", "PROPUESTO ", 3359.6491, 3985.6021, 0.1541),
            T("8BF64", "EXIGIDOS ", 3358.2632, 3985.5951, 0.1541),
            T("8BF74", "VIVIENDA ", 3356.4844, 3985.1459, 0.1541),
            T("8BF75", "1 X VIV", 3358.4538, 3985.1459, 0.1541),
            T("8BF76", "1", 3360.3311, 3985.1459, 0.1541),
            T("8C53B", "CUADRO DE OCUPACION ", 3357.3816, 3984.3068, 0.1778),
            T("8C53E", "NO. DE PERSONAS ", 3359.1302, 3983.8604, 0.1541),
            T("8C537", "R1", 3357.2007, 3983.8535, 0.1541),
            T("8C547", "UNIFAMILIAR ", 3356.4844, 3983.4043, 0.1541),
            T("8C549", "6", 3360.0099, 3983.4043, 0.1541),
            T("8C6E6", "GRUPO DE USO I: ESTRUCTURA DE OCUPACION NORMAL ", 3356.2575, 3982.6613, 0.1380),
        };

        /// <summary>MTEXT rotados del plano de localización (capa TEXTOS).</summary>
        private static List<TextItem> RuidoRotado() => new List<TextItem>
        {
            M("B76A5", "LOTE 20", 3312.0453, 3996.0537, 0.1, -55.52),
            M("B76A7", "MANZANA U", 3314.5539, 3995.4403, 0.2, 27.08),
            M("B76B2", "CALLE 9I", 3314.5750, 3994.3191, 0.2, -63.85),
            M("B76B4", "CALLE 9H", 3312.3677, 3993.0842, 0.2, -63.85),
        };

        /// <summary>Restos de una versión anterior (capa MUROS, TEXT rot 0) fuera de los viewports de los layouts.</summary>
        private static List<TextItem> Restos()
        {
            const double h1 = 0.3709;
            const double h2 = 0.1286;
            return new List<TextItem>
            {
                // Resto 1.
                T("1A460", "CUADRO DE AREAS", -2149.6411, 3832.9805, h1),
                T("1A461", "ÁREA LOTE", -2151.2664, 3831.6611, h1),
                T("1A462", "72.10 m2", -2144.5234, 3831.6611, h1),
                T("1A46A", "ÁREA CONSTRUIDA PISO 1", -2151.2664, 3831.0241, h1),
                T("1A46B", "50.45 m2", -2144.5234, 3831.0241, h1),
                T("30A14", "ÁREA CONSTRUIDA PISO 2", -2151.2664, 3830.3285, h1),
                T("1A463", "55.71 m2", -2144.5641, 3830.3638, h1),
                T("30A15", "ÁREA CONSTRUIDA PISO 3", -2151.2664, 3829.6669, h1),
                T("1A470", "55.71 m2", -2144.5641, 3829.6814, h1),
                T("1A464", "TOTAL CONSTRUIDO", -2151.2664, 3828.4047, h1),
                T("1A465", "161.87m2", -2144.7586, 3828.4139, h1),
                T("1A46C", "AREA LIBRE", -2151.2664, 3827.7576, h1),
                T("1A46D", " 21.65m2", -2144.8880, 3827.7669, h1),
                T("1A46E", "I.C", -2151.2664, 3827.1106, h1),
                T("1A46F", "0.70%%%", -2144.7586, 3827.1198, h1),
                T("1A466", "I.O", -2151.2664, 3826.4744, h1),
                T("1A467", "2.25%%%", -2144.7131, 3826.4744, h1),
                T("1B21C", "106.4", -2144.7586, 3825.1188, h1),
                // Resto 2.
                T("17712", "CUADRO DE AREAS", -2192.0946, 3724.2180, 0.1597),
                T("17713", "AREA DEL LOTE", -2193.9384, 3723.7756, h2),
                T("1771C", "72", -2189.3781, 3723.7756, h2),
                T("17714", "AREA PRIMER PISO CONSTRUIDA", -2193.9384, 3723.3752, h2),
                T("17733", "50.15", -2189.3781, 3723.2481, h2),
                T("17728", "AREA SEGUNDO  PISO CONSTRUIDA", -2193.9384, 3722.6760, h2),
                T("17734", "55.89", -2189.3781, 3722.5715, h2),
                T("1772C", "AREA A REFORZAR PRIMER PISO ", -2193.9384, 3722.0499, h2),
                T("17735", "50.15", -2189.3781, 3722.0241, h2),
                T("1772E", "AREA A REFORZAR SEGUNDO PISO ", -2193.9384, 3721.6390, h2),
                T("17736", "55.89", -2189.3781, 3721.6146, h2),
                T("1772A", "AREA TERCER  PISO CONSTRUIDA", -2193.9384, 3721.2229, h2),
                T("17737", "55.89", -2189.3781, 3721.1160, h2),
                T("17730", "AREA TOTAL CONSTRUIDA ", -2193.9384, 3720.5624, h2),
                T("17738", "161.93", -2189.3781, 3720.5563, h2),
                T("17732", "AREA TOTAL CONSTRUIDA ", -2193.9384, 3720.1734, h2),
                T("17739", "21.85", -2189.3781, 3720.1527, h2),
                T("1773A", "INDICE DE OCUPACION ", -2193.9384, 3719.7593, h2),
                T("1773E", "0.7", -2189.3781, 3719.7663, h2),
                T("1773B", "INDICE DE CONSTRUCCION ", -2193.9384, 3719.4152, h2),
                T("1773F", "2.25", -2189.3781, 3719.4176, h2),
            };
        }

        /// <summary>
        /// Ventanas de modelo reales de los viewports de papel (Layout1 nº2, Layout1 nº3, Layout2 nº2). Layout1 nº3 tiene
        /// ViewTarget (2590.96, 1494.53) ≠ 0: su centro real es ViewTarget + ViewCenter ≈ (3294.44, 3995.03), es decir,
        /// la ventana x≈3272.7…3316.2, y≈3978.5…4011.5, que contiene el cuadro A (ya en layout por Layout2).
        /// </summary>
        private static List<BBox2> VentanasReales() => new List<BBox2>
        {
            ViewportWindow.FromViewport(2938.2225, 3774.9610, 367.3573, 205.6, 156, 0),
            ViewportWindow.FromViewportTarget(2590.96, 1494.53, 703.4832, 2500.5022, 33.0001, 0.869, 0.66, 0),
            ViewportWindow.FromViewport(3359.3424, 3758.9783, 644.9657, 205.6, 156, 0),
        };

        private static List<TextItem> PlanoSinRestos()
        {
            var items = new List<TextItem>();
            items.AddRange(RuidoRotado());
            items.AddRange(CuadroA());
            items.AddRange(CuadroB());
            return items;
        }

        private static List<TextItem> PlanoConRestos()
        {
            List<TextItem> items = PlanoSinRestos();
            items.AddRange(Restos());
            return items;
        }

        private static IEnumerable<Finding> ByRule(AreaTableResult r, string rule) =>
            r.Hallazgos.Where(f => Equals(f.Datos["regla"], rule));

        private static readonly string[] Internas = { "suma_pisos", "libre", "ocupacion", "construccion" };

        private static readonly (string etiqueta, double a, double b)[] Esperados =
        {
            ("LOTE", 72.0, 73.11), ("AREA PISO 1", 52.5, 52.5), ("AREA PISO 2", 56.1, 56.1), ("AREA PISO 3", 56.1, 56.1),
            ("REMATE PUNTO FIJO", 7.52, 7.52), ("TOTAL CONSTRUIDO", 172.22, 172.22), ("LIBRE", 19.5, 20.61),
            ("OCUPACION", 0.73, 0.72), ("CONSTRUCCION", 2.39, 2.36),
        };

        private static AreaTableBox BoxWithLote(AreaTableResult r, double lote) =>
            r.Cuadros.Single(c => c.Valores.TryGetValue("LOTE", out double v) && Math.Abs(v - lote) < 1e-9);

        // ------------------------------------------------------------------ (a) A + B + auxiliares + ruido rotado

        [Fact]
        public void PlanoReal_DosCuadrosCompletos_IndicesAnexados_CuatroDiscrepantesAlta()
        {
            AreaTableResult r = AreaTable.Parse(PlanoSinRestos());

            Assert.Equal(2, r.Cuadros.Count);
            AreaTableBox a = r.Cuadros[0];
            AreaTableBox b = r.Cuadros[1];
            Assert.Equal("cuadro-1", a.Id);
            Assert.Equal("AREAS DE PROYECTO ARQUITECTONICO", a.Titulo);
            Assert.Equal(9, a.Valores.Count);
            Assert.Equal(9, b.Valores.Count);
            foreach ((string etiqueta, double va, double vb) in Esperados)
            {
                Assert.Equal(va, a.Valores[etiqueta], 9);
                Assert.Equal(vb, b.Valores[etiqueta], 9);
            }

            // F1: el "LOTE 20" rotado no crea cuadro ni valor.
            Assert.DoesNotContain(r.Cuadros, c => c.Valores.TryGetValue("LOTE", out double v) && Math.Abs(v - 20) < 1e-9);

            // F2: OCUPACION/CONSTRUCCION anexadas a su cuadro con LOTE, al final (de arriba abajo).
            Assert.Equal(("95D1D", (string?)"95D24"), a.Handles["OCUPACION"]);
            Assert.Equal(("95D4D", (string?)"95D37"), a.Handles["CONSTRUCCION"]);
            Assert.Equal(("8BD71", (string?)"8BD78"), b.Handles["OCUPACION"]);
            Assert.Equal(("8BF77", (string?)"8BD95"), b.Handles["CONSTRUCCION"]);
            Assert.Equal(new[] { "OCUPACION", "CONSTRUCCION" }, a.Filas.Skip(a.Filas.Count - 2).Select(f => f.Etiqueta));
            foreach (AreaTableBox c in r.Cuadros)
            {
                Assert.Equal(Internas, c.ReglasEvaluadas);
                Assert.Empty(c.ReglasNoEvaluables);
                Assert.True(c.EnLayout);
            }

            // Los auxiliares 'CUADRO DE OCUPACION' y 'GRUPO DE USO I…' (OCUPACION sin valor) no desplazan los valores.
            Assert.DoesNotContain(r.Cuadros, c => c.Handles.Values.Any(h => h.etiqueta == "95D52" || h.etiqueta == "95D5F"));

            foreach (string rule in Internas)
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
                Assert.Equal("en_layout", f.Datos["ambito"]);
                Assert.False(f.Datos.ContainsKey("cuadros_fuera_de_layout"));
            });
            Assert.Equal("LOTE difiere entre cuadros: cuadro-1 = 72.00, cuadro-2 = 73.11.", disc[0].Mensaje);

            // F6: la caja del cuadro cubre la columna de valores y los m² (no solo los puntos de inserción).
            Assert.True(a.Bbox!.Value.Xmax >= 3278.28, "Xmax de A = " + a.Bbox.Value.Xmax);
            Assert.True(b.Bbox!.Value.Xmax >= 3361.14, "Xmax de B = " + b.Bbox.Value.Xmax);
            // La caja del m² de 72.00 (95D39) queda dentro de la del cuadro A.
            Assert.True(a.Bbox.Value.Contains(3278.2836, 3993.4891 + HV));
            Assert.True(a.Bbox.Value.Ymin <= 3989.0292);
            Assert.Equal(0, r.NCuadrosFueraDeLayout);
        }

        [Fact]
        public void PlanoReal_BboxDelHallazgo_UsaExtensionGeometrica()
        {
            AreaTableResult r = AreaTable.Parse(PlanoSinRestos());
            Finding lote = ByRule(r, "cuadros_discrepantes").First(f => Equals(f.Datos["etiqueta"], "LOTE"));
            Assert.Equal(new[] { "95CF0", "95D38", "8BD41", "8BD98" }, lote.Handles);
            // Borde derecho del valor 73.11 (alineado a la derecha en ax = 3360.7557), no su punto de inserción.
            Assert.Equal(AxValB, lote.Bbox!.Value.Xmax, 6);
        }

        // ------------------------------------------------------------------ (b) restos + ventanas reales

        [Fact]
        public void PlanoReal_ConRestosYViewports_FueraDeLayoutYPasadaMedia()
        {
            AreaTableResult r = AreaTable.Parse(PlanoConRestos(), VentanasReales());

            AreaTableBox a = r.Cuadros.Single(c => c.Handles.TryGetValue("LOTE", out var h) && h.etiqueta == "95CF0");
            AreaTableBox b = r.Cuadros.Single(c => c.Handles.TryGetValue("LOTE", out var h) && h.etiqueta == "8BD41");
            Assert.True(a.EnLayout);
            Assert.True(b.EnLayout);
            Assert.Equal("cuadro-1", a.Id);
            Assert.Equal("cuadro-2", b.Id);

            // Lo que el parser reconoce en los restos (sin forzar más): 4 cuadros fuera de layout.
            List<AreaTableBox> restos = r.Cuadros.Where(c => c != a && c != b).ToList();
            Assert.Equal(4, restos.Count);
            Assert.All(restos, c => Assert.False(c.EnLayout));
            Assert.Equal(4, r.NCuadrosFueraDeLayout);
            Assert.Equal(new[] { "cuadro-3", "cuadro-4", "cuadro-5", "cuadro-6" }, r.IdsFueraDeLayout);

            AreaTableBox r1Lote = restos.Single(c => c.Valores.ContainsKey("LOTE") && Math.Abs(c.Valores["LOTE"] - 72.10) < 1e-9);
            Assert.Single(r1Lote.Valores);
            AreaTableBox r1Total = restos.Single(c => c.Valores.ContainsKey("TOTAL CONSTRUIDO"));
            Assert.Equal(2, r1Total.Valores.Count);
            Assert.Equal(161.87, r1Total.Valores["TOTAL CONSTRUIDO"], 9);
            Assert.Equal(21.65, r1Total.Valores["LIBRE"], 9);
            AreaTableBox r2Lote = restos.Single(c => c.Valores.ContainsKey("LOTE") && Math.Abs(c.Valores["LOTE"] - 72) < 1e-9);
            Assert.Single(r2Lote.Valores);
            AreaTableBox r2Ind = restos.Single(c => c.Valores.ContainsKey("OCUPACION"));
            Assert.Equal(2, r2Ind.Valores.Count);
            Assert.Equal(0.7, r2Ind.Valores["OCUPACION"], 9);
            Assert.Equal(2.25, r2Ind.Valores["CONSTRUCCION"], 9);
            // El cuadro de índices del resto 2 está a 4 m de su LOTE (> 15·0,1286): no se anexa y lo declara.
            Assert.Contains(("ocupacion", "falta AREA PISO 1"), r2Ind.ReglasNoEvaluables);

            // Pasada (i): los 4 alta entre A y B se conservan.
            List<Finding> alta = ByRule(r, "cuadros_discrepantes").Where(f => f.Severidad == Severity.Alta).ToList();
            Assert.Equal(new[] { "LOTE", "LIBRE", "OCUPACION", "CONSTRUCCION" }, alta.Select(f => (string)f.Datos["etiqueta"]!));
            Assert.All(alta, f => Assert.Equal("en_layout", f.Datos["ambito"]));
            Assert.Equal("LOTE difiere entre cuadros: cuadro-1 = 72.00, cuadro-2 = 73.11.", alta[0].Mensaje);

            // Pasada (ii): media con el sufijo y los ids fuera de layout.
            List<Finding> media = ByRule(r, "cuadros_discrepantes").Where(f => f.Severidad == Severity.Media).ToList();
            Assert.All(media, f =>
            {
                Assert.Equal("con_fuera_de_layout", f.Datos["ambito"]);
                Assert.EndsWith(" (cuadro fuera de los layouts: posible versión anterior)", f.Mensaje);
            });

            Finding loteMedia = media.Single(f => Equals(f.Datos["etiqueta"], "LOTE"));
            Assert.Equal(
                "LOTE difiere entre cuadros: cuadro-1 = 72.00, cuadro-2 = 73.11, cuadro-3 = 72.10, cuadro-5 = 72.00."
                + " (cuadro fuera de los layouts: posible versión anterior)",
                loteMedia.Mensaje);
            Assert.Equal(new[] { r1Lote.Id, r2Lote.Id }, (List<string>)loteMedia.Datos["cuadros_fuera_de_layout"]!);
            var valores = (List<Dictionary<string, object?>>)loteMedia.Datos["valores"]!;
            Assert.Equal(new[] { 72.0, 73.11, 72.10, 72.0 }, valores.Select(v => (double)v["valor"]!));

            Finding totalMedia = media.Single(f => Equals(f.Datos["etiqueta"], "TOTAL CONSTRUIDO"));
            Assert.Contains("172.22", totalMedia.Mensaje);
            Assert.Contains("161.87", totalMedia.Mensaje);
            Assert.Equal(new[] { r1Total.Id }, (List<string>)totalMedia.Datos["cuadros_fuera_de_layout"]!);
            // TOTAL CONSTRUIDO: A = B, así que no hay hallazgo alta.
            Assert.DoesNotContain(alta, f => Equals(f.Datos["etiqueta"], "TOTAL CONSTRUIDO"));

            // LIBRE (21.65), OCUPACION (0.70) y CONSTRUCCION (2.25) de los restos también difieren.
            Assert.Equal(new[] { "LOTE", "TOTAL CONSTRUIDO", "LIBRE", "OCUPACION", "CONSTRUCCION" },
                media.Select(f => (string)f.Datos["etiqueta"]!));

            // Ningún hallazgo interno en ningún cuadro (los restos no tienen etiquetas suficientes).
            foreach (string rule in Internas)
            {
                Assert.Empty(ByRule(r, rule));
            }
        }

        // ------------------------------------------------------------------ (c) sin ventanas → conducta 0.3.0

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PlanoReal_ConRestosSinVentanas_TodosEnLayoutYSoloPasadaAlta(bool listaVacia)
        {
            AreaTableResult r = listaVacia
                ? AreaTable.Parse(PlanoConRestos(), new List<BBox2>())
                : AreaTable.Parse(PlanoConRestos(), null);

            Assert.Equal(6, r.Cuadros.Count);
            Assert.All(r.Cuadros, c => Assert.True(c.EnLayout));
            Assert.Equal(0, r.NCuadrosFueraDeLayout);

            List<Finding> disc = ByRule(r, "cuadros_discrepantes").ToList();
            Assert.Equal(r.Hallazgos.Count, disc.Count);
            Assert.All(disc, f =>
            {
                Assert.Equal(Severity.Alta, f.Severidad);
                Assert.Equal("en_layout", f.Datos["ambito"]);
                Assert.DoesNotContain("fuera de los layouts", f.Mensaje);
            });
            Assert.Equal(new[] { "LOTE", "TOTAL CONSTRUIDO", "LIBRE", "OCUPACION", "CONSTRUCCION" },
                disc.Select(f => (string)f.Datos["etiqueta"]!));
        }

        [Fact]
        public void PlanoReal_SoloCuadrosEnLayout_SinPasadaMedia()
        {
            // Con las ventanas reales pero sin restos: A y B dentro → idéntico a (a).
            AreaTableResult r = AreaTable.Parse(PlanoSinRestos(), VentanasReales());
            Assert.All(r.Cuadros, c => Assert.True(c.EnLayout));
            Assert.Equal(4, r.Hallazgos.Count);
            Assert.All(r.Hallazgos, f => Assert.Equal(Severity.Alta, f.Severidad));
        }

        [Fact]
        public void FueraDeLayout_ConValorIgual_NoGeneraMedia()
        {
            // A y B en layout (72.00 / 73.11) y un resto fuera con LOTE 72: no aporta un valor nuevo → solo la alta.
            var items = PlanoSinRestos();
            items.Add(T("X1", "AREA DEL LOTE", -2193.9384, 3723.7756, 0.1286));
            items.Add(T("X2", "72", -2189.3781, 3723.7756, 0.1286));
            AreaTableResult r = AreaTable.Parse(items, VentanasReales());
            Assert.Equal(1, r.NCuadrosFueraDeLayout);
            Assert.DoesNotContain(r.Hallazgos, f => f.Severidad == Severity.Media);
            Assert.Equal(4, r.Hallazgos.Count);
        }

        // ------------------------------------------------------------------ (d) texto combinado (F1)

        [Theory]
        [InlineData("LOTE: 72.00 m2", 72.0)]
        [InlineData("LOTE 72,5", 72.5)]
        [InlineData("LOTE 72 m2", 72.0)]
        [InlineData("ÁREA LOTE = 72 m²", 72.0)]
        public void Combinado_ConDecimalesOUnidad_SeAcepta(string texto, double esperado)
        {
            Assert.True(AreaTable.TryParseCombined(texto, out string etiqueta, out double v));
            Assert.Equal("LOTE", etiqueta);
            Assert.Equal(esperado, v, 9);
        }

        [Theory]
        [InlineData("LOTE 20")]
        [InlineData("LOTE 72")]
        [InlineData("MANZANA U LOTE 20")]
        [InlineData("LOTE 7")]
        public void Combinado_EnteroSinUnidad_EsNumeroDeLote(string texto)
        {
            Assert.False(AreaTable.TryParseCombined(texto, out _, out _));
        }

        [Fact]
        public void LoteVeinteHorizontal_SeDescartaEnteroYNoCapturaValorVecino()
        {
            // "LOTE 20" con rotación 0 y un número a su derecha: no es fila combinada ni etiqueta suelta.
            var items = new List<TextItem>
            {
                new TextItem("L20", "LOTE 20", 0, 10, 0.18),
                new TextItem("V1", "72.00", 4, 10, 0.18),
            };

            Assert.Empty(AreaTable.Parse(items).Cuadros);
        }

        // ------------------------------------------------------------------ (e) rotación (F1)

        [Theory]
        [InlineData(90.0, false)]
        [InlineData(304.48, false)]
        [InlineData(-55.52, false)]
        [InlineData(1.5, false)]
        [InlineData(0.0, true)]
        [InlineData(180.0, true)]
        [InlineData(180.9, true)]
        [InlineData(359.5, true)]
        [InlineData(-0.5, true)]
        [InlineData(720.5, true)]
        public void TextoRotado_SoloHorizontalesSonCandidatos(double rot, bool aceptado)
        {
            var items = new List<TextItem>
            {
                new TextItem("L1", "LOTE :", 0, 10, 0.18, rot, null),
                new TextItem("V1", "72.00", 4, 10, 0.18, rot, null),
            };

            AreaTableResult r = AreaTable.Parse(items);
            Assert.Equal(aceptado ? 1 : 0, r.Cuadros.Count);
            Assert.Equal(aceptado, AreaTable.IsHorizontal(rot));
        }

        // ------------------------------------------------------------------ (f) índices sin LOTE (F2)

        [Fact]
        public void IndicesSolos_CuadroPropioConReglasNoEvaluables()
        {
            var items = new List<TextItem>
            {
                T("O1", "OCUPACION ", 0, 10, 0.1541),
                T("O2", "0.73", 4, 10, 0.1541),
                T("C1", "CONSTRUCCION ", 0, 9.55, 0.1541),
                T("C2", "2.39", 4, 9.55, 0.1541),
            };

            AreaTableBox box = Assert.Single(AreaTable.Parse(items).Cuadros);
            Assert.Empty(box.ReglasEvaluadas);
            // Motivo = primera etiqueta que falta en el orden de la tabla de reglas de §5C.5.
            Assert.Equal(new[]
            {
                ("suma_pisos", "falta TOTAL CONSTRUIDO"),
                ("libre", "falta LOTE"),
                ("ocupacion", "falta AREA PISO 1"),
                ("construccion", "falta TOTAL CONSTRUIDO"),
            }, box.ReglasNoEvaluables);
        }

        [Fact]
        public void FaltaLote_ConAreaPiso1_MotivoFaltaLote()
        {
            var items = new List<TextItem>
            {
                T("P1", "AREA PISO 1", 0, 11, 0.15),
                T("P2", "52.50", 4, 11, 0.15),
                T("T1", "TOTAL CONSTRUIDO", 0, 10.5, 0.15),
                T("T2", "52.50", 4, 10.5, 0.15),
                T("O1", "OCUPACION", 0, 10, 0.15),
                T("O2", "0.73", 4, 10, 0.15),
            };

            AreaTableBox box = Assert.Single(AreaTable.Parse(items).Cuadros);
            Assert.Equal(new[] { "suma_pisos" }, box.ReglasEvaluadas);
            Assert.Contains(("ocupacion", "falta LOTE"), box.ReglasNoEvaluables);
            Assert.Contains(("libre", "falta LOTE"), box.ReglasNoEvaluables);
            Assert.Contains(("construccion", "falta CONSTRUCCION"), box.ReglasNoEvaluables);
        }

        [Fact]
        public void LoteCero_MotivoLoteNoMayorQueCero()
        {
            var items = new List<TextItem>
            {
                T("L1", "LOTE", 0, 11, 0.15),
                T("L2", "0.00", 4, 11, 0.15),
                T("P1", "AREA PISO 1", 0, 10.5, 0.15),
                T("P2", "52.50", 4, 10.5, 0.15),
                T("O1", "OCUPACION", 0, 10, 0.15),
                T("O2", "0.73", 4, 10, 0.15),
            };

            AreaTableBox box = Assert.Single(AreaTable.Parse(items).Cuadros);
            Assert.Contains(("ocupacion", "LOTE no es mayor que 0"), box.ReglasNoEvaluables);
        }

        [Fact]
        public void Indices_DemasiadoLejosODesplazadosEnX_NoSeAnexan()
        {
            // Cuadro A real sin sus índices + índices a 3 m por debajo de LIBRE (> 15·hmed ≈ 2,5) y otros
            // desplazados 20 m en X a la altura correcta.
            List<TextItem> a = CuadroA().Where(t => t.Handle is not ("95D1D" or "95D24" or "95D4D" or "95D37")).ToList();
            a.Add(T("O1", "OCUPACION ", 3273.7041, 3987.6, 0.1541));
            a.Add(T("O2", "0.73", 3277.4407, 3987.6, 0.1541));
            a.Add(T("O3", "OCUPACION ", 3293.7041, 3989.4784, 0.1541));
            a.Add(T("O4", "0.73", 3297.4407, 3989.4784, 0.1541));
            AreaTableResult r = AreaTable.Parse(a);
            Assert.Equal(3, r.Cuadros.Count);
            AreaTableBox main = BoxWithLote(r, 72.0);
            Assert.False(main.Valores.ContainsKey("OCUPACION"));
            Assert.Contains(("ocupacion", "falta OCUPACION"), main.ReglasNoEvaluables);
        }

        [Fact]
        public void Anexion_ValorRepetido_GanaElDelReceptor()
        {
            // El receptor ya declara OCUPACION 0.73; la fila de índices de abajo declara 0.99: gana la de arriba.
            List<TextItem> a = CuadroA();
            a.Add(T("Z1", "OCUPACION ", 3273.7041, 3988.6, 0.1541));
            a.Add(T("Z2", "0.99", 3277.4407, 3988.6, 0.1541));
            AreaTableResult r = AreaTable.Parse(a);
            AreaTableBox box = Assert.Single(r.Cuadros);
            Assert.Equal(0.73, box.Valores["OCUPACION"], 9);
            Assert.Equal(("95D1D", (string?)"95D24"), box.Handles["OCUPACION"]);
        }

        // ------------------------------------------------------------------ (F6) bbox null = conducta 0.3.0

        [Fact]
        public void SinBbox_CajaDelCuadroIgualAPuntosDeInsercion()
        {
            // Constructor de 5 argumentos (rotación 0, sin caja), como en 0.3.0; sin el ruido rotado.
            List<TextItem> sinCaja = CuadroA().Concat(CuadroB())
                .Select(t => new TextItem(t.Handle, t.Texto, t.X, t.Y, t.Altura))
                .ToList();
            AreaTableResult r = AreaTable.Parse(sinCaja);

            Assert.Equal(2, r.Cuadros.Count);
            foreach (AreaTableBox box in r.Cuadros)
            {
                var texts = new List<TextItem>();
                foreach (AreaTableRow row in box.Filas)
                {
                    texts.Add(row.Label);
                    if (row.ValueText != null)
                    {
                        texts.Add(row.ValueText);
                    }
                }

                Assert.Equal(texts.Min(t => t.X), box.Bbox!.Value.Xmin, 9);
                Assert.Equal(texts.Max(t => t.X), box.Bbox.Value.Xmax, 9);
                Assert.Equal(texts.Min(t => t.Y), box.Bbox.Value.Ymin, 9);
                Assert.Equal(texts.Max(t => t.Y + t.Altura), box.Bbox.Value.Ymax, 9);
            }

            // Sin cajas el valor 72.00 queda en su punto de inserción (3277.2605): la columna se "corta".
            Assert.True(r.Cuadros[0].Bbox!.Value.Xmax < 3277.9);
        }

        [Fact]
        public void Unidades_SoloAmplianLaCaja_NoSonHandles()
        {
            AreaTableResult r = AreaTable.Parse(CuadroA());
            AreaTableBox a = Assert.Single(r.Cuadros);
            Assert.DoesNotContain(a.Handles.Values, h => h.valor == "95D39" || h.etiqueta == "95D39");
            // m² de TOTAL CONSTRUIDO (ax 3278.4108) es el borde derecho del cuadro.
            Assert.Equal(3278.4108, a.Bbox!.Value.Xmax, 6);
        }
    }
}
