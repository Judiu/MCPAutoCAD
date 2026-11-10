using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.GeometryCore.Lisp;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Lisp
{
    /// <summary>
    /// ejecutar_comando (0.8.4). Hilo principal; requiere documento; sin comando activo (BUSY si lo hay). Lanza con
    /// Document.SendStringToExecute un comando y sus respuestas SOLO si la secuencia exacta (comando + argumentos, mismo
    /// orden) está en "comandos" de lisp_permitidos.json; los comandos de <see cref="LispRules.DeniedCommands"/> nunca. El
    /// agente no compone respuestas libres: una respuesta sobrante se ejecutaría como otro comando. Es asíncrono: el comando
    /// corre después de responder; su resultado se comprueba con estado o consultar_entidades. Puede escribir en el dibujo
    /// (los pasos de deshacer los crea el propio comando).
    /// </summary>
    internal sealed class EjecutarComandoHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "comando", "argumentos" };

        public string Name => "ejecutar_comando";

        public string Description =>
            "Lanza un comando de AutoCAD con una secuencia de respuestas que el usuario dejó permitida en " +
            "%LOCALAPPDATA%\\MCPAutoCAD\\lisp_permitidos.json (\"comandos\"); comando y argumentos deben coincidir exactamente con " +
            "una entrada. Rechaza siempre SHELL, SCRIPT, NETLOAD, APPLOAD y similares. Es asíncrono: compruebe el resultado con estado; " +
            "si el comando queda esperando una respuesta, use cancelar_comando.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""comando"": { ""type"": ""string"", ""pattern"": ""^[A-Za-z][A-Za-z0-9_\\-]{0,63}$"", ""description"": ""Nombre del comando (sin '_' ni '.' delante), tal como está en lisp_permitidos.json."" },
    ""argumentos"": { ""type"": ""array"", ""maxItems"": 20, ""items"": { ""type"": ""string"", ""maxLength"": 100 }, ""description"": ""Respuestas a los prompts, idénticas (mismo orden, mayúsculas incluidas) a una entrada permitida. \""\"" = Intro. Por defecto []."" }
  },
  ""required"": [""comando""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""comando"": { ""type"": ""string"" },
    ""argumentos"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""enviado"": { ""type"": ""boolean"" },
    ""cadena"": { ""type"": ""string"" },
    ""archivo_permitidos"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""comando"", ""argumentos"", ""enviado"", ""cadena"", ""archivo_permitidos"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (string comando, List<string> argumentos) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string? comando = ParamReader.OptionalString(p, "comando", 1, 64);
            if (comando == null || !LispRules.IsValidCommandName(comando))
            {
                throw ToolException.InvalidParams("comando: obligatorio; letras, dígitos, '_' o '-', empezando por letra");
            }

            List<string> args = ParamReader.OptionalStringArray(p, "argumentos", 0, LispRules.MaxArguments, 0) ?? new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (!LispRules.IsValidArgument(args[i]))
                {
                    throw ToolException.InvalidParams(
                        "argumentos[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "]: sin espacios, paréntesis, '!', comillas ni ';' (máx. 100 caracteres)");
                }
            }

            return (comando.ToUpperInvariant(), args);
        }

        /// <summary>Ejemplo del formato de lisp_permitidos.json. Es solo texto para el mensaje de error: no concede nada.</summary>
        private const string FormatoEjemplo = "{ \"comandos\": [ \"REGEN\", { \"comando\": \"ZOOM\", \"argumentos\": [ \"_E\" ] } ] }";

        /// <summary>
        /// Mensaje de "no permitido": dice qué secuencia falta, en qué archivo buscarla, que solo lo edita el usuario y el
        /// formato exacto (clave "comandos", cadena = sin respuestas, objeto = con argumentos en orden). Si el archivo tiene
        /// errores de lectura, los cita. No crea el archivo ni propone entradas.
        /// </summary>
        private static string MensajeNoPermitido(string comando, List<string> args, string file, bool exists, PinFile pins)
        {
            var partes = new List<string> { comando };
            foreach (string a in args)
            {
                partes.Add(a.Length == 0 ? "\"\" (Intro)" : a);
            }

            string secuencia = string.Join(" ", partes);
            string cabecera = exists
                ? "comando: la secuencia " + secuencia + " no está en \"comandos\" de " + file + "."
                : "comando: no hay permiso para " + secuencia + " porque el archivo de permisos no existe: " + file + ".";
            string errores = pins.Errores.Count == 0
                ? string.Empty
                : " Ese archivo tiene " + pins.Errores.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " entrada(s) no válidas, p. ej.: " + pins.Errores[0] + ".";
            return cabecera + errores
                + " Lo crea y lo edita solo el usuario a mano; el puente no lo escribe ni propone entradas. "
                + "Formato: un objeto JSON con la clave \"comandos\" (lista). Cada elemento es una cadena (comando sin respuestas) "
                + "o un objeto {\"comando\": nombre, \"argumentos\": [respuestas en orden exacto]}. Ejemplo: " + FormatoEjemplo
                + ". La comparación es exacta (nombre, argumentos, mayúsculas y orden).";
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (string comando, List<string> args) = Parse(parameters);
            Document doc = ctx.RequireDocument();
            var warnings = new List<string>();
            string file = LispPinStore.FilePath;

            if (LispRules.IsDenied(comando))
            {
                throw ToolException.InvalidParams("comando: " + comando + " está prohibido siempre en el puente");
            }

            (PinFile pins, bool exists) = LispPinStore.Read();
            foreach (string e in pins.Errores)
            {
                warnings.Add(LispRules.PinFileName + ": " + e);
            }

            if (LispRules.FindCommand(pins, comando, args) == null)
            {
                throw ToolException.InvalidParams(
                    MensajeNoPermitido(comando, args, file, exists, pins),
                    new Dictionary<string, object?>
                    {
                        ["archivo_permitidos"] = file,
                        ["archivo_existe"] = exists,
                        ["formato"] = FormatoEjemplo,
                    });
            }

            string cadena = LispRules.BuildCommandString(comando, args);
            doc.SendStringToExecute(cadena, true, false, true);
            PluginLog.Info("ejecutar_comando: enviado '" + cadena.TrimEnd() + "'");
            warnings.Add("Comando enviado; AutoCAD lo ejecuta después de esta respuesta: compruebe el resultado con estado.");

            return new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["comando"] = comando,
                ["argumentos"] = args,
                ["enviado"] = true,
                ["cadena"] = cadena,
                ["archivo_permitidos"] = file,
                ["advertencias"] = warnings,
            };
        }
    }
}
