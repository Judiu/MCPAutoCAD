using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// Almacén de previsualizaciones del plugin (protocol.md §5E.5): un <see cref="PreviewRegistry"/> con reloj real,
    /// vivo mientras el plugin esté cargado, usado SOLO desde el hilo principal. Identidad de documento =
    /// <see cref="DocKey"/> (huella de la Database + nombre del archivo). RESTRICCIÓN DE SEGURIDAD: nada de aquí escribe
    /// en el dibujo por sí mismo (ni al caducar, ni al desconectar un cliente, ni por temporizador); las entidades
    /// huérfanas de MCP-PREVIEW solo se informan (<see cref="OrphanWarning"/>) y se borran dentro de una petición del
    /// cliente (descartar_preview, confirmar, limpiar_observaciones).
    /// <para>
    /// Coste (0.6, protocol.md §5F.5): el recuento de huérfanas usa <c>Editor.SelectAll</c> con un filtro de capa
    /// (selección nativa de modelo + layouts, sin tocar la selección implícita del usuario: nunca se fija ni se borra) y
    /// solo cae al recorrido completo de 0.5 (con el tope de coste de R4, <see cref="PlacementBudget"/>) si la capa
    /// MCP-PREVIEW está congelada o apagada, o si SelectAll lanza. Objetivo medible del gate: <c>estado</c> con
    /// MCP-PREVIEW vacía en el plano madre &lt; 30 ms (en 0.5, 168 ms de media).
    /// </para>
    /// </summary>
    internal static class PreviewStore
    {
        /// <summary>Capa de previsualización (lps.json capas_mcp: color 4, no imprimible).</summary>
        public const string PreviewLayer = "MCP-PREVIEW";

        public static PreviewRegistry Registry { get; } = new PreviewRegistry(() => DateTimeOffset.Now);

        /// <summary>true cuando ya se registró en plugin.log que SelectAll falló (se avisa una sola vez por sesión).</summary>
        private static bool _selectAllWarned;

        /// <summary>Clave del documento: FingerprintGuid de la Database + nombre del archivo (estable mientras esté abierto).</summary>
        public static string DocKey(Document doc)
        {
            Database db = doc.Database;
            string fp;
            try
            {
                fp = db.FingerprintGuid ?? "";
            }
            catch (Exception)
            {
                fp = "";
            }

            string name;
            try
            {
                name = db.Filename ?? doc.Name ?? "";
            }
            catch (Exception)
            {
                name = doc.Name ?? "";
            }

            return fp + "|" + name;
        }

        /// <summary>
        /// Advertencia de huérfanas (o null): entidades vivas de la capa MCP-PREVIEW en modelo y layouts que no pertenecen a
        /// ninguna previsualización viva de este documento. Lectura con la transacción del llamador; nunca escribe.
        /// Si la capa MCP-PREVIEW no existe → null sin recorrer nada.
        /// <para>
        /// Desde 0.6 (§5F.5): selección nativa <c>Editor.SelectAll</c> con el filtro DXF 8 = "MCP-PREVIEW" (equivale a
        /// ssget "_X"): AutoCAD recorre el modelo y todos los layouts en código nativo, sin entrar en definiciones de bloque
        /// (las que nunca contaron); aquí solo se lee el handle de cada ObjectId devuelto, sin abrir el objeto. Sin
        /// resultados (estado distinto de OK o 0 objetos) → null sin recorrer nada.
        /// </para>
        /// <para>
        /// Respaldo (el recorrido de 0.5, intacto): capa congelada o apagada (la selección nativa puede omitir sus
        /// entidades) o SelectAll lanza (se registra una vez con PluginLog.Warn). La pasada de respaldo respeta el tope de
        /// coste de R4 (<see cref="PlacementBudget"/>: entidades y tiempo); si se agota, no bloquea: con huérfanas ya
        /// encontradas se avisa como cota inferior ("al menos n"), sin ninguna → null.
        /// </para>
        /// </summary>
        public static string? OrphanWarning(Document doc, Database db, Transaction tr)
        {
            int orphans = 0;
            bool partial = false;
            try
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(PreviewLayer))
                {
                    return null;
                }

                ObjectId previewLayerId = lt[PreviewLayer];
                bool useFallback = true;
                if (tr.GetObject(previewLayerId, OpenMode.ForRead, false) is LayerTableRecord layer && !layer.IsFrozen && !layer.IsOff)
                {
                    ObjectId[]? selected = TrySelectPreviewLayer(doc, out bool selectionFailed);
                    if (!selectionFailed)
                    {
                        if (selected == null || selected.Length == 0)
                        {
                            return null;
                        }

                        useFallback = false;
                        HashSet<string> tracked = Registry.TrackedHandles(DocKey(doc));
                        foreach (ObjectId id in selected)
                        {
                            if (id.IsNull || id.IsErased)
                            {
                                continue;
                            }

                            // El handle se lee del ObjectId: no se abre el objeto.
                            if (!tracked.Contains(AcadUtil.HandleToString(id.Handle)))
                            {
                                orphans++;
                            }
                        }
                    }
                }

                if (useFallback)
                {
                    partial = ScanAllSpaces(doc, db, tr, previewLayerId, ref orphans);
                }
            }
            catch (Exception ex)
            {
                // La advertencia de huérfanas es informativa: un fallo de lectura no bloquea la herramienta, pero se registra.
                PluginLog.Error("PreviewStore.OrphanWarning: no se pudieron contar las entidades huérfanas de " + PreviewLayer, ex);
                return null;
            }

            if (orphans == 0)
            {
                return null;
            }

            return partial ? PreviewRegistry.OrphansWarningPartial(orphans) : PreviewRegistry.OrphansWarning(orphans);
        }

        /// <summary>
        /// Selección nativa de las entidades de MCP-PREVIEW en modelo y layouts (ssget "_X" con filtro de capa). No toca la
        /// selección implícita del usuario (nunca se fija ni se borra). Devuelve null si no hay resultados;
        /// <paramref name="failed"/> = true si SelectAll lanzó (el llamador usa entonces el recorrido de respaldo).
        /// </summary>
        private static ObjectId[]? TrySelectPreviewLayer(Document doc, out bool failed)
        {
            failed = false;
            try
            {
                Editor ed = doc.Editor;
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.LayerName, PreviewLayer) });
                PromptSelectionResult sel = ed.SelectAll(filter);
                if (sel.Status != PromptStatus.OK || sel.Value == null || sel.Value.Count == 0)
                {
                    return null;
                }

                return sel.Value.GetObjectIds();
            }
            catch (Exception ex)
            {
                failed = true;
                if (!_selectAllWarned)
                {
                    _selectAllWarned = true;
                    PluginLog.Warn("PreviewStore.OrphanWarning: Editor.SelectAll no está disponible (" + ex.GetType().Name + ": "
                        + ex.Message + "); se usa el recorrido completo de modelo y layouts");
                }

                return null;
            }
        }

        /// <summary>
        /// Recorrido de respaldo (el de 0.5, sin cambios): todas las entidades de modelo y layouts con el tope de coste de
        /// R4. Suma las huérfanas a <paramref name="orphans"/> y devuelve true si el tope se agotó (recuento parcial).
        /// </summary>
        private static bool ScanAllSpaces(Document doc, Database db, Transaction tr, ObjectId previewLayerId, ref int orphans)
        {
            bool partial = false;
            HashSet<string> tracked = Registry.TrackedHandles(DocKey(doc));
            var sw = Stopwatch.StartNew();
            int scanned = 0;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in bt)
            {
                if (partial)
                {
                    break;
                }

                if (tr.GetObject(btrId, OpenMode.ForRead, false) is not BlockTableRecord btr || !btr.IsLayout)
                {
                    continue;
                }

                foreach (ObjectId id in btr)
                {
                    if (scanned % PlacementBudget.CheckEvery == 0 && PlacementBudget.Exhausted(scanned, sw.Elapsed.TotalMilliseconds))
                    {
                        partial = true;
                        break;
                    }

                    scanned++;
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity e)
                    {
                        continue;
                    }

                    if (e.LayerId == previewLayerId && !tracked.Contains(AcadUtil.HandleToString(e.Handle)))
                    {
                        orphans++;
                    }
                }
            }

            return partial;
        }
    }
}
