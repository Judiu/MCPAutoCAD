using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Tabla INSUNITS → nombre corto de unidad (protocol.md §8) y advertencias de unidades (§2.1 y §5.2).
    /// </summary>
    public static class UnitsTable
    {
        /// <summary>Advertencia obligatoria cuando INSUNITS = 0 (texto exacto de protocol.md §2.1).</summary>
        public const string WarningUnitsUnknown =
            "INSUNITS=0: el dibujo no declara unidades; los valores se devuelven en unidades de dibujo sin interpretar.";

        /// <summary>MEASUREMENT imperial con INSUNITS métrico (texto exacto de protocol.md §5.2).</summary>
        public const string WarningImperialWithMetric =
            "MEASUREMENT=0 (imperial) con INSUNITS métrico: unidades inconsistentes.";

        /// <summary>
        /// Caso inverso: MEASUREMENT métrico con INSUNITS imperial. protocol.md §5.2 solo dice "o viceversa";
        /// este texto es el espejo exacto del anterior.
        /// </summary>
        public const string WarningMetricWithImperial =
            "MEASUREMENT=1 (métrico) con INSUNITS imperial: unidades inconsistentes.";

        public const string Unknown = "desconocida";

        private static readonly string[] Names =
        {
            Unknown,     // 0
            "in",        // 1
            "ft",        // 2
            "mi",        // 3
            "mm",        // 4
            "cm",        // 5
            "m",         // 6
            "km",        // 7
            "µin",  // 8  (µin, signo micro U+00B5 como en protocol.md)
            "mil",       // 9
            "yd",        // 10
            "Å",    // 11 (Å, U+00C5 como en protocol.md)
            "nm",        // 12
            "µm",   // 13 (µm)
            "dm",        // 14
            "dam",       // 15
            "hm",        // 16
            "Gm",        // 17
            "au",        // 18
            "ly",        // 19
            "pc",        // 20
            "us_ft",     // 21
            "us_in",     // 22
            "us_yd",     // 23
            "us_mi",     // 24
        };

        /// <summary>Nombres en español (columna "español" de protocol.md §8), usados en la advertencia LPS (§2.2).</summary>
        private static readonly string[] SpanishNames =
        {
            "desconocida",            // 0
            "pulgadas",               // 1
            "pies",                   // 2
            "millas",                 // 3
            "milímetros",             // 4
            "centímetros",            // 5
            "metros",                 // 6
            "kilómetros",             // 7
            "micropulgadas",          // 8
            "milésimas de pulgada",   // 9
            "yardas",                 // 10
            "ångströms",              // 11
            "nanómetros",             // 12
            "micrómetros",            // 13
            "decímetros",             // 14
            "decámetros",             // 15
            "hectómetros",            // 16
            "gigámetros",             // 17
            "unidades astronómicas",  // 18
            "años luz",               // 19
            "pársecs",                // 20
            "pies US",                // 21
            "pulgadas US",            // 22
            "yardas US",              // 23
            "millas US",              // 24
        };

        /// <summary>true si INSUNITS está en la tabla (0–24).</summary>
        public static bool IsInRange(int insunits) => insunits >= 0 && insunits < Names.Length;

        /// <summary>Nombre en español de la unidad (protocol.md §8); "desconocida" si INSUNITS = 0 o fuera de rango.</summary>
        public static string SpanishName(int insunits)
        {
            if (insunits < 0 || insunits >= SpanishNames.Length)
            {
                return Unknown;
            }

            return SpanishNames[insunits];
        }

        /// <summary>Nombre corto de la unidad; "desconocida" si INSUNITS = 0 o está fuera de rango.</summary>
        public static string Name(int insunits)
        {
            if (insunits < 0 || insunits >= Names.Length)
            {
                return Unknown;
            }

            return Names[insunits];
        }

        /// <summary>INSUNITS métrico según protocol.md §5.2: {4, 5, 6, 7} (mm, cm, m, km).</summary>
        public static bool IsMetric(int insunits) => insunits >= 4 && insunits <= 7;

        /// <summary>INSUNITS imperial según protocol.md §5.2: {1, 2, 3} (in, ft, mi).</summary>
        public static bool IsImperial(int insunits) => insunits >= 1 && insunits <= 3;

        /// <summary>
        /// Advertencias de unidades de una respuesta. <paramref name="measurement"/>: 0 = imperial, 1 = métrico.
        /// </summary>
        public static IReadOnlyList<string> Warnings(int insunits, int measurement)
        {
            var warnings = new List<string>();
            if (insunits == 0)
            {
                warnings.Add(WarningUnitsUnknown);
            }

            if (measurement == 0 && IsMetric(insunits))
            {
                warnings.Add(WarningImperialWithMetric);
            }
            else if (measurement == 1 && IsImperial(insunits))
            {
                warnings.Add(WarningMetricWithImperial);
            }

            return warnings;
        }
    }
}
