using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Logging;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Biblioteca de bloques de insertar_bloque (protocol.md §5D.5, §9). Carpeta: la clave opcional
    /// <c>biblioteca_bloques</c> de lps.json (absoluta o relativa a la carpeta de la DLL) si existe y es carpeta; si no,
    /// <c>&lt;carpeta de la DLL&gt;\biblioteca</c>. El cliente solo da NOMBRES ya validados por
    /// <see cref="BlockNameRules"/>; la ruta se construye siempre con <see cref="BlockNameRules.LibraryFile"/>.
    /// La importación usa una base de datos lateral que se descarta SIEMPRE (Dispose en finally); nunca se guarda.
    /// </summary>
    internal static class BlockLibrary
    {
        /// <summary>Clave opcional de lps.json con la carpeta de la biblioteca.</summary>
        public const string LpsKey = "biblioteca_bloques";

        private static readonly Lazy<string> FolderLazy = new Lazy<string>(ResolveFolder, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Carpeta efectiva de la biblioteca (se resuelve una vez; lps.json se lee una vez al cargar).</summary>
        public static string Folder => FolderLazy.Value;

        /// <summary>
        /// Ruta del DWG de la biblioteca para <paramref name="nombre"/> (ya validado), con las mayúsculas reales del archivo,
        /// o null si no existe. Nombre inseguro → INVALID_PARAMS (defensa adicional; Parse ya lo rechaza).
        /// </summary>
        public static string? FindFile(string nombre)
        {
            string candidate;
            try
            {
                candidate = BlockNameRules.LibraryFile(Folder, nombre);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            try
            {
                if (!File.Exists(candidate))
                {
                    return null;
                }

                // Mismas mayúsculas que en disco (el nombre del bloque importado será el del archivo).
                string wanted = Path.GetFileName(candidate);
                foreach (string f in Directory.EnumerateFiles(Folder, "*" + BlockNameRules.FileExtension, SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(Path.GetFileName(f), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return f;
                    }
                }

                return candidate;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PluginLog.Warn("Biblioteca de bloques: no se pudo consultar '" + candidate + "' (" + ex.GetType().Name + ": " + ex.Message + ")");
                return null;
            }
        }

        /// <summary>
        /// Importa el espacio modelo de <paramref name="ruta"/> como la definición <paramref name="nombre"/> en
        /// <paramref name="db"/>, dentro de la transacción de escritura <paramref name="tr"/> (mismo paso de deshacer).
        /// Devuelve el ObjectId del BlockTableRecord creado, el INSUNITS del archivo y las capas que la importación
        /// añadió al dibujo. DWG ilegible → INTERNAL con el detalle de AutoCAD.
        /// Llamar ANTES de abrir nada en <paramref name="tr"/> (ni la LayerTable ni la BlockTable): Database.Insert abre
        /// y escribe las tablas por su cuenta (clonación profunda) y no debe encontrarlas abiertas por la transacción.
        /// Las capas previas se leen con una StartOpenCloseTransaction propia que se cierra antes de importar.
        /// <paramref name="validarAntesDeInsertar"/> (opcional) se ejecuta sobre la base lateral recién leída y ANTES de
        /// Database.Insert: Database.Insert no se revierte con el Abort de la transacción (corrección 0.4.0), así que
        /// toda validación que dependa del contenido del archivo debe lanzar aquí, no después.
        /// </summary>
        public static ObjectId Import(
            Database db, Transaction tr, string nombre, string ruta, Action<Database>? validarAntesDeInsertar,
            out int unidadesArchivo, out List<string> capasImportadas)
        {
            HashSet<string> before;
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                before = new HashSet<string>(LayerNamesOrdered(db, ro), StringComparer.OrdinalIgnoreCase);
                ro.Commit();
            }

            Database? side = null;
            ObjectId btrId;
            try
            {
                side = ReadSide(ruta);
                validarAntesDeInsertar?.Invoke(side);
                unidadesArchivo = (int)side.Insunits;
                try
                {
                    // preserveSourceDatabase: true → la base lateral queda intacta (y se descarta igualmente).
                    btrId = db.Insert(nombre, side, true);
                }
                catch (AcRuntimeException ex)
                {
                    throw new ToolException(
                        ErrorCodes.Internal,
                        "No se pudo importar el bloque de la biblioteca: " + Path.GetFileName(ruta) + ".",
                        new Dictionary<string, object?> { ["detalle"] = "Database.Insert: " + ex.ErrorStatus + ": " + ex.Message, ["archivo"] = ruta });
                }
            }
            finally
            {
                // §5D.5: Dispose SIEMPRE; nunca se guarda la base lateral.
                side?.Dispose();
            }

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (btrId.IsNull || !bt.Has(nombre))
            {
                throw new ToolException(
                    ErrorCodes.Internal,
                    "No se pudo importar el bloque de la biblioteca: " + Path.GetFileName(ruta) + ".",
                    new Dictionary<string, object?> { ["detalle"] = "Database.Insert no creó la definición '" + nombre + "'", ["archivo"] = ruta });
            }

            ObjectId importedId = bt[nombre];

            // §5D.5 (corrección del gate en vivo de Fase 3): Database.Insert NO copia el INSUNITS del archivo a la
            // definición creada (BlockTableRecord.Units quedaba en 0 = sin unidades), y una inserción posterior sin
            // desde_biblioteca no podía aplicar el factor de unidades. Se fija aquí, en la MISMA transacción de escritura
            // (mismo paso de deshacer; el Abort lo revierte), solo con un INSUNITS válido distinto de 0.
            StampUnits(tr, importedId, unidadesArchivo, nombre);

            capasImportadas = new List<string>();
            foreach (string name in LayerNamesOrdered(db, tr))
            {
                if (!before.Contains(name))
                {
                    capasImportadas.Add(name);
                }
            }

            return importedId;
        }

        /// <summary>
        /// Fija BlockTableRecord.Units = <paramref name="unidadesArchivo"/> en la definición importada si es un INSUNITS
        /// válido (1..24) y distinto del actual. 0 (sin unidades) o fuera de rango → no se toca. Dentro de <paramref name="tr"/>.
        /// </summary>
        private static void StampUnits(Transaction tr, ObjectId btrId, int unidadesArchivo, string nombre)
        {
            if (unidadesArchivo == 0 || !UnitsTable.IsInRange(unidadesArchivo))
            {
                PluginLog.Info("Biblioteca de bloques: '" + nombre + "' sin unidades válidas en el archivo (INSUNITS="
                    + unidadesArchivo.ToString(System.Globalization.CultureInfo.InvariantCulture) + "); no se fijan las unidades de la definición.");
                return;
            }

            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            int previas = (int)btr.Units;
            if (previas == unidadesArchivo)
            {
                return;
            }

            btr.UpgradeOpen();
            btr.Units = (UnitsValue)unidadesArchivo;
            PluginLog.Info("Biblioteca de bloques: '" + nombre + "' importado; BlockTableRecord.Units "
                + previas.ToString(System.Globalization.CultureInfo.InvariantCulture) + " → "
                + unidadesArchivo.ToString(System.Globalization.CultureInfo.InvariantCulture) + " (INSUNITS del archivo).");
        }

        /// <summary>
        /// Lee el DWG de la biblioteca en una base lateral (en memoria; el archivo se suelta con CloseInput). El llamador
        /// la descarta SIEMPRE con Dispose y nunca la guarda. DWG ilegible → INTERNAL con el detalle de AutoCAD.
        /// </summary>
        public static Database ReadSide(string ruta)
        {
            var side = new Database(false, true);
            try
            {
                side.ReadDwgFile(ruta, FileOpenMode.OpenForReadAndAllShare, true, "");
                // Carga todo en memoria y suelta el archivo.
                side.CloseInput(true);
                return side;
            }
            catch (AcRuntimeException ex)
            {
                side.Dispose();
                throw new ToolException(
                    ErrorCodes.Internal,
                    "No se pudo leer el bloque de la biblioteca: " + Path.GetFileName(ruta) + ".",
                    new Dictionary<string, object?> { ["detalle"] = "ReadDwgFile: " + ex.ErrorStatus + ": " + ex.Message, ["archivo"] = ruta });
            }
            catch
            {
                side.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Etiquetas de los ATTDEF NO constantes del espacio modelo de la base lateral (lo que Database.Insert convierte
        /// en la definición), en su orden. Lectura pura; no modifica <paramref name="side"/>.
        /// </summary>
        public static List<string> ReadAttributeTags(Database side)
        {
            var tags = new List<string>();
            using (Transaction tr = side.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(side.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                    {
                        tags.Add(ad.Tag);
                    }
                }

                tr.Commit();
            }

            return tags;
        }

        /// <summary>
        /// Comprueba con <paramref name="comprobar"/> la capa <paramref name="nombre"/> de la base lateral, si existe
        /// (sin mayúsculas). Para la capa destino que aún no existe en el dibujo: si el archivo trae una capa con ese
        /// nombre, Database.Insert la traerá con SUS propiedades (bloqueada, congelada…) y será esa la que encuentre
        /// DrawLayerFactory.Ensure después de importar. Criterio conservador: se comprueba aunque la geometría del
        /// archivo no la use (Insert solo clona las capas referenciadas). false si el archivo no tiene esa capa.
        /// </summary>
        public static bool CheckSideLayer(Database side, string nombre, Action<LayerTableRecord> comprobar)
        {
            using (Transaction tr = side.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (LayerTable)tr.GetObject(side.LayerTableId, OpenMode.ForRead);
                ObjectId id = McpLayerFactory.FindLayer(table, nombre);
                if (id.IsNull)
                {
                    tr.Commit();
                    return false;
                }

                comprobar((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead));
                tr.Commit();
                return true;
            }
        }

        private static List<string> LayerNamesOrdered(Database db, Transaction tr)
        {
            var names = new List<string>();
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                try
                {
                    names.Add(((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
                }
                catch (AcRuntimeException)
                {
                    // Registro ilegible: no cuenta.
                }
            }

            return names;
        }

        /// <summary>lps.json 'biblioteca_bloques' (si existe la carpeta) o &lt;carpeta de la DLL&gt;\biblioteca.</summary>
        private static string ResolveFolder()
        {
            string dllDir = Path.GetDirectoryName(typeof(PluginApplication).Assembly.Location) ?? AppContext.BaseDirectory;
            string fallback = Path.Combine(dllDir, PluginInfo.LibraryFolder);
            try
            {
                JsonElement? root = LpsStandard.Root;
                if (root is JsonElement r
                    && r.ValueKind == JsonValueKind.Object
                    && r.TryGetProperty(LpsKey, out JsonElement el))
                {
                    string? configured = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(configured))
                    {
                        string full = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(dllDir, configured));
                        if (Directory.Exists(full))
                        {
                            PluginLog.Info("Biblioteca de bloques (lps.json " + LpsKey + "): " + full);
                            return full;
                        }

                        PluginLog.Warn("Biblioteca de bloques: la carpeta de lps.json " + LpsKey + " no existe (" + full + "); se usa " + fallback);
                    }
                    else
                    {
                        PluginLog.Warn("Biblioteca de bloques: lps.json " + LpsKey + " no es una ruta válida; se usa " + fallback);
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException || ex is System.Security.SecurityException)
            {
                PluginLog.Warn("Biblioteca de bloques: ruta de lps.json inválida (" + ex.Message + "); se usa " + fallback);
            }

            return fallback;
        }
    }
}
