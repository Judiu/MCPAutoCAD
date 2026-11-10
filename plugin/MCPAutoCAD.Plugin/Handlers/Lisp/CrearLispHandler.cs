using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using MCPAutoCAD.GeometryCore.Lisp;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Lisp
{
    /// <summary>
    /// crear_lisp (0.8.5). Guarda un .lsp escrito por el agente en %LOCALAPPDATA%\MCPAutoCAD\lisp\ (la crea si falta). NO
    /// ejecuta nada ni toca el dibujo: cargarlo exige cargar_lisp con confirmar: true y la aprobación del usuario en el
    /// diálogo de AutoCAD (<see cref="LispApprovalDialog"/>). La carpeta no debe declararse de confianza en AutoCAD.
    /// </summary>
    internal sealed class CrearLispHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "nombre", "codigo", "sobrescribir" };

        public string Name => "crear_lisp";

        public string Description =>
            "Guarda un archivo .lsp con el código indicado en %LOCALAPPDATA%\\MCPAutoCAD\\lisp\\<nombre>.lsp, sin ejecutarlo. " +
            "Devuelve la ruta, el SHA-256, los comandos c: que define y las funciones sensibles. Para cargarlo use cargar_lisp con " +
            "confirmar: true: el usuario debe aprobarlo en un diálogo de AutoCAD tras leer el código.";

        public bool RequiresDocument => false;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": { ""type"": ""string"", ""pattern"": ""^[A-Za-z0-9][A-Za-z0-9_\\-]{0,63}$"", ""description"": ""Nombre del archivo sin extensión (letras, dígitos, '_' o '-'); no se admiten CON, NUL, COM1… ni nombres que empiecen por 'acad'."" },
    ""codigo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200000, ""description"": ""Código AutoLISP completo, legible y comentado: el usuario lo lee antes de aprobar la carga. Se rechazan caracteres invisibles o de control (salvo tab y saltos de línea), líneas de más de 200 caracteres y más de 10 líneas en blanco seguidas."" },
    ""sobrescribir"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = reemplazar un archivo con ese nombre."" }
  },
  ""required"": [""nombre"", ""codigo""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""ruta"": { ""type"": ""string"" },
    ""tamano_bytes"": { ""type"": ""integer"" },
    ""sha256"": { ""type"": ""string"" },
    ""sobrescrito"": { ""type"": ""boolean"" },
    ""comandos_definidos"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""funciones_sensibles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""ruta"", ""tamano_bytes"", ""sha256"", ""sobrescrito"", ""comandos_definidos"", ""funciones_sensibles"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (string nombre, string codigo, bool sobrescribir) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string? nombre = ParamReader.OptionalString(p, "nombre", 1, 64);
            if (nombre == null || !LispRules.IsValidLispName(nombre))
            {
                throw ToolException.InvalidParams("nombre: obligatorio; letras, dígitos, '_' o '-' (máx. 64), sin extensión; no se admiten nombres de dispositivo (CON, NUL, COM1…) ni los que empiezan por 'acad'");
            }

            string? codigo = ParamReader.OptionalString(p, "codigo", 1, LispRules.MaxSourceChars);
            if (codigo == null)
            {
                throw ToolException.InvalidParams("codigo: obligatorio (1 a 200000 caracteres)");
            }

            List<string> problems = LispRules.DisplayProblems(codigo);
            if (problems.Count > 0)
            {
                throw ToolException.InvalidParams(
                    "codigo: el usuario no podría leerlo fielmente para aprobarlo: " + string.Join("; ", problems));
            }

            return (nombre, codigo, ParamReader.OptionalBool(p, "sobrescribir") ?? false);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (string nombre, string codigo, bool sobrescribir) = Parse(parameters);
            var warnings = new List<string>();
            string dir = LispPinStore.AgentFolder;
            string ruta = Path.Combine(dir, nombre + ".lsp");
            bool existed;
            byte[] bytes = new UTF8Encoding(false).GetBytes(codigo);
            try
            {
                Directory.CreateDirectory(dir);
                existed = File.Exists(ruta);
                if (existed && !sobrescribir)
                {
                    throw ToolException.InvalidParams("nombre: ya existe " + ruta + "; pase sobrescribir: true para reemplazarlo");
                }

                File.WriteAllBytes(ruta, bytes);
            }
            catch (ToolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ToolException.InvalidParams("no se pudo escribir " + ruta + " (" + ex.GetType().Name + ")");
            }

            string sha = LispRules.Sha256Hex(bytes);
            List<string> sensibles = LispRules.FoundSensitive(codigo);
            PluginLog.Info("crear_lisp: escrito " + ruta + " sha256=" + sha);
            warnings.Add("Archivo guardado sin ejecutar. cargar_lisp con confirmar: true abrirá en AutoCAD un diálogo para que el usuario lo apruebe.");
            if (sensibles.Count > 0)
            {
                warnings.Add("Usa funciones sensibles (" + string.Join(", ", sensibles) + "): el diálogo se las mostrará al usuario.");
            }

            return new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["ruta"] = ruta,
                ["tamano_bytes"] = bytes.LongLength,
                ["sha256"] = sha,
                ["sobrescrito"] = existed,
                ["comandos_definidos"] = LispRules.DefinedCommands(codigo),
                ["funciones_sensibles"] = sensibles,
                ["advertencias"] = warnings,
            };
        }
    }
}
