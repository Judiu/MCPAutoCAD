using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Lisp
{
    /// <summary>
    /// cancelar_comando (0.8.4). Hilo principal; requiere documento. ÚNICA herramienta que se ejecuta con un comando activo
    /// (<see cref="IToolHandler.RunsDuringCommand"/>): envía dos Ctrl+C ("\x03\x03") con Document.SendStringToExecute, como
    /// pulsar Esc dos veces. Es asíncrono: la cancelación ocurre cuando AutoCAD procesa la entrada, después de responder;
    /// se comprueba con estado. Sin comando activo no envía nada.
    /// </summary>
    internal sealed class CancelarComandoHandler : IToolHandler
    {
        public string Name => "cancelar_comando";

        public string Description =>
            "Cancela el comando que tenga activo AutoCAD (como pulsar Esc dos veces) y libera el puente cuando las herramientas " +
            "responden BUSY por un comando a medias. Sin comando activo no hace nada. La cancelación es asíncrona: confírmela con estado. " +
            "LÍMITES: no cierra diálogos modales de AutoCAD (p. ej. el de aprobación de cargar_lisp; ese solo lo cierra el usuario o su " +
            "tiempo de 120 s) y, durante una escritura diferida en curso, responde BUSY hasta que termine.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public bool RunsDuringCommand => true;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{ ""type"": ""object"", ""properties"": {}, ""additionalProperties"": false }");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""habia_comando"": { ""type"": ""boolean"" },
    ""comando"": { ""type"": [""string"", ""null""] },
    ""enviado"": { ""type"": ""boolean"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""habia_comando"", ""comando"", ""enviado"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => ParamReader.RequireObject(parameters, System.Array.Empty<string>());

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            ValidateParams(parameters);
            Document doc = ctx.RequireDocument();
            var warnings = new List<string>();
            bool active = ctx.CmdActive != 0 || ctx.Busy.CommandActive;
            string? name = null;
            if (active)
            {
                try
                {
                    name = AcadUtil.GetStringSysVar("CMDNAMES");
                }
                catch (System.Exception)
                {
                    name = null;
                }

                if (string.IsNullOrEmpty(name))
                {
                    name = ctx.Busy.CurrentCommand;
                }

                if (ctx.CmdActive == 0)
                {
                    // Fallo 26: solo la pila de eventos dice que hay comando; AutoCAD no tiene ninguno en este documento.
                    PluginLog.Warn("cancelar_comando: los eventos indican '" + (name ?? "?") + "' pero CMDACTIVE = 0.");
                    warnings.Add("Los eventos del plugin indican el comando '" + (name ?? "?") + "', pero AutoCAD no tiene ningún "
                        + "comando activo en este documento (CMDACTIVE = 0): el Ctrl+C no tendrá efecto. Si estado sigue ocupado, "
                        + "haga clic en el dibujo y pulse Esc; si persiste, avise (fallo 26).");
                }

                doc.SendStringToExecute("\x03\x03", true, false, false);
                PluginLog.Info("cancelar_comando: Ctrl+C enviado (comando activo: " + (name ?? "?") + ")");
                warnings.Add("Cancelación enviada; AutoCAD la procesa después de esta respuesta: confírmela con estado.");
            }

            return new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["habia_comando"] = active,
                ["comando"] = string.IsNullOrEmpty(name) ? null : name,
                ["enviado"] = active,
                ["advertencias"] = warnings,
            };
        }
    }
}
