using System;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>Tipo de color pedido para una entidad.</summary>
    public enum ColorKind
    {
        /// <summary>ByLayer (ACI 256 o 'porcapa').</summary>
        PorCapa,

        /// <summary>ByBlock (ACI 0 o 'porbloque').</summary>
        PorBloque,

        /// <summary>Índice ACI 1–255.</summary>
        Aci,

        /// <summary>Color verdadero RGB.</summary>
        Rgb,
    }

    /// <summary>Color pedido: tipo + ACI (si Kind = Aci) o componentes RGB (si Kind = Rgb).</summary>
    public readonly record struct ColorSpec(ColorKind Kind, int Aci, int R, int G, int B);

    /// <summary>
    /// Reglas puras de color (protocol.md §5D.2–§5D.7, parámetros color / color_rgb). Sin AutoCAD: el plugin lee el
    /// JSON y traduce el <see cref="ColorSpec"/> a Autodesk.AutoCAD.Colors.Color. Textos de error en español.
    /// </summary>
    public static class ColorRules
    {
        /// <summary>ACI de ByBlock (0) y de ByLayer (256), como en AutoCAD.</summary>
        public const int AciByBlock = 0;

        public const int AciByLayer = 256;

        public const string ColorAndRgb = "color: no se puede combinar con color_rgb";

        public const string AciRange =
            "color: debe ser un entero entre 0 y 256 (0 = porbloque, 256 = porcapa), 'porcapa' o 'porbloque'";

        public const string RgbFormat = "color_rgb: debe ser un array de 3 enteros entre 0 y 255";

        /// <summary>true si el número es un ACI válido (entero 0–256; 0 = ByBlock, 256 = ByLayer).</summary>
        public static bool IsAci(double v) =>
            double.IsFinite(v) && Math.Floor(v) == v && v >= 0 && v <= AciByLayer;

        /// <summary>Color desde ACI (0 = PorBloque, 256 = PorCapa). Lanza <see cref="ArgumentOutOfRangeException"/>.</summary>
        public static ColorSpec FromAci(int aci)
        {
            if (aci < 0 || aci > AciByLayer)
            {
                throw new ArgumentOutOfRangeException(nameof(aci), AciRange);
            }

            if (aci == AciByBlock)
            {
                return new ColorSpec(ColorKind.PorBloque, 0, 0, 0, 0);
            }

            if (aci == AciByLayer)
            {
                return new ColorSpec(ColorKind.PorCapa, 0, 0, 0, 0);
            }

            return new ColorSpec(ColorKind.Aci, aci, 0, 0, 0);
        }

        /// <summary>'porcapa' / 'porbloque' (sin distinguir mayúsculas, sin espacios); null si es otra cadena.</summary>
        public static ColorSpec? FromWord(string text)
        {
            string w = (text ?? "").Trim();
            if (string.Equals(w, "porcapa", StringComparison.OrdinalIgnoreCase))
            {
                return new ColorSpec(ColorKind.PorCapa, 0, 0, 0, 0);
            }

            if (string.Equals(w, "porbloque", StringComparison.OrdinalIgnoreCase))
            {
                return new ColorSpec(ColorKind.PorBloque, 0, 0, 0, 0);
            }

            return null;
        }

        /// <summary>Color RGB; cada componente entre 0 y 255 o <see cref="ArgumentOutOfRangeException"/>.</summary>
        public static ColorSpec FromRgb(int r, int g, int b)
        {
            foreach (int c in new[] { r, g, b })
            {
                if (c < 0 || c > 255)
                {
                    throw new ArgumentOutOfRangeException(nameof(r), RgbFormat);
                }
            }

            return new ColorSpec(ColorKind.Rgb, 0, r, g, b);
        }

        /// <summary>Texto corto para respuestas: "porcapa", "porbloque", "ACI 9", "RGB(1,2,3)".</summary>
        public static string Describe(ColorSpec c)
        {
            switch (c.Kind)
            {
                case ColorKind.PorCapa:
                    return "porcapa";
                case ColorKind.PorBloque:
                    return "porbloque";
                case ColorKind.Aci:
                    return "ACI " + c.Aci.ToString(CultureInfo.InvariantCulture);
                default:
                    return "RGB(" + c.R.ToString(CultureInfo.InvariantCulture) + "," + c.G.ToString(CultureInfo.InvariantCulture)
                        + "," + c.B.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }
    }

    /// <summary>
    /// Reglas puras de grosor, transparencia y escala de tipo de línea (cambiar_propiedades y parámetros de dibujo).
    /// </summary>
    public static class EntityPropertyRules
    {
        /// <summary>Máximo de handles de cambiar_propiedades por llamada.</summary>
        public const int MaxHandles = 500;

        /// <summary>Transparencia máxima pedida (%). 0 = opaco.</summary>
        public const double MaxTransparencyPercent = 90.0;

        /// <summary>
        /// Grosores válidos de AutoCAD (LineWeight, centésimas de mm). Cada valor se nombra LineWeightNNN en el enum.
        /// </summary>
        public static readonly int[] LineWeightHundredths =
        {
            0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211,
        };

        public const string NoProperties =
            "propiedades: indique al menos una de color, color_rgb, tipo_linea, escala_tipo_linea, grosor, transparencia";

        public const string Grosores =
            "0, 0.05, 0.09, 0.13, 0.15, 0.18, 0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.53, 0.60, 0.70, 0.80, 0.90, 1.00, 1.06, 1.20, 1.40, 1.58, 2.00, 2.11";

        /// <summary>Grosor en mm (0.25) → centésimas (25) si es uno de los válidos.</summary>
        public static bool TryLineWeightHundredths(double mm, out int hundredths)
        {
            hundredths = 0;
            if (!double.IsFinite(mm) || mm < 0)
            {
                return false;
            }

            double scaled = Math.Round(mm * 100.0, 6);
            int candidate = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
            if (Math.Abs(scaled - candidate) > 1e-6)
            {
                return false;
            }

            foreach (int valid in LineWeightHundredths)
            {
                if (valid == candidate)
                {
                    hundredths = valid;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Mensaje de grosor no válido (cita los valores permitidos).</summary>
        public static string BadLineWeight(double mm) =>
            "grosor: " + mm.ToString("0.###", CultureInfo.InvariantCulture)
            + " no es un grosor de línea válido; use porcapa, porbloque, defecto o uno de: " + Grosores + " (mm)";

        /// <summary>Transparencia (%) válida: número finito entre 0 y 90.</summary>
        public static bool IsTransparencyPercent(double pct) =>
            double.IsFinite(pct) && pct >= 0 && pct <= MaxTransparencyPercent;

        /// <summary>
        /// Alfa de AutoCAD (0–255, 255 = opaco) para una transparencia en %: alfa = 255 · (100 − %) / 100.
        /// [Probable] convención de AutoCAD (alfa = opacidad); verificar en vivo.
        /// </summary>
        public static byte AlphaFromPercent(double pct)
        {
            if (!IsTransparencyPercent(pct))
            {
                throw new ArgumentOutOfRangeException(nameof(pct), "transparencia fuera de 0–90");
            }

            return (byte)Math.Round(255.0 * (100.0 - pct) / 100.0, MidpointRounding.AwayFromZero);
        }

        /// <summary>Transparencia % a partir del alfa (inverso de <see cref="AlphaFromPercent"/>), redondeada a entero.</summary>
        public static int PercentFromAlpha(byte alpha) =>
            (int)Math.Round(100.0 - 100.0 * alpha / 255.0, MidpointRounding.AwayFromZero);

        /// <summary>
        /// Lista de nombres de tipo de línea de los que se puede pedir la carga: MEASUREMENT 1 = métrico (acadiso.lin),
        /// otro valor = imperial (acad.lin).
        /// </summary>
        public static string LinetypeFileFor(int measurement) => measurement == 1 ? "acadiso.lin" : "acad.lin";

        /// <summary>Caracteres que AutoCAD no admite en nombres de símbolos (tipos de línea, capas, …).</summary>
        public const string InvalidSymbolChars = "*?~,[]|<>/\\\":;=`'";

        /// <summary>
        /// Nombre de tipo de línea admisible (sin comodines ni caracteres inválidos), o el motivo de rechazo. Se comprueba
        /// antes de intentar cargarlo del fichero de tipos.
        /// </summary>
        public static string? LinetypeNameError(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "tipo_linea: debe tener al menos 1 carácter(es)";
            }

            foreach (char c in name)
            {
                if (InvalidSymbolChars.IndexOf(c) >= 0 || char.IsControl(c))
                {
                    return "tipo_linea: '" + name + "' contiene caracteres no válidos (* ? ~ , [ ] | < > / \\ \" : ; = ` ')";
                }
            }

            return null;
        }

        /// <summary>Escala de tipo de línea: número finito > 0.</summary>
        public static bool IsLinetypeScale(double v) => double.IsFinite(v) && v > 0;

        /// <summary>Nombres de palabra clave de propiedad ByLayer / ByBlock / defecto para grosor.</summary>
        public static bool IsLineWeightWord(string text, out string word)
        {
            word = (text ?? "").Trim().ToLowerInvariant();
            return word == "porcapa" || word == "porbloque" || word == "defecto";
        }
    }
}
