using System;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Utilidades AutoCAD compartidas para <c>fondo_texto</c> (DIMTFILL / DIMTFILLCLR) en <see cref="EstiloCotaHandler"/>,
    /// <see cref="EditarCotaHandler"/> y <see cref="EntityFormatter"/>.
    /// </summary>
    internal static class DimTextFillUtil
    {
        public const string SchemaProperty =
            @"{ ""anyOf"": [ { ""type"": ""string"", ""enum"": [""ninguno"", ""fondo_dibujo""] }, { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 }, { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 } } ], ""description"": ""Máscara de fondo del texto de cota (DIMTFILL / DIMTFILLCLR): 'ninguno' (0), 'fondo_dibujo' (1), índice ACI 1–255 o color verdadero [r, g, b] (2)."" }";

        public static DimTextFillSpec? ParseOptional(JsonElement p)
        {
            if (!p.TryGetProperty("fondo_texto", out JsonElement el) || el.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                if (DimTextFillRules.TryParseWord(el.GetString(), out DimTextFillSpec? spec) && spec != null)
                {
                    return spec;
                }

                throw ToolException.InvalidParams(DimTextFillRules.InvalidMessage);
            }

            if (el.ValueKind == JsonValueKind.Number)
            {
                if (el.TryGetInt32(out int aci) && aci >= 1 && aci <= 255)
                {
                    return DimTextFillSpec.FromAci(aci);
                }

                throw ToolException.InvalidParams(DimTextFillRules.InvalidMessage);
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                if (el.GetArrayLength() != 3)
                {
                    throw ToolException.InvalidParams(DimTextFillRules.InvalidMessage);
                }

                var c = new int[3];
                int i = 0;
                foreach (JsonElement x in el.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out int v) || v < 0 || v > 255)
                    {
                        throw ToolException.InvalidParams(DimTextFillRules.InvalidMessage);
                    }

                    c[i++] = v;
                }

                return DimTextFillSpec.FromRgb(c[0], c[1], c[2]);
            }

            throw ToolException.InvalidParams(DimTextFillRules.InvalidMessage);
        }

        public static Color ToAcadColor(DimTextFillSpec spec)
        {
            if (spec.Rgb is { } rgb)
            {
                return Color.FromRgb((byte)rgb.R, (byte)rgb.G, (byte)rgb.B);
            }

            int aci = spec.Aci ?? 7;
            return Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
        }

        public static void Apply(DimStyleTableRecord rec, DimTextFillSpec spec)
        {
            rec.Dimtfill = spec.Mode;
            if (spec.Mode == DimTextFillRules.ModeColor)
            {
                rec.Dimtfillclr = ToAcadColor(spec);
            }
        }

        public static void Apply(Dimension dim, DimTextFillSpec spec)
        {
            dim.Dimtfill = spec.Mode;
            if (spec.Mode == DimTextFillRules.ModeColor)
            {
                dim.Dimtfillclr = ToAcadColor(spec);
            }
        }

        public static string Describe(int dimtfill, Color? clr)
        {
            if (clr == null)
            {
                return DimTextFillRules.Describe(dimtfill, hasColor: false, isByAci: false, 0, 0, 0, 0);
            }

            bool isByAci = clr.IsByAci;
            return DimTextFillRules.Describe(
                dimtfill,
                hasColor: true,
                isByAci: isByAci,
                aci: isByAci ? clr.ColorIndex : 0,
                r: clr.Red,
                g: clr.Green,
                b: clr.Blue);
        }

        public static string SafeDescribe(Func<int> readMode, Func<Color> readColor)
        {
            try
            {
                int mode = readMode();
                Color? clr = mode == DimTextFillRules.ModeColor ? readColor() : null;
                return Describe(mode, clr);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return DimTextFillRules.WordNone;
            }
        }
    }
}
