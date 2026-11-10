using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>Resultado de <see cref="CuadroAreasCheck.Analyze"/>.</summary>
    internal sealed class AreaTableAnalysis
    {
        public AreaTableAnalysis(AreaTableResult result, int nTextos, bool completo)
        {
            Result = result;
            NTextos = nTextos;
            Completo = completo;
        }

        /// <summary>Cuadros y hallazgos de GeometryCore (hallazgos con Espacio ya asignado, sin id).</summary>
        public AreaTableResult Result { get; }

        /// <summary>TEXT y MTEXT leídos del espacio (tras el filtro bbox).</summary>
        public int NTextos { get; }

        public bool Completo { get; }
    }

    /// <summary>
    /// Regla 4 de auditar_dibujo, <c>verificar_cuadro_areas</c> (protocol.md §5C.5): lee TEXT y MTEXT del espacio,
    /// los convierte a <see cref="TextItem"/> (TEXT: Position y TextString; MTEXT: Location.X, Location.Y − TextHeight y
    /// MText.Text; rotación en grados y caja 2D de GeometricExtents desde 0.3.1) y delega detección y reglas en
    /// GeometryCore (<see cref="AreaTable"/>). Una pasada por el espacio. En el espacio modelo calcula además las
    /// ventanas de modelo de los viewports de papel (F3) para marcar los cuadros fuera de los layouts. Solo lectura:
    /// todo se abre para lectura dentro de la transacción de quien llama; no cambia variables de sistema.
    /// </summary>
    internal sealed class CuadroAreasCheck : IAuditCheck
    {
        public const string RuleName = AreaTable.Regla;

        public string Regla => RuleName;

        public int Orden => 4;

        /// <summary>Lectura completa del espacio de <paramref name="ctx"/>, opcionalmente filtrada por caja.</summary>
        public AreaTableAnalysis Analyze(AuditContext ctx, BBox2? bbox = null)
        {
            Transaction tr = ctx.Tr;
            var items = new List<TextItem>();
            bool completo = true;
            int seen = 0;
            foreach (ObjectId id in ctx.Espacio.Btr)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                if (++seen % AuditSupport.CheckEvery == 0 && ctx.TimeExceeded)
                {
                    completo = false;
                    break;
                }

                string? quick = AcadUtil.DxfNameFromId(id);
                if (quick != null && quick != "TEXT" && quick != "MTEXT")
                {
                    continue;
                }

                Entity? e = AuditSupport.Open(tr, id);
                TextItem? item = e == null ? null : ToItem(e);
                if (item == null)
                {
                    continue;
                }

                if (bbox.HasValue)
                {
                    if (item.Bbox == null || !item.Bbox.Value.Intersects(bbox.Value))
                    {
                        continue;
                    }
                }

                items.Add(item);
            }

            // F3: solo el espacio modelo se compara con las ventanas de los viewports; en papel, todos en layout.
            List<BBox2>? ventanas = string.Equals(ctx.Espacio.Name, SpaceResolver.ModelName, StringComparison.Ordinal)
                ? LayoutWindows(ctx.Db, tr)
                : null;
            AreaTableResult result = AreaTable.Parse(items, ventanas);
            foreach (Finding f in result.Hallazgos)
            {
                f.Espacio = ctx.Espacio.Name;
            }

            return new AreaTableAnalysis(result, items.Count, completo);
        }

        public AuditCheckResult Run(AuditContext ctx)
        {
            AreaTableAnalysis analysis = Analyze(ctx);
            var result = new AuditCheckResult { Completo = analysis.Completo };
            result.Hallazgos.AddRange(analysis.Result.Hallazgos);
            result.Advertencias.AddRange(analysis.Result.Advertencias);
            result.Resumen["n_cuadros"] = analysis.Result.Cuadros.Count;
            result.Resumen["n_cuadros_fuera_de_layout"] = analysis.Result.NCuadrosFueraDeLayout;
            result.Resumen["n_textos_leidos"] = analysis.NTextos;
            string? warning = OutsideLayoutWarning(analysis.Result);
            if (warning != null)
            {
                result.Advertencias.Add(warning);
            }

            return result;
        }

        /// <summary>
        /// Advertencia de §5C.5 si hay cuadros fuera de los viewports: "&lt;n&gt; cuadro(s) fuera de los viewports de los
        /// layouts (&lt;ids&gt;): posible versión anterior del plano."; null si no hay.
        /// </summary>
        public static string? OutsideLayoutWarning(AreaTableResult r)
        {
            List<string> ids = r.IdsFueraDeLayout;
            if (ids.Count == 0)
            {
                return null;
            }

            return AuditSupport.Int(ids.Count) + " cuadro(s) fuera de los viewports de los layouts ("
                + string.Join(", ", ids) + "): posible versión anterior del plano.";
        }

        /// <summary>
        /// Ventanas de modelo de los viewports de papel de todos los layouts (F3, protocol.md §5C.5). Desde 0.6 la lectura
        /// vive en <see cref="LayoutViewports.Windows"/> (compartida con borrar_fuera_de, §5F.1); aquí solo se toman las cajas.
        /// </summary>
        private static List<BBox2> LayoutWindows(Database db, Transaction tr)
        {
            var windows = new List<BBox2>();
            foreach (LayoutViewports.ViewportWindowInfo vp in LayoutViewports.Windows(db, tr))
            {
                windows.Add(vp.Ventana);
            }

            return windows;
        }

        /// <summary>Entrada de GeometryCore para un TEXT o MTEXT; null para otros tipos o si no se puede leer.</summary>
        private static TextItem? ToItem(Entity e)
        {
            try
            {
                string handle = AcadUtil.HandleToString(e.Handle);
                switch (e)
                {
                    case DBText t when !(e is AttributeDefinition) && !(e is AttributeReference):
                        return new TextItem(handle, t.TextString ?? "", t.Position.X, t.Position.Y, t.Height,
                            RadToDeg(t.Rotation), SafeBox(e));
                    case MText mt:
                        double h = mt.TextHeight;
                        return new TextItem(handle, mt.Text ?? "", mt.Location.X, mt.Location.Y - h, h,
                            RadToDeg(mt.Rotation), SafeBox(e));
                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

        /// <summary>Caja 2D de GeometricExtents (F6) o null si AutoCAD no la calcula.</summary>
        private static BBox2? SafeBox(Entity e)
        {
            try
            {
                return AcadUtil.TryBBox2(e);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
