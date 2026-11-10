using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Documento
{
    /// <summary>
    /// guardar (0.9.x): equivalente a QSAVE sobre el documento activo, o sobre <c>documento</c> (nombre o ruta entre los
    /// abiertos). Guarda en su propio archivo con <c>Database.SaveAs</c> (misma ruta, versión original del archivo, .bak según ISAVEBAK) bajo LockDocument.
    /// Rechaza: documento sin ruta (nunca guardado: use guardar_copia), documento de solo lectura, archivo con atributo de solo
    /// lectura de Windows, carpeta de <c>carpetas_solo_lectura</c> de lps.json (fail-closed si esa clave está mal formada) y
    /// documento con un comando en curso. Sin confirmar: true solo previsualiza. Escribe en disco: escritura: true.
    /// Sin <see cref="UndoGroup"/>: no modifica entidades, así que no hay nada que deshacer.
    /// </summary>
    internal sealed class GuardarHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "documento", "confirmar" };

        public string Name => "guardar";

        public string Description =>
            "Guarda en su propio archivo el documento activo (o el indicado por 'documento', nombre o ruta entre los abiertos), " +
            "como QSAVE. Sin confirmar: true solo informa de la ruta, si está modificado y la versión de formato. Rechaza un " +
            "dibujo que nunca se ha guardado (use guardar_copia), uno de solo lectura o en una carpeta de solo lectura de lps.json.";

        public bool RequiresDocument => false;

        /// <summary>Escribe en disco (no idempotente para el servidor).</summary>
        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        /// <summary>Guardar un dibujo grande puede tardar más que el plazo general.</summary>
        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        /// <summary>Si se agota la espera, AutoCAD puede seguir escribiendo: no reintentar a ciegas.</summary>
        public string? TimeoutHint(JsonElement parameters) =>
            "AutoCAD puede seguir guardando el archivo: compruebe su fecha de modificación antes de volver a pedirlo.";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""documento"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Nombre (p. ej. PLANTA.dwg) o ruta completa de un documento abierto. Por defecto, el documento activo. Si hay varios con ese nombre, indique la ruta."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = guarda; por defecto solo previsualiza (ruta, modificado y versión de formato)."" }
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""preview"": { ""type"": ""boolean"" },
    ""guardado"": { ""type"": ""boolean"" },
    ""nombre"": { ""type"": ""string"" },
    ""ruta"": { ""type"": ""string"" },
    ""activo"": { ""type"": ""boolean"" },
    ""modificado"": { ""type"": [""boolean"", ""null""], ""description"": ""Antes de guardar. null si el documento no es el activo (AutoCAD no lo expone sin activarlo)."" },
    ""modificado_despues"": { ""type"": [""boolean"", ""null""] },
    ""version_formato"": { ""type"": ""string"", ""description"": ""Versión de formato con la que se escribe: la del archivo abierto (Database.OriginalFileVersion). Es lo que se escribe realmente."" },
    ""version_original"": { ""type"": [""string"", ""null""], ""description"": ""Igual que version_formato (se conserva el formato de apertura)."" },
    ""tamano_bytes"": { ""type"": [""integer"", ""null""] },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview"", ""guardado"", ""nombre"", ""ruta"", ""activo"", ""modificado"", ""version_formato"", ""advertencias""]
}");

        private sealed class Params
        {
            public Params(string? documento, bool confirmar)
            {
                Documento = documento;
                Confirmar = confirmar;
            }

            public string? Documento { get; }

            public bool Confirmar { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            return new Params(
                ParamReader.OptionalString(p, "documento", 1, 1000),
                ParamReader.OptionalBool(p, "confirmar") ?? false);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            DocumentCollection docs = AcApp.DocumentManager;
            if (docs.Count == 0)
            {
                throw ToolException.NoDoc();
            }

            Document? activo = docs.MdiActiveDocument;
            Document doc = p.Documento == null
                ? (ctx.Document ?? activo ?? throw ToolException.NoDoc())
                : DocumentoSupport.Resolver(docs, p.Documento);
            bool esActivo = ReferenceEquals(doc, activo);
            var warnings = new List<string>();

            // Rechazos (antes de escribir nada).
            if (!string.IsNullOrEmpty(doc.CommandInProgress))
            {
                throw ToolException.BusyCommand(doc.CommandInProgress);
            }

            if (doc.IsReadOnly)
            {
                throw ToolException.InvalidParams(
                    "documento: está abierto en solo lectura; guardar no puede escribirlo. Use guardar_copia para escribir una copia",
                    new Dictionary<string, object?> { ["nombre"] = Path.GetFileName(doc.Name ?? "") });
            }

            string? ruta = DocumentoSupport.RutaDe(doc);
            if (ruta == null)
            {
                throw ToolException.InvalidParams(
                    "documento: nunca se ha guardado (no tiene ruta); use guardar_copia con la ruta de destino",
                    new Dictionary<string, object?> { ["nombre"] = Path.GetFileName(doc.Name ?? "") });
            }

            // Carpetas de solo lectura: se relee lps.json en cada llamada; si existe y está roto → INVALID_PARAMS (fail-closed).
            if (DocumentoSupport.EnCarpetaSoloLectura(ruta, DocumentoSupport.CarpetasSoloLectura()))
            {
                throw ToolException.InvalidParams(
                    "documento: está en una carpeta de solo lectura de lps.json (carpetas_solo_lectura); no se guarda desde el puente",
                    new Dictionary<string, object?> { ["ruta"] = ruta });
            }

            try
            {
                if (File.Exists(ruta))
                {
                    if ((File.GetAttributes(ruta) & FileAttributes.ReadOnly) != 0)
                    {
                        throw ToolException.InvalidParams(
                            "documento: el archivo tiene el atributo de solo lectura de Windows; quítelo en el Explorador si quiere guardarlo",
                            new Dictionary<string, object?> { ["ruta"] = ruta });
                    }
                }
                else
                {
                    warnings.Add("El archivo ya no existe en disco: el guardado lo volverá a crear.");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw ToolException.InvalidParams(
                    "documento: no se puede comprobar el archivo en disco (" + ex.GetType().Name + "): " + ex.Message,
                    new Dictionary<string, object?> { ["ruta"] = ruta });
            }

            Database db = doc.Database;
            bool? modificado = Modificado(esActivo);
            if (!esActivo)
            {
                warnings.Add("El documento no es el activo: AutoCAD no informa de si está modificado sin activarlo.");
            }

            // Formato: el archivo se guarda con la versión con la que se abrió (no se sube ni se baja de formato sin avisar).
            Autodesk.AutoCAD.DatabaseServices.DwgVersion formato = db.OriginalFileVersion;
            string version = ResumenArchivoHandler.VersionName(formato);
            string? versionOriginal = version;
            if (formato != Autodesk.AutoCAD.DatabaseServices.DwgVersion.Current)
            {
                warnings.Add("El archivo se guarda en el formato con el que se abrió (" + version + "), no en el actual: funciones posteriores no se podrán guardar.");
            }

            bool bak = (DocumentoSupport.SafeIntSysVar("ISAVEBAK") ?? 1) != 0;
            if (!bak)
            {
                warnings.Add("ISAVEBAK=0: no se crea copia .bak al guardar; el archivo anterior se pierde.");
            }

            if (!p.Confirmar)
            {
                return Build(ctx, true, false, doc, ruta, esActivo, modificado, null, version, versionOriginal, null, warnings);
            }

            try
            {
                // Siempre con LockDocument (también en documentos no activos) y SaveAs determinista: misma ruta, versión
                // original y .bak según ISAVEBAK. Save() sin argumentos no garantiza nada de eso.
                using (doc.LockDocument())
                {
                    db.SaveAs(ruta, bak, formato, db.SecurityParameters);
                }
            }
            catch (Exception ex) when (ex is AcRuntimeException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                PluginLog.Error("guardar: no se pudo guardar '" + ruta + "'", ex);
                throw new ToolException(
                    ErrorCodes.Internal,
                    "No se pudo guardar el documento en su ruta.",
                    new Dictionary<string, object?> { ["ruta"] = ruta, ["detalle"] = ex.GetType().Name + ": " + ex.Message });
            }

            bool? modificadoDespues = Modificado(esActivo);
            // El guardado ya se hizo: si no se puede leer el tamaño, no falla la llamada; queda como null con advertencia.
            long? tamano = null;
            try
            {
                tamano = new FileInfo(ruta).Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                warnings.Add("Guardado, pero no se pudo leer el tamaño del archivo (" + ex.GetType().Name + ").");
            }

            PluginLog.Info("guardar: '" + ruta + "' guardado" + (tamano == null ? "" : ", " + tamano + " bytes") + ".");
            return Build(ctx, false, true, doc, ruta, esActivo, modificado, modificadoDespues, version, versionOriginal, tamano, warnings);
        }

        /// <summary>DBMOD solo es fiable para el documento activo. null si no es activo o si la sysvar no se puede leer.</summary>
        private static bool? Modificado(bool esActivo)
        {
            if (!esActivo)
            {
                return null;
            }

            int? dbmod = DocumentoSupport.SafeIntSysVar("DBMOD");
            return dbmod == null ? (bool?)null : dbmod.Value != 0;
        }

        private static Dictionary<string, object?> Build(
            ExecutionContext ctx, bool preview, bool guardado, Document doc, string ruta, bool activo, bool? modificado,
            bool? modificadoDespues, string version, string? versionOriginal, long? tamano, List<string> warnings)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = ctx.Pid,
                ["preview"] = preview,
                ["guardado"] = guardado,
                ["nombre"] = Path.GetFileName(ruta),
                ["ruta"] = ruta,
                ["activo"] = activo,
                ["modificado"] = modificado,
                ["modificado_despues"] = modificadoDespues,
                ["version_formato"] = version,
                ["version_original"] = versionOriginal,
                ["tamano_bytes"] = tamano,
                ["advertencias"] = warnings,
            };
        }

    }
}
