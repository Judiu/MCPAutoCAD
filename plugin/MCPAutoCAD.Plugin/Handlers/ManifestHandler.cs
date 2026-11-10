using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// tools/manifest (protocol.md §4). Hilo del pipe; no toca AutoCAD. Construye tools[] a partir de los handlers
    /// registrados, en orden. No se publica a sí mismo como herramienta.
    /// </summary>
    internal sealed class ManifestHandler : IToolHandler
    {
        private static readonly string[] Allowed = Array.Empty<string>();
        private readonly IReadOnlyList<IToolHandler> _tools;
        private readonly int _pid;

        public ManifestHandler(IReadOnlyList<IToolHandler> tools, int pid)
        {
            _tools = tools;
            _pid = pid;
        }

        public string Name => ToolRegistry.ManifestMethod;

        public string Description => "Devuelve el manifiesto de herramientas del plugin.";

        public bool RequiresDocument => false;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => true;

        public JsonElement InputSchema { get; } = Schema.Parse(
            @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");

        public JsonElement? OutputSchema => null;

        public void ValidateParams(JsonElement parameters) => ParamReader.RequireObject(parameters, Allowed);

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            var tools = new List<Dictionary<string, object?>>(_tools.Count);
            foreach (IToolHandler tool in _tools)
            {
                var entry = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["escritura"] = tool.IsWrite,
                    ["requiere_documento"] = tool.RequiresDocument,
                    // Fallo 25: el despachador acepta documento_esperado en estas herramientas; se publica en su schema.
                    ["inputSchema"] = Bridge.DocumentoEsperado.Applies(tool)
                        ? Bridge.DocumentoEsperado.AugmentSchema(tool.InputSchema)
                        : tool.InputSchema,
                };
                if (tool.OutputSchema.HasValue)
                {
                    entry["outputSchema"] = tool.OutputSchema.Value;
                }

                tools.Add(entry);
            }

            return new Dictionary<string, object?>
            {
                ["protocol_version"] = PluginInfo.ProtocolVersion,
                ["plugin_version"] = PluginInfo.PluginVersion,
                ["pid"] = _pid,
                ["tools"] = tools,
            };
        }
    }
}
