using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.GeometryCore.Lisp;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Lisp
{
    /// <summary>
    /// cargar_lisp (0.8.4). Hilo principal; requiere documento; sin comando activo. SEGURIDAD:
    /// <list type="bullet">
    /// <item>Sin confirmar: true solo analiza (ruta, tamaño, SHA-256, c:COMANDOS, funciones sensibles, si está fijado y si la
    /// carpeta es de confianza) y devuelve la entrada JSON que el USUARIO debe pegar en lisp_permitidos.json.</item>
    /// <item>Con confirmar: true carga SOLO si ruta + SHA-256 están en "lisp" de lisp_permitidos.json (que el puente nunca
    /// escribe), y si con SECURELOAD ≥ 1 la carpeta está en TRUSTEDPATHS (si no, AutoCAD abriría un diálogo que bloquea
    /// la sesión: se rechaza con la carpeta a añadir; el puente no toca SECURELOAD ni TRUSTEDPATHS).</item>
    /// <item>La carga es (load "ruta") enviado con SendStringToExecute: asíncrona, después de responder. Ventana TOCTOU:
    /// entre el hash y la carga el archivo podría cambiar; quien puede escribir en esa carpeta ya puede alterar el LISP
    /// que el usuario carga a mano, así que no se añade otra protección.</item>
    /// <item>Desde 0.8.5, un .lsp NO fijado se puede cargar si el USUARIO lo aprueba en <see cref="LispApprovalDialog"/>
    /// (casilla "He leído el código" + Cargar; 120 s sin respuesta = no). Se carga una copia exacta de los bytes mostrados,
    /// guardada por hash en lisp\cargados, para que un cambio del archivo tras la aprobación no se cargue. .fas/.vlx no
    /// fijados se rechazan (no se puede mostrar su código). Aprobar no fija nada: la siguiente carga vuelve a preguntar.</item>
    /// <item>No registra nada en la Startup Suite (sería escribir en el registro) ni evalúa expresiones.</item>
    /// </list>
    /// </summary>
    internal sealed class CargarLispHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "ruta", "confirmar" };

        /// <summary>
        /// true mientras el diálogo de aprobación está abierto: su bucle modal despacha trabajos de otras conexiones del
        /// pipe, y un segundo cargar_lisp no debe apilar otro diálogo (→ BUSY).
        /// </summary>
        private static bool _dialogOpen;

        public string Name => "cargar_lisp";

        public string Description =>
            "Analiza o carga un archivo .lsp/.fas/.vlx en el dibujo activo. Sin confirmar: true solo lo analiza (SHA-256, comandos c: " +
            "que define, funciones sensibles) y devuelve la entrada que el usuario debe añadir a %LOCALAPPDATA%\\MCPAutoCAD\\lisp_permitidos.json. " +
            "Con confirmar: true carga lo permitido ahí directamente; un .lsp no permitido (p. ej. uno de crear_lisp) solo si el usuario " +
            "lo aprueba en un diálogo de AutoCAD tras leer el código (hasta 2 minutos; puede rechazarlo). " +
            "La carga es asíncrona. No registra en la Startup Suite.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        /// <summary>El diálogo de aprobación espera hasta 120 s al usuario.</summary>
        public int MainThreadTimeoutMs => (LispApprovalDialog.TimeoutSeconds + 20) * 1000;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta del .lsp, .fas o .vlx, sin '..' ni comodines."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = cargar (exige ruta y SHA-256 permitidos por el usuario); por defecto solo analiza."" }
  },
  ""required"": [""ruta""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""preview"": { ""type"": ""boolean"" },
    ""enviado"": { ""type"": ""boolean"" },
    ""ruta"": { ""type"": ""string"" },
    ""tamano_bytes"": { ""type"": ""integer"" },
    ""sha256"": { ""type"": ""string"" },
    ""comandos_definidos"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""string"" } },
    ""funciones_sensibles"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""string"" } },
    ""fijado"": { ""type"": ""boolean"" },
    ""entrada_para_fijar"": { ""type"": ""string"" },
    ""archivo_permitidos"": { ""type"": ""string"" },
    ""secureload"": { ""type"": ""integer"" },
    ""carpeta_de_confianza"": { ""type"": ""boolean"" },
    ""aprobacion"": { ""type"": ""string"", ""enum"": [""no_aplica"", ""fijado"", ""dialogo_aceptado"", ""dialogo_rechazado""] },
    ""motivo"": { ""type"": [""string"", ""null""], ""enum"": [""rechazado_por_usuario"", ""cerrado_sin_respuesta"", ""timeout"", ""error_dialogo"", null], ""description"": ""Solo si el diálogo no se aprobó: rechazado_por_usuario (No cargar o Esc), cerrado_sin_respuesta (cerrado con la ventana), timeout (120 s sin respuesta), error_dialogo (no se pudo mostrar). null si se aprobó o no hubo diálogo."" },
    ""segundos_abierto"": { ""type"": [""number"", ""null""], ""description"": ""Segundos que estuvo visible el diálogo de aprobación; null si no se mostró."" },
    ""ruta_cargada"": { ""type"": [""string"", ""null""] },
    ""origen_texto"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview"", ""enviado"", ""ruta"", ""tamano_bytes"", ""sha256"", ""comandos_definidos"", ""funciones_sensibles"",
    ""fijado"", ""entrada_para_fijar"", ""archivo_permitidos"", ""secureload"", ""carpeta_de_confianza"", ""aprobacion"", ""ruta_cargada"", ""origen_texto"", ""advertencias"",
    ""motivo"", ""segundos_abierto""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (string ruta, bool confirmar) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string? ruta = ParamReader.OptionalString(p, "ruta", 1, 1000);
            if (ruta == null)
            {
                throw ToolException.InvalidParams("ruta: obligatoria");
            }

            string? err = LispRules.ValidateLispPath(ruta);
            if (err != null)
            {
                throw ToolException.InvalidParams(err);
            }

            return (ruta, ParamReader.OptionalBool(p, "confirmar") ?? false);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (string ruta, bool confirmar) = Parse(parameters);
            Document doc = ctx.RequireDocument();
            var warnings = new List<string>();
            string file = LispPinStore.FilePath;

            byte[] bytes = ReadFile(ruta);
            string sha = LispRules.Sha256Hex(bytes);
            bool isSource = ruta.EndsWith(".lsp", StringComparison.OrdinalIgnoreCase);
            List<string>? comandos = null;
            List<string>? sensibles = null;
            if (isSource)
            {
                string text = LispRules.DecodeSource(bytes);
                comandos = LispRules.DefinedCommands(text);
                sensibles = LispRules.FoundSensitive(text);
                if (sensibles.Count > 0)
                {
                    warnings.Add("El archivo usa funciones que tocan el sistema o evalúan código (" + string.Join(", ", sensibles)
                        + "); revíselo antes de permitirlo. Este análisis por texto no es una garantía.");
                }
            }
            else
            {
                warnings.Add("Archivo compilado (.fas/.vlx): no se pueden listar sus comandos ni sus funciones; permítalo solo si confía en su origen.");
            }

            (PinFile pins, bool exists) = LispPinStore.Read();
            foreach (string e in pins.Errores)
            {
                warnings.Add(LispRules.PinFileName + ": " + e);
            }

            bool fijado = LispRules.IsPinned(pins, ruta, sha);
            int secureload = SafeInt("SECURELOAD");
            bool trusted = LispRules.IsInTrustedPaths(ruta, SafeString("TRUSTEDPATHS"));
            string entrada = LispRules.PinEntryJson(ruta, sha);

            if (!fijado)
            {
                warnings.Add("No está fijado: " + (isSource
                    ? "cargarlo exige la aprobación del usuario en un diálogo de AutoCAD; para cargarlo sin diálogo, el usuario puede añadir "
                    : "un .fas/.vlx no fijado no se carga (no se puede mostrar al usuario); el usuario puede añadir ")
                    + entrada + " a \"lisp\" en " + file + (exists ? "" : " (el archivo no existe todavía)") + ".");
            }

            if (secureload == 0)
            {
                warnings.Add("SECURELOAD=0: AutoCAD carga código de cualquier carpeta sin avisar.");
            }
            else if (!trusted && fijado)
            {
                warnings.Add("La carpeta no está en TRUSTEDPATHS: con SECURELOAD=" + secureload
                    + " la carga se rechaza (AutoCAD abriría un diálogo de seguridad). El usuario debe añadir la carpeta en Opciones > Archivos > Ubicaciones de confianza.");
            }

            bool enviado = false;
            string aprobacion = "no_aplica";
            string? rutaCargada = null;
            string? motivo = null;
            double? segundosAbierto = null;
            if (confirmar)
            {
                string cargar;
                if (fijado)
                {
                    if (secureload != 0 && !trusted)
                    {
                        throw ToolException.InvalidParams(
                            "ruta: la carpeta " + Path.GetDirectoryName(ruta) + " no está en TRUSTEDPATHS (SECURELOAD=" + secureload
                            + "); el usuario debe añadirla en Opciones > Archivos > Ubicaciones de confianza");
                    }

                    aprobacion = "fijado";
                    cargar = ruta;
                }
                else
                {
                    if (!isSource)
                    {
                        throw ToolException.InvalidParams(
                            "ruta: un .fas/.vlx solo se carga si está fijado en " + file + " (no se puede mostrar su código al usuario)",
                            new Dictionary<string, object?> { ["sha256"] = sha, ["archivo_permitidos"] = file });
                    }

                    string snapshotDir = LispPinStore.SnapshotFolder;
                    bool snapshotTrusted = LispRules.IsInTrustedPaths(Path.Combine(snapshotDir, "x.lsp"), SafeString("TRUSTEDPATHS"));
                    if (secureload == 2 && !snapshotTrusted)
                    {
                        throw ToolException.InvalidParams(
                            "SECURELOAD=2: AutoCAD solo carga desde ubicaciones de confianza y " + snapshotDir
                            + " no debe serlo (la escribe el agente); fije el archivo en " + file + " y cópielo a una carpeta de confianza");
                    }

                    if (!LispRules.IsValidUtf8(bytes))
                    {
                        throw ToolException.InvalidParams(
                            "ruta: el archivo no es UTF-8 válido; no se puede mostrar exactamente como lo leería AutoCAD (guárdelo como UTF-8 o fíjelo en " + file + ")");
                    }

                    List<string> problems = LispRules.DisplayProblems(LispRules.DecodeSource(bytes));
                    if (problems.Count > 0)
                    {
                        throw ToolException.InvalidParams(
                            "ruta: el código no se puede mostrar fielmente para aprobarlo: " + string.Join("; ", problems));
                    }

                    if (_dialogOpen)
                    {
                        throw ToolException.BusyQueue();
                    }

                    PluginLog.Info("cargar_lisp: pidiendo aprobación al usuario para " + ruta + " sha256=" + sha);
                    LispApprovalDialog.Resultado dialogo;
                    _dialogOpen = true;
                    try
                    {
                        dialogo = LispApprovalDialog.Ask(ruta, sha, comandos ?? new List<string>(), sensibles ?? new List<string>(), LispRules.DecodeSource(bytes));
                    }
                    finally
                    {
                        _dialogOpen = false;
                    }

                    segundosAbierto = dialogo.SegundosAbierto;
                    if (!dialogo.Aprobado)
                    {
                        motivo = dialogo.Motivo ?? LispApprovalDialog.MotivoCerrado;
                        PluginLog.Info("cargar_lisp: el usuario NO aprobó " + ruta + " (motivo=" + motivo + ", " + (segundosAbierto?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "sin mostrar") + " s)");
                        warnings.Add(MensajeNoAprobado(motivo));
                        aprobacion = "dialogo_rechazado";
                        return Build(ctx, confirmar, false, ruta, bytes, sha, comandos, sensibles, fijado, entrada, file, secureload, trusted, aprobacion, null, motivo, segundosAbierto, warnings);
                    }

                    aprobacion = "dialogo_aceptado";
                    cargar = WriteSnapshot(snapshotDir, sha, bytes);
                    if (secureload == 1 && !snapshotTrusted)
                    {
                        warnings.Add("AutoCAD (SECURELOAD=1) mostrará además su propio aviso de seguridad: el usuario debe elegir 'Cargar una vez'.");
                    }
                }

                doc.SendStringToExecute(LispRules.BuildLoadExpression(cargar), true, false, false);
                enviado = true;
                rutaCargada = cargar;
                PluginLog.Info("cargar_lisp: (load) enviado para " + cargar + " sha256=" + sha + " (" + aprobacion + ")");
                warnings.Add("Carga enviada; AutoCAD la ejecuta después de esta respuesta.");
            }

            return Build(ctx, confirmar, enviado, ruta, bytes, sha, comandos, sensibles, fijado, entrada, file, secureload, trusted, aprobacion, rutaCargada, motivo, segundosAbierto, warnings);
        }

        private static string MensajeNoAprobado(string motivo) => motivo switch
        {
            LispApprovalDialog.MotivoRechazado => "El usuario rechazó la carga en el diálogo (No cargar o Esc). No se cargó nada.",
            LispApprovalDialog.MotivoTimeout => "El diálogo de aprobación se cerró sin respuesta a los " + LispApprovalDialog.TimeoutSeconds + " s. No se cargó nada.",
            LispApprovalDialog.MotivoError => "No se pudo mostrar el diálogo de aprobación; no se cargó nada. Revise plugin.log.",
            _ => "El diálogo de aprobación se cerró sin aprobar la carga. No se cargó nada.",
        };

        private static Dictionary<string, object?> Build(
            ExecutionContext ctx, bool confirmar, bool enviado, string ruta, byte[] bytes, string sha, List<string>? comandos,
            List<string>? sensibles, bool fijado, string entrada, string file, int secureload, bool trusted, string aprobacion,
            string? rutaCargada, string? motivo, double? segundosAbierto, List<string> warnings)
        {
            return new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["preview"] = !confirmar,
                ["enviado"] = enviado,
                ["ruta"] = ruta,
                ["tamano_bytes"] = bytes.LongLength,
                ["sha256"] = sha,
                ["comandos_definidos"] = comandos,
                ["funciones_sensibles"] = sensibles,
                ["fijado"] = fijado,
                ["entrada_para_fijar"] = entrada,
                ["archivo_permitidos"] = file,
                ["secureload"] = secureload,
                ["carpeta_de_confianza"] = trusted,
                ["aprobacion"] = aprobacion,
                ["ruta_cargada"] = rutaCargada,
                ["origen_texto"] = "contenido_del_archivo",
                ["motivo"] = motivo,
                ["segundos_abierto"] = segundosAbierto,
                ["advertencias"] = warnings,
            };
        }

        /// <summary>
        /// Copia exacta de los bytes aprobados en lisp\cargados\&lt;sha&gt;.lsp, releída y verificada por hash antes de
        /// devolver su ruta. Cualquier fallo → INVALID_PARAMS sin cargar.
        /// </summary>
        private static string WriteSnapshot(string dir, string sha, byte[] bytes)
        {
            string path = Path.Combine(dir, sha + ".lsp");
            try
            {
                Directory.CreateDirectory(dir);
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                File.WriteAllBytes(path, bytes);
                File.SetAttributes(path, FileAttributes.ReadOnly);
                if (LispRules.Sha256Hex(File.ReadAllBytes(path)) != sha)
                {
                    throw new IOException("hash distinto tras escribir");
                }
            }
            catch (Exception ex)
            {
                throw ToolException.InvalidParams("no se pudo preparar la copia aprobada en " + dir + " (" + ex.GetType().Name + "); no se cargó nada");
            }

            return path;
        }

        private static byte[] ReadFile(string ruta)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(ruta);
            }
            catch (Exception ex)
            {
                throw ToolException.InvalidParams("ruta: " + ex.GetType().Name);
            }

            if (!info.Exists)
            {
                throw new ToolException(ErrorCodes.NotFound, "No existe el archivo: " + ruta);
            }

            if (info.Length > LispRules.MaxLispBytes)
            {
                throw ToolException.InvalidParams("ruta: el archivo supera 5 MB");
            }

            try
            {
                return File.ReadAllBytes(ruta);
            }
            catch (Exception ex)
            {
                throw ToolException.InvalidParams("ruta: no se pudo leer (" + ex.GetType().Name + ")");
            }
        }

        private static int SafeInt(string name)
        {
            try
            {
                return AcadUtil.GetIntSysVar(name);
            }
            catch (Exception)
            {
                return 1;
            }
        }

        private static string SafeString(string name)
        {
            try
            {
                return AcadUtil.GetStringSysVar(name);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
