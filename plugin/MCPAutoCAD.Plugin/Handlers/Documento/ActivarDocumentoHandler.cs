using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.Plugin.Bridge;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MCPAutoCAD.Plugin.Handlers.Documento
{
    /// <summary>
    /// activar_documento (0.9.x): hace activo un documento ya abierto (por nombre o ruta). No modifica ningún dibujo, pero
    /// CAMBIA el destino de las herramientas que actúan sobre "el documento activo": por eso la respuesta siempre dice cuál
    /// era el anterior y cuál es el nuevo. Sin comando activo (BUSY por el despachador). Ambigüedad → INVALID_PARAMS con la lista.
    /// </summary>
    internal sealed class ActivarDocumentoHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "documento" };

        public string Name => "activar_documento";

        public string Description =>
            "Hace activo uno de los documentos abiertos en AutoCAD, por nombre o por ruta completa. No modifica ningún dibujo, pero TODAS " +
            "las herramientas siguientes (también las que escriben y guardar) actuarán sobre el documento activado: compruebe antes con estado. Error con la lista de abiertos si " +
            "no coincide o si el nombre es ambiguo.";

        public bool RequiresDocument => false;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""documento"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Nombre (p. ej. PLANTA.dwg, o sin extensión si es único) o ruta completa de un documento abierto."" }
  },
  ""required"": [""documento""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""cambio"": { ""type"": ""boolean"", ""description"": ""false si ya era el activo."" },
    ""activo"": { ""type"": ""object"", ""properties"": {
      ""nombre"": { ""type"": ""string"" }, ""ruta"": { ""type"": [""string"", ""null""] } } },
    ""anterior"": { ""type"": [""object"", ""null""], ""properties"": {
      ""nombre"": { ""type"": ""string"" }, ""ruta"": { ""type"": [""string"", ""null""] } } },
    ""documentos"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""nombre"": { ""type"": ""string"" }, ""ruta"": { ""type"": [""string"", ""null""] }, ""activo"": { ""type"": ""boolean"" } } } },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""cambio"", ""activo"", ""anterior"", ""documentos"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static string Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string? documento = ParamReader.OptionalString(p, "documento", 1, 1000);
            if (documento == null)
            {
                throw ToolException.InvalidParams("documento: obligatorio");
            }

            return documento;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            string consulta = Parse(parameters);
            DocumentCollection docs = AcApp.DocumentManager;
            if (docs.Count == 0)
            {
                throw ToolException.NoDoc();
            }

            DocumentoSupport.RequerirContextoAplicacion(docs);
            Document objetivo = DocumentoSupport.Resolver(docs, consulta);
            Document? anterior = docs.MdiActiveDocument;
            var warnings = new List<string>();

            if (ReferenceEquals(objetivo, anterior))
            {
                warnings.Add("El documento ya era el activo; no cambió nada.");
            }
            else
            {
                if (!string.IsNullOrEmpty(objetivo.CommandInProgress))
                {
                    throw ToolException.BusyCommand(objetivo.CommandInProgress);
                }

                if (!DocumentoSupport.Activar(docs, objetivo))
                {
                    throw new ToolException(
                        ErrorCodes.Busy,
                        "AutoCAD no permite cambiar el documento activo en este momento. Reintente cuando termine.",
                        new Dictionary<string, object?> { ["motivo"] = "activacion", ["comando"] = "" });
                }

                warnings.Add("Las herramientas que actúan sobre el documento activo ahora actúan sobre '" + Path.GetFileName(objetivo.Name ?? "")
                    + "'. Compruebe con estado antes de escribir.");
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = ctx.Pid,
                ["cambio"] = !ReferenceEquals(objetivo, anterior),
                ["activo"] = Describe(objetivo),
                ["anterior"] = anterior == null || ReferenceEquals(anterior, objetivo) ? null : Describe(anterior),
                ["documentos"] = DocumentoSupport.Filas(DocumentoSupport.Describir(DocumentoSupport.Abiertos(docs), docs.MdiActiveDocument)),
                ["advertencias"] = warnings,
            };
        }

        private static Dictionary<string, object?> Describe(Document d) => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["nombre"] = Path.GetFileName(d.Name ?? ""),
            ["ruta"] = DocumentoSupport.RutaDe(d),
        };
    }
}
