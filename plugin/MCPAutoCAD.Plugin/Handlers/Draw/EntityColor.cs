using System;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Parámetros de color de las herramientas de dibujo (linea, polilinea, circulo, arco, texto, mtext, hatch) y de
    /// cambiar_propiedades. <c>color</c>: ACI 0–256 (0 = porbloque, 256 = porcapa) o 'porcapa' / 'porbloque';
    /// <c>color_rgb</c>: [r, g, b]. Sin ninguno, la entidad queda PorCapa (ByLayer, como hasta ahora). La validación
    /// pura está en <see cref="ColorRules"/> (GeometryCore); aquí se lee el JSON y se traduce a Autodesk Color.
    /// </summary>
    internal static class EntityColor
    {
        /// <summary>Nombres de parámetro de color (añadir a la lista Allowed de cada handler).</summary>
        public static readonly string[] ParamNames = { "color", "color_rgb" };

        public const string SchemaColor =
            @"{ ""anyOf"": [ { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 256 }, { ""type"": ""string"", ""enum"": [""porcapa"", ""porbloque""] } ], ""description"": ""Color: ACI 1–255, 0 = porbloque, 256 = porcapa (por defecto), o 'porcapa' / 'porbloque'. No combinar con color_rgb."" }";

        public const string SchemaColorRgb =
            @"{ ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 }, ""description"": ""Color verdadero [r, g, b] (0–255). No combinar con color."" }";

        /// <summary>Fragmento "color" + "color_rgb" de un schema (terminado sin coma).</summary>
        public static string SchemaProperties =>
            "    \"color\": " + SchemaColor + ",\n    \"color_rgb\": " + SchemaColorRgb;

        /// <summary>Lee color/color_rgb (sin AutoCAD). null si no se pasó ninguno (→ PorCapa).</summary>
        public static ColorSpec? Read(JsonElement p)
        {
            bool hasColor = ParamReader.Has(p, "color");
            bool hasRgb = ParamReader.Has(p, "color_rgb");
            if (hasColor && hasRgb)
            {
                throw ToolException.InvalidParams(ColorRules.ColorAndRgb);
            }

            if (hasRgb)
            {
                return ReadRgb(p.GetProperty("color_rgb"));
            }

            if (!hasColor)
            {
                return null;
            }

            JsonElement c = p.GetProperty("color");
            if (c.ValueKind == JsonValueKind.String)
            {
                string text = c.GetString() ?? "";
                ColorSpec? word = ColorRules.FromWord(text);
                if (word != null)
                {
                    return word;
                }

                // Fallo 29: con el schema anyOf [integer, string] algunos clientes MCP envían el ACI como texto ("7").
                // Un entero escrito como texto se acepta igual que el número.
                if (int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int aci)
                    && ColorRules.IsAci(aci))
                {
                    return ColorRules.FromAci(aci);
                }

                throw ToolException.InvalidParams(ColorRules.AciRange);
            }

            if (c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out double v) && ColorRules.IsAci(v))
            {
                return ColorRules.FromAci((int)v);
            }

            throw ToolException.InvalidParams(ColorRules.AciRange);
        }

        /// <summary>color_rgb: array de 3 enteros 0–255.</summary>
        public static ColorSpec ReadRgb(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 3)
            {
                throw ToolException.InvalidParams(ColorRules.RgbFormat);
            }

            var c = new int[3];
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double v) || Math.Floor(v) != v || v < 0 || v > 255)
                {
                    throw ToolException.InvalidParams(ColorRules.RgbFormat);
                }

                c[i++] = (int)v;
            }

            return ColorRules.FromRgb(c[0], c[1], c[2]);
        }

        /// <summary>Traduce a Autodesk.AutoCAD.Colors.Color.</summary>
        public static Color ToColor(ColorSpec spec)
        {
            switch (spec.Kind)
            {
                case ColorKind.PorBloque:
                    return Color.FromColorIndex(ColorMethod.ByBlock, 0);
                case ColorKind.Aci:
                    return Color.FromColorIndex(ColorMethod.ByAci, (short)spec.Aci);
                case ColorKind.Rgb:
                    return Color.FromRgb((byte)spec.R, (byte)spec.G, (byte)spec.B);
                default:
                    return Color.FromColorIndex(ColorMethod.ByLayer, 256);
            }
        }

        /// <summary>Aplica el color pedido a una entidad recién creada (null = no tocar: ya es ByLayer).</summary>
        public static void Apply(Entity e, ColorSpec? spec)
        {
            if (spec != null)
            {
                e.Color = ToColor(spec.Value);
            }
        }

        /// <summary>Descripción del color actual de una entidad: "porcapa", "porbloque", "ACI 9" o "RGB(r,g,b)".</summary>
        public static string Describe(Color c)
        {
            if (c.IsByLayer)
            {
                return "porcapa";
            }

            if (c.IsByBlock)
            {
                return "porbloque";
            }

            if (c.IsByColor)
            {
                return ColorRules.Describe(ColorRules.FromRgb(c.Red, c.Green, c.Blue));
            }

            if (c.IsForeground)
            {
                return "frente";
            }

            if (c.IsByPen)
            {
                return "pen";
            }

            if (c.IsNone)
            {
                return "ninguno";
            }

            return "ACI " + c.ColorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
