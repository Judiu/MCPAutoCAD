using System;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Clean
{
    /// <summary>
    /// Advertencia obligatoria de borrar_fuera_de (protocol.md §5F.1): un viewport de papel cuya ventana de modelo
    /// (calculada con <c>Audit.ViewportWindow</c>) mira una zona que quedará vacía o parcialmente borrada. Puro.
    /// </summary>
    public static class ViewportImpact
    {
        public const string EstadoVacia = "vacia";
        public const string EstadoParcial = "parcial";
        public const string EstadoSinCambio = "sin_cambio";

        /// <summary>
        /// Estado de la ventana: <paramref name="nBorradas"/> = entidades a borrar cuya caja interseca la ventana,
        /// <paramref name="nConservadas"/> = entidades que se conservan (con caja) y la intersecan.
        /// nBorradas &gt; 0 y nConservadas = 0 → "vacia"; nBorradas &gt; 0 y nConservadas &gt; 0 → "parcial";
        /// nBorradas = 0 → "sin_cambio". Negativos → ArgumentException.
        /// </summary>
        public static string Classify(int nBorradas, int nConservadas)
        {
            if (nBorradas < 0 || nConservadas < 0)
            {
                throw new ArgumentException(NegativeCounts);
            }

            if (nBorradas == 0)
            {
                return EstadoSinCambio;
            }

            return nConservadas == 0 ? EstadoVacia : EstadoParcial;
        }

        /// <summary>Mensaje de conteos negativos.</summary>
        public const string NegativeCounts = "viewport: conteos negativos";

        /// <summary>
        /// Texto exacto (§5F.1): "El viewport <handle> del layout '<layout>' mira la ventana de modelo [[x, y], [x, y]]
        /// que quedará vacía: se borran <n> entidades que la intersecan." o "... que quedará parcialmente borrada: se
        /// borran <n> entidades que la intersecan y se conservan <m>." Coordenadas con 2 decimales (InvariantCulture).
        /// Para "sin_cambio" devuelve null.
        /// </summary>
        public static string? Warning(string layout, string handle, string estado, BBox2 ventana, int nBorradas, int nConservadas)
        {
            if (nBorradas < 0 || nConservadas < 0)
            {
                throw new ArgumentException(NegativeCounts);
            }

            string prefix = "El viewport " + handle + " del layout '" + layout + "' mira la ventana de modelo [["
                + Coord(ventana.Xmin) + ", " + Coord(ventana.Ymin) + "], [" + Coord(ventana.Xmax) + ", " + Coord(ventana.Ymax) + "]]";
            string borradas = nBorradas.ToString(CultureInfo.InvariantCulture);
            return estado switch
            {
                EstadoSinCambio => null,
                EstadoVacia => prefix + " que quedará vacía: se borran " + borradas + " entidades que la intersecan.",
                EstadoParcial => prefix + " que quedará parcialmente borrada: se borran " + borradas
                    + " entidades que la intersecan y se conservan " + nConservadas.ToString(CultureInfo.InvariantCulture) + ".",
                _ => throw new ArgumentException("viewport: estado desconocido '" + estado + "'"),
            };
        }

        /// <summary>Formato de coordenada con 2 decimales, InvariantCulture (p. ej. "3272.72").</summary>
        public static string Coord(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
