using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;
using MCPAutoCAD.Plugin.Logging;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// guardar_copia (0.8.1). Hilo principal; requiere documento; no cambia el dibujo abierto.
    /// Flujo: (1) validación de forma de la ruta (<see cref="ExportRules.ValidatePath"/> sin carpetas) en el hilo del pipe;
    /// (2) en Execute, carpetas de lps.json (<see cref="InsertarExportHandler.ReadAllowedFolders"/>, la misma regla que
    /// insertar_export) y comprobación de carpeta, de que la ruta no es la de un documento abierto y de si ya existe;
    /// (3) <c>Database.Wblock()</c> clona el dibujo ENTERO desde memoria (incluye los cambios sin guardar) y solo el CLON se
    /// guarda con <c>SaveAs(ruta, DwgVersion.Current)</c>, que no cambia el Database del documento: su nombre, Filename,
    /// estado de modificado y el documento siguen igual. Nunca se llama SaveAs sobre el Database vivo.
    /// Sin <see cref="UndoGroup"/>: no hay escritura en el dibujo. Escritura en disco → escritura: true (no idempotente).
    /// </summary>
    internal sealed class GuardarCopiaHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "ruta_destino", "sobrescribir", "solo_seleccion" };

        private const string WblockWarning =
            "La copia se hace con Database.Wblock(), que no garantiza conservar las definiciones no usadas (capas, estilos, tipos de línea): compare la copia con el original si las necesita.";

        public string Name => "guardar_copia";

        public string Description =>
            "ESCRIBE un .dwg en disco con una COPIA del documento activo, incluidos sus cambios sin guardar; el dibujo abierto no cambia de nombre, de ruta ni de estado de modificado (solo se guarda el clon). " +
            "Rechaza la ruta de un documento abierto, una ruta existente (salvo sobrescribir: true), carpetas fuera de carpetas_export_permitidas y dentro de carpetas_solo_lectura de lps.json. Con solo_seleccion: true guarda solo lo seleccionado ahora en AutoCAD (como WBLOCK de la selección; el dibujo abierto tampoco cambia). No crea paso de deshacer.";

        public bool RequiresDocument => true;

        /// <summary>Escribe en disco (no idempotente para el servidor), aunque no toca el dibujo.</summary>
        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        /// <summary>Guardar un dibujo grande puede tardar más que el plazo general.</summary>
        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta_destino"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta (unidad o UNC) del .dwg de destino, sin '..' ni comodines. Si lps.json define carpetas_export_permitidas, debe estar dentro de una. No puede ser la ruta de un documento abierto."" },
    ""sobrescribir"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = reemplaza el archivo si ya existe; por defecto se rechaza con INVALID_PARAMS."" },
    ""solo_seleccion"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = la copia contiene solo la selección actual del usuario (punto base 0,0,0, mismas coordenadas); selección vacía → INVALID_PARAMS."" }
  },
  ""required"": [""ruta_destino""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""ruta"": { ""type"": ""string"" },
    ""tamano_bytes"": { ""type"": ""integer"" },
    ""sobrescrito"": { ""type"": ""boolean"" },
    ""solo_seleccion"": { ""type"": ""boolean"" },
    ""n_seleccion"": { ""type"": [""integer"", ""null""] },
    ""entidades_modelo"": { ""type"": ""object"", ""properties"": {
      ""copia"": { ""type"": ""integer"" },
      ""dibujo"": { ""type"": ""integer"" } } },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""ruta"", ""tamano_bytes"", ""sobrescrito"", ""entidades_modelo"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Params
        {
            public Params(string ruta, bool sobrescribir, bool soloSeleccion)
            {
                Ruta = ruta;
                Sobrescribir = sobrescribir;
                SoloSeleccion = soloSeleccion;
            }

            public bool SoloSeleccion { get; }

            public string Ruta { get; }

            public bool Sobrescribir { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string ruta = ParamReader.RequiredText(p, "ruta_destino", 1, 1000);
            bool sobrescribir = ParamReader.OptionalBool(p, "sobrescribir") ?? false;
            string? forma = ExportRules.ValidatePath(ruta, null);
            if (forma != null)
            {
                throw ToolException.InvalidParams(forma);
            }

            bool soloSeleccion = ParamReader.OptionalBool(p, "solo_seleccion") ?? false;
            return new Params(ruta, sobrescribir, soloSeleccion);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            var warnings = new List<string>();
            int ins = AcadUtil.InsUnits(db);

            // Pasos 2–4: todo se comprueba antes de crear nada.
            List<string>? folders = InsertarExportHandler.ReadAllowedFolders(warnings);
            string? carpeta = ExportRules.ValidatePath(p.Ruta, folders);
            if (carpeta != null)
            {
                throw ToolException.InvalidParams(carpeta, new Dictionary<string, object?> { ["ruta_destino"] = p.Ruta });
            }

            string full = Path.GetFullPath(p.Ruta);

            // carpetas_solo_lectura de lps.json (decisión del usuario 2026-11-10: O:\, planos de clientes): nunca se escribe ahí.
            // También la carpeta (existe): resuelve uniones/SUBST que la ruta del archivo nuevo, aún inexistente, no resuelve.
            List<string> soloLectura = Documento.DocumentoSupport.CarpetasSoloLectura();
            string? carpetaDestino = Path.GetDirectoryName(full);
            if (Documento.DocumentoSupport.EnCarpetaSoloLectura(full, soloLectura)
                || (carpetaDestino != null && Directory.Exists(carpetaDestino) && Documento.DocumentoSupport.EnCarpetaSoloLectura(carpetaDestino, soloLectura)))
            {
                throw ToolException.InvalidParams(
                    "ruta_destino: está en una carpeta de carpetas_solo_lectura de lps.json; guarde la copia en otra carpeta",
                    new Dictionary<string, object?> { ["ruta_destino"] = p.Ruta });
            }

            foreach (Document other in Application.DocumentManager)
            {
                if (SamePath(full, other.Database.Filename) || SamePath(full, other.Name))
                {
                    throw ToolException.InvalidParams(
                        "ruta_destino: es la ruta de un documento abierto; guardar_copia no escribe sobre dibujos abiertos",
                        new Dictionary<string, object?> { ["ruta_destino"] = p.Ruta });
                }
            }

            string? dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                throw ToolException.InvalidParams("ruta_destino: la carpeta no existe", new Dictionary<string, object?> { ["ruta_destino"] = p.Ruta });
            }

            bool existed = File.Exists(full);
            if (existed && !p.Sobrescribir)
            {
                throw ToolException.InvalidParams(
                    "ruta_destino: el archivo ya existe; pase sobrescribir: true para reemplazarlo",
                    new Dictionary<string, object?> { ["ruta_destino"] = p.Ruta });
            }

            // solo_seleccion: la selección se lee en contexto de aplicación y se restaura enseguida.
            ObjectIdCollection? seleccion = null;
            if (p.SoloSeleccion)
            {
                seleccion = ReadSelection(doc, warnings);
            }

            // Paso 5: la copia. Ni el Database vivo ni el documento se guardan; solo el clon.
            int dibujo;
            int copia;
            using (doc.LockDocument())
            {
                dibujo = CountModelSpace(db);
                try
                {
                    using (Database clone = seleccion == null ? db.Wblock() : db.Wblock(seleccion, Point3d.Origin))
                    {
                        copia = CountModelSpace(clone);
                        clone.SaveAs(full, DwgVersion.Current);
                    }
                }
                catch (Exception ex) when (ex is AcRuntimeException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    PluginLog.Error("guardar_copia: no se pudo escribir '" + full + "'", ex);
                    if (!existed && File.Exists(full))
                    {
                        TryDelete(full);   // no dejar un DWG a medias que parezca válido
                    }

                    throw new ToolException(
                        ErrorCodes.Internal,
                        "No se pudo guardar la copia en la ruta indicada.",
                        new Dictionary<string, object?> { ["ruta_destino"] = p.Ruta, ["detalle"] = ex.GetType().Name + ": " + ex.Message });
                }
            }

            long size = new FileInfo(full).Length;
            int? nSeleccion = seleccion?.Count;
            if (nSeleccion != null)
            {
                if (copia != nSeleccion)
                {
                    warnings.Add("La copia tiene " + copia + " entidades en el modelo y la selección " + nSeleccion + ": revise la copia antes de usarla.");
                }
            }
            else if (copia != dibujo)
            {
                warnings.Add("La copia tiene " + copia + " entidades en el modelo y el dibujo abierto " + dibujo + ": revise la copia antes de usarla.");
            }

            warnings.Add(WblockWarning);
            PluginLog.Info("guardar_copia: '" + full + "' escrito, " + size + " bytes, " + copia + " entidades en el modelo.");

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = ctx.Pid,
                ["ruta"] = full,
                ["tamano_bytes"] = size,
                ["sobrescrito"] = existed,
                ["solo_seleccion"] = p.SoloSeleccion,
                ["n_seleccion"] = nSeleccion,
                ["entidades_modelo"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["copia"] = copia, ["dibujo"] = dibujo },
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = warnings,
            };
        }

        /// <summary>
        /// Selección implícita como ObjectIdCollection para Database.Wblock(ids, base). Vacía → INVALID_PARAMS. Wblock exige
        /// que todas las entidades tengan el mismo propietario: si la selección mezcla espacios → INVALID_PARAMS.
        /// </summary>
        private static ObjectIdCollection ReadSelection(Document doc, List<string> warnings)
        {
            List<ObjectId> ids = SelectionReader.ReadImplied(doc.Editor);
            SelectionReader.Restore(doc.Editor, ids, warnings);
            if (ids.Count == 0)
            {
                throw ToolException.InvalidParams("solo_seleccion: no hay selección; seleccione en AutoCAD lo que quiere guardar");
            }

            var owners = new HashSet<ObjectId>();
            var entities = new List<ObjectId>(ids.Count);
            int discarded = 0;
            string? ownerName = null;
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity? e = Audit.AuditSupport.Open(tr, id);
                    if (e == null)
                    {
                        discarded++;
                        continue;
                    }

                    owners.Add(e.OwnerId);
                    entities.Add(id);
                }

                if (owners.Count == 1)
                {
                    foreach (ObjectId o in owners)
                    {
                        if (tr.GetObject(o, OpenMode.ForRead, false) is BlockTableRecord btr)
                        {
                            ownerName = SpaceResolver.SpaceName(btr, tr);
                        }
                    }
                }

                tr.Commit();
            }

            if (owners.Count > 1)
            {
                throw ToolException.InvalidParams("solo_seleccion: la selección mezcla entidades de varios espacios");
            }

            if (entities.Count == 0)
            {
                throw ToolException.InvalidParams("solo_seleccion: ninguna entidad seleccionada se pudo leer");
            }

            if (discarded > 0)
            {
                warnings.Add(discarded + " objeto(s) seleccionados no se pudieron leer como entidades y no van en la copia.");
            }

            if (ownerName != null && !string.Equals(ownerName, SpaceResolver.ModelName, StringComparison.Ordinal))
            {
                warnings.Add("La selección estaba en el layout '" + ownerName + "'; en la copia queda en el espacio modelo.");
            }

            return new ObjectIdCollection(entities.ToArray());
        }

        /// <summary>Entidades (no borradas) del espacio modelo de una base de datos; solo lectura.</summary>
        private static int CountModelSpace(Database database)
        {
            using (Transaction tr = database.TransactionManager.StartOpenCloseTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
                int n = space.Cast<ObjectId>().Count();
                tr.Commit();
                return n;
            }
        }

        /// <summary>true si ambas rutas apuntan al mismo archivo (comparación completa, sin mayúsculas). Rutas relativas o vacías → false.</summary>
        private static bool SamePath(string fullDestino, string? otra)
        {
            if (string.IsNullOrWhiteSpace(otra) || !Path.IsPathRooted(otra))
            {
                return false;
            }

            try
            {
                return string.Equals(fullDestino, Path.GetFullPath(otra), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                PluginLog.Warn("guardar_copia: no se pudo borrar la copia fallida '" + path + "' (" + ex.Message + ").");
            }
            catch (UnauthorizedAccessException ex)
            {
                PluginLog.Warn("guardar_copia: no se pudo borrar la copia fallida '" + path + "' (" + ex.Message + ").");
            }
        }
    }
}
