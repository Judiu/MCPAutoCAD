using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// Ventanas de modelo de los viewports de papel de todos los layouts (protocol.md §5C.5 F3 y §5F.1). Extraído de
    /// <c>CuadroAreasCheck</c> en 0.6 para compartirlo con borrar_fuera_de. Solo lectura, solo hilo principal, dentro
    /// de la transacción del llamador. Por cada Viewport no borrado que no sea el de papel (n.º 1, el propio espacio
    /// papel, resuelto con <see cref="PaperViewportId"/>: Layout.GetViewports() si el layout está inicializado, si no
    /// Number = 1 y, como respaldo, el primero del layout; en los layouts no actuales Number = −1) y, SOLO en el layout
    /// actual, On (en los demás layouts AutoCAD informa On = false si no se han activado en la sesión), en vista en
    /// planta (ViewDirection paralela a Z, tolerancia 1e-6). Centro = ViewTarget + ViewCenter (ViewCenter está en
    /// DCS, cuyo origen es ViewTarget; en el plano real, Layout1 nº3 tiene ViewTarget = (2590.96, 1494.53) y su
    /// ventana real está en x≈3273…3316). Un viewport que falle se omite. Lista vacía si no hay ninguno.
    /// </summary>
    internal static class LayoutViewports
    {
        /// <summary>Un viewport de papel evaluable y su ventana de modelo en WCS.</summary>
        internal sealed class ViewportWindowInfo
        {
            public ViewportWindowInfo(string layout, string handle, int numero, string capa, BBox2 ventana)
            {
                Layout = layout;
                Handle = handle;
                Numero = numero;
                Capa = capa;
                Ventana = ventana;
            }

            /// <summary>Nombre del layout (clave del LayoutDictionary).</summary>
            public string Layout { get; }

            /// <summary>Handle del VIEWPORT (§2.1).</summary>
            public string Handle { get; }

            /// <summary>Viewport.Number.</summary>
            public int Numero { get; }

            /// <summary>Capa del viewport.</summary>
            public string Capa { get; }

            /// <summary>Ventana de modelo [[xmin, ymin], [xmax, ymax]] (GeometryCore <see cref="ViewportWindow"/>).</summary>
            public BBox2 Ventana { get; }
        }

        /// <summary>
        /// Ventanas de todos los viewports evaluables, en el orden del LayoutDictionary y de cada layout. Sobre una base
        /// lateral (auditar_archivo) ningún layout es el actual: TILEMODE y CTAB son de la sesión, no de ese archivo (§5H.0.3).
        /// </summary>
        public static List<ViewportWindowInfo> Windows(Database db, Transaction tr)
        {
            var windows = new List<ViewportWindowInfo>();
            DBDictionary? layouts;
            try
            {
                layouts = tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
            }
            catch (Exception)
            {
                return windows;
            }

            if (layouts == null)
            {
                return windows;
            }

            // Layout de papel actual (CTAB con TILEMODE = 0); null si el usuario está en el modelo o si db no es la base
            // activa. Solo lectura de sysvars.
            string? currentLayout = null;
            try
            {
                if (db == HostApplicationServices.WorkingDatabase && AcadUtil.GetIntSysVar("TILEMODE") == 0)
                {
                    currentLayout = AcadUtil.GetStringSysVar("CTAB");
                }
            }
            catch (Exception)
            {
                currentLayout = null;
            }

            foreach (DBDictionaryEntry entry in layouts)
            {
                BlockTableRecord? btr;
                try
                {
                    if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout) || layout.ModelType)
                    {
                        continue;
                    }

                    btr = tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead) as BlockTableRecord;
                }
                catch (Exception)
                {
                    continue;
                }

                if (btr == null)
                {
                    continue;
                }

                // F3 (0.3.2): On solo se respeta en el layout actual. 0.6.1: el de papel se resuelve antes (O1).
                bool isCurrent = IsCurrentLayout(entry.Key, currentLayout);
                ObjectId paperId = PaperViewportId(tr, btr);
                foreach (ObjectId id in btr)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    try
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Viewport vp))
                        {
                            continue;
                        }

                        if (id == paperId || (isCurrent && !vp.On))
                        {
                            continue;
                        }

                        Vector3d dir = vp.ViewDirection;
                        double len = dir.Length;
                        if (!(len > 0) || Math.Abs(dir.X / len) > 1e-6 || Math.Abs(dir.Y / len) > 1e-6)
                        {
                            continue;
                        }

                        Point3d target = vp.ViewTarget;
                        Point2d center = vp.ViewCenter;
                        BBox2 window = ViewportWindow.FromViewportTarget(
                            target.X, target.Y, center.X, center.Y, vp.ViewHeight, vp.Width, vp.Height, vp.TwistAngle);
                        windows.Add(new ViewportWindowInfo(entry.Key, AcadUtil.HandleToString(vp.Handle), vp.Number, vp.Layer, window));
                    }
                    catch (Exception)
                    {
                        // Viewport indeterminable: se omite.
                    }
                }
            }

            return windows;
        }

        /// <summary>
        /// Viewport de papel (n.º 1) del layout de <paramref name="btr"/> (§5C.5, §5F.3; 0.6.1): criterio puro en
        /// <see cref="ViewportWindow.TryResolvePaperViewport{T}"/> con Layout.GetViewports() (fuente de verdad cuando el
        /// layout está inicializado: AutoCAD devuelve primero el de papel) y, como respaldo, Number = 1 o el primer
        /// Viewport del registro de bloque. ObjectId.Null si no hay viewports. Solo lectura.
        /// </summary>
        public static ObjectId PaperViewportId(Transaction tr, BlockTableRecord btr)
        {
            var inBtr = new List<KeyValuePair<ObjectId, int>>();
            foreach (ObjectId id in btr)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false) is Viewport vp)
                    {
                        int number;
                        try
                        {
                            number = vp.Number;
                        }
                        catch (Exception)
                        {
                            number = -1;
                        }

                        inBtr.Add(new KeyValuePair<ObjectId, int>(id, number));
                    }
                }
                catch (Exception)
                {
                    // Entidad ilegible: no cuenta.
                }
            }

            var fromLayout = new List<ObjectId>();
            try
            {
                if (!btr.LayoutId.IsNull && tr.GetObject(btr.LayoutId, OpenMode.ForRead, false) is Layout layout)
                {
                    foreach (ObjectId id in layout.GetViewports())
                    {
                        fromLayout.Add(id);
                    }
                }
            }
            catch (Exception)
            {
                // Layout no inicializado o ilegible: solo el respaldo.
            }

            return ViewportWindow.TryResolvePaperViewport(fromLayout, inBtr, out ObjectId paper) ? paper : ObjectId.Null;
        }

        private static bool IsCurrentLayout(string layoutName, string? currentLayout) =>
            !string.IsNullOrEmpty(currentLayout) && string.Equals(layoutName, currentLayout, StringComparison.OrdinalIgnoreCase);
    }
}
