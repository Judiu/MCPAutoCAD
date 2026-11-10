using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Reglas puras de propiedades de capa para crear_capa (protocol.md §5D.1): grosores válidos, transparencia en %
    /// (misma convención que normalizar_capas: alfa 255 = opaca = 0 %), formato de color y diferencias entre el estado
    /// actual de una capa y lo pedido. Sin AutoCAD.
    /// </summary>
    public static class LayerProps
    {
        /// <summary>Grosores válidos para una CAPA: milímetros de AutoCAD en centésimas de mm (LineWeight) y -3 (Default). ByLayer (-1) y ByBlock (-2) no valen en una capa.</summary>
        public static readonly IReadOnlyList<int> LineWeights = new[]
        {
            -3, 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211,
        };

        /// <summary>Transparencia máxima admitida en % (100 = invisible, no tiene sentido para una capa).</summary>
        public const int MaxTransparenciaPct = 90;

        /// <summary>Mensaje de INVALID_PARAMS para un grosor no válido.</summary>
        public const string InvalidLineWeight = "grosor: debe ser un grosor de AutoCAD en centésimas de mm (0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211) o -3 (Default). ByLayer y ByBlock no valen en una capa";

        /// <summary>Mensaje de INVALID_PARAMS para una transparencia fuera de rango.</summary>
        public const string InvalidTransparencia = "transparencia: debe ser un entero de 0 a 90 (porcentaje; 0 = opaca)";

        /// <summary>Mensaje de INVALID_PARAMS para un color no válido.</summary>
        public const string InvalidColor = "color: debe ser un índice ACI de 1 a 255 o [r, g, b] con valores de 0 a 255";

        public static bool IsValidLineWeight(int value) => LineWeights.Contains(value);

        /// <summary>Alfa de AutoCAD para una transparencia en % (0 = 255 opaca; 90 = 25).</summary>
        public static byte AlphaFromPercent(int pct) => (byte)Math.Clamp(Math.Round(255.0 * (1.0 - (pct / 100.0))), 0.0, 255.0);

        /// <summary>Transparencia en % a partir del alfa de AutoCAD (inversa de <see cref="AlphaFromPercent"/>).</summary>
        public static int PercentFromAlpha(int alpha) => (int)Math.Round((255 - alpha) * 100.0 / 255.0);

        /// <summary>Texto del color ACI: "ACI 151".</summary>
        public static string FormatAci(int aci) => "ACI " + aci.ToString(CultureInfo.InvariantCulture);

        /// <summary>Texto del color verdadero: "RGB(1, 2, 3)".</summary>
        public static string FormatRgb(int r, int g, int b) =>
            "RGB(" + r.ToString(CultureInfo.InvariantCulture) + ", " + g.ToString(CultureInfo.InvariantCulture) + ", "
            + b.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>Estado de una capa con las propiedades que crear_capa sabe fijar. Textos ya formateados.</summary>
        public sealed record Values(string Color, string TipoLinea, int Grosor, int TransparenciaPct);

        /// <summary>Propiedades pedidas; null = no se pidió (no se compara ni se cambia).</summary>
        public sealed record Request(string? Color, string? TipoLinea, int? Grosor, int? TransparenciaPct);

        /// <summary>Un campo que cambiaría: valores antes y después formateados.</summary>
        public sealed record Change(string Campo, string Antes, string Despues);

        /// <summary>
        /// Diferencias entre el estado actual y lo pedido (solo los campos pedidos). Tipo de línea sin distinguir mayúsculas.
        /// Lista vacía = la capa ya tiene lo pedido.
        /// </summary>
        public static IReadOnlyList<Change> Diff(Values actual, Request pedido)
        {
            var cambios = new List<Change>();
            if (pedido.Color != null && !string.Equals(pedido.Color, actual.Color, StringComparison.Ordinal))
            {
                cambios.Add(new Change("color", actual.Color, pedido.Color));
            }

            if (pedido.TipoLinea != null && !string.Equals(pedido.TipoLinea, actual.TipoLinea, StringComparison.OrdinalIgnoreCase))
            {
                cambios.Add(new Change("tipo_linea", actual.TipoLinea, pedido.TipoLinea));
            }

            if (pedido.Grosor is int g && g != actual.Grosor)
            {
                cambios.Add(new Change("grosor", actual.Grosor.ToString(CultureInfo.InvariantCulture), g.ToString(CultureInfo.InvariantCulture)));
            }

            if (pedido.TransparenciaPct is int t && t != actual.TransparenciaPct)
            {
                cambios.Add(new Change("transparencia", actual.TransparenciaPct.ToString(CultureInfo.InvariantCulture), t.ToString(CultureInfo.InvariantCulture)));
            }

            return cambios;
        }
    }
}
