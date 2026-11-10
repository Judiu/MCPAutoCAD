using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Entrada del catálogo de bloques de vano (lps.json "biblioteca_vanos", protocol.md §5G.0/§5G.1): cómo está
    /// orientado cada bloque de puerta/ventana de la biblioteca y qué propiedad dinámica fija su ancho.
    /// <see cref="EjeAncho"/> "x" | "y" = eje local del bloque a lo largo del vano; <see cref="DireccionEspesor"/>
    /// "+x" | "-x" | "+y" | "-y" = eje local que entra en el muro desde la cara del punto base;
    /// <see cref="AnchoNominal"/> = ancho del hueco que cubre la definición tal cual (puerta 0,90; ventana 0,30);
    /// <see cref="EspesorNominal"/> = profundidad de las jambas del bloque (0,13 / 0,12 / 0,10);
    /// <see cref="PropiedadAncho"/> = nombre de la propiedad dinámica de ancho (null = desconocida: se deduce en vivo,
    /// <see cref="DynamicPropertyRules.FindWidthProperty"/>); <see cref="Concepto"/> = concepto de capa del INSERT ("puertas" | "ventanas");
    /// <see cref="AjusteAncho"/> = lo que hay que sumar al ancho para el valor de la propiedad (VENTANA01: 0,0045, porque
    /// Distance1 mide desde un origen desplazado: con 1,2 el largo real es 1,1955; ajuste_ancho, por defecto 0).
    /// </summary>
    public sealed record VanoBlockSpec(
        string Bloque,
        string Tipo,
        string EjeAncho,
        string DireccionEspesor,
        double AnchoNominal,
        double EspesorNominal,
        string? PropiedadAncho,
        string Concepto,
        double AjusteAncho = 0.0)
    {
        /// <summary>Valor de la propiedad de ancho para un hueco de <paramref name="ancho"/>.</summary>
        public double ValorPropiedad(double ancho) => ancho + AjusteAncho;

        /// <summary>Valor de la propiedad con el ancho nominal de la definición: el que se busca al deducirla.</summary>
        public double ValorNominal => AnchoNominal + AjusteAncho;
    }

    /// <summary>
    /// Lectura del catálogo. Puro; nunca lanza por un lps.json mal formado: las entradas inválidas se ignoran con una
    /// advertencia por entrada.
    /// </summary>
    public static class VanoCatalog
    {
        public const string LpsKey = "biblioteca_vanos";

        public const string TipoPuerta = "puerta";
        public const string TipoVentana = "ventana";
        public const string TipoHueco = "hueco";

        /// <summary>
        /// Lee <c>lpsRoot[biblioteca_vanos]</c> (objeto nombre de bloque → {tipo, eje_ancho, direccion_espesor, ancho_nominal,
        /// espesor_nominal, propiedad_ancho, capa, ajuste_ancho}). Entrada inválida (tipo fuera de puerta|ventana, eje_ancho fuera de x|y,
        /// direccion_espesor fuera de ±x|±y, números no positivos, eje y dirección colineales) → se omite y se añade
        /// "biblioteca_vanos['&lt;bloque&gt;']: entrada ignorada (&lt;motivo&gt;)". Sin clave o lpsRoot null → lista vacía sin advertencia.
        /// 'capa' del JSON es el concepto (por defecto "puertas" para puerta y "ventanas" para ventana).
        /// </summary>
        public static IReadOnlyList<VanoBlockSpec> Parse(JsonElement? lpsRoot, List<string> advertencias)
        {
            if (advertencias == null)
            {
                throw new ArgumentNullException(nameof(advertencias));
            }

            var specs = new List<VanoBlockSpec>();
            if (lpsRoot == null || lpsRoot.Value.ValueKind != JsonValueKind.Object
                || !lpsRoot.Value.TryGetProperty(LpsKey, out JsonElement catalogo))
            {
                return specs;
            }

            if (catalogo.ValueKind != JsonValueKind.Object)
            {
                advertencias.Add(LpsKey + ": debe ser un objeto; se ignoró");
                return specs;
            }

            foreach (JsonProperty entrada in catalogo.EnumerateObject())
            {
                VanoBlockSpec? spec = ParseEntry(entrada.Name, entrada.Value, out string? motivo);
                if (spec != null)
                {
                    specs.Add(spec);
                }
                else
                {
                    advertencias.Add(LpsKey + "['" + entrada.Name + "']: entrada ignorada (" + motivo + ")");
                }
            }

            return specs;
        }

        /// <summary>Entrada por nombre de bloque (sin distinguir mayúsculas), o null.</summary>
        public static VanoBlockSpec? Find(IReadOnlyList<VanoBlockSpec> specs, string bloque)
        {
            if (specs == null || bloque == null)
            {
                return null;
            }

            foreach (VanoBlockSpec s in specs)
            {
                if (string.Equals(s.Bloque, bloque, StringComparison.OrdinalIgnoreCase))
                {
                    return s;
                }
            }

            return null;
        }

        /// <summary>Primera entrada del tipo dado ("puerta" | "ventana") en el orden del catálogo, o null.</summary>
        public static VanoBlockSpec? DefaultFor(IReadOnlyList<VanoBlockSpec> specs, string tipo)
        {
            if (specs == null)
            {
                return null;
            }

            foreach (VanoBlockSpec s in specs)
            {
                if (string.Equals(s.Tipo, tipo, StringComparison.Ordinal))
                {
                    return s;
                }
            }

            return null;
        }

        /// <summary>"x" → (1, 0); "y" → (0, 1). Otro → ArgumentException "eje_ancho: debe ser x o y".</summary>
        public static (double x, double y) AxisVector(string ejeAncho)
        {
            return ejeAncho switch
            {
                "x" => (1.0, 0.0),
                "y" => (0.0, 1.0),
                _ => throw new ArgumentException("eje_ancho: debe ser x o y"),
            };
        }

        /// <summary>"+x" → (1, 0); "-x" → (−1, 0); "+y" → (0, 1); "-y" → (0, −1). Otro → ArgumentException "direccion_espesor: debe ser +x, -x, +y o -y".</summary>
        public static (double x, double y) ThicknessVector(string direccionEspesor)
        {
            return direccionEspesor switch
            {
                "+x" => (1.0, 0.0),
                "-x" => (-1.0, 0.0),
                "+y" => (0.0, 1.0),
                "-y" => (0.0, -1.0),
                _ => throw new ArgumentException("direccion_espesor: debe ser +x, -x, +y o -y"),
            };
        }

        /// <summary>Entrada validada, o null con el <paramref name="motivo"/> del rechazo.</summary>
        private static VanoBlockSpec? ParseEntry(string bloque, JsonElement e, out string? motivo)
        {
            motivo = null;
            if (e.ValueKind != JsonValueKind.Object)
            {
                motivo = "no es un objeto";
                return null;
            }

            string? tipo = Text(e, "tipo");
            if (tipo != TipoPuerta && tipo != TipoVentana)
            {
                motivo = "tipo debe ser puerta o ventana";
                return null;
            }

            string? eje = Text(e, "eje_ancho");
            if (eje != "x" && eje != "y")
            {
                motivo = "eje_ancho debe ser x o y";
                return null;
            }

            string? direccion = Text(e, "direccion_espesor");
            if (direccion != "+x" && direccion != "-x" && direccion != "+y" && direccion != "-y")
            {
                motivo = "direccion_espesor debe ser +x, -x, +y o -y";
                return null;
            }

            if (direccion[1] == eje[0])
            {
                motivo = "eje_ancho y direccion_espesor son colineales";
                return null;
            }

            double? ancho = Positive(e, "ancho_nominal");
            if (ancho == null)
            {
                motivo = "ancho_nominal debe ser un número mayor que 0";
                return null;
            }

            double? espesor = Positive(e, "espesor_nominal");
            if (espesor == null)
            {
                motivo = "espesor_nominal debe ser un número mayor que 0";
                return null;
            }

            string? propiedad = null;
            if (e.TryGetProperty("propiedad_ancho", out JsonElement p) && p.ValueKind != JsonValueKind.Null)
            {
                if (p.ValueKind != JsonValueKind.String)
                {
                    motivo = "propiedad_ancho debe ser una cadena o null";
                    return null;
                }

                propiedad = p.GetString();
            }

            double ajuste = 0.0;
            if (e.TryGetProperty("ajuste_ancho", out JsonElement a) && a.ValueKind != JsonValueKind.Null
                && (a.ValueKind != JsonValueKind.Number || !a.TryGetDouble(out ajuste) || !double.IsFinite(ajuste)))
            {
                motivo = "ajuste_ancho debe ser un número";
                return null;
            }

            string concepto = tipo == TipoPuerta ? "puertas" : "ventanas";
            if (e.TryGetProperty("capa", out JsonElement c) && c.ValueKind != JsonValueKind.Null)
            {
                if (c.ValueKind != JsonValueKind.String)
                {
                    motivo = "capa debe ser una cadena";
                    return null;
                }

                string? valor = c.GetString();
                concepto = string.IsNullOrWhiteSpace(valor) ? concepto : valor;
            }

            return new VanoBlockSpec(bloque, tipo, eje, direccion, ancho.Value, espesor.Value, propiedad, concepto, ajuste);
        }

        private static string? Text(JsonElement e, string nombre) =>
            e.TryGetProperty(nombre, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Positive(JsonElement e, string nombre)
        {
            if (e.TryGetProperty(nombre, out JsonElement v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetDouble(out double d) && double.IsFinite(d) && d > 0)
            {
                return d;
            }

            return null;
        }
    }
}
