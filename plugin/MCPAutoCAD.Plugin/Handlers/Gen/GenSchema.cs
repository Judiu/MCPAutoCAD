using System;
using System.Collections.Generic;
using System.Text.Json;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// Fragmentos de JSON Schema y lectura de los parámetros comunes de las herramientas GENERADORAS de la Fase 4A
    /// (protocol.md §5E.0): preview, dibujar_preview, espacio, forzar. NO existe aceptar_sin_unidades: la Fase 4 se niega a
    /// generar con INSUNITS = 0 (PLAN.md §9). Los textos de las descripciones están fijados en el contrato.
    /// </summary>
    internal static class GenSchema
    {
        public const string SchemaPreview =
            @"{ ""type"": ""boolean"", ""default"": false, ""description"": ""true = NO escribe la geometría definitiva: la calcula, devuelve sus números (bbox, longitudes, áreas, n_entidades, puntos resueltos) y un preview_id; después confirmar(preview_id) la dibuja definitivamente o descartar_preview la olvida. Los preview_id caducan a los 15 minutos."" }";

        public const string SchemaDibujarPreview =
            @"{ ""type"": ""boolean"", ""default"": true, ""description"": ""Solo con preview: true. true = dibuja la previsualización en la capa MCP-PREVIEW (color 4, no imprimible) en su propio paso de deshacer; false = solo devuelve números."" }";

        public const string SchemaEspacio = Draw.DrawWrite.SchemaEspacio;

        public const string SchemaForzar = Draw.DrawWrite.SchemaForzar;

        /// <summary>PuntoRef (§5E.1).</summary>
        public const string SchemaPuntoRef = PointRef.Schema;

        /// <summary>Parámetros comunes de toda generadora.</summary>
        public static readonly string[] CommonParams = { "preview", "dibujar_preview", "espacio", "forzar" };

        /// <summary>Texto JSON de las cuatro propiedades comunes, para concatenar dentro de "properties" (sin coma final).</summary>
        public const string CommonProperties =
            "    \"preview\": " + SchemaPreview + ",\n"
            + "    \"dibujar_preview\": " + SchemaDibujarPreview + ",\n"
            + "    \"espacio\": " + SchemaEspacio + ",\n"
            + "    \"forzar\": " + SchemaForzar;

        /// <summary>
        /// Construye el InputSchema completo de una generadora: <paramref name="ownProperties"/> = propiedades propias
        /// (texto JSON sin llaves ni coma final), <paramref name="required"/> = obligatorias propias.
        /// </summary>
        public static JsonElement Build(string ownProperties, params string[] required)
        {
            var req = new List<string>();
            foreach (string r in required)
            {
                req.Add("\"" + r + "\"");
            }

            string own = string.IsNullOrWhiteSpace(ownProperties) ? "" : ownProperties + ",\n";
            string json = "{\n  \"type\": \"object\",\n  \"properties\": {\n" + own + CommonProperties + "\n  },\n  \"required\": ["
                + string.Join(", ", req) + "],\n  \"additionalProperties\": false\n}";
            return Schema.Parse(json);
        }

        /// <summary>
        /// OutputSchema informativo común de una generadora (§5E.0) + campos propios en 'numeros' y, en
        /// <paramref name="rootProperties"/>, en la raíz (texto JSON con coma final).
        /// </summary>
        public static JsonElement Output(string numerosProperties, string rootProperties = "")
        {
            string own = string.IsNullOrWhiteSpace(numerosProperties) ? "" : ",\n" + numerosProperties;
            string json = @"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""preview"": { ""type"": ""boolean"" },
    ""preview_id"": { ""type"": [""string"", ""null""] },
    ""preview_dibujada"": { ""type"": ""boolean"" },
    ""caduca"": { ""type"": [""string"", ""null""] },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""handles_borrados"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""entidades"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""capa"": { ""type"": ""string"" },
    ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capas_creadas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
" + rootProperties + @"
    ""espacio"": { ""type"": ""string"" },
    ""puntos_resueltos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""numeros"": { ""type"": ""object"", ""properties"": { ""n_entidades"": { ""type"": ""integer"" }, ""bbox"": { ""type"": [""array"", ""null""] }" + own + @" } },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview"", ""preview_id"", ""preview_dibujada"", ""handles"", ""handles_borrados"", ""entidades"", ""capa"", ""capas"", ""espacio"", ""puntos_resueltos"", ""numeros"", ""insunits"", ""unidades"", ""advertencias""]
}";
            return Schema.Parse(json);
        }

        /// <summary>Parámetros comunes leídos.</summary>
        internal sealed class Common
        {
            public Common(bool preview, bool dibujarPreview, string? espacio, bool forzar)
            {
                Preview = preview;
                DibujarPreview = dibujarPreview;
                Espacio = espacio;
                Forzar = forzar;
            }

            public bool Preview { get; }
            public bool DibujarPreview { get; }
            public string? Espacio { get; }
            public bool Forzar { get; }
        }

        /// <summary>Lee los comunes (sin AutoCAD). No comprueba additionalProperties: lo hace cada handler con su lista.</summary>
        public static Common ReadCommon(JsonElement p)
        {
            bool preview = ParamReader.OptionalBool(p, "preview") ?? false;
            bool dibujar = ParamReader.OptionalBool(p, "dibujar_preview") ?? true;
            string? espacio = ParamReader.OptionalString(p, "espacio", 1, 255);
            bool forzar = ParamReader.OptionalBool(p, "forzar") ?? false;
            return new Common(preview, dibujar, espacio, forzar);
        }

        /// <summary>Lee un PuntoRef obligatorio del campo <paramref name="name"/> (ArgumentException → INVALID_PARAMS).</summary>
        public static PointRef RequiredPointRef(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            return ReadPointRef(el, name);
        }

        public static PointRef? OptionalPointRef(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out JsonElement el) ? ReadPointRef(el, name) : null;

        public static PointRef ReadPointRef(JsonElement el, string path)
        {
            try
            {
                return PointRef.Parse(el, path);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }

        /// <summary>Lista obligatoria de PuntoRef [minItems, maxItems].</summary>
        public static List<PointRef> RequiredPointRefs(JsonElement obj, string name, int minItems, int maxItems)
        {
            if (!obj.TryGetProperty(name, out JsonElement el))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            try
            {
                return PointRef.ParseList(el, name, minItems, maxItems);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }

        /// <summary>Copia de los parámetros SIN preview/dibujar_preview (lo que guarda el almacén para recalcular en confirmar).</summary>
        public static string StripPreviewParams(JsonElement p)
        {
            using var stream = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                foreach (JsonProperty prop in p.EnumerateObject())
                {
                    if (prop.Name == "preview" || prop.Name == "dibujar_preview")
                    {
                        continue;
                    }

                    prop.WriteTo(w);
                }

                w.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
