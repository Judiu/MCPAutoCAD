using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Resolución del parámetro <c>espacio</c> (protocol.md §5B.1) y del nombre del propietario de una entidad
    /// (§5B.2 <c>propietario</c>, §5B.4 <c>espacio</c>). Solo lectura; solo hilo principal.
    /// </summary>
    internal static class SpaceResolver
    {
        public const string ModelName = "modelo";
        public const string BlockPrefix = "bloque:";

        /// <summary>Espacio resuelto: registro de bloque del espacio y nombre de salida ("modelo" o nombre del layout).</summary>
        internal sealed class ResolvedSpace
        {
            public ResolvedSpace(BlockTableRecord btr, string name)
            {
                Btr = btr;
                Name = name;
            }

            public BlockTableRecord Btr { get; }

            public string Name { get; }
        }

        /// <summary>
        /// <paramref name="espacio"/> null → Database.CurrentSpaceId. "modelo" (sin mayúsculas) → *Model_Space.
        /// Otro texto → layout con ese nombre (sin mayúsculas) del LayoutDictionary; inexistente → NOT_FOUND
        /// (<see cref="ToolException.LayoutNotFound"/>).
        /// </summary>
        public static ResolvedSpace Resolve(Database db, Transaction tr, string? espacio)
        {
            if (espacio == null)
            {
                var current = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                return new ResolvedSpace(current, SpaceName(current, tr));
            }

            if (string.Equals(espacio, ModelName, StringComparison.OrdinalIgnoreCase))
            {
                var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                return new ResolvedSpace(model, ModelName);
            }

            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (!string.Equals(entry.Key, espacio, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Value.IsNull || entry.Value.IsErased)
                {
                    break;
                }

                if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.BlockTableRecordId.IsNull)
                {
                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    return new ResolvedSpace(btr, SpaceName(btr, tr));
                }

                break;
            }

            throw ToolException.LayoutNotFound(espacio, AvailableSpaces(db, tr, includeModel: true));
        }

        /// <summary>
        /// Nombres de espacio válidos para el mensaje NOT_FOUND (fallo 22): "modelo" (si se pide) y los layouts de papel en
        /// orden de pestaña. Nunca lanza: si algo falla devuelve lo reunido hasta entonces.
        /// </summary>
        public static List<string> AvailableSpaces(Database db, Transaction tr, bool includeModel)
        {
            var names = new List<string>();
            if (includeModel)
            {
                names.Add(ModelName);
            }

            try
            {
                var paper = new List<(string name, int order)>();
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    if (entry.Value.IsNull || entry.Value.IsErased)
                    {
                        continue;
                    }

                    if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType)
                    {
                        paper.Add((layout.LayoutName, layout.TabOrder));
                    }
                }

                paper.Sort((a, b) => a.order.CompareTo(b.order));
                foreach ((string name, int _) in paper)
                {
                    names.Add(name);
                }
            }
            catch (Exception ex)
            {
                Logging.PluginLog.Warn("SpaceResolver: no se pudo listar los layouts para el mensaje NOT_FOUND (" + ex.GetType().Name + ").");
            }

            return names;
        }

        /// <summary>
        /// Nombre de un registro de bloque como espacio: "modelo", nombre del layout si es un espacio papel,
        /// o "bloque:&lt;nombre&gt;" si es una definición de bloque.
        /// </summary>
        public static string SpaceName(BlockTableRecord btr, Transaction tr)
        {
            if (string.Equals(btr.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
            {
                return ModelName;
            }

            if (btr.IsLayout)
            {
                try
                {
                    if (!btr.LayoutId.IsNull && tr.GetObject(btr.LayoutId, OpenMode.ForRead) is Layout layout)
                    {
                        return layout.LayoutName;
                    }
                }
                catch (Exception)
                {
                    // Layout ilegible: se usa el nombre del bloque.
                }

                return btr.Name;
            }

            return BlockPrefix + btr.Name;
        }

        /// <summary>
        /// Propietario de la entidad: "modelo" | nombre de layout | "bloque:&lt;nombre&gt;". Para un atributo
        /// (propietario = INSERT) se devuelve el espacio del INSERT. null si no se puede determinar.
        /// </summary>
        public static string? OwnerName(Entity e, Transaction tr)
        {
            BlockTableRecord? owner = OwnerBlock(e, tr);
            return owner == null ? null : SpaceName(owner, tr);
        }

        /// <summary>true si la entidad vive (directamente o vía su INSERT) en un layout de espacio papel.</summary>
        public static bool IsInPaperSpace(Entity e, Transaction tr)
        {
            BlockTableRecord? owner = OwnerBlock(e, tr);
            return owner != null
                && owner.IsLayout
                && !string.Equals(owner.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Registro de bloque que contiene la entidad, subiendo por propietarios no-BTR (atributo → INSERT).</summary>
        private static BlockTableRecord? OwnerBlock(Entity e, Transaction tr)
        {
            try
            {
                ObjectId ownerId = e.OwnerId;
                for (int depth = 0; depth < 8 && !ownerId.IsNull; depth++)
                {
                    DBObject owner = tr.GetObject(ownerId, OpenMode.ForRead, false);
                    if (owner is BlockTableRecord btr)
                    {
                        return btr;
                    }

                    ownerId = owner.OwnerId;
                }
            }
            catch (Exception)
            {
                // Sin propietario legible.
            }

            return null;
        }
    }
}
