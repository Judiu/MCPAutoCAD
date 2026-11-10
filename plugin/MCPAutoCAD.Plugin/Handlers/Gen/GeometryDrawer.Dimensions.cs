using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// Parte de <see cref="GeometryDrawer"/> de la Fase 4B (protocol.md §5G.3): cotas planificadas. Misma construcción que
    /// <c>cota</c> (§5D.6): RotatedDimension / AlignedDimension, DimensionStyle = estilo pedido o el actual
    /// (Database.Dimstyle), RecomputeDimensionBlock y lectura de Measurement. DIMSCALE y la escala anotativa son las del
    /// dibujo: no se toca ninguna variable.
    /// Registra {handle, tipo "DIMENSION", capa, tipo_cota, medida, estilo, p1, p2, posicion_linea, bbox}.
    /// </summary>
    internal static partial class GeometryDrawer
    {
        private const string TipoCotaLineal = "lineal";

        /// <summary>
        /// Estilo pedido (sin mayúsculas) o el actual (Database.Dimstyle). Inexistente → NOT_FOUND data.estilo. Lo usan la
        /// lectura de cotas_automaticas (en una StartOpenCloseTransaction) y el dibujo. Copia de la lógica de CotaHandler.
        /// </summary>
        internal static (ObjectId Id, string Nombre) ResolveDimStyle(Database db, Transaction tr, string? estilo)
        {
            if (estilo == null)
            {
                ObjectId cur = db.Dimstyle;
                string curName = "";
                if (!cur.IsNull && !cur.IsErased)
                {
                    curName = ((DimStyleTableRecord)tr.GetObject(cur, OpenMode.ForRead)).Name;
                }

                return (cur, curName);
            }

            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            bool has;
            try
            {
                has = table.Has(estilo);
            }
            catch (AcRuntimeException)
            {
                has = false;
            }

            ObjectId id = has ? table[estilo] : ObjectId.Null;
            if (id.IsNull || id.IsErased)
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El estilo de cota '" + estilo + "' no existe en el dibujo.",
                    new Dictionary<string, object?> { ["estilo"] = estilo });
            }

            return (id, ((DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
        }

        private static ObjectId DrawDimension(Database db, Transaction tr, BlockTableRecord btr, PlannedDimension p, DrawLayerFactory.Result layer, Result result, List<string> warnings)
        {
            (ObjectId styleId, string styleName) = ResolveDimStyle(db, tr, p.Estilo);
            Point3d a = AcadUtil.ToPoint3d(p.P1);
            Point3d b = AcadUtil.ToPoint3d(p.P2);
            Point3d l = AcadUtil.ToPoint3d(p.PosicionLinea);

            Dimension dim = string.Equals(p.TipoCota, TipoCotaLineal, StringComparison.Ordinal)
                ? new RotatedDimension(Angles.ToRadians(Angles.Normalize360(p.RotacionGrados)), a, b, l, "", styleId)
                : new AlignedDimension(a, b, l, "", styleId);

            DrawWrite.ApplyByLayer(dim, db, layer.Id);
            if (!styleId.IsNull)
            {
                // SetDatabaseDefaults (en ApplyByLayer) podría fijar el estilo actual: se reafirma el pedido.
                dim.DimensionStyle = styleId;
            }

            DrawWrite.Append(btr, tr, dim);

            try
            {
                dim.RecomputeDimensionBlock(true);
            }
            catch (AcRuntimeException)
            {
                // Sin bloque de cota regenerado: la medida y la caja se leen igualmente (pueden salir null).
            }

            double? measurement = null;
            try
            {
                double m = dim.Measurement;
                measurement = double.IsFinite(m) ? m : null;
            }
            catch (AcRuntimeException)
            {
                measurement = null;
            }

            Point3d x1 = a, x2 = b, dl = l;
            if (dim is RotatedDimension rd)
            {
                x1 = rd.XLine1Point;
                x2 = rd.XLine2Point;
                dl = rd.DimLinePoint;
            }
            else if (dim is AlignedDimension ad)
            {
                x1 = ad.XLine1Point;
                x2 = ad.XLine2Point;
                dl = ad.DimLinePoint;
            }

            Register(dim, "DIMENSION", result, new Dictionary<string, object?>
            {
                ["tipo_cota"] = p.TipoCota,
                ["medida"] = measurement,
                ["estilo"] = styleName,
                ["p1"] = AcadUtil.ToArray(x1),
                ["p2"] = AcadUtil.ToArray(x2),
                ["posicion_linea"] = AcadUtil.ToArray(dl),
            });
            return dim.ObjectId;
        }
    }
}
