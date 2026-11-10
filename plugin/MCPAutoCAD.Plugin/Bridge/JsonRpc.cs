using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MCPAutoCAD.Plugin.Bridge
{
    /// <summary>Petición JSON-RPC 2.0 ya validada en su forma (protocol.md §2).</summary>
    internal sealed class JsonRpcRequest
    {
        public JsonRpcRequest(JsonElement id, string method, JsonElement parameters)
        {
            Id = id;
            Method = method;
            Params = parameters;
        }

        /// <summary>id tal cual llegó (entero o cadena), clonado e independiente del documento original.</summary>
        public JsonElement Id { get; }

        public string Method { get; }

        /// <summary>params como objeto (vacío si se omitió), clonado.</summary>
        public JsonElement Params { get; }

        /// <summary>
        /// Interpreta un mensaje. Lanza <see cref="JsonRpcParseException"/> con el id recuperado (o null) si la forma no es válida.
        /// </summary>
        public static JsonRpcRequest Parse(byte[] utf8Json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (JsonException ex)
            {
                throw new JsonRpcParseException(null, "JSON inválido: " + ex.Message);
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonRpcParseException(null, "el mensaje debe ser un objeto JSON-RPC (no se admiten lotes)");
                }

                JsonElement? id = null;
                if (root.TryGetProperty("id", out JsonElement idEl)
                    && (idEl.ValueKind == JsonValueKind.Number || idEl.ValueKind == JsonValueKind.String))
                {
                    id = idEl.Clone();
                }

                if (id == null)
                {
                    throw new JsonRpcParseException(null, "falta 'id' (entero o cadena); no se admiten notificaciones");
                }

                if (!root.TryGetProperty("jsonrpc", out JsonElement ver)
                    || ver.ValueKind != JsonValueKind.String
                    || ver.GetString() != "2.0")
                {
                    throw new JsonRpcParseException(id, "'jsonrpc' debe ser \"2.0\"");
                }

                if (!root.TryGetProperty("method", out JsonElement methodEl)
                    || methodEl.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(methodEl.GetString()))
                {
                    throw new JsonRpcParseException(id, "falta 'method' (cadena)");
                }

                JsonElement parameters;
                if (!root.TryGetProperty("params", out JsonElement paramsEl) || paramsEl.ValueKind == JsonValueKind.Null)
                {
                    parameters = EmptyObject();
                }
                else
                {
                    // Un params que no es objeto lo rechaza el validador de la herramienta como INVALID_PARAMS.
                    parameters = paramsEl.Clone();
                }

                return new JsonRpcRequest(id.Value, methodEl.GetString()!, parameters);
            }
        }

        public static JsonElement EmptyObject()
        {
            using JsonDocument d = JsonDocument.Parse("{}");
            return d.RootElement.Clone();
        }
    }

    /// <summary>Petición imposible de interpretar como JSON-RPC (→ PARSE_ERROR).</summary>
    internal sealed class JsonRpcParseException : System.Exception
    {
        public JsonRpcParseException(JsonElement? id, string detail)
            : base(detail)
        {
            Id = id;
        }

        public JsonElement? Id { get; }
    }

    /// <summary>Construcción de respuestas JSON-RPC (resultado o error) en UTF-8 sin BOM.</summary>
    internal static class JsonRpcResponse
    {
        public static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            // Sin política de nombres: los nombres de campo del contrato se escriben tal cual.
            PropertyNamingPolicy = null,
            // Texto legible (acentos sin escapar). El destino es un cliente JSON, no HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        };

        private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        };

        public static byte[] Success(JsonElement id, object result)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, WriterOptions))
            {
                w.WriteStartObject();
                w.WriteString("jsonrpc", "2.0");
                w.WritePropertyName("id");
                id.WriteTo(w);
                w.WritePropertyName("result");
                JsonSerializer.Serialize(w, result, result.GetType(), SerializerOptions);
                w.WriteEndObject();
            }

            return ms.ToArray();
        }

        public static byte[] Error(JsonElement? id, ToolException error)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, WriterOptions))
            {
                w.WriteStartObject();
                w.WriteString("jsonrpc", "2.0");
                w.WritePropertyName("id");
                if (id.HasValue)
                {
                    id.Value.WriteTo(w);
                }
                else
                {
                    w.WriteNullValue();
                }

                w.WritePropertyName("error");
                w.WriteStartObject();
                w.WriteNumber("code", ErrorCodes.Numeric(error.Code));
                w.WriteString("message", error.Message);
                w.WritePropertyName("data");
                w.WriteStartObject();
                w.WriteString("code", error.Code);
                foreach (KeyValuePair<string, object?> kv in error.ExtraData)
                {
                    if (kv.Key == "code")
                    {
                        continue;
                    }

                    w.WritePropertyName(kv.Key);
                    JsonSerializer.Serialize(w, kv.Value, kv.Value?.GetType() ?? typeof(object), SerializerOptions);
                }

                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
            }

            return ms.ToArray();
        }
    }
}
