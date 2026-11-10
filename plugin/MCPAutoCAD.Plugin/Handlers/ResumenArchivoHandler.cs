using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using ErrorStatus = Autodesk.AutoCAD.Runtime.ErrorStatus;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// resumen_archivo (protocol.md §5B.8). Hilo principal; NO requiere documento; solo lectura.
    /// Abre el DWG en una base de datos lateral (side database), nunca lo guarda y la libera siempre.
    /// HostApplicationServices.WorkingDatabase NO se cambia: ninguna de las lecturas de aquí lo exige
    /// (tablas, BlockTableRecord, ObjectId.ObjectClass, Layout.TabOrder/LayoutName, Insunits/Measurement).
    /// </summary>
    internal sealed class ResumenArchivoHandler : IToolHandler
    {
        private const int MaxPathLength = 32767;
        private const int TopBlocks = 20;

        /// <summary>Texto exacto de §5B.8 cuando el archivo también está abierto en esta sesión.</summary>
        internal const string OpenInSessionWarning =
            "El archivo está abierto en esta sesión de AutoCAD; se leyó la copia en disco, no los cambios sin guardar.";

        private static readonly string[] Allowed = { "ruta" };
        private static readonly Regex AcVersionName = new Regex(@"^AC\d{4}$", RegexOptions.CultureInvariant);

        public string Name => "resumen_archivo";

        public string Description =>
            "Resume un archivo DWG cerrado leyéndolo del disco sin abrirlo en pantalla ni modificarlo (nunca se guarda): " +
            "versión, unidades, número de capas, entidades del modelo por tipo, bloques más usados, layouts y xrefs. " +
            "Recomendado sobre copias de los planos, no sobre los originales. Funciona sin documentos abiertos.";

        public bool RequiresDocument => false;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 32767, ""description"": ""Ruta absoluta del archivo .dwg a resumir (de preferencia una copia)."" }
  },
  ""required"": [""ruta""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""ruta"": { ""type"": ""string"" },
    ""nombre"": { ""type"": ""string"" },
    ""tamano_bytes"": { ""type"": ""integer"" },
    ""modificado"": { ""type"": ""string"" },
    ""version_dwg"": { ""type"": ""string"" },
    ""abierto_en_sesion"": { ""type"": ""boolean"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""measurement"": { ""type"": ""integer"" },
    ""n_capas"": { ""type"": ""integer"" },
    ""entidades_modelo"": {
      ""type"": ""object"",
      ""properties"": {
        ""total"": { ""type"": ""integer"" },
        ""por_tipo"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } }
      }
    },
    ""n_bloques"": { ""type"": ""integer"" },
    ""bloques_mas_usados"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""object"", ""properties"": { ""nombre"": { ""type"": ""string"" }, ""n_inserciones"": { ""type"": ""integer"" } } }
    },
    ""layouts"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""n_xrefs"": { ""type"": ""integer"" },
    ""origen_texto"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""ruta"", ""nombre"", ""tamano_bytes"", ""modificado"", ""version_dwg"", ""abierto_en_sesion"", ""insunits"", ""unidades"", ""measurement"", ""n_capas"", ""entidades_modelo"", ""n_bloques"", ""bloques_mas_usados"", ""layouts"", ""n_xrefs"", ""origen_texto"", ""advertencias""]
}");

        /// <summary>Hilo del pipe: SIN API de AutoCAD. Mensajes exactos de §5B.8.</summary>
        public void ValidateParams(JsonElement parameters)
        {
            ParamReader.RequireObject(parameters, Allowed);
            ReadPath(parameters);
        }

        private static string ReadPath(JsonElement parameters)
        {
            if (!ParamReader.Has(parameters, "ruta"))
            {
                throw ToolException.InvalidParams("ruta: obligatorio");
            }

            string path = ParamReader.OptionalString(parameters, "ruta", 1)!;
            if (path.Length > MaxPathLength)
            {
                throw ToolException.InvalidParams("ruta: debe tener como máximo 32767 caracteres");
            }

            bool rooted;
            try
            {
                rooted = Path.IsPathRooted(path) && Path.IsPathFullyQualified(path);
            }
            catch (ArgumentException)
            {
                rooted = false;
            }

            if (!rooted)
            {
                throw ToolException.InvalidParams("ruta: debe ser absoluta");
            }

            if (!path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                throw ToolException.InvalidParams("ruta: debe terminar en .dwg");
            }

            if (!File.Exists(path))
            {
                throw ToolException.InvalidParams("ruta: el archivo no existe");
            }

            return path;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            string path = ReadPath(parameters);
            var file = new FileInfo(path);
            long size = file.Length;
            string modified = new DateTimeOffset(file.LastWriteTime).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
            bool openInSession = IsOpenInSession(path);

            Database? db = null;
            try
            {
                // Base de datos lateral: sin dibujo por defecto, sin documento asociado.
                db = new Database(false, true);
                try
                {
                    db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                    // Carga todo en memoria y suelta el archivo (no queda bloqueado para otros).
                    db.CloseInput(true);
                }
                catch (AcRuntimeException ex)
                {
                    throw ReadError(ex);
                }

                Summary summary;
                try
                {
                    summary = Summarize(db);
                }
                catch (AcRuntimeException ex)
                {
                    PluginLog.Error("resumen_archivo: excepción de AutoCAD leyendo '" + path + "' (" + ex.ErrorStatus + ")", ex);
                    throw new ToolException(
                        ErrorCodes.Internal,
                        "Error interno del plugin de AutoCAD.",
                        new Dictionary<string, object?> { ["detalle"] = Describe(ex) });
                }

                int ins = AcadUtil.InsUnits(db);
                // §2.1 + §5.2 + §2.2 sobre el archivo leído (AcadUtil añade la de LPS cuando esté disponible).
                List<string> warnings = AcadUtil.UnitWarnings(db);
                if (openInSession)
                {
                    warnings.Add(OpenInSessionWarning);
                }

                return new
                {
                    pid = ctx.Pid,
                    ruta = path,
                    nombre = Path.GetFileName(path),
                    tamano_bytes = size,
                    modificado = modified,
                    version_dwg = VersionName(db.OriginalFileVersion),
                    abierto_en_sesion = openInSession,
                    insunits = ins,
                    unidades = UnitsTable.Name(ins),
                    measurement = AcadUtil.Measurement(db),
                    n_capas = summary.LayerCount,
                    entidades_modelo = new { total = summary.ModelTotal, por_tipo = summary.ByType },
                    n_bloques = summary.BlockCount,
                    bloques_mas_usados = summary.TopBlocks,
                    layouts = summary.Layouts,
                    n_xrefs = summary.XrefCount,
                    origen_texto = "contenido_del_plano",
                    advertencias = warnings,
                };
            }
            finally
            {
                // §5B.8: Dispose SIEMPRE; nunca Save/SaveAs.
                db?.Dispose();
            }
        }

        internal static Summary Summarize(Database db)
        {
            var summary = new Summary();
            using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();

            // n_capas: capas no borradas (incluidas las dependientes de xref).
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in layers)
            {
                if (!id.IsErased)
                {
                    summary.LayerCount++;
                }
            }

            var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            // entidades_modelo: entidades no borradas de *Model_Space por DxfName (sin abrirlas: ObjectId.ObjectClass
            // es la misma RXClass que Entity.GetRXClass()).
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var ms = (BlockTableRecord)tr.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (id.IsErased)
                {
                    continue;
                }

                string type = TypeName(id);
                counts.TryGetValue(type, out int c);
                counts[type] = c + 1;
                summary.ModelTotal++;
            }

            var byType = new List<KeyValuePair<string, int>>(counts);
            byType.Sort(CompareCountDescThenName);
            foreach (KeyValuePair<string, int> kv in byType)
            {
                summary.ByType.Add(kv.Key, kv.Value);
            }

            // Bloques: definiciones no anónimas, no layout, no xref; xrefs contadas aparte.
            var usage = new List<KeyValuePair<string, int>>();
            foreach (ObjectId id in blocks)
            {
                if (id.IsErased)
                {
                    continue;
                }

                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsFromExternalReference)
                {
                    summary.XrefCount++;
                    continue;
                }

                if (btr.IsLayout || btr.IsAnonymous || btr.Name.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                summary.BlockCount++;
                usage.Add(new KeyValuePair<string, int>(btr.Name, CountInsertions(btr, tr)));
            }

            usage.Sort(CompareCountDescThenName);
            for (int i = 0; i < usage.Count && i < TopBlocks; i++)
            {
                summary.TopBlocks.Add(new BlockUsage(usage[i].Key, usage[i].Value));
            }

            // layouts: sin Model, por TabOrder.
            var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var layouts = new List<(int order, string name)>();
            foreach (DBDictionaryEntry entry in layoutDict)
            {
                if (entry.Value.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType)
                {
                    layouts.Add((layout.TabOrder, layout.LayoutName));
                }
            }

            layouts.Sort((a, b) =>
            {
                int byOrder = a.order.CompareTo(b.order);
                return byOrder != 0 ? byOrder : StringComparer.OrdinalIgnoreCase.Compare(a.name, b.name);
            });
            foreach ((int _, string name) in layouts)
            {
                summary.Layouts.Add(name);
            }

            tr.Commit();
            return summary;
        }

        /// <summary>n_inserciones como §5B.4: directas no borradas + las de los anónimos derivados (dinámicos).</summary>
        private static int CountInsertions(BlockTableRecord btr, Transaction tr)
        {
            int n = CountLive(btr.GetBlockReferenceIds(true, false));
            ObjectIdCollection anon = btr.GetAnonymousBlockIds();
            foreach (ObjectId anonId in anon)
            {
                if (anonId.IsErased)
                {
                    continue;
                }

                var anonBtr = (BlockTableRecord)tr.GetObject(anonId, OpenMode.ForRead);
                n += CountLive(anonBtr.GetBlockReferenceIds(true, false));
            }

            return n;
        }

        private static int CountLive(ObjectIdCollection ids)
        {
            int n = 0;
            foreach (ObjectId id in ids)
            {
                if (!id.IsErased)
                {
                    n++;
                }
            }

            return n;
        }

        private static string TypeName(ObjectId id)
        {
            Autodesk.AutoCAD.Runtime.RXClass? cls = id.ObjectClass;
            string name = cls?.DxfName ?? "";
            if (name.Length == 0)
            {
                name = cls?.Name ?? "DESCONOCIDO";
            }

            return name.ToUpperInvariant();
        }

        private static int CompareCountDescThenName(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
        {
            int byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
        }

        /// <summary>
        /// version_dwg (§5B.8): se usa Database.OriginalFileVersion. Su ToString() NO sirve tal cual: en AutoCAD 2026
        /// AC1032 = Current = Newest (mismo valor 33) y Enum.ToString puede devolver "Current". Por eso se elige el
        /// nombre con forma "AC" + 4 dígitos que tenga ese valor; si no hay, el ToString().
        /// </summary>
        internal static string VersionName(DwgVersion version)
        {
            foreach (string name in Enum.GetNames(typeof(DwgVersion)))
            {
                if (AcVersionName.IsMatch(name) && (DwgVersion)Enum.Parse(typeof(DwgVersion), name) == version)
                {
                    return name;
                }
            }

            return version.ToString();
        }

        /// <summary>abierto_en_sesion: algún Document.Name coincide con la ruta (sin mayúsculas, rutas normalizadas).</summary>
        internal static bool IsOpenInSession(string path)
        {
            string full = Normalize(path);
            foreach (Document d in AcApp.DocumentManager)
            {
                string? name = d.Name;
                if (!string.IsNullOrEmpty(name) && string.Equals(Normalize(name), full, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        internal static ToolException ReadError(AcRuntimeException ex, string tool = "resumen_archivo")
        {
            PluginLog.Error(tool + ": ReadDwgFile falló (" + ex.ErrorStatus + ")", ex);
            string detail = IsPasswordError(ex.ErrorStatus) ? "archivo protegido con contraseña" : Describe(ex);
            return new ToolException(
                ErrorCodes.Internal,
                "No se pudo leer el archivo DWG.",
                new Dictionary<string, object?> { ["detalle"] = detail });
        }

        /// <summary>
        /// Estados de AutoCAD 2026 que corresponden a un DWG cifrado leído con contraseña vacía. No hay un
        /// "eWrongPassword" en la API .NET; se usan los errores de seguridad de descifrado. Validar en el gate.
        /// </summary>
        private static bool IsPasswordError(ErrorStatus status) =>
            status == ErrorStatus.SecErrorDecryptingData || status == ErrorStatus.SecErrorCipherNotSupported;

        private static string Describe(AcRuntimeException ex) =>
            ex.GetType().FullName + " (" + ex.ErrorStatus + "): " + ex.Message;

        internal sealed class Summary
        {
            public int LayerCount { get; set; }

            public int ModelTotal { get; set; }

            /// <summary>Insertado en orden (conteo desc, nombre): System.Text.Json conserva el orden del diccionario.</summary>
            public Dictionary<string, int> ByType { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            public int BlockCount { get; set; }

            public List<BlockUsage> TopBlocks { get; } = new List<BlockUsage>();

            public List<string> Layouts { get; } = new List<string>();

            public int XrefCount { get; set; }
        }

        internal sealed class BlockUsage
        {
            public BlockUsage(string name, int count)
            {
                nombre = name;
                n_inserciones = count;
            }

#pragma warning disable IDE1006 // Nombres en minúsculas: son los campos JSON del contrato.
            public string nombre { get; }

            public int n_inserciones { get; }
#pragma warning restore IDE1006
        }
    }
}
