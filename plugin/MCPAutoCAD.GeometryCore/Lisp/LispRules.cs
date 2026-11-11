using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.GeometryCore.Lisp
{
    /// <summary>Una entrada de LISP fijada por el usuario: ruta exacta + SHA-256 del contenido.</summary>
    public sealed record LispPin(string Ruta, string Sha256);

    /// <summary>
    /// Una secuencia de comando permitida por el usuario: el comando y la lista EXACTA de respuestas que se le envían.
    /// El agente no puede añadir ni cambiar respuestas: solo elige una secuencia ya escrita en el archivo.
    /// </summary>
    public sealed record CommandPin(string Comando, IReadOnlyList<string> Argumentos);

    /// <summary>Contenido válido de lisp_permitidos.json más los errores de las entradas descartadas.</summary>
    public sealed class PinFile
    {
        public List<LispPin> Lisp { get; } = new List<LispPin>();

        public List<CommandPin> Comandos { get; } = new List<CommandPin>();

        public List<string> Errores { get; } = new List<string>();
    }

    /// <summary>
    /// Reglas puras de cargar_lisp, ejecutar_comando y cancelar_comando (plugin 0.8.4, protocol.md §5I bloque 0.8.4).
    /// SEGURIDAD: la única fuente de autorización es el archivo del usuario <c>%LOCALAPPDATA%\MCPAutoCAD\lisp_permitidos.json</c>,
    /// que ninguna herramienta del puente escribe. Un LISP se carga solo si su ruta Y su SHA-256 están fijados; un comando se
    /// lanza solo con una secuencia de respuestas idéntica a una fijada. Nada de esto evalúa código que el agente escriba.
    /// </summary>
    public static class LispRules
    {
        public const string PinFileName = "lisp_permitidos.json";

        public const long MaxLispBytes = 5L * 1024 * 1024;

        public const int MaxArguments = 20;

        public const int MaxArgumentLength = 100;

        /// <summary>Máximo de caracteres del código que acepta crear_lisp (0.8.5).</summary>
        public const int MaxSourceChars = 200000;

        /// <summary>Subcarpeta de %LOCALAPPDATA%\MCPAutoCAD donde crear_lisp guarda lo que escribe el agente.</summary>
        public const string AgentFolderName = "lisp";

        /// <summary>Subcarpeta de la anterior con las copias exactas (por hash) de lo que el usuario aprobó cargar.</summary>
        public const string SnapshotFolderName = "cargados";

        /// <summary>Extensiones que AutoCAD carga con (load ...).</summary>
        public static readonly string[] Extensions = { ".lsp", ".fas", ".vlx" };

        /// <summary>
        /// Comandos que NUNCA se lanzan aunque el archivo los liste: ejecutan programas o código, cargan módulos, cambian la
        /// seguridad de AutoCAD o escriben/cierran archivos. Incluye variables de sistema de seguridad (escribir su nombre en
        /// la línea de comando las cambia).
        /// </summary>
        public static readonly IReadOnlyCollection<string> DeniedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SHELL", "SH", "RUN", "START", "SCRIPT", "SCR", "RSCRIPT", "NETLOAD", "APPLOAD", "AP", "ARX", "LOAD",
            "VBALOAD", "VBARUN", "VBAIDE", "VBAMAN", "VBASTMT", "VBAUNLOAD", "-VBARUN", "VLISP", "VLIDE",
            "SETVAR", "SET", "SECURELOAD", "TRUSTEDPATHS", "TRUSTEDDOMAINS", "LISPSYS", "LEGACYCODESEARCH",
            "ACADLSPASDOC", "SAFEMODE", "CUI", "CUILOAD", "CUIUNLOAD", "MENU", "MENULOAD", "MENUUNLOAD", "OPTIONS", "OP",
            "PREFERENCES", "BROWSER", "HYPERLINK", "OPENURL", "QUIT", "EXIT", "CLOSE", "CLOSEALL", "SAVE", "SAVEAS",
            "QSAVE", "SAVEALL", "WBLOCK", "W", "EXPORT", "EXP", "PUBLISH", "PLOT", "PRINT", "ETRANSMIT", "ARCHIVE",
            "OPEN", "NEW", "QNEW", "RECOVER", "ACTUSERMESSAGE", "ACTSTOP", "ACTRECORD", "ACTBASEPOINT",
            "SYSVDLG", "REINIT", "XREF", "-XREF", "XATTACH", "IMAGEATTACH", "ATTACH", "INSERT",
            "-INSERT", "CLASSICINSERT", "DATAEXTRACTION", "DATALINK", "TOOLPALETTES", "FILEOPEN",
            "SCRIPTCALL", "SHARE", "SHAREDVIEWS", "APPAUTOLOAD", "SECUREREMOTEACCESS", "EXPORTPDF", "EXPORTDWF",
            "EXPORTDWFX", "DXFOUT", "SAVETOWEBMOBILE", "DBCONNECT", "CLOSEALLOTHER",
            // Auditoría 2026-11-10 (O:\ en solo lectura): más comandos que escriben archivos junto al dibujo activo.
            "BMPOUT", "JPGOUT", "PNGOUT", "TIFOUT", "WMFOUT", "PSOUT", "STLOUT", "ACISOUT", "MSLIDE", "ATTEXT", "-ATTEXT",
            "EATTEXT", "DGNEXPORT", "FBXEXPORT", "IGESEXPORT", "3DDWF", "EXPORTLAYOUT", "EXPORTTOAUTOCAD", "-EXPORTTOAUTOCAD",
            "REFEDIT", "REFCLOSE", "BSAVEAS", "BSAVE", "LOGFILEON", "TABLEEXPORT", "SAVEIMG", "DWGCONVERT", "AUTOPUBLISH",
            "MASSPROP", "-LAYOUT", "LAYOUT",
            // Auditoría: comandos que escriben archivos, cargan código o abren URLs/objetos externos.
            "ATTOUT", "MKLTYPE", "MKSHAPE", "COMPILE", "DIMEX", "LMAN", "REVERT", "QQUIT", "CONVERTCTB", "CONVERTPSTYLES",
            "PLOTSTAMP", "CUIEXPORT", "LAYERSTATE", "FLATSHOT", "SECTIONPLANETOBLOCK", "RENDER", "3DPRINT", "XOPEN",
            "GOTOURL", "INSERTOBJ", "LOGFILEMODE",
        };

        /// <summary>
        /// Funciones AutoLISP/Visual LISP que tocan el sistema (archivos, registro, programas, COM, red). Solo informativo:
        /// un análisis por texto NO es una garantía (se elude con (eval (read ...)) o con strcat); la protección real es
        /// el hash fijado por el usuario.
        /// </summary>
        public static readonly string[] SensitiveFunctions =
        {
            "startapp", "vl-file-delete", "vl-file-copy", "vl-file-rename", "vl-mkdir", "vl-registry-write",
            "vl-registry-delete", "vlax-create-object", "vlax-get-or-create-object", "vlax-invoke", "vlax-invoke-method",
            "vla-sendcommand", "setenv", "getenv", "open", "write-line", "write-char", "eval", "read", "load",
            "command", "command-s", "vl-cmdf", "dos_", "acet-sys-", "acet-file-", "vl-vbarun", "vl-vbaload",
            "arxload", "vl-load-com", "vlr-", "vl-acad-defun",
        };

        private static readonly Regex CommandNameRegex = new Regex("^[A-Za-z][A-Za-z0-9_\\-]{0,63}\\z", RegexOptions.CultureInvariant);

        // Letras (incluidas las acentuadas), dígitos y . , - + @ < _ #. Sin espacios (son Intro en la línea de comando), sin
        // paréntesis ni '!' (evaluarían AutoLISP), sin comillas, ';', '\', '\'' ni caracteres de control.
        private static readonly Regex ArgumentRegex = new Regex("^[\\p{L}\\p{N}_.,\\-+@<#]{0,100}\\z", RegexOptions.CultureInvariant);

        private static readonly Regex DefunRegex = new Regex(
            "\\(\\s*defun\\s+c:([^\\s()\"';]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex LispNameRegex = new Regex("^[A-Za-z0-9][A-Za-z0-9_\\-]{0,63}\\z", RegexOptions.CultureInvariant);

        /// <summary>Nombre de archivo de crear_lisp (sin extensión ni carpeta).</summary>
        public static bool IsValidLispName(string nombre)
        {
            string n = nombre ?? "";
            if (!LispNameRegex.IsMatch(n))
            {
                return false;
            }

            // Dispositivos de Windows (NUL.lsp escribe en el vacío) y nombres que AutoCAD carga solo (acad*.lsp).
            string u = n.ToUpperInvariant();
            if (u is "CON" or "PRN" or "AUX" or "NUL" || u.StartsWith("ACAD", StringComparison.Ordinal))
            {
                return false;
            }

            return !((u.StartsWith("COM", StringComparison.Ordinal) || u.StartsWith("LPT", StringComparison.Ordinal))
                && u.Length == 4 && char.IsAsciiDigit(u[3]));
        }

        /// <summary>Longitud máxima de línea de un LISP que se muestra al usuario para aprobarlo (más larga se esconde fuera de la vista).</summary>
        public const int MaxDisplayLineLength = 200;

        /// <summary>Máximo de líneas en blanco seguidas (más esconderían código fuera de la vista del diálogo).</summary>
        public const int MaxBlankLines = 10;

        /// <summary>
        /// Motivos por los que un código NO puede mostrarse fielmente en el diálogo de aprobación (vacío = se puede):
        /// caracteres de control distintos de tab/CR/LF, CR suelto (el cuadro de texto no lo muestra como salto de línea),
        /// caracteres invisibles o de dirección (U+200B–U+200F, U+202A–U+202E, U+2060–U+2069, U+FEFF salvo al inicio,
        /// U+00AD) y líneas de más de <see cref="MaxDisplayLineLength"/> caracteres.
        /// </summary>
        public static List<string> DisplayProblems(string codigo)
        {
            var problems = new List<string>();
            string t = codigo ?? "";
            bool control = false, loneCr = false, invisible = false, blankRun = false;
            int line = 0, longLines = 0, blanks = 0;
            bool lineHasText = false;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '\n')
                {
                    blanks = lineHasText ? 0 : blanks + 1;
                    blankRun |= blanks > MaxBlankLines;
                    lineHasText = false;
                    line = 0;
                    continue;
                }

                if (c == '\r')
                {
                    if (i + 1 >= t.Length || t[i + 1] != '\n')
                    {
                        loneCr = true;
                    }

                    line = 0;
                    continue;
                }

                if (!char.IsWhiteSpace(c))
                {
                    lineHasText = true;
                }

                if (c != '\t' && char.IsControl(c))
                {
                    control = true;
                }

                UnicodeCategory cat = char.GetUnicodeCategory(c);
                if ((c >= '\u200B' && c <= '\u200F') || (c >= '\u202A' && c <= '\u202E') || (c >= '\u2060' && c <= '\u2069')
                    || c == '\u00AD' || (c == '\uFEFF' && i != 0)
                    || (cat == UnicodeCategory.Format && !(c == '\uFEFF' && i == 0))
                    || char.IsSurrogate(c) || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator
                    || c == '\u034F' || c == '\u115F' || c == '\u1160' || c == '\u3164' || c == '\uFFA0'
                    || (c >= '\uFE00' && c <= '\uFE0F'))
                {
                    invisible = true;
                }

                line++;
                if (line == MaxDisplayLineLength + 1)
                {
                    longLines++;
                }
            }

            if (control)
            {
                problems.Add("caracteres de control (solo se admiten tabulador y saltos de línea)");
            }

            if (loneCr)
            {
                problems.Add("retorno de carro (CR) sin salto de línea");
            }

            if (invisible)
            {
                problems.Add("caracteres invisibles o de cambio de dirección del texto");
            }

            if (blankRun)
            {
                problems.Add("más de " + MaxBlankLines.ToString(CultureInfo.InvariantCulture) + " líneas en blanco seguidas");
            }

            if (longLines > 0)
            {
                problems.Add(longLines.ToString(CultureInfo.InvariantCulture) + " línea(s) de más de "
                    + MaxDisplayLineLength.ToString(CultureInfo.InvariantCulture) + " caracteres");
            }

            return problems;
        }

        /// <summary>true si los bytes son UTF-8 válido (un LISP para aprobar debe mostrarse exactamente como AutoCAD lo lee).</summary>
        public static bool IsValidUtf8(byte[] bytes)
        {
            try
            {
                _ = new UTF8Encoding(false, true).GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static readonly Regex Sha256Regex = new Regex("^[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant);

        /// <summary>Valida la ruta de un LISP sin tocar el disco. null si es válida, o el detalle exacto.</summary>
        public static string? ValidateLispPath(string ruta)
        {
            if (!ExportRules.IsSafeAbsolutePath(ruta))
            {
                return "ruta: debe ser una ruta absoluta sin '..', comodines ni comillas";
            }

            if (!Extensions.Any(e => ruta.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                return "ruta: solo se admiten archivos .lsp, .fas o .vlx";
            }

            return null;
        }

        /// <summary>SHA-256 en hexadecimal minúsculo.</summary>
        public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>Comandos c:NOMBRE que el texto parece definir (aproximado: no entiende comentarios ni cadenas).</summary>
        public static List<string> DefinedCommands(string source)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (Match m in DefunRegex.Matches(source))
            {
                string name = m.Groups[1].Value.ToUpperInvariant();
                if (seen.Add(name))
                {
                    list.Add(name);
                }
            }

            return list;
        }

        /// <summary>Funciones sensibles que aparecen como llamada "(nombre" en el texto (orden de <see cref="SensitiveFunctions"/>).</summary>
        public static List<string> FoundSensitive(string source)
        {
            var found = new List<string>();
            foreach (string f in SensitiveFunctions)
            {
                // Prefijos (dos_, acet-sys-, vlr-) admiten cualquier sufijo; el resto exige fin de símbolo.
                string tail = f.EndsWith("_", StringComparison.Ordinal) || f.EndsWith("-", StringComparison.Ordinal)
                    ? "[^\\s()]*"
                    : "(?=[\\s()\"';]|$)";
                if (Regex.IsMatch(source, "\\(\\s*" + Regex.Escape(f) + tail, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    found.Add(f);
                }
            }

            return found;
        }

        /// <summary>Nombre de comando válido (sintaxis), sin mirar listas.</summary>
        public static bool IsValidCommandName(string name) => CommandNameRegex.IsMatch(name);

        /// <summary>
        /// true si el nombre, también sin los prefijos de línea de comando '_', '.', '-' (p. ej. "_.SHELL", "-PLOT"), está
        /// prohibido. Así una respuesta fijada como "_SHELL" tampoco pasa.
        /// </summary>
        public static bool IsDenied(string name)
        {
            string n = name ?? "";
            string bare = n.TrimStart('_', '.', '-');
            if (DeniedCommands.Contains(n))
            {
                return true;
            }

            // Sin prefijo solo para nombres de 2+ letras: "_W" (opción Ventana de ZOOM y de las selecciones) no es WBLOCK.
            return bare.Length >= 2 && (DeniedCommands.Contains(bare) || DeniedCommands.Contains("-" + bare));
        }

        /// <summary>Texto de un .lsp: UTF-8 si es válido; si no, Latin-1 (los LISP antiguos suelen estar en Windows-1252).</summary>
        public static string DecodeSource(byte[] bytes)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }

        /// <summary>Respuesta a un prompt admisible: ver <see cref="ArgumentRegex"/>. "" = Intro.</summary>
        public static bool IsValidArgument(string arg) => ArgumentRegex.IsMatch(arg);

        /// <summary>
        /// Texto para Document.SendStringToExecute: comando y respuestas separados por espacio (= Intro) y un espacio final.
        /// Solo se llama con valores ya validados (<see cref="IsValidCommandName"/>, <see cref="IsValidArgument"/>).
        /// </summary>
        public static string BuildCommandString(string comando, IReadOnlyList<string> argumentos)
        {
            var sb = new StringBuilder(comando);
            foreach (string a in argumentos)
            {
                sb.Append(' ').Append(a);
            }

            return sb.Append(' ').ToString();
        }

        /// <summary>Expresión (load "ruta") con '/' como separador. La ruta ya pasó <see cref="ValidateLispPath"/> (sin comillas).</summary>
        public static string BuildLoadExpression(string ruta) =>
            "(load \"" + ExportRules.NormalizePath(ruta).Replace('\\', '/') + "\") ";

        /// <summary>Entrada JSON lista para pegar en "lisp" de lisp_permitidos.json.</summary>
        public static string PinEntryJson(string ruta, string sha) =>
            JsonSerializer.Serialize(new Dictionary<string, string> { ["ruta"] = ruta, ["sha256"] = sha });

        /// <summary>
        /// Lee lisp_permitidos.json. Formato:
        /// <code>{ "lisp": [ { "ruta": "C:\\LPSAPP\\lisp\\X.lsp", "sha256": "64 hex" } ],
        ///   "comandos": [ "REGEN", { "comando": "ZOOM", "argumentos": ["_E"] } ] }</code>
        /// Una cadena en "comandos" = el comando sin respuestas. Las entradas inválidas se descartan con un error (no
        /// invalidan el resto). JSON ilegible → archivo vacío con un error.
        /// </summary>
        public static PinFile ParsePins(string json)
        {
            var file = new PinFile();
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException ex)
            {
                file.Errores.Add("JSON inválido: " + ex.Message);
                return file;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    file.Errores.Add("la raíz debe ser un objeto");
                    return file;
                }

                if (root.TryGetProperty("lisp", out JsonElement lisp))
                {
                    ReadLisp(lisp, file);
                }

                if (root.TryGetProperty("comandos", out JsonElement cmds))
                {
                    ReadCommands(cmds, file);
                }
            }

            return file;
        }

        /// <summary>true si (ruta normalizada, sin mayúsculas) + sha256 coinciden con una entrada fijada.</summary>
        public static bool IsPinned(PinFile pins, string ruta, string sha)
        {
            string r = ExportRules.NormalizePath(ruta);
            return pins.Lisp.Any(p =>
                string.Equals(ExportRules.NormalizePath(p.Ruta), r, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Sha256, sha, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Secuencia fijada idéntica (comando sin mayúsculas; respuestas exactas, mismo orden), o null.</summary>
        public static CommandPin? FindCommand(PinFile pins, string comando, IReadOnlyList<string> argumentos) =>
            pins.Comandos.FirstOrDefault(c =>
                string.Equals(c.Comando, comando, StringComparison.OrdinalIgnoreCase)
                && c.Argumentos.SequenceEqual(argumentos, StringComparer.Ordinal));

        /// <summary>
        /// true si la carpeta del archivo está en TRUSTEDPATHS (lista separada por ';'; una entrada que acaba en "\..." incluye
        /// subcarpetas; se expanden variables de entorno). Comparación de rutas normalizadas sin mayúsculas.
        /// </summary>
        public static bool IsInTrustedPaths(string ruta, string trustedPaths)
        {
            string file = ExportRules.NormalizePath(ruta);
            int cut = file.LastIndexOf('\\');
            if (cut <= 0)
            {
                return false;
            }

            string dir = file.Substring(0, cut);
            foreach (string raw in (trustedPaths ?? "").Split(';'))
            {
                string entry = Environment.ExpandEnvironmentVariables(raw.Trim());
                if (entry.Length == 0)
                {
                    continue;
                }

                entry = ExportRules.NormalizePath(entry);
                bool sub = entry.EndsWith("\\...", StringComparison.Ordinal);
                if (sub)
                {
                    entry = entry.Substring(0, entry.Length - 4);
                }

                entry = entry.TrimEnd('\\');
                if (entry.Length == 0)
                {
                    continue;
                }

                if (string.Equals(dir, entry, StringComparison.OrdinalIgnoreCase)
                    || (sub && dir.StartsWith(entry + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ReadLisp(JsonElement lisp, PinFile file)
        {
            if (lisp.ValueKind != JsonValueKind.Array)
            {
                file.Errores.Add("lisp: debe ser un array");
                return;
            }

            int i = 0;
            foreach (JsonElement e in lisp.EnumerateArray())
            {
                string at = "lisp[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                i++;
                string? ruta = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("ruta", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                string? sha = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("sha256", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (ruta == null || sha == null)
                {
                    file.Errores.Add(at + ": debe ser un objeto con ruta y sha256");
                    continue;
                }

                string? err = ValidateLispPath(ruta);
                if (err != null)
                {
                    file.Errores.Add(at + ": " + err);
                    continue;
                }

                if (!Sha256Regex.IsMatch(sha))
                {
                    file.Errores.Add(at + ": sha256 debe tener 64 dígitos hexadecimales");
                    continue;
                }

                file.Lisp.Add(new LispPin(ruta, sha.ToLowerInvariant()));
            }
        }

        private static void ReadCommands(JsonElement cmds, PinFile file)
        {
            if (cmds.ValueKind != JsonValueKind.Array)
            {
                file.Errores.Add("comandos: debe ser un array");
                return;
            }

            int i = 0;
            foreach (JsonElement e in cmds.EnumerateArray())
            {
                string at = "comandos[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                i++;
                string? name;
                var args = new List<string>();
                if (e.ValueKind == JsonValueKind.String)
                {
                    name = e.GetString();
                }
                else if (e.ValueKind == JsonValueKind.Object
                    && e.TryGetProperty("comando", out JsonElement c) && c.ValueKind == JsonValueKind.String)
                {
                    name = c.GetString();
                    if (e.TryGetProperty("argumentos", out JsonElement a))
                    {
                        if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() > MaxArguments
                            || a.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                        {
                            file.Errores.Add(at + ": argumentos debe ser un array de hasta 20 cadenas");
                            continue;
                        }

                        args.AddRange(a.EnumerateArray().Select(x => x.GetString() ?? ""));
                    }
                }
                else
                {
                    file.Errores.Add(at + ": debe ser una cadena o un objeto con comando");
                    continue;
                }

                if (name == null || !IsValidCommandName(name))
                {
                    file.Errores.Add(at + ": nombre de comando inválido");
                    continue;
                }

                if (IsDenied(name))
                {
                    file.Errores.Add(at + ": " + name.ToUpperInvariant() + " está prohibido siempre");
                    continue;
                }

                // Una respuesta que es un comando prohibido se ejecutaría si el comando terminara antes de consumirla.
                string? bad = args.FirstOrDefault(x => !IsValidArgument(x) || IsDenied(x));
                if (bad != null)
                {
                    file.Errores.Add(at + ": argumento no admitido '" + bad + "' (sin espacios, paréntesis, '!', comillas, ';' ni comandos prohibidos)");
                    continue;
                }

                file.Comandos.Add(new CommandPin(name.ToUpperInvariant(), args));
            }
        }
    }
}
