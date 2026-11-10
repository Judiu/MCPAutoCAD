using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.GeometryCore.Documentos;
using MCPAutoCAD.Plugin.Handlers;
using MCPAutoCAD.Plugin.Handlers.Documento;

namespace MCPAutoCAD.Plugin.Bridge
{
    /// <summary>
    /// Fallo 25 (decisión del usuario, 2026-11-10): parámetro opcional <c>documento_esperado</c> en toda herramienta que
    /// requiere documento. NO cambia de documento: si el documento activo no es el esperado, la llamada se rechaza antes de
    /// ejecutar nada. Lo gestiona el despachador (se retira de los parámetros antes de validarlos) y el manifest lo añade al
    /// schema, así que los handlers no lo conocen. Quedan fuera abrir_documento, activar_documento y guardar (tienen su
    /// propio parámetro de documento) y las herramientas del hilo del pipe.
    /// </summary>
    internal static class DocumentoEsperado
    {
        public const string Param = "documento_esperado";

        public const int MaxLength = 1024;

        private static readonly HashSet<string> Excluidas = new HashSet<string>(StringComparer.Ordinal)
        {
            "abrir_documento", "activar_documento", "guardar",
        };

        private const string SchemaFragment =
            @"{ ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1024, ""description"": ""Nombre o ruta del documento que debe estar activo; si no lo está, WRONG_DOC sin ejecutar nada (no cambia de documento)."" }";

        /// <summary>true si la herramienta admite documento_esperado.</summary>
        public static bool Applies(IToolHandler handler) =>
            handler.RequiresDocument && !handler.RunsOnPipeThread && !Excluidas.Contains(handler.Name);

        /// <summary>
        /// Si <paramref name="parameters"/> trae documento_esperado (y la herramienta lo admite), lo valida, lo devuelve en
        /// <paramref name="esperado"/> y devuelve los parámetros sin él. En otro caso devuelve los parámetros tal cual.
        /// </summary>
        public static JsonElement Strip(IToolHandler handler, JsonElement parameters, out string? esperado)
        {
            esperado = null;
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(Param, out JsonElement value))
            {
                return parameters;
            }

            if (!Applies(handler))
            {
                throw ToolException.InvalidParams(
                    Param + " no se admite en " + handler.Name
                    + (Excluidas.Contains(handler.Name) ? " (use su parámetro de documento)" : " (no actúa sobre un documento)"));
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw ToolException.InvalidParams(Param + ": debe ser un texto (nombre o ruta del documento)");
            }

            string s = (value.GetString() ?? "").Trim();
            if (s.Length == 0 || s.Length > MaxLength)
            {
                throw ToolException.InvalidParams(Param + ": debe tener entre 1 y " + MaxLength + " caracteres");
            }

            esperado = s;
            var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty prop in parameters.EnumerateObject())
            {
                if (!string.Equals(prop.Name, Param, StringComparison.Ordinal))
                {
                    copy[prop.Name] = prop.Value;
                }
            }

            return JsonSerializer.SerializeToElement(copy);
        }

        /// <summary>
        /// Hilo principal. Exige que el documento activo coincida con <paramref name="esperado"/> (nombre o ruta, mismas reglas
        /// que activar_documento). Si el nombre coincide con varios abiertos y uno es el activo, se acepta: la condición
        /// "el activo es X" se cumple. Ninguno → NOT_FOUND; otro documento → WRONG_DOC. Errores con la clave documento_esperado.
        /// </summary>
        public static void Check(DocumentCollection docs, Document activo, string esperado)
        {
            List<Document> abiertos = DocumentoSupport.Abiertos(docs);
            List<DocumentoRef> refs = DocumentoSupport.Describir(abiertos, activo);
            ResultadoConsulta r = DocumentoRules.Buscar(refs, esperado);
            int iActivo = abiertos.FindIndex(d => ReferenceEquals(d, activo));
            string activoNombre = Path.GetFileName(activo.Name ?? "");
            var data = new Dictionary<string, object?>
            {
                ["documento_esperado"] = esperado,
                ["documento_activo"] = activoNombre,
                ["ruta_activo"] = DocumentoSupport.RutaDe(activo),
            };

            if (r.Estado == ResultadoBusqueda.Encontrado && r.Indice == iActivo)
            {
                return;
            }

            if (r.Estado == ResultadoBusqueda.Ambiguo && System.Linq.Enumerable.Contains(r.Candidatos, iActivo))
            {
                return;
            }

            if (r.Estado == ResultadoBusqueda.NoEncontrado)
            {
                data["documentos"] = DocumentoSupport.Filas(refs);
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "documento_esperado: no hay ningún documento abierto que coincida con '" + esperado + "' (el activo es '"
                    + activoNombre + "'). No se ejecutó nada.",
                    data);
            }

            throw new ToolException(
                ErrorCodes.WrongDoc,
                "documento_esperado: el documento activo es '" + activoNombre + "', no '" + esperado
                + "'. No se ejecutó nada. Use activar_documento o pida al usuario que vuelva a esa pestaña.",
                data);
        }

        /// <summary>Schema publicado en el manifest: el de la herramienta con documento_esperado añadido a properties.</summary>
        public static JsonElement AugmentSchema(JsonElement schema)
        {
            JsonNode? node = JsonNode.Parse(schema.GetRawText());
            if (node is not JsonObject obj)
            {
                return schema;
            }

            if (obj["properties"] is not JsonObject props)
            {
                props = new JsonObject();
                obj["properties"] = props;
            }

            if (!props.ContainsKey(Param))
            {
                props[Param] = JsonNode.Parse(SchemaFragment);
            }

            return JsonSerializer.SerializeToElement(obj);
        }
    }
}
