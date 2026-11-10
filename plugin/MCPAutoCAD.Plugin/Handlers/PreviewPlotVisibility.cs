using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Handlers.Gen;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// vista_png con trazado (protocol.md §5B.7 "Previsualización visible en el trazado", §5E.4; corrección BLOQUEANTE del
    /// gate en vivo de Fase 4A, 2026-10-27, prueba 7). La capa MCP-PREVIEW se crea NO imprimible (lps.json capas_mcp,
    /// plot: false, para que nunca salga en los planos del usuario) y el mecanismo "plot" de vista_png respeta esa
    /// propiedad: la previsualización no aparecía en la imagen y el bucle preview → vista_png → confirmar quedaba ciego.
    /// <para>
    /// Solución: SOLO dentro del grupo neutro de vista_png (<see cref="Bridge.UndoGroup.RunNeutral"/>: BE → … → E → U) y
    /// ANTES del trazado, si la capa existe, no es imprimible y TIENE ENTIDADES en el espacio que se plotea (O1: el cambio
    /// marca DBMOD y U no lo devuelve, así que sin nada que mostrar no se toca) se pone IsPlottable = true en una transacción propia
    /// (registrada en el deshacer, fuera de la suspensión de PlotCapture); tras el trazado (finally, también ante error)
    /// se devuelve explícitamente a no imprimible en otra transacción, todavía dentro del grupo. El "_.U" final deshace
    /// el grupo entero (ambos cambios incluidos), así que el estado neto es el de antes por dos vías independientes:
    /// la restauración explícita cubre el caso en que el grupo no se pudo abrir/cerrar/deshacer (UNDOCTL limitado,
    /// fallo de "_E" o de "_U"), y U cubre el caso en que la restauración explícita fallara. Si la restauración explícita
    /// falla se añade <see cref="RestoreFailedWarning"/>. lps.json NO cambia: la capa sigue sin imprimirse en los planos.
    /// </para>
    /// La captura nativa (sin bbox) no plotea: muestra la pantalla, donde la capa se ve aunque no sea imprimible; no aplica.
    /// SOLO hilo principal, en contexto de comando (el documento ya está bloqueado; el bloqueo aquí es anidado).
    /// </summary>
    internal static class PreviewPlotVisibility
    {
        /// <summary>Advertencia si no se pudo hacer imprimible la capa (la imagen puede no mostrar la preview).</summary>
        public static string EnableFailedWarning(string detalle) =>
            "No se pudo hacer imprimible temporalmente la capa " + PreviewStore.PreviewLayer + " para el trazado (" + detalle
            + "): la previsualización puede no verse en la imagen.";

        /// <summary>Advertencia si no se pudo devolver la capa a no imprimible tras el trazado.</summary>
        public static string RestoreFailedWarning(string detalle) =>
            "No se pudo devolver la capa " + PreviewStore.PreviewLayer + " a no imprimible tras el trazado (" + detalle
            + "): el deshacer del grupo neutro debería haberla restaurado; compruebe su propiedad Trazar en el administrador de capas antes de imprimir.";

        /// <summary>
        /// Si MCP-PREVIEW existe, no está borrada y NO es imprimible, la hace imprimible y devuelve su ObjectId (para
        /// <see cref="Restore"/>); en cualquier otro caso devuelve null sin escribir nada. Un fallo no impide el trazado:
        /// se registra y se añade <see cref="EnableFailedWarning"/>.
        /// </summary>
        public static ObjectId? Enable(Document doc, List<string> advertencias)
        {
            try
            {
                Database db = doc.Database;
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(PreviewStore.PreviewLayer))
                    {
                        tr.Commit();
                        return null;
                    }

                    ObjectId id = lt[PreviewStore.PreviewLayer];
                    if (id.IsNull || id.IsErased)
                    {
                        tr.Commit();
                        return null;
                    }

                    var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (layer.IsPlottable)
                    {
                        tr.Commit();
                        return null;
                    }

                    // O1 (revisión del gate, 2026-10-27): cambiar la capa marca DBMOD y el "_.U" del grupo no lo devuelve.
                    // Solo se toca si hay algo que mostrar: al menos una entidad de MCP-PREVIEW en el espacio modelo o en el
                    // layout que se va a plotear (lectura, sin marcar DBMOD). Si el tope de coste se agota, se toca.
                    if (!HasPreviewEntities(db, tr, id))
                    {
                        tr.Commit();
                        PluginLog.Info("vista_png plot: " + PreviewStore.PreviewLayer + " sin entidades en el espacio trazado; no se toca la capa.");
                        return null;
                    }

                    layer.UpgradeOpen();
                    layer.IsPlottable = true;
                    tr.Commit();
                    PluginLog.Info("vista_png plot: capa " + PreviewStore.PreviewLayer + " imprimible durante el trazado (dentro del grupo neutro; se restaura al terminar).");
                    return id;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("vista_png plot: no se pudo hacer imprimible " + PreviewStore.PreviewLayer, ex);
                advertencias.Add(EnableFailedWarning(ex.GetType().Name + ": " + ex.Message));
                return null;
            }
        }

        /// <summary>
        /// true si hay al menos una entidad viva en la capa <paramref name="layerId"/> en el espacio modelo (visible desde
        /// las ventanas de un layout) o en el bloque del layout actual (el que se plotea). Salida anticipada en la primera;
        /// respeta el tope de coste de R4 (<see cref="PlacementBudget"/>): si se agota sin encontrar ninguna → true (mejor
        /// tocar la capa que dejar la preview invisible). Solo lectura.
        /// </summary>
        private static bool HasPreviewEntities(Database db, Transaction tr, ObjectId layerId)
        {
            var spaces = new List<ObjectId> { SymbolUtilityServices.GetBlockModelSpaceId(db) };
            try
            {
                LayoutManager lm = LayoutManager.Current;
                var layout = (Layout)tr.GetObject(lm.GetLayoutId(lm.CurrentLayout), OpenMode.ForRead);
                if (!spaces.Contains(layout.BlockTableRecordId))
                {
                    spaces.Add(layout.BlockTableRecordId);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("vista_png plot: no se pudo leer el layout actual (" + ex.GetType().Name + "); solo se busca en el modelo.");
            }

            var sw = Stopwatch.StartNew();
            int scanned = 0;
            foreach (ObjectId spaceId in spaces)
            {
                if (tr.GetObject(spaceId, OpenMode.ForRead, false) is not BlockTableRecord btr)
                {
                    continue;
                }

                foreach (ObjectId eid in btr)
                {
                    if (scanned % PlacementBudget.CheckEvery == 0 && PlacementBudget.Exhausted(scanned, sw.Elapsed.TotalMilliseconds))
                    {
                        PluginLog.Info("vista_png plot: búsqueda de entidades de " + PreviewStore.PreviewLayer + " cortada por el tope de coste; se hace imprimible igualmente.");
                        return true;
                    }

                    scanned++;
                    if (eid.IsNull || eid.IsErased)
                    {
                        continue;
                    }

                    if (tr.GetObject(eid, OpenMode.ForRead, false) is Entity e && e.LayerId == layerId)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Devuelve la capa a no imprimible (solo si <see cref="Enable"/> la cambió). Nunca lanza: un fallo se registra y
        /// añade <see cref="RestoreFailedWarning"/> (el "_.U" del grupo neutro sigue deshaciendo el cambio).
        /// </summary>
        public static void Restore(Document doc, ObjectId layerId, List<string> advertencias)
        {
            try
            {
                Database db = doc.Database;
                using (doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
                    layer.IsPlottable = false;
                    tr.Commit();
                }

                PluginLog.Info("vista_png plot: capa " + PreviewStore.PreviewLayer + " devuelta a no imprimible.");
            }
            catch (Exception ex)
            {
                PluginLog.Error("vista_png plot: no se pudo devolver " + PreviewStore.PreviewLayer + " a no imprimible", ex);
                advertencias.Add(RestoreFailedWarning(ex.GetType().Name + ": " + ex.Message));
            }
        }
    }
}
