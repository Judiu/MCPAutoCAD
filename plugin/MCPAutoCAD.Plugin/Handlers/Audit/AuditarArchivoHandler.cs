using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// auditar_archivo (protocol.md §5H.1). Hilo principal; NO requiere documento; SOLO LECTURA. Lee el DWG en una base
    /// lateral (por defecto desde una copia en %LOCALAPPDATA%\MCPAutoCAD\tmp) y le pasa las reglas de auditar_dibujo.
    /// Nunca guarda, nunca crea un Document y nunca cambia WorkingDatabase ni el documento activo; la base se libera y la
    /// copia se borra en finally, también ante excepción. Además de §5H.1 devuelve los campos auxiliares de la fila del lote
    /// (_cype, _lamina, _bloques, _mediana_texto; §5H.2), los mismos que mide lote.py con origen ODA.
    /// </summary>
    internal sealed class AuditarArchivoHandler : IToolHandler
    {
        private const int MinBudgetMs = 1000;
        private const int DefaultBudgetMs = PluginInfo.AuditBudgetMs;
        private const int MaxBudgetMs = PluginInfo.AuditBudgetMaxMs;
        private const int InsunitsMeters = 6;
        private const string XrefsRule = "xrefs_no_resueltas";
        private const string FolderRejected = "ruta: fuera de las carpetas permitidas de lps.json (carpetas_auditoria_permitidas)";

        /// <summary>Frase añadida a la advertencia de archivo abierto (0.8.1): cómo auditar lo que no está guardado.</summary>
        private const string OpenInSessionHint = "Para auditar los cambios sin guardar use auditar_dibujo con el documento activo.";

        private static readonly string[] Allowed = { "ruta", "comprobaciones", "espacio", "copiar", "presupuesto_ms", "limit", "cursor" };

        /// <summary>Correlativo <c>n</c> de la copia temporal (§5H.1).</summary>
        private static int copyCounter;

        /// <summary>1 cuando este proceso ya barrió las copias huérfanas.</summary>
        private static int orphansSwept;

        public string Name => "auditar_archivo";

        public string Description =>
            "Audita un archivo .dwg CERRADO sin abrirlo en pantalla ni modificarlo (nunca se guarda; trabaja sobre una copia temporal local y la borra): las mismas reglas y severidades que auditar_dibujo (capas, capa 0, bloques, cuadro de áreas, duplicados, textos fuera de escala, xrefs, unidades) más el resumen de resumen_archivo. Devuelve 'origen', las comprobaciones no disponibles y los hallazgos paginados. INSUNITS se informa como declarado, no como unidad real. Funciona sin documentos abiertos.";

        public bool RequiresDocument => false;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        /// <summary>Un timeout no cancela la lectura, y esta no escribe nada (§3, regla 7).</summary>
        public string TimeoutHint(JsonElement parameters) => FileAuditRules.TimeoutHint;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta (unidad o UNC) del .dwg, sin '..' ni comodines. Solo se lee: nunca se ejecuta ni se escribe. Si lps.json define carpetas_auditoria_permitidas, debe estar dentro de una."" },
    ""comprobaciones"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""maxItems"": 9,
      ""uniqueItems"": true,
      ""items"": { ""type"": ""string"", ""enum"": [""auditar_capas"", ""entidades_en_capa_0"", ""auditar_bloques"", ""verificar_cuadro_areas"", ""duplicados"", ""textos_fuera_de_escala"", ""xrefs_no_resueltas"", ""unidades"", ""resumen""] },
      ""description"": ""Comprobaciones a ejecutar (por defecto todas); las reglas siempre van en el orden fijo del contrato y 'resumen' es el bloque de resumen_archivo.""
    },
    ""espacio"": " + ParamReader.SchemaEspacio.Replace("Por defecto, el espacio actual.", "Por defecto, 'modelo'; aplica a verificar_cuadro_areas, duplicados y textos_fuera_de_escala.") + @",
    ""copiar"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true = lee una copia temporal local y la borra (xrefs_no_resueltas queda en no_disponible; máximo 50 MB, use auditar_carpeta para archivos mayores); false = lee el original en modo compartido, sin copia: úselo solo con copias locales ya hechas, como las del lote. La lectura corre en el hilo de AutoCAD y no se puede cancelar: fuera de la carpeta temporal del plugin el máximo también es 50 MB."" },
    ""presupuesto_ms"": { ""type"": ""integer"", ""minimum"": 1000, ""maximum"": 30000, ""default"": 8000, ""description"": ""Plazo de las reglas en ms (por defecto 8000, máximo 30000): lo que no cabe sale parcial u omitido, y reglas_omitidas lista las comprobaciones que no llegaron a ejecutarse por falta de tiempo. La copia y la lectura no cuentan."" },
    ""limit"": " + ParamReader.SchemaLimit + @",
    ""cursor"": " + ParamReader.SchemaCursor + @"
  },
  ""required"": [""ruta""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""origen"": { ""type"": ""string"" },
    ""archivo"": { ""type"": ""object"", ""properties"": {
      ""ruta"": { ""type"": ""string"" },
      ""nombre"": { ""type"": ""string"" },
      ""tamano_bytes"": { ""type"": ""integer"" },
      ""modificado"": { ""type"": ""string"" },
      ""version_dwg"": { ""type"": ""string"" },
      ""abierto_en_sesion"": { ""type"": ""boolean"" },
      ""copia_temporal"": { ""type"": ""boolean"" } } },
    ""espacio"": { ""type"": ""string"" },
    ""comprobaciones"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""no_disponible"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""comprobacion"": { ""type"": ""string"" }, ""motivo"": { ""type"": ""string"" } } } },
    ""resumen"": { ""type"": ""object"", ""properties"": {
      ""total_hallazgos"": { ""type"": ""integer"" },
      ""por_severidad"": { ""type"": ""object"" },
      ""por_regla"": { ""type"": ""object"" },
      ""n_cuadros_fuera_de_layout"": { ""type"": ""integer"", ""minimum"": 0 } } },
    ""resumen_archivo"": { ""type"": [""object"", ""null""], ""properties"": {
      ""measurement"": { ""type"": ""integer"" },
      ""n_capas"": { ""type"": ""integer"" },
      ""entidades_modelo"": { ""type"": ""object"", ""properties"": {
        ""total"": { ""type"": ""integer"" },
        ""por_tipo"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } } } },
      ""n_bloques"": { ""type"": ""integer"" },
      ""bloques_mas_usados"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
        ""nombre"": { ""type"": ""string"" }, ""n_inserciones"": { ""type"": ""integer"" } } } },
      ""layouts"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
      ""n_xrefs"": { ""type"": ""integer"" } } },
    ""reglas_omitidas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""reglas_con_error"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""regla"": { ""type"": ""string"" }, ""excepcion"": { ""type"": ""string"" } } } },
    ""duracion_ms"": { ""type"": ""integer"" },
    ""hallazgos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""total"": { ""type"": ""integer"" },
    ""cursor_siguiente"": { ""type"": [""string"", ""null""] },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""en_metros_declarado"": { ""type"": ""boolean"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""_cype"": { ""type"": [""object"", ""null""], ""description"": ""Datos para detectar un export CYPE; null = sin medir."", ""properties"": {
      ""capas_con_entidades"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
      ""estilos_texto"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } } },
    ""_lamina"": { ""type"": [""object"", ""null""], ""description"": ""Datos para detectar una lámina; null = sin medir."", ""properties"": {
      ""recuadros"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 2 } },
      ""textos_escala"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } } },
    ""_bloques"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""string"" }, ""description"": ""Definiciones no anónimas; null = sin medir."" },
    ""_mediana_texto"": { ""type"": [""number"", ""null""], ""description"": ""Mediana de alturas de texto del espacio; null si textos_fuera_de_escala no terminó."" }
  },
  ""required"": [""pid"", ""origen"", ""archivo"", ""espacio"", ""comprobaciones"", ""no_disponible"", ""resumen"", ""resumen_archivo"", ""reglas_omitidas"", ""reglas_con_error"", ""duracion_ms"", ""hallazgos"", ""total"", ""cursor_siguiente"", ""origen_texto"", ""insunits"", ""unidades"", ""en_metros_declarado"", ""advertencias""]
}");

        private sealed class FileParams
        {
            public FileParams(string ruta, List<string> checks, string? espacio, bool copiar, int budgetMs, int limit, int start)
            {
                Ruta = ruta;
                Checks = checks;
                Espacio = espacio;
                Copiar = copiar;
                BudgetMs = budgetMs;
                Limit = limit;
                Start = start;
            }

            public string Ruta { get; }

            /// <summary>Comprobaciones pedidas, en el orden fijo (§5H.1).</summary>
            public List<string> Checks { get; }

            public string? Espacio { get; }
            public bool Copiar { get; }
            public int BudgetMs { get; }
            public int Limit { get; }
            public int Start { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        /// <summary>
        /// Valida y convierte params: hilo del pipe y otra vez en el principal. Sin AutoCAD y sin disco, por eso aquí solo se
        /// comprueba la forma de la ruta; las carpetas de lps.json se leen en Execute (<see cref="ReadAllowedFolders"/>).
        /// </summary>
        private static FileParams Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string ruta = ParamReader.RequiredText(p, "ruta", 1, 1000);
            List<string> checks = ReadChecks(p);
            string? espacio = ParamReader.OptionalString(p, "espacio", 1, 255);
            bool copiar = ParamReader.OptionalBool(p, "copiar") ?? true;
            int budget = ParamReader.OptionalInt(p, "presupuesto_ms", MinBudgetMs, MaxBudgetMs) ?? DefaultBudgetMs;
            int limit = ParamReader.Limit(p);
            int start = ParamReader.OptionalCursor(p);

            string? pathError = ExportRules.ValidatePath(ruta, null);
            if (pathError != null)
            {
                throw ToolException.InvalidParams(pathError);
            }

            return new FileParams(ruta, checks, espacio, copiar, budget, limit, start);
        }

        private static List<string> ReadChecks(JsonElement p)
        {
            try
            {
                return FileAuditRules.NormalizeChecks(ParamReader.OptionalStringArray(p, "comprobaciones", 1, FileAuditRules.Comprobaciones.Count, 0));
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }

        /// <summary>
        /// lps.json carpetas_auditoria_permitidas, con la lógica de carpetas_export_permitidas (§5G.4). Solo en el hilo
        /// principal: la primera lectura de LpsStandard.Root va a disco. Ausente (o lps.json no disponible) → null. Presente
        /// pero sin ser un array no vacío de rutas → se rechaza: una clave mal escrita no debe abrir todas las rutas.
        /// </summary>
        private static List<string>? ReadAllowedFolders()
        {
            if (!(LpsStandard.Root is JsonElement root)
                || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(FileAuditRules.LpsKeyFolders, out JsonElement el))
            {
                return null;
            }

            var folders = new List<string>();
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in el.EnumerateArray())
                {
                    string? folder = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        folders.Clear();
                        break;
                    }

                    folders.Add(folder);
                }
            }

            if (folders.Count == 0)
            {
                PluginLog.Warn("auditar_archivo: lps.json " + FileAuditRules.LpsKeyFolders + " no es un array no vacío de rutas; se rechaza la ruta.");
                throw ToolException.InvalidParams(FolderRejected);
            }

            return folders;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Stopwatch watch = Stopwatch.StartNew();
            FileParams p = Parse(parameters);

            // Antes de File.Exists, como §5G.4: una ruta rechazada no revela si el archivo existe. La forma ya pasó en Parse,
            // así que aquí ValidatePath solo puede rechazar por carpeta.
            List<string>? folders = ReadAllowedFolders();
            if (folders != null && ExportRules.ValidatePath(p.Ruta, FileAuditRules.WithOwnTempFolder(folders, OwnTempFolder())) != null)
            {
                throw ToolException.InvalidParams(FolderRejected);
            }

            if (!File.Exists(p.Ruta))
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El archivo '" + p.Ruta + "' no existe.",
                    new Dictionary<string, object?> { ["archivo"] = p.Ruta });
            }

            var file = new FileInfo(p.Ruta);
            long size = file.Length;
            string? sizeError = FileAuditRules.SizeRejection(size, p.Copiar, p.Ruta, OwnTempFolder());
            if (sizeError != null)
            {
                throw ToolException.InvalidParams(sizeError);
            }

            SweepOrphanCopies();
            string modified = new DateTimeOffset(file.LastWriteTime).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            bool openInSession = ResumenArchivoHandler.IsOpenInSession(p.Ruta);

            // La misma lista que devuelve la respuesta: el finally aún puede añadir la advertencia de la copia.
            var warnings = new List<string>();
            string? tempCopy = null;
            Database? db = null;
            try
            {
                string readPath = p.Ruta;
                if (p.Copiar)
                {
                    tempCopy = ReserveTempCopy(ctx.Pid, size);
                    CopyToTemp(p.Ruta, tempCopy);
                    readPath = tempCopy;
                }

                long copyMs = watch.ElapsedMilliseconds;
                db = ReadSide(readPath);

                // Ni la copia ni la lectura se pueden cancelar: el log deja su tiempo real por tamaño de archivo.
                PluginLog.Info(
                    "auditar_archivo: '" + p.Ruta + "' " + size.ToString(CultureInfo.InvariantCulture) + " bytes, copiar="
                    + (p.Copiar ? "sí" : "no") + ", hasta la lectura " + copyMs.ToString(CultureInfo.InvariantCulture)
                    + " ms, lectura " + (watch.ElapsedMilliseconds - copyMs).ToString(CultureInfo.InvariantCulture) + " ms.");

                var rules = new List<string>();
                var unavailable = new List<Dictionary<string, object?>>();
                foreach (string check in p.Checks)
                {
                    if (string.Equals(check, FileAuditRules.Resumen, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (p.Copiar && string.Equals(check, XrefsRule, StringComparison.Ordinal))
                    {
                        unavailable.Add(new Dictionary<string, object?>
                        {
                            ["comprobacion"] = check,
                            ["motivo"] = FileAuditRules.XrefsUnavailableReason,
                        });
                        continue;
                    }

                    rules.Add(check);
                }

                SideAuditResult audit;
                string spaceName;
                FileAuxiliaries aux;
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, p.Espacio ?? SpaceResolver.ModelName);
                    spaceName = space.Name;
                    audit = SideAudit.Run(db, tr, space, rules, DateTime.UtcNow.AddMilliseconds(p.BudgetMs), Name, p.BudgetMs);
                    aux = FileAuxiliaries.Measure(db, tr, FileAuditRules.AuxBudgetMs(p.BudgetMs));
                    tr.Commit();
                }

                object? fileSummary = null;
                string? summaryWarning = null;
                if (p.Checks.Contains(FileAuditRules.Resumen))
                {
                    try
                    {
                        fileSummary = BuildFileSummary(db);
                    }
                    catch (Exception ex)
                    {
                        // Un resumen ilegible no tira los hallazgos ya calculados.
                        PluginLog.Error("auditar_archivo: no se pudo calcular el resumen del archivo", ex);
                        summaryWarning = FileAuditRules.SummaryFailedWarning(ex.GetType().Name);
                    }
                }

                (List<Dictionary<string, object?>> page, string? next) = audit.Page(p.Start, p.Limit);

                warnings.AddRange(AcadUtil.UnitWarnings(db));
                if (openInSession)
                {
                    warnings.Add(ResumenArchivoHandler.OpenInSessionWarning + " " + OpenInSessionHint);
                }

                warnings.AddRange(audit.OwnWarnings);
                if (aux.Warning != null)
                {
                    warnings.Add(aux.Warning);
                }

                if (summaryWarning != null)
                {
                    warnings.Add(summaryWarning);
                }

                if (folders == null)
                {
                    warnings.Add(FileAuditRules.NoFoldersWarning);
                }

                int ins = AcadUtil.InsUnits(db);
                PluginLog.Info("auditar_archivo: '" + p.Ruta + "' terminado en " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
                return new Dictionary<string, object?>
                {
                    ["pid"] = ctx.Pid,
                    ["origen"] = "plugin",
                    ["archivo"] = new Dictionary<string, object?>
                    {
                        ["ruta"] = p.Ruta,
                        ["nombre"] = Path.GetFileName(p.Ruta),
                        ["tamano_bytes"] = size,
                        ["modificado"] = modified,
                        ["version_dwg"] = ResumenArchivoHandler.VersionName(db.OriginalFileVersion),
                        ["abierto_en_sesion"] = openInSession,
                        ["copia_temporal"] = p.Copiar,
                    },
                    ["espacio"] = spaceName,
                    ["comprobaciones"] = p.Checks,
                    ["no_disponible"] = unavailable,
                    ["resumen"] = audit.BuildResumen(rules),
                    ["resumen_archivo"] = fileSummary,
                    ["reglas_omitidas"] = audit.Omitted,
                    ["reglas_con_error"] = audit.Errors,
                    ["duracion_ms"] = watch.ElapsedMilliseconds,
                    ["hallazgos"] = page,
                    ["total"] = audit.All.Count,
                    ["cursor_siguiente"] = next,
                    ["origen_texto"] = AuditSupport.OrigenTexto,
                    ["insunits"] = ins,
                    ["unidades"] = UnitsTable.Name(ins),
                    ["en_metros_declarado"] = ins == InsunitsMeters,
                    ["advertencias"] = warnings,
                    ["_cype"] = aux.Cype,
                    ["_lamina"] = aux.Lamina,
                    ["_bloques"] = aux.Bloques,
                    ["_mediana_texto"] = audit.MedianaTexto,
                };
            }
            finally
            {
                // §5H.1: Dispose SIEMPRE y antes de borrar; nunca Save/SaveAs.
                db?.Dispose();
                DeleteTempCopy(tempCopy, warnings);
            }
        }

        /// <summary>%LOCALAPPDATA%\MCPAutoCAD\tmp (§5H.0.7). Solo calcula la ruta: no toca el disco.</summary>
        private static string OwnTempFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPAutoCAD", "tmp");

        /// <summary>
        /// Borra, una vez por proceso, las copias auditar_&lt;pid&gt;_&lt;n&gt;.dwg de procesos ya muertos (AutoCAD cayó en plena
        /// lectura y su finally no corrió). Las de procesos vivos no se tocan, y tampoco las subcarpetas del lote. Nunca
        /// tumba la auditoría.
        /// </summary>
        private static void SweepOrphanCopies()
        {
            if (Interlocked.Exchange(ref orphansSwept, 1) != 0)
            {
                return;
            }

            try
            {
                string folder = OwnTempFolder();
                if (!Directory.Exists(folder))
                {
                    return;
                }

                foreach (string path in Directory.EnumerateFiles(folder, "auditar_*.dwg"))
                {
                    if (!FileAuditRules.TryParseTempCopyPid(Path.GetFileName(path), out int pid) || IsProcessAlive(pid))
                    {
                        continue;
                    }

                    try
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                        PluginLog.Info("auditar_archivo: borrada la copia huérfana '" + path + "'.");
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        PluginLog.Warn("auditar_archivo: no se pudo borrar la copia huérfana '" + path + "': " + ex.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PluginLog.Warn("auditar_archivo: no se pudo revisar la carpeta temporal: " + ex.Message);
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (Process.GetProcessById(pid))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Comprueba el espacio libre (sin copiar nada si no alcanza), crea la carpeta y devuelve la ruta de la copia.</summary>
        private static string ReserveTempCopy(int pid, long size)
        {
            string folder = OwnTempFolder();
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            long free = drive.AvailableFreeSpace;
            long required = FileAuditRules.RequiredFreeBytes(size);
            if (free < required)
            {
                throw new ToolException(
                    ErrorCodes.Internal,
                    "No hay espacio en disco para la copia temporal.",
                    new Dictionary<string, object?> { ["detalle"] = FileAuditRules.DiskSpaceDetail(free, required, drive.Name) });
            }

            Directory.CreateDirectory(folder);
            return Path.Combine(folder, FileAuditRules.TempCopyName(pid, Interlocked.Increment(ref copyCounter)));
        }

        /// <summary>Un origen que no se deja copiar (red caída, permisos) es un DWG ilegible (§5H.1).</summary>
        private static void CopyToTemp(string source, string destination)
        {
            try
            {
                File.Copy(source, destination, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PluginLog.Error("auditar_archivo: no se pudo copiar el archivo a la carpeta temporal", ex);
                throw UnreadableDwg(ex);
            }
        }

        /// <summary>INTERNAL de §5H.1 para una excepción que no es de AutoCAD.</summary>
        private static ToolException UnreadableDwg(Exception ex) => new ToolException(
            ErrorCodes.Internal,
            "No se pudo leer el archivo DWG.",
            new Dictionary<string, object?> { ["detalle"] = FileAuditRules.ReadFailureDetail(ex.GetType().FullName ?? ex.GetType().Name, ex.Message) });

        /// <summary>
        /// Base lateral sin documento, cargada en memoria y con el archivo ya suelto. Mismos errores que §5B.8, también para una
        /// excepción que no es de AutoCAD (un DWG truncado): quien llama aún no tiene la base, así que aquí se libera siempre.
        /// </summary>
        private Database ReadSide(string path)
        {
            var db = new Database(false, true);
            try
            {
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                db.CloseInput(true);
                return db;
            }
            catch (AcRuntimeException ex)
            {
                db.Dispose();
                throw ResumenArchivoHandler.ReadError(ex, Name);
            }
            catch (Exception ex)
            {
                db.Dispose();
                PluginLog.Error(Name + ": ReadDwgFile falló", ex);
                throw UnreadableDwg(ex);
            }
        }

        /// <summary>Campos de §5B.8 salvo los que ya van en <c>archivo</c> o en la raíz (§5H.1).</summary>
        private static Dictionary<string, object?> BuildFileSummary(Database db)
        {
            ResumenArchivoHandler.Summary summary = ResumenArchivoHandler.Summarize(db);
            return new Dictionary<string, object?>
            {
                ["measurement"] = AcadUtil.Measurement(db),
                ["n_capas"] = summary.LayerCount,
                ["entidades_modelo"] = new Dictionary<string, object?>
                {
                    ["total"] = summary.ModelTotal,
                    ["por_tipo"] = summary.ByType,
                },
                ["n_bloques"] = summary.BlockCount,
                ["bloques_mas_usados"] = summary.TopBlocks,
                ["layouts"] = summary.Layouts,
                ["n_xrefs"] = summary.XrefCount,
            };
        }

        /// <summary>
        /// Borra la copia. File.Copy conserva el atributo de solo lectura del origen, que impediría File.Delete: se quita antes.
        /// </summary>
        private static void DeleteTempCopy(string? path, List<string> warnings)
        {
            if (path == null || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PluginLog.Warn("auditar_archivo: no se pudo borrar la copia temporal '" + path + "': " + ex.Message);
                warnings.Add(FileAuditRules.CopyNotDeletedWarning(path));
            }
        }
    }

    /// <summary>
    /// Campos auxiliares de la fila del lote (§5H.2) sobre la transacción de la auditoría; solo lectura. _bloques y los estilos
    /// son tablas y salen siempre. Capas y recuadros recorren el modelo y los layouts con plazo propio y, si no caben, _cype y
    /// _lamina quedan en null: una medida parcial daría un es_export_cype falso, y null es "sin medir" para lote.py. Nunca
    /// tumba la auditoría.
    /// </summary>
    internal sealed class FileAuxiliaries
    {
        public Dictionary<string, object?>? Cype { get; private set; }

        public Dictionary<string, object?>? Lamina { get; private set; }

        public List<string>? Bloques { get; private set; }

        /// <summary>Advertencia si algo quedó sin medir.</summary>
        public string? Warning { get; private set; }

        public static FileAuxiliaries Measure(Database db, Transaction tr, int budgetMs)
        {
            var aux = new FileAuxiliaries();
            try
            {
                var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                aux.Bloques = BlockNames(blocks, tr);
                List<string> styles = TextStyleNames(db, tr);

                var measure = new FileAuxMeasure();
                if (ScanSpaces(blocks, tr, measure, DateTime.UtcNow.AddMilliseconds(budgetMs)))
                {
                    aux.Cype = new Dictionary<string, object?>
                    {
                        ["capas_con_entidades"] = measure.Capas,
                        ["estilos_texto"] = styles,
                    };
                    aux.Lamina = new Dictionary<string, object?>
                    {
                        ["recuadros"] = measure.Recuadros,
                        ["textos_escala"] = measure.TextosEscala,
                    };
                }
                else
                {
                    aux.Warning = FileAuditRules.AuxTimedOutWarning(budgetMs);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("auditar_archivo: no se pudieron medir los datos auxiliares", ex);
                aux.Warning = FileAuditRules.AuxFailedWarning(ex.GetType().Name);
            }

            return aux;
        }

        /// <summary>Definiciones no anónimas ni de layout; las xrefs cuentan, como en el DXF de ODA: un rótulo enlazado es un rótulo.</summary>
        private static List<string> BlockNames(BlockTable blocks, Transaction tr)
        {
            var names = new List<string>();
            foreach (ObjectId id in blocks)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!btr.IsLayout && !btr.IsAnonymous && !btr.Name.StartsWith("*", StringComparison.Ordinal))
                {
                    names.Add(btr.Name);
                }
            }

            return names;
        }

        private static List<string> TextStyleNames(Database db, Transaction tr)
        {
            var names = new List<string>();
            var styles = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in styles)
            {
                // Las entradas de archivos de formas no tienen nombre.
                if (!id.IsNull && !id.IsErased && tr.GetObject(id, OpenMode.ForRead) is TextStyleTableRecord style && style.Name.Length > 0)
                {
                    names.Add(style.Name);
                }
            }

            return names;
        }

        /// <summary>
        /// Capas de las entidades del modelo y los layouts; recuadros y escalas, solo del modelo (misma entrada que
        /// InsertarExportHandler.Measure, §5G.4). Devuelve false si se agotó el plazo.
        /// </summary>
        private static bool ScanSpaces(BlockTable blocks, Transaction tr, FileAuxMeasure measure, DateTime deadlineUtc)
        {
            ObjectId modelId = blocks[BlockTableRecord.ModelSpace];
            int seen = 0;
            foreach (ObjectId btrId in blocks)
            {
                if (btrId.IsNull || btrId.IsErased)
                {
                    continue;
                }

                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (!btr.IsLayout)
                {
                    continue;
                }

                bool isModel = btrId == modelId;
                foreach (ObjectId id in btr)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    if (++seen % AuditSupport.CheckEvery == 0 && DateTime.UtcNow >= deadlineUtc)
                    {
                        return false;
                    }

                    if (!FileAuditRules.CountsAsLayerContent(AcadUtil.DxfNameFromId(id)))
                    {
                        continue;
                    }

                    Entity? e = AuditSupport.Open(tr, id);
                    if (e == null)
                    {
                        continue;
                    }

                    try
                    {
                        measure.AddLayer(e.Layer);
                        if (isModel)
                        {
                            MeasureSheet(e, measure);
                        }
                    }
                    catch (Exception)
                    {
                        // Entidad ilegible: se omite, como en las reglas.
                    }
                }
            }

            return true;
        }

        private static void MeasureSheet(Entity e, FileAuxMeasure measure)
        {
            if (ExportRules.IsSheetLayer(e.Layer) && AcadUtil.TryBBox2(e) is BBox2 box)
            {
                measure.AddRecuadro(box.Xmax - box.Xmin, box.Ymax - box.Ymin);
            }

            measure.AddText(e is DBText text ? text.TextString : (e as MText)?.Text);
        }
    }
}
