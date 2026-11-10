using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Comprobación de ubicación (protocol.md §5D.0, regla R4; generaliza la F9 de marcar_hallazgos §5C.9): cada caja
    /// a escribir debe intersecar la extensión del espacio destino ampliada un 10 %; si no → INVALID_PARAMS sin
    /// escribir, salvo forzar:true. Lectura con StartOpenCloseTransaction, SIEMPRE antes y fuera del UndoGroup.
    /// Tope de coste (<see cref="PlacementBudget"/>): si la pasada se corta con cajas aún fuera, no se bloquea y se
    /// advierte. Solo hilo principal.
    /// </summary>
    internal static class PlacementCheck
    {
        /// <summary>Resultado: nombre del espacio resuelto (para la respuesta) y si fue parcial.</summary>
        internal sealed class Result
        {
            public Result(string espacio, bool esModelo, BBox2? extension, bool parcial, int fuera)
            {
                Espacio = espacio;
                EsModelo = esModelo;
                Extension = extension;
                Parcial = parcial;
                Fuera = fuera;
            }

            public string Espacio { get; }
            public bool EsModelo { get; }
            public BBox2? Extension { get; }
            public bool Parcial { get; }

            /// <summary>Cajas fuera que se escribieron por forzar o por comprobación parcial (0 si todas caben).</summary>
            public int Fuera { get; }
        }

        /// <summary>
        /// <paramref name="espacio"/> = parámetro del cliente (null = actual; inexistente → NOT_FOUND aquí).
        /// <paramref name="cajas"/> = una caja por entidad a crear/mover (un punto = caja de tamaño cero), en el orden
        /// que cita el mensaje; <paramref name="etiqueta"/> = nombre del array citado ("entidades", "vertices", "marcas").
        /// </summary>
        public static Result Run(Database db, string? espacio, IReadOnlyList<BBox2> cajas, bool forzar, string etiqueta, List<string> advertencias)
        {
            string spaceName;
            bool isModel;
            BBox2? extents = null;
            bool partial = false;
            int scanned = 0;
            var sw = Stopwatch.StartNew();

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, espacio);
                spaceName = resolved.Name;
                isModel = string.Equals(spaceName, SpaceResolver.ModelName, StringComparison.Ordinal);
                BlockTableRecord btr = resolved.Btr;

                if (cajas.Count > 0)
                {
                    if (!isModel && btr.IsLayout && !btr.LayoutId.IsNull)
                    {
                        try
                        {
                            var layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);
                            Extents2d lim = layout.Limits;
                            if (double.IsFinite(lim.MinPoint.X) && double.IsFinite(lim.MinPoint.Y) && double.IsFinite(lim.MaxPoint.X)
                                && double.IsFinite(lim.MaxPoint.Y) && lim.MinPoint.X <= lim.MaxPoint.X && lim.MinPoint.Y <= lim.MaxPoint.Y)
                            {
                                extents = BBox2.FromCorners(lim.MinPoint.X, lim.MinPoint.Y, lim.MaxPoint.X, lim.MaxPoint.Y);
                            }
                        }
                        catch (Exception)
                        {
                            // Límites ilegibles: solo cuentan las entidades.
                        }
                    }

                    // Corte temprano (0.4.0): las cajas que ya caben se retiran de 'pending' y solo se vuelve a mirar
                    // cuando la extensión creció, en los mismos puntos de control del reloj (cada CheckEvery
                    // entidades). Antes se recalculaba OutsideIndices sobre TODAS las cajas en cada entidad
                    // (O(entidades × cajas)). El veredicto final no cambia: lo da OutsideIndices con la extensión final;
                    // solo puede recorrerse hasta CheckEvery − 1 entidades más antes de cortar.
                    var pending = new PlacementPending(cajas);
                    bool grown = extents != null;
                    bool firstViewport = true;
                    foreach (ObjectId id in btr)
                    {
                        if (scanned % PlacementBudget.CheckEvery == 0)
                        {
                            if (grown)
                            {
                                grown = false;
                                if (pending.Refresh(extents))
                                {
                                    break;
                                }
                            }

                            if (PlacementBudget.Exhausted(scanned, sw.Elapsed.TotalMilliseconds))
                            {
                                partial = true;
                                break;
                            }
                        }

                        scanned++;
                        if (id.IsNull || id.IsErased)
                        {
                            continue;
                        }

                        Entity? e;
                        try
                        {
                            e = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (e == null || e.Layer.StartsWith(PluginInfo.McpLayerPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (e is Viewport vp)
                        {
                            bool paper = vp.Number == 1 || (firstViewport && vp.Number <= 0);
                            firstViewport = false;
                            if (paper)
                            {
                                continue;
                            }
                        }

                        BBox2? b = AcadUtil.TryBBox2(e);
                        if (b != null)
                        {
                            extents = extents == null ? b : extents.Value.Union(b.Value);
                            grown = true;
                        }
                    }
                }

                tr.Commit();
            }

            List<int> outside = MarkPlacement.OutsideIndices(cajas, extents);
            if (outside.Count == 0 || extents == null)
            {
                return new Result(spaceName, isModel, extents, partial, 0);
            }

            if (partial)
            {
                advertencias.Add(PlacementBudget.PartialWarning(spaceName, scanned, sw.Elapsed.TotalMilliseconds));
                return new Result(spaceName, isModel, extents, true, outside.Count);
            }

            string message = MarkPlacement.OutsideMessage(spaceName, isModel, extents.Value, outside, cajas, etiqueta);
            if (!forzar)
            {
                throw ToolException.InvalidParams(
                    message,
                    new Dictionary<string, object?>
                    {
                        ["espacio"] = spaceName,
                        ["extension"] = extents.Value.ToArray2D(),
                        [etiqueta + "_fuera"] = outside,
                    });
            }

            advertencias.Add("forzar: true: se escribió fuera de la extensión del espacio destino '" + spaceName + "' "
                + MarkPlacement.Format(extents.Value) + " (" + etiqueta + " " + string.Join(", ", outside) + ").");
            return new Result(spaceName, isModel, extents, false, outside.Count);
        }

        /// <summary>Caja de un punto (tamaño cero) para R4.</summary>
        public static BBox2 PointBox(Point3 p) => BBox2.FromCorners(p.X, p.Y, p.X, p.Y);

        /// <summary>Caja de un punto 3D de AutoCAD (proyección XY).</summary>
        public static BBox2 PointBox(Point3d p) => BBox2.FromCorners(p.X, p.Y, p.X, p.Y);
    }
}
