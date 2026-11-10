using System;
using System.Globalization;
using System.Text.Json;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>ping (protocol.md §5.1). Hilo del pipe; no toca AutoCAD.</summary>
    internal sealed class PingHandler : IToolHandler
    {
        private static readonly string[] Allowed = Array.Empty<string>();

        public string Name => "ping";

        public string Description =>
            "Comprueba que el plugin de AutoCAD responde. No toca el dibujo ni AutoCAD y funciona aunque AutoCAD esté ocupado.";

        public bool RequiresDocument => false;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => true;

        public JsonElement InputSchema { get; } = Schema.Parse(
            @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pong"": { ""type"": ""boolean"" },
    ""plugin_version"": { ""type"": ""string"" },
    ""protocol_version"": { ""type"": ""string"" },
    ""pid"": { ""type"": ""integer"" },
    ""hora"": { ""type"": ""string"", ""description"": ""ISO 8601 con offset local"" }
  },
  ""required"": [""pong"", ""plugin_version"", ""protocol_version"", ""pid"", ""hora""]
}");

        public void ValidateParams(JsonElement parameters) => ParamReader.RequireObject(parameters, Allowed);

        public object Execute(JsonElement parameters, ExecutionContext ctx) => new
        {
            pong = true,
            plugin_version = PluginInfo.PluginVersion,
            protocol_version = PluginInfo.ProtocolVersion,
            pid = ctx.Pid,
            hora = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        };
    }
}
