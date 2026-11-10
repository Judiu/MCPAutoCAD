using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// confirmar (protocol.md §5E.5). Hilo principal; requiere documento; ESCRITURA. Comprueba que el preview_id existe
    /// (NOT_FOUND) y que es del documento activo (INVALID_PARAMS sin escribir) y delega en
    /// <see cref="GeneratorBase.Confirm"/>, que verifica las anclas, recalcula y, en UN paso de deshacer, borra la preview
    /// que siga en MCP-PREVIEW y dibuja la geometría definitiva.
    /// </summary>
    internal sealed class ConfirmarHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "preview_id" };

        public const string SchemaPreviewId =
            @"{ ""type"": ""string"", ""minLength"": 1, ""maxLength"": 64, ""description"": ""Identificador devuelto por una herramienta generadora con preview: true (p. ej. 'prev-1a2b3c4d5e6f')."" }";

        public string Name => "confirmar";

        public string Description =>
            "ESCRIBE la geometría definitiva de una previsualización (preview_id): en un solo paso de deshacer borra las entidades de MCP-PREVIEW que sigan existiendo y recalcula y dibuja la geometría en sus capas definitivas. Falla sin escribir si el preview_id caducó (15 min), es de otro documento o sus entidades ancla cambiaron.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""preview_id"": " + SchemaPreviewId + @"
  },
  ""required"": [""preview_id""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""confirmado"": { ""type"": ""boolean"" },
    ""preview_id"": { ""type"": ""string"" },
    ""herramienta"": { ""type"": ""string"" },
    ""n_preview_borradas"": { ""type"": ""integer"" },
    ""preview"": { ""type"": ""boolean"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""handles_borrados"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capas_importadas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""propiedades_dinamicas"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""entidades"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""capa"": { ""type"": ""string"" },
    ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""espacio"": { ""type"": ""string"" },
    ""puntos_resueltos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""numeros"": { ""type"": ""object"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""confirmado"", ""preview_id"", ""herramienta"", ""n_preview_borradas"", ""handles"", ""handles_borrados"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => ParsePreviewId(parameters, Allowed);

        /// <summary>Lee el preview_id obligatorio (1–64 caracteres). Compartido con descartar_preview.</summary>
        internal static string ParsePreviewId(JsonElement p, IReadOnlyCollection<string> allowed)
        {
            ParamReader.RequireObject(p, allowed);
            return ParamReader.OptionalString(p, "preview_id", 1, 64) ?? throw ToolException.InvalidParams("preview_id: obligatorio");
        }

        /// <summary>
        /// Pasos (1) y (2) de §5E.5: registro vivo y del documento activo. No escribe nada.
        /// </summary>
        internal static PreviewRecord RequireRecord(string id, Document doc)
        {
            PreviewRecord rec = PreviewStore.Registry.Get(id) ?? throw ToolException.PreviewNotFound(id);
            if (rec.DocKey != PreviewStore.DocKey(doc))
            {
                throw ToolException.InvalidParams(PreviewRegistry.OtherDocumentDetail(id),
                    new Dictionary<string, object?> { ["preview_id"] = id });
            }

            return rec;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            string id = ParsePreviewId(parameters, Allowed);
            Document doc = ctx.RequireDocument();
            PreviewRecord rec = RequireRecord(id, doc);
            GeneratorBase generator = GeneratorBase.Find(rec.Herramienta)
                ?? throw new ToolException(ErrorCodes.Internal, "La herramienta generadora '" + rec.Herramienta + "' no está disponible.");
            return generator.Confirm(rec, ctx);
        }
    }
}
