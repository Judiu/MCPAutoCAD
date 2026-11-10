using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Lectura común de la selección implícita (pickfirst) para seleccion_actual, borrar_excepto_seleccion y
    /// guardar_copia(solo_seleccion). Debe llamarse en contexto de aplicación (hilo principal, fuera de
    /// ExecuteInCommandContextAsync): dentro de un comando la selección implícita puede venir vacía.
    /// </summary>
    internal static class SelectionReader
    {
        /// <summary>
        /// ObjectId no nulos ni borrados de la selección implícita. Si SelectImplied lanza o no hay selección, lista vacía
        /// (no es error). La selección se vuelve a fijar con <see cref="Restore"/>.
        /// </summary>
        public static List<ObjectId> ReadImplied(Editor ed)
        {
            var ids = new List<ObjectId>();
            try
            {
                PromptSelectionResult res = ed.SelectImplied();
                if (res.Status == PromptStatus.OK && res.Value != null)
                {
                    foreach (ObjectId id in res.Value.GetObjectIds())
                    {
                        if (!id.IsNull && !id.IsErased)
                        {
                            ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SelectImplied lanzó (" + ex.GetType().Name + ": " + ex.Message + "); selección vacía.");
                ids.Clear();
            }

            return ids;
        }

        /// <summary>Vuelve a fijar la selección para que el usuario no pierda sus pinzamientos (solo ids aún vivos).</summary>
        public static void Restore(Editor ed, IReadOnlyList<ObjectId> ids, List<string> warnings)
        {
            var alive = new List<ObjectId>(ids.Count);
            foreach (ObjectId id in ids)
            {
                if (!id.IsNull && !id.IsErased)
                {
                    alive.Add(id);
                }
            }

            if (alive.Count == 0)
            {
                return;
            }

            try
            {
                ed.SetImpliedSelection(alive.ToArray());
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SetImpliedSelection lanzó (" + ex.GetType().Name + ": " + ex.Message + ").");
                warnings.Add("No se pudo restaurar la selección del usuario: " + ex.Message + ".");
            }
        }
    }
}
