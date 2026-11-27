using System;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Especificación inmutable de máscara de fondo de texto de cota (DIMTFILL / DIMTFILLCLR): ninguno (0), fondo del
    /// dibujo (1) o color explícito ACI / RGB (2).
    /// </summary>
    public sealed class DimTextFillSpec
    {
        private DimTextFillSpec(int mode, int? aci, (int R, int G, int B)? rgb, string label)
        {
            Mode = mode;
            Aci = aci;
            Rgb = rgb;
            Label = label;
        }

        public static DimTextFillSpec None { get; } = new DimTextFillSpec(DimTextFillRules.ModeNone, null, null, DimTextFillRules.WordNone);

        public static DimTextFillSpec Background { get; } = new DimTextFillSpec(DimTextFillRules.ModeBackground, null, null, DimTextFillRules.WordBackground);

        public int Mode { get; }

        public int? Aci { get; }

        public (int R, int G, int B)? Rgb { get; }

        public string Label { get; }

        public static DimTextFillSpec FromAci(int aci)
        {
            if (aci < 1 || aci > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(aci), "ACI fuera de rango (1–255)");
            }

            return new DimTextFillSpec(DimTextFillRules.ModeColor, aci, null, LayerProps.FormatAci(aci));
        }

        public static DimTextFillSpec FromRgb(int r, int g, int b)
        {
            if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(r), "Componente RGB fuera de rango (0–255)");
            }

            string label = string.Format(CultureInfo.InvariantCulture, "RGB({0},{1},{2})", r, g, b);
            return new DimTextFillSpec(DimTextFillRules.ModeColor, null, (r, g, b), label);
        }
    }

    /// <summary>Reglas puras (sin AutoCAD) para parsear y describir <c>fondo_texto</c> (DIMTFILL / DIMTFILLCLR).</summary>
    public static class DimTextFillRules
    {
        public const int ModeNone = 0;
        public const int ModeBackground = 1;
        public const int ModeColor = 2;

        public const string WordNone = "ninguno";
        public const string WordBackground = "fondo_dibujo";

        public const string InvalidMessage =
            "fondo_texto: use 'ninguno', 'fondo_dibujo', un entero ACI 1–255 o un color [r, g, b] de 0–255";

        public static bool TryParseWord(string? text, out DimTextFillSpec? spec)
        {
            string norm = (text ?? "").Trim().ToLowerInvariant();
            if (norm == WordNone)
            {
                spec = DimTextFillSpec.None;
                return true;
            }

            if (norm == WordBackground)
            {
                spec = DimTextFillSpec.Background;
                return true;
            }

            spec = null;
            return false;
        }

        public static string Describe(int mode, bool hasColor, bool isByAci, int aci, byte r, byte g, byte b)
        {
            if (mode == ModeBackground)
            {
                return WordBackground;
            }

            if (mode == ModeColor)
            {
                if (!hasColor)
                {
                    return "color";
                }

                if (isByAci)
                {
                    return LayerProps.FormatAci(aci);
                }

                return string.Format(CultureInfo.InvariantCulture, "RGB({0},{1},{2})", r, g, b);
            }

            return WordNone;
        }
    }
}
