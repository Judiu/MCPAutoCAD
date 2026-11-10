using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>
    /// Equivalencia métrica de los valores de INSUNITS (protocol.md §8) y factor de escala implícito entre
    /// unidades (§5C.3 a). 18–20 (au, ly, pc), 0 y fuera de rango no tienen equivalencia → null.
    /// </summary>
    public static class UnitsScale
    {
        private static readonly double?[] Meters =
        {
            null,               // 0 desconocida
            0.0254,             // 1 in
            0.3048,             // 2 ft
            1609.344,           // 3 mi
            0.001,              // 4 mm
            0.01,               // 5 cm
            1.0,                // 6 m
            1000.0,             // 7 km
            2.54e-8,            // 8 µin
            2.54e-5,            // 9 mil
            0.9144,             // 10 yd
            1e-10,              // 11 Å
            1e-9,               // 12 nm
            1e-6,               // 13 µm
            0.1,                // 14 dm
            10.0,               // 15 dam
            100.0,              // 16 hm
            1e9,                // 17 Gm
            null,               // 18 au
            null,               // 19 ly
            null,               // 20 pc
            1200.0 / 3937.0,    // 21 us_ft
            100.0 / 3937.0,     // 22 us_in
            3600.0 / 3937.0,    // 23 us_yd
            6336000.0 / 3937.0, // 24 us_mi
        };

        /// <summary>Metros por unidad, o null si INSUNITS no tiene equivalencia métrica.</summary>
        public static double? MetersPerUnit(int insunits)
        {
            if (insunits < 0 || insunits >= Meters.Length)
            {
                return null;
            }

            return Meters[insunits];
        }

        /// <summary>
        /// Factor que AutoCAD aplica al insertar algo definido en <paramref name="from"/> en un dibujo en
        /// <paramref name="to"/>: metros por unidad de <paramref name="from"/> ÷ metros por unidad de <paramref name="to"/>.
        /// null si alguna no tiene equivalencia. Ej.: Factor(4, 6) = 0.001 (bloque en mm en dibujo en m).
        /// </summary>
        public static double? Factor(int from, int to)
        {
            double? a = MetersPerUnit(from);
            double? b = MetersPerUnit(to);
            if (a == null || b == null)
            {
                return null;
            }

            return a.Value / b.Value;
        }

        /// <summary>Tolerancia relativa por defecto para considerar compensada una inserción (§5C.3, 1 %).</summary>
        public const double DefaultTolerance = 0.01;

        /// <summary>
        /// Corrección F4 del gate de Fase 2 (§5C.3): una inserción está compensada si su escala ya lleva aplicado el
        /// factor implícito, es decir |escala / factor − 1| ≤ <paramref name="tolerancia"/>. El llamador pasa la escala
        /// en valor absoluto (|ScaleFactors.X|): un espejo (escala negativa) no cambia las unidades; por robustez aquí
        /// también se toma |escala|. factor ≤ 0, no finito, o escala no finita → false.
        /// </summary>
        public static bool IsCompensated(double escala, double factor, double tolerancia = DefaultTolerance)
        {
            if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(escala) || !double.IsFinite(tolerancia))
            {
                return false;
            }

            // Margen de 1e-9 para el redondeo binario: 0.00101 / 0.001 − 1 da 0.0100000000000002 y debe contar como 1 %.
            return Math.Abs((Math.Abs(escala) / factor) - 1.0) <= tolerancia + 1e-9;
        }

        /// <summary>
        /// Clasifica inserciones (handle, escala) en compensadas y no compensadas respecto de <paramref name="factor"/>,
        /// conservando el orden de entrada. <paramref name="factor"/> null (unidad sin equivalencia métrica) → todas a
        /// noCompensadas; el llamador lo trata como "no evaluable" (§5C.3, severidad media), no como alta.
        /// </summary>
        public static (List<(string handle, double escala)> compensadas, List<(string handle, double escala)> noCompensadas) ClassifyInsertions(
            IEnumerable<(string handle, double escala)> inserciones, double? factor, double tolerancia = DefaultTolerance)
        {
            if (inserciones == null)
            {
                throw new ArgumentNullException(nameof(inserciones));
            }

            var compensadas = new List<(string handle, double escala)>();
            var noCompensadas = new List<(string handle, double escala)>();
            foreach ((string handle, double escala) item in inserciones)
            {
                if (factor != null && IsCompensated(item.escala, factor.Value, tolerancia))
                {
                    compensadas.Add(item);
                }
                else
                {
                    noCompensadas.Add(item);
                }
            }

            return (compensadas, noCompensadas);
        }

        /// <summary>Dimensión mayor mínima (m) para considerar plausible el tamaño real de una inserción (§5C.3, F4).</summary>
        public const double MinPlausibleMeters = 0.02;

        /// <summary>Dimensión mayor máxima (m) para considerar plausible el tamaño real de una inserción (§5C.3, F4).</summary>
        public const double MaxPlausibleMeters = 200.0;

        /// <summary>
        /// Tamaño real en metros [ancho, alto] de una inserción cuya caja en el dibujo mide <paramref name="ancho"/> ×
        /// <paramref name="alto"/> unidades de dibujo, con el dibujo en <paramref name="insunitsDibujo"/>. null si algún
        /// valor no es finito o es negativo, o si el INSUNITS del dibujo no tiene equivalencia métrica.
        /// </summary>
        public static double[]? RealSizeMeters(double ancho, double alto, int insunitsDibujo)
        {
            double? m = MetersPerUnit(insunitsDibujo);
            if (m == null || !double.IsFinite(ancho) || !double.IsFinite(alto) || ancho < 0 || alto < 0)
            {
                return null;
            }

            return new[] { ancho * m.Value, alto * m.Value };
        }

        /// <summary>
        /// true si la dimensión mayor del tamaño real (m) está en [<see cref="MinPlausibleMeters"/>,
        /// <see cref="MaxPlausibleMeters"/>]: la geometría ya está en las unidades del dibujo y lo erróneo es el metadato
        /// de unidades del bloque. null o valores no finitos → false.
        /// </summary>
        public static bool IsPlausibleRealSize(double[]? tamanoRealM)
        {
            if (tamanoRealM == null || tamanoRealM.Length < 2 || !double.IsFinite(tamanoRealM[0]) || !double.IsFinite(tamanoRealM[1]))
            {
                return false;
            }

            double mayor = Math.Max(Math.Abs(tamanoRealM[0]), Math.Abs(tamanoRealM[1]));
            return mayor >= MinPlausibleMeters && mayor <= MaxPlausibleMeters;
        }

        /// <summary>
        /// Severidad de un hallazgo unidades_distintas con alguna inserción NO compensada (§5C.3, decisión F4 del gate,
        /// ratificada): tamaño real plausible (<see cref="IsPlausibleRealSize"/>) → "media" (metadato de unidades
        /// incorrecto, la geometría ya está bien). Desde 0.3.2 "alta" SOLO cuando se MIDE un tamaño implausible; sin
        /// medida (null: inserciones anidadas que no se pudieron llevar a un espacio, o INSUNITS del dibujo sin
        /// equivalencia métrica) → "media" (no medible). Gate en vivo 0.3.1: el bloque 'C' (5 464 inserciones anidadas
        /// a escala 0.045) salía "alta" sin haberse medido nada.
        /// </summary>
        public static string NonCompensatedSeverity(double[]? tamanoRealM)
        {
            if (tamanoRealM == null)
            {
                return Severity.Media;
            }

            return IsPlausibleRealSize(tamanoRealM) ? Severity.Media : Severity.Alta;
        }

        /// <summary>
        /// Mensaje del hallazgo "media" cuando no se pudo medir el tamaño real (§5C.3, 0.3.2): "El bloque 'C' declara
        /// unidades mm y 500 de sus inserciones evaluadas no están compensadas (escala 0.045), pero su tamaño real es no
        /// medible (inserciones anidadas): revise el metadato de unidades del bloque." <paramref name="anidadas"/> = todas
        /// las no compensadas están anidadas en otros bloques.
        /// </summary>
        public static string UnmeasurableMessage(string bloque, string unidadBloque, int nNoCompensadas, string escalas, bool anidadas) =>
            "El bloque '" + bloque + "' declara unidades " + unidadBloque + " y "
            + nNoCompensadas.ToString(CultureInfo.InvariantCulture) + " de sus inserciones evaluadas no están compensadas ("
            + escalas + "), pero su tamaño real es no medible" + (anidadas ? " (inserciones anidadas)" : "")
            + ": revise el metadato de unidades del bloque.";

        /// <summary>Metros con hasta 2 decimales, cultura invariante (p. ej. 0.45).</summary>
        public static string FormatMeters(double v) =>
            Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Efecto de reinsertar el bloque con escala automática de unidades: factor &lt; 1 → "1000× más pequeño"
        /// (1/factor, hasta 4 decimales); factor &gt; 1 → "1000× más grande"; factor = 1 o inválido → "del mismo tamaño".
        /// </summary>
        public static string ReinsertionEffect(double factor)
        {
            if (!double.IsFinite(factor) || factor <= 0 || Math.Abs(factor - 1.0) < 1e-12)
            {
                return "del mismo tamaño";
            }

            double veces = factor < 1 ? 1.0 / factor : factor;
            string txt = Math.Round(veces, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);
            return txt + "× " + (factor < 1 ? "más pequeño" : "más grande");
        }

        /// <summary>
        /// Mensaje del hallazgo "media" de metadato de unidades incorrecto (§5C.3, F4): "El bloque 'lav' declara unidades
        /// mm pero su geometría ya está en las unidades del dibujo (tamaño real ≈ 0.45×0.51 m): el metadato de unidades
        /// es incorrecto; si se reinserta con escala automática quedará 1000× más pequeño."
        /// </summary>
        public static string MetadataMessage(string bloque, string unidadBloque, double[] tamanoRealM, double factor)
        {
            if (tamanoRealM == null || tamanoRealM.Length < 2)
            {
                throw new ArgumentException("tamanoRealM: se esperan [ancho, alto]");
            }

            return "El bloque '" + bloque + "' declara unidades " + unidadBloque
                + " pero su geometría ya está en las unidades del dibujo (tamaño real ≈ " + FormatMeters(tamanoRealM[0]) + "×"
                + FormatMeters(tamanoRealM[1]) + " m): el metadato de unidades es incorrecto; si se reinserta con escala automática quedará "
                + ReinsertionEffect(factor) + ".";
        }
    }
}
