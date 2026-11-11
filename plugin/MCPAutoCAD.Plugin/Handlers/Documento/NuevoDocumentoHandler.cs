using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MCPAutoCAD.Plugin.Handlers.Documento
{
    /// <summary>
    /// nuevo_documento (fallo 35, 0.10.1). Crea un .dwg NUEVO a partir de una plantilla (por defecto la del estándar LPS,
    /// <c>plantillas\LPS_METROS.dwt</c> del bundle) y lo abre como documento activo.
    /// <list type="bullet">
    /// <item>El archivo se crea fuera de AutoCAD (Database lateral: ReadDwgFile de la plantilla + SaveAs a un temporal en la
    /// MISMA carpeta) y luego se publica con <c>File.Move(..., overwrite: false)</c>, así nunca se sobrescribe ni siquiera si
    /// alguien crea la ruta en medio. Después se abre con <c>DocumentCollection.Open</c>, el mismo camino que abrir_documento.</item>
    /// <item>La ruta sigue las mismas reglas que guardar_copia: forma de ExportRules.ValidatePath y carpetas_export_permitidas
    /// de lps.json. La carpeta debe existir y no estar en carpetas_solo_lectura; tampoco se crea si la carpeta tiene
    /// archivos de carga automática, ni si hay un .dwl huérfano junto a la ruta o el dibujo ya está abierto.</item>
    /// <item>Sin confirmar: true solo previsualiza (no crea nada): crear el archivo y abrirlo en escritura deja un .dwl.</item>
    /// <item>Si AutoCAD no puede abrir el archivo recién creado, se borra (best effort) y se dice en el mensaje.</item>
    /// </list>
    /// </summary>
    internal sealed class NuevoDocumentoHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "ruta", "plantilla", "activar", "confirmar" };

        /// <summary>Nombre de la plantilla del estándar LPS en el bundle.</summary>
        public const string PlantillaLps = "LPS_METROS.dwt";

        public string Name => "nuevo_documento";

        public string Description =>
            "ESCRIBE un .dwg NUEVO en disco a partir de una plantilla (por defecto la del estándar LPS, en metros) y lo abre como " +
            "documento activo. Nunca sobrescribe: la ruta no debe existir y su carpeta sí, fuera de carpetas_solo_lectura de " +
            "lps.json y dentro de carpetas_export_permitidas si esa clave existe. Sin confirmar: true solo previsualiza (ruta, " +
            "plantilla, unidades). Después use guardar para guardar los cambios.";

        public bool RequiresDocument => false;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        public string? TimeoutHint(JsonElement parameters) =>
            "AutoCAD puede seguir creando o abriendo el dibujo: compruebe con estado antes de volver a pedirlo (la ruta ya puede existir).";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta del .dwg a crear (unidad o UNC), sin '..' ni comodines. No debe existir; su carpeta sí y debe estar en carpetas_export_permitidas de lps.json si esa clave existe."" },
    ""plantilla"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta de un .dwt o .dwg a usar como base (sin '..' ni comodines). Por defecto, la plantilla LPS del bundle (LPS_METROS.dwt, metros)."" },
    ""activar"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true = el dibujo nuevo queda como documento activo."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Obligatorio para crear el archivo y abrirlo. Sin él solo previsualiza."" }
  },
  ""required"": [""ruta""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""preview"": { ""type"": ""boolean"" },
    ""creado"": { ""type"": ""boolean"" },
    ""ruta"": { ""type"": ""string"" },
    ""nombre"": { ""type"": ""string"" },
    ""plantilla"": { ""type"": ""string"" },
    ""insunits"": { ""type"": [""integer"", ""null""] },
    ""unidades"": { ""type"": [""string"", ""null""] },
    ""activo"": { ""type"": ""boolean"" },
    ""activo_anterior"": { ""type"": [""string"", ""null""] },
    ""documentos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview"", ""creado"", ""ruta"", ""plantilla"", ""activo"", ""advertencias""]
}");

        private sealed class Params
        {
            public Params(string ruta, string? plantilla, bool activar, bool confirmar)
            {
                Ruta = ruta;
                Plantilla = plantilla;
                Activar = activar;
                Confirmar = confirmar;
            }

            public string Ruta { get; }

            public string? Plantilla { get; }

            public bool Activar { get; }

            public bool Confirmar { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string ruta = ParamReader.RequiredText(p, "ruta", 1, 1000);
            string? forma = ExportRules.ValidatePath(ruta, null);
            if (forma != null)
            {
                throw ToolException.InvalidParams(forma);
            }

            string? plantilla = ParamReader.OptionalString(p, "plantilla", 1, 1000);
            if (plantilla != null)
            {
                // Las plantillas son .dwt: ValidatePath exige .dwg, por eso la plantilla tiene su propia regla (.dwt o .dwg).
                string? formaP = ExportRules.ValidateTemplatePath(plantilla);
                if (formaP != null)
                {
                    throw ToolException.InvalidParams(formaP);
                }
            }

            return new Params(
                ruta,
                plantilla,
                ParamReader.OptionalBool(p, "activar") ?? true,
                ParamReader.OptionalBool(p, "confirmar") ?? false);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            DocumentCollection docs = AcApp.DocumentManager;
            DocumentoSupport.RequerirContextoAplicacion(docs);
            var warnings = new List<string>();

            string full;
            try
            {
                full = Path.GetFullPath(p.Ruta);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw ToolException.InvalidParams("ruta: no es una ruta válida (" + ex.GetType().Name + ")", new Dictionary<string, object?> { ["ruta"] = p.Ruta });
            }

            if (DocumentoSupport.TieneNombreCorto(p.Ruta))
            {
                throw ToolException.InvalidParams(
                    "ruta: contiene un nombre corto 8.3 (con '~'); use la ruta con el nombre largo",
                    new Dictionary<string, object?> { ["ruta"] = p.Ruta });
            }

            string? carpeta = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(carpeta) || !Directory.Exists(carpeta))
            {
                throw ToolException.InvalidParams("ruta: la carpeta no existe", new Dictionary<string, object?> { ["ruta"] = full });
            }

            // Mismas carpetas permitidas que guardar_copia (lps.json carpetas_export_permitidas): se valida la ruta pedida.
            List<string>? folders = InsertarExportHandler.ReadAllowedFolders(warnings);
            string? fueraDeCarpetas = ExportRules.ValidatePath(p.Ruta, folders);
            if (fueraDeCarpetas != null)
            {
                throw ToolException.InvalidParams(fueraDeCarpetas, new Dictionary<string, object?> { ["ruta"] = p.Ruta });
            }

            if (File.Exists(full) || Directory.Exists(full))
            {
                throw ToolException.InvalidParams(
                    "ruta: ya existe; nuevo_documento nunca sobrescribe (use abrir_documento para abrirlo o elija otro nombre)",
                    new Dictionary<string, object?> { ["ruta"] = full });
            }

            List<string> soloLectura = DocumentoSupport.CarpetasSoloLectura();
            if (DocumentoSupport.EnCarpetaSoloLectura(full, soloLectura) || DocumentoSupport.EnCarpetaSoloLectura(carpeta, soloLectura))
            {
                throw ToolException.InvalidParams(
                    "ruta: está en una carpeta de carpetas_solo_lectura de lps.json; cree el dibujo en otra carpeta",
                    new Dictionary<string, object?> { ["ruta"] = full });
            }

            List<string> autoCarga = DocumentoSupport.AutoCargadosEn(full);
            if (autoCarga.Count > 0)
            {
                throw ToolException.InvalidParams(
                    "ruta: la carpeta contiene archivos de carga automática (" + autoCarga.Count + "). AutoCAD los ejecutaría al abrir el "
                    + "dibujo sin la aprobación del usuario; elija otra carpeta.",
                    new Dictionary<string, object?> { ["ruta"] = full });
            }

            // Ya abierto (ruta distinta de la existencia en disco: otra sesión o un documento sin guardar con ese nombre).
            if (DocumentoSupport.PorRuta(docs, full) != null)
            {
                throw ToolException.InvalidParams(
                    "ruta: ya hay un documento abierto con esa ruta en AutoCAD; elija otro nombre",
                    new Dictionary<string, object?> { ["ruta"] = full });
            }

            // Un .dwl sin dibujo (sesión anterior que no cerró bien) haría que AutoCAD pregunte al abrir el dibujo nuevo.
            string? bloqueo = DocumentoSupport.BloqueoDe(full);
            if (bloqueo != null)
            {
                throw ToolException.InvalidParams(
                    "ruta: hay un archivo de bloqueo (" + Path.GetFileName(bloqueo) + ") junto a la ruta; AutoCAD pediría una decisión "
                    + "en un diálogo. Elija otro nombre o retire el bloqueo a mano.",
                    new Dictionary<string, object?> { ["ruta"] = full, ["bloqueo"] = bloqueo });
            }

            // Avisos de los diálogos de apertura (DWGCHECK, PROXYNOTICE, SECURELOAD), igual que abrir_documento.
            DocumentoSupport.AnadirAvisosDeApertura(warnings);

            string plantilla = ResolverPlantilla(p.Plantilla);
            Document? previo = docs.MdiActiveDocument;
            string? nombrePrevio = previo == null ? null : Path.GetFileName(previo.Name ?? "");

            int? insunits;
            using (var side = new Database(false, true))
            {
                try
                {
                    side.ReadDwgFile(plantilla, FileOpenMode.OpenForReadAndAllShare, true, "");
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("nuevo_documento: no se pudo leer la plantilla '" + plantilla + "' (" + ex.GetType().Name + ": " + ex.Message + ").");
                    throw ToolException.InvalidParams("plantilla: no se pudo leer (" + ex.Message + ")", new Dictionary<string, object?> { ["plantilla"] = plantilla });
                }

                insunits = (int)side.Insunits;
                if (LpsStandard.Disponible && LpsStandard.Insunits != null && insunits != LpsStandard.Insunits)
                {
                    warnings.Add("La plantilla declara INSUNITS = " + insunits + " (" + UnitsTable.Name(insunits.Value)
                        + ") y el estándar LPS es " + LpsStandard.Insunits + " (" + LpsStandard.Unidades + ").");
                }

                if (!p.Confirmar)
                {
                    warnings.Add("Previsualización: no se creó nada. Repita con confirmar: true para crear " + Path.GetFileName(full)
                        + " y abrirlo en escritura (crea el bloqueo .dwl mientras esté abierto).");
                    return Build(ctx, true, false, full, plantilla, insunits, false, nombrePrevio, warnings, docs);
                }

                // B2: se escribe a un temporal de la misma carpeta y se publica con File.Move(overwrite: false).
                string tmp = Path.Combine(carpeta, ".mcp_nuevo_" + Guid.NewGuid().ToString("N") + ".dwg");
                try
                {
                    side.CloseInput(true);   // B5: suelta la lectura de la plantilla antes de escribir.
                    side.SaveAs(tmp, DwgVersion.Current);
                }
                catch (Exception ex)
                {
                    TryDelete(tmp);
                    PluginLog.Error("nuevo_documento: no se pudo crear '" + full + "'", ex);
                    throw ToolException.InvalidParams("ruta: no se pudo crear el archivo (" + ex.Message + ")", new Dictionary<string, object?> { ["ruta"] = full });
                }

                try
                {
                    File.Move(tmp, full, false);
                }
                catch (IOException ex)
                {
                    TryDelete(tmp);
                    if (File.Exists(full))
                    {
                        throw ToolException.InvalidParams(
                            "ruta: ya existe; nuevo_documento nunca sobrescribe (use abrir_documento para abrirlo o elija otro nombre)",
                            new Dictionary<string, object?> { ["ruta"] = full });
                    }

                    PluginLog.Error("nuevo_documento: no se pudo publicar '" + full + "'", ex);
                    throw ToolException.InvalidParams("ruta: no se pudo crear el archivo (" + ex.Message + ")", new Dictionary<string, object?> { ["ruta"] = full });
                }
                catch (UnauthorizedAccessException ex)
                {
                    TryDelete(tmp);
                    throw ToolException.InvalidParams("ruta: sin permiso para crear el archivo (" + ex.Message + ")", new Dictionary<string, object?> { ["ruta"] = full });
                }
            }

            PluginLog.Info("nuevo_documento: creado '" + full + "' desde '" + plantilla + "'.");

            Document nuevo;
            try
            {
                nuevo = docs.Open(full, false);
            }
            catch (Exception ex)
            {
                // B6: el archivo lo acabamos de crear y no tenía .dwl (se comprobó antes): se retira para no dejar un dibujo a medias.
                PluginLog.Error("nuevo_documento: creado pero no se pudo abrir '" + full + "'", ex);
                BorrarBloqueos(full);
                bool borrado = TryDelete(full);
                string causa = ex.GetType().Name + ": " + ex.Message;
                string mensaje = borrado
                    ? "AutoCAD no pudo abrir el dibujo recién creado, así que se borró el archivo (no queda en disco). Causa: " + causa + "."
                    : "El dibujo se creó en disco pero AutoCAD no pudo abrirlo y no se pudo borrar. Causa: " + causa + ". Ábralo con abrir_documento.";
                throw new ToolException(
                    ErrorCodes.Internal,
                    mensaje,
                    new Dictionary<string, object?> { ["ruta"] = full, ["creado"] = !borrado, ["borrado"] = borrado });
            }

            bool activo;
            if (p.Activar)
            {
                activo = DocumentoSupport.Activar(docs, nuevo);
                if (!activo)
                {
                    warnings.Add("El dibujo está abierto pero no se pudo dejar como activo: AutoCAD no permite cambiar la activación ahora.");
                }
            }
            else
            {
                // AutoCAD activa lo que abre: se devuelve el activo anterior para no cambiar el contexto del usuario.
                if (previo != null && !ReferenceEquals(previo, nuevo))
                {
                    DocumentoSupport.Activar(docs, previo);
                }

                activo = ReferenceEquals(docs.MdiActiveDocument, nuevo);
                if (previo != null && !ReferenceEquals(docs.MdiActiveDocument, previo))
                {
                    warnings.Add("No se pudo restaurar el documento activo anterior ('" + nombrePrevio + "'); el activo ahora es '"
                        + Path.GetFileName(docs.MdiActiveDocument?.Name ?? "") + "'. Revise estado antes de escribir.");
                }
            }

            return Build(ctx, false, true, full, plantilla, insunits, activo, nombrePrevio, warnings, docs);
        }

        /// <summary>
        /// Plantilla pedida (debe existir) o la del estándar LPS del bundle: junto a la DLL (<c>plantillas\</c>) o en la
        /// carpeta Contents (un nivel arriba). Ninguna → INVALID_PARAMS pidiendo 'plantilla'.
        /// </summary>
        private static string ResolverPlantilla(string? pedida)
        {
            if (pedida != null)
            {
                string fullP;
                try
                {
                    fullP = Path.GetFullPath(pedida);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    throw ToolException.InvalidParams("plantilla: no es una ruta válida (" + ex.GetType().Name + ")");
                }

                if (!File.Exists(fullP))
                {
                    throw new ToolException(ErrorCodes.NotFound, "No existe la plantilla '" + fullP + "'.", new Dictionary<string, object?> { ["plantilla"] = fullP });
                }

                return fullP;
            }

            string? dir = Path.GetDirectoryName(typeof(PluginApplication).Assembly.Location);
            if (dir != null)
            {
                foreach (string candidata in new[]
                {
                    Path.Combine(dir, "plantillas", PlantillaLps),
                    Path.GetFullPath(Path.Combine(dir, "..", "plantillas", PlantillaLps)),
                })
                {
                    if (File.Exists(candidata))
                    {
                        return candidata;
                    }
                }
            }

            throw ToolException.InvalidParams(
                "plantilla: no se encontró la plantilla LPS (" + PlantillaLps + ") junto al plugin; indique 'plantilla' con una ruta .dwt o .dwg");
        }

        /// <summary>Borra el temporal o el archivo si existe. true si al terminar ya no existe. Nunca lanza.</summary>
        private static bool TryDelete(string ruta)
        {
            try
            {
                if (File.Exists(ruta))
                {
                    File.Delete(ruta);
                }

                return !File.Exists(ruta);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PluginLog.Warn("nuevo_documento: no se pudo borrar '" + ruta + "' (" + ex.Message + ").");
                return false;
            }
        }

        /// <summary>Retira los .dwl/.dwl2 del dibujo que acabamos de crear (se comprobó que no existían antes). Best effort.</summary>
        private static void BorrarBloqueos(string rutaDwg)
        {
            TryDelete(Path.ChangeExtension(rutaDwg, ".dwl"));
            TryDelete(Path.ChangeExtension(rutaDwg, ".dwl2"));
        }

        private static Dictionary<string, object?> Build(
            ExecutionContext ctx, bool preview, bool creado, string ruta, string plantilla, int? insunits, bool activo,
            string? activoAnterior, List<string> warnings, DocumentCollection docs)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = ctx.Pid,
                ["preview"] = preview,
                ["creado"] = creado,
                ["ruta"] = ruta,
                ["nombre"] = Path.GetFileName(ruta),
                ["plantilla"] = plantilla,
                ["insunits"] = insunits,
                ["unidades"] = insunits == null ? null : UnitsTable.Name(insunits.Value),
                ["activo"] = activo,
                ["activo_anterior"] = activoAnterior,
                ["documentos"] = DocumentoSupport.Filas(DocumentoSupport.Describir(DocumentoSupport.Abiertos(docs), docs.MdiActiveDocument)),
                ["advertencias"] = warnings,
            };
        }
    }
}
