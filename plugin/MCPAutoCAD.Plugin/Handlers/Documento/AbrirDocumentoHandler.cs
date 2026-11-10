using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Documento
{
    /// <summary>
    /// abrir_documento (0.9.x). Abre un .dwg con <c>DocumentCollection.Open</c> (extensión de acmgd) en contexto de aplicación.
    /// <list type="bullet">
    /// <item>Si ya está abierto (misma ruta), no lo reabre: lo activa (si <c>activar</c>) y avisa si pidió otro modo.</item>
    /// <item><c>solo_lectura: true</c> no bloquea el archivo y se abre sin confirmación. Abrir en ESCRITURA crea el bloqueo
    /// <c>.dwl</c> que ven los demás usuarios de la red, así que exige <c>confirmar: true</c> (sin él, solo previsualiza).</item>
    /// <item>Si existe un <c>.dwl</c>/<c>.dwl2</c> se rechaza antes de abrir: AutoCAD pediría una decisión en un diálogo que
    /// bloquearía el hilo principal y el puente.</item>
    /// <item><c>activar: false</c>: AutoCAD activa el documento al abrirlo; se restaura el activo anterior. Si no se puede,
    /// se avisa en advertencias.</item>
    /// </list>
    /// Sin <see cref="UndoGroup"/>: no modifica entidades. Sin comando activo (BUSY por el despachador).
    /// </summary>
    internal sealed class AbrirDocumentoHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "ruta", "solo_lectura", "activar", "confirmar" };

        public string Name => "abrir_documento";

        public string Description =>
            "Abre un dibujo .dwg (ruta absoluta) en AutoCAD. Si ya está abierto solo lo activa. En solo lectura se abre sin " +
            "confirmación; en escritura bloquea el archivo para otros usuarios, así que sin confirmar: true solo previsualiza. " +
            "Rechaza si hay un .dwl/.dwl2 (también en solo lectura), si la carpeta del dibujo tiene archivos de carga automática " +
            "(acaddoc, acad, acad.mnl), nombres 8.3 o carpetas de solo lectura de lps.json (ahí fuerza solo lectura). " +
            "Por defecto lo deja como documento activo (activar: true).";

        public bool RequiresDocument => false;

        /// <summary>Puede crear el bloqueo de escritura del dibujo (.dwl): se trata como escritura.</summary>
        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        /// <summary>Abrir un dibujo grande puede tardar más que el plazo general.</summary>
        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        /// <summary>Si se agota la espera, AutoCAD puede seguir abriendo: no reintentar a ciegas.</summary>
        public string? TimeoutHint(JsonElement parameters) =>
            "AutoCAD puede seguir abriendo el dibujo: compruebe con instancias o estado antes de volver a pedirlo.";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta del .dwg (unidad o UNC), sin '..' ni comodines. Debe existir."" },
    ""solo_lectura"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = abre en solo lectura (no bloquea el archivo y no pide confirmación)."" },
    ""activar"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true = queda como documento activo; false = el activo anterior se mantiene."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Obligatorio para abrir en escritura (bloquea el archivo). Sin él solo previsualiza."" }
  },
  ""required"": [""ruta""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""preview"": { ""type"": ""boolean"" },
    ""abierto"": { ""type"": ""boolean"", ""description"": ""true si el documento queda abierto (recién abierto o ya lo estaba)."" },
    ""ya_abierto"": { ""type"": ""boolean"" },
    ""ruta"": { ""type"": ""string"" },
    ""nombre"": { ""type"": ""string"" },
    ""solo_lectura"": { ""type"": ""boolean"" },
    ""activo"": { ""type"": ""boolean"", ""description"": ""Si el documento es ahora el activo."" },
    ""activo_anterior"": { ""type"": [""string"", ""null""], ""description"": ""Nombre del documento activo antes de la llamada."" },
    ""documentos"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""nombre"": { ""type"": ""string"" }, ""ruta"": { ""type"": [""string"", ""null""] }, ""activo"": { ""type"": ""boolean"" } } } },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview"", ""abierto"", ""ya_abierto"", ""ruta"", ""solo_lectura"", ""activo"", ""advertencias""]
}");

        private sealed class Params
        {
            public Params(string ruta, bool soloLectura, bool activar, bool confirmar)
            {
                Ruta = ruta;
                SoloLectura = soloLectura;
                Activar = activar;
                Confirmar = confirmar;
            }

            public string Ruta { get; }

            public bool SoloLectura { get; set; }

            public bool Activar { get; }

            public bool Confirmar { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string? ruta = ParamReader.OptionalString(p, "ruta", 1, 1000);
            if (ruta == null)
            {
                throw ToolException.InvalidParams("ruta: obligatoria");
            }

            string? forma = ExportRules.ValidatePath(ruta, null);
            if (forma != null)
            {
                throw ToolException.InvalidParams(forma);
            }

            return new Params(
                ruta,
                ParamReader.OptionalBool(p, "solo_lectura") ?? false,
                ParamReader.OptionalBool(p, "activar") ?? true,
                ParamReader.OptionalBool(p, "confirmar") ?? false);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            DocumentCollection docs = AcApp.DocumentManager;
            DocumentoSupport.RequerirContextoAplicacion(docs);

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
                    "ruta: contiene un nombre corto 8.3 (con '~'); use la ruta con el nombre largo para que la comprobación de carpetas sea fiable",
                    new Dictionary<string, object?> { ["ruta"] = p.Ruta });
            }

            if (!File.Exists(full))
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "No existe el archivo '" + full + "'.",
                    new Dictionary<string, object?> { ["ruta"] = full });
            }

            Document? previo = docs.MdiActiveDocument;
            string? nombrePrevio = previo == null ? null : Path.GetFileName(previo.Name ?? "");
            var warnings = new List<string>();

            // Diálogos y comportamientos de apertura que AutoCAD puede mostrar (bloquean el hilo principal si aparecen).
            int? dwgCheck = DocumentoSupport.SafeIntSysVar("DWGCHECK");
            // DWGCHECK 1 o 3: AutoCAD pregunta al abrir un dibujo que puede estar dañado. Otros valores no bloquean.
            if (dwgCheck == 1 || dwgCheck == 3)
            {
                warnings.Add("DWGCHECK=" + dwgCheck.Value + ": AutoCAD puede abrir un diálogo al abrir el dibujo que bloquee el puente hasta que el usuario responda.");
            }

            int? proxyNotice = DocumentoSupport.SafeIntSysVar("PROXYNOTICE");
            if (proxyNotice != null && proxyNotice.Value != 0)
            {
                warnings.Add("PROXYNOTICE=" + proxyNotice.Value + ": AutoCAD puede abrir un diálogo por objetos proxy que bloquee el puente hasta que el usuario responda.");
            }

            if (DocumentoSupport.SafeIntSysVar("SECURELOAD") == 0)
            {
                warnings.Add("SECURELOAD=0: AutoCAD carga código de la carpeta del dibujo sin avisar (aquí se rechaza si hay archivos de carga automática).");
            }

            // Carpetas de solo lectura: dentro → se abre siempre en solo lectura (con aviso). lps.json roto → INVALID_PARAMS.
            if (DocumentoSupport.EnCarpetaSoloLectura(full, DocumentoSupport.CarpetasSoloLectura()) && !p.SoloLectura)
            {
                p.SoloLectura = true;
                warnings.Add("La ruta está en una carpeta de carpetas_solo_lectura de lps.json: se abre en solo lectura.");
            }

            // Ya abierto: solo se activa (no hay nada que confirmar).
            Document? existente = DocumentoSupport.PorRuta(docs, full);
            if (existente != null)
            {
                if (p.SoloLectura && !existente.IsReadOnly)
                {
                    warnings.Add("Ya estaba abierto en escritura; no se reabrió en solo lectura.");
                }
                else if (!p.SoloLectura && existente.IsReadOnly)
                {
                    warnings.Add("Ya estaba abierto en solo lectura; no se reabrió en escritura.");
                }

                bool activo = ReferenceEquals(docs.MdiActiveDocument, existente);
                if (p.Activar && !activo)
                {
                    activo = DocumentoSupport.Activar(docs, existente);
                    if (!activo)
                    {
                        warnings.Add("No se pudo activar el documento: AutoCAD no permite cambiar la activación en este momento.");
                    }
                }

                return Build(ctx, false, true, true, full, existente, activo, nombrePrevio, warnings, docs);
            }

            // Fail-closed: AutoCAD ejecutaría estos archivos al abrir el dibujo sin pasar por el diálogo de aprobación de LISP.
            List<string> autoCarga = DocumentoSupport.AutoCargadosEn(full);
            if (autoCarga.Count > 0)
            {
                throw ToolException.InvalidParams(
                    "ruta: la carpeta del dibujo contiene archivos de carga automática (" + string.Join(", ", Nombres(autoCarga))
                    + "). AutoCAD los ejecutaría al abrir sin la aprobación del usuario; no se abre desde el puente. Ábralo a mano.",
                    new Dictionary<string, object?> { ["ruta"] = full, ["archivos"] = Nombres(autoCarga) });
            }

            string? bloqueo = DocumentoSupport.BloqueoDe(full);
            if (bloqueo != null)
            {
                throw ToolException.InvalidParams(
                    "ruta: el dibujo tiene un archivo de bloqueo (" + Path.GetFileName(bloqueo) + "): otro usuario lo tiene abierto "
                    + "o una sesión anterior no lo cerró bien. AutoCAD pediría una decisión en un diálogo; no se abre.",
                    new Dictionary<string, object?> { ["ruta"] = full, ["bloqueo"] = bloqueo });
            }

            if (!p.SoloLectura && !p.Confirmar)
            {
                warnings.Add("Abrir en escritura crea el bloqueo " + Path.GetFileName(Path.ChangeExtension(full, ".dwl"))
                    + " que ven los demás usuarios de la red hasta cerrar el dibujo. Repita con confirmar: true, o use solo_lectura: true.");
                return Build(ctx, true, false, false, full, null, false, nombrePrevio, warnings, docs);
            }

            if (!p.SoloLectura)
            {
                PluginLog.Info("abrir_documento: abriendo en escritura '" + full + "' (confirmado).");
            }

            Document nuevo;
            try
            {
                nuevo = docs.Open(full, p.SoloLectura);
            }
            catch (Exception ex)
            {
                PluginLog.Error("abrir_documento: no se pudo abrir '" + full + "'", ex);
                throw new ToolException(
                    ErrorCodes.Internal,
                    "No se pudo abrir el dibujo.",
                    new Dictionary<string, object?> { ["ruta"] = full, ["detalle"] = ex.GetType().Name + ": " + ex.Message });
            }

            bool nuevoActivo;
            if (p.Activar)
            {
                nuevoActivo = DocumentoSupport.Activar(docs, nuevo);
                if (!nuevoActivo)
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

                nuevoActivo = ReferenceEquals(docs.MdiActiveDocument, nuevo);
                if (previo != null && !ReferenceEquals(docs.MdiActiveDocument, previo))
                {
                    warnings.Add("No se pudo restaurar el documento activo anterior ('" + nombrePrevio + "'); el activo ahora es '"
                        + Path.GetFileName(docs.MdiActiveDocument?.Name ?? "") + "'. Revise estado antes de escribir.");
                }
            }

            PluginLog.Info("abrir_documento: '" + full + "' abierto (solo_lectura=" + p.SoloLectura + ", activar=" + p.Activar + ").");
            return Build(ctx, false, true, false, full, nuevo, nuevoActivo, nombrePrevio, warnings, docs);
        }

        private static List<string> Nombres(IEnumerable<string> rutas)
        {
            var nombres = new List<string>();
            foreach (string r in rutas)
            {
                nombres.Add(Path.GetFileName(r));
            }

            return nombres;
        }

        private static Dictionary<string, object?> Build(
            ExecutionContext ctx, bool preview, bool abierto, bool yaAbierto, string ruta, Document? doc, bool activo,
            string? activoAnterior, List<string> warnings, DocumentCollection docs)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = ctx.Pid,
                ["preview"] = preview,
                ["abierto"] = abierto,
                ["ya_abierto"] = yaAbierto,
                ["ruta"] = ruta,
                ["nombre"] = Path.GetFileName(ruta),
                ["solo_lectura"] = doc?.IsReadOnly ?? false,
                ["activo"] = activo,
                ["activo_anterior"] = activoAnterior,
                ["documentos"] = DocumentoSupport.Filas(DocumentoSupport.Describir(DocumentoSupport.Abiertos(docs), docs.MdiActiveDocument)),
                ["advertencias"] = warnings,
            };
        }
    }
}
