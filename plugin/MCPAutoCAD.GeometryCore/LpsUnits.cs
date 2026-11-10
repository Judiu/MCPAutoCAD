using System.Globalization;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Advertencia de unidades frente al estándar LPS (protocol.md §2.2). Los textos son parte del contrato.
    /// </summary>
    public static class LpsUnits
    {
        /// <summary>
        /// Devuelve la advertencia LPS, o null si INSUNITS del dibujo coincide con el del estándar.
        /// <paramref name="lpsNombreCorto"/> solo se usa como nombre del estándar cuando su INSUNITS está fuera de la tabla §8.
        /// </summary>
        public static string? Warning(int insunits, int lpsInsunits, string lpsNombreCorto)
        {
            if (insunits == lpsInsunits)
            {
                return null;
            }

            string lpsName = UnitsTable.IsInRange(lpsInsunits)
                ? UnitsTable.SpanishName(lpsInsunits)
                : (string.IsNullOrEmpty(lpsNombreCorto) ? UnitsTable.Unknown : lpsNombreCorto);

            string lpsPart = string.Format(
                CultureInfo.InvariantCulture, "el estándar LPS es {0} (INSUNITS={1}).", lpsName, lpsInsunits);

            if (insunits == 0)
            {
                return "El dibujo no declara unidades (INSUNITS=0); " + lpsPart;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "El dibujo está en {0} (INSUNITS={1}); {2}",
                UnitsTable.SpanishName(insunits),
                insunits,
                lpsPart);
        }
    }
}
