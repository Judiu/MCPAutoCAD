using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// Regla 7 de auditar_dibujo, <c>xrefs_no_resueltas</c> (protocol.md §5C.8): un hallazgo <c>alta</c> por xref con
    /// estado no_encontrada o sin_cargar y <c>media</c> por descargada (estados como en §5B.6, misma lógica que
    /// <see cref="XrefsHandler"/>). datos = {nombre, estado, ruta_guardada, ruta_resuelta}; handles = sus INSERT.
    /// Ámbito: todo el dibujo. Solo lectura.
    /// </summary>
    internal sealed class XrefsCheck : IAuditCheck
    {
        public const string RuleName = "xrefs_no_resueltas";

        public string Regla => RuleName;

        public int Orden => 7;

        public AuditCheckResult Run(AuditContext ctx)
        {
            var result = new AuditCheckResult();
            Database db = ctx.Db;
            Transaction tr = ctx.Tr;
            int xrefs = 0;

            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                if (ctx.TimeExceeded)
                {
                    result.Completo = false;
                    break;
                }

                BlockTableRecord btr;
                try
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead, false);
                    if (!btr.IsFromExternalReference)
                    {
                        continue;
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    continue;
                }

                xrefs++;
                string estado = MapStatus(btr);
                string? severidad = estado switch
                {
                    "no_encontrada" => Severity.Alta,
                    "sin_cargar" => Severity.Alta,
                    "descargada" => Severity.Media,
                    _ => null,
                };

                if (severidad == null)
                {
                    continue;
                }

                string name = SafeString(() => btr.Name) ?? "";
                string? saved = SafeString(() => btr.PathName);
                string? resolved = Resolve(saved, db);
                string rutaTexto = string.IsNullOrEmpty(saved) ? "sin ruta guardada" : "ruta guardada " + saved;

                var finding = new Finding(
                    RuleName,
                    severidad,
                    "La xref '" + name + "' tiene estado " + estado + " (" + rutaTexto + ").");
                finding.Datos["nombre"] = name;
                finding.Datos["estado"] = estado;
                finding.Datos["ruta_guardada"] = saved;
                finding.Datos["ruta_resuelta"] = resolved;

                AddInserts(finding, btr, tr);
                result.Hallazgos.Add(finding);
            }

            result.Resumen["n_xrefs"] = xrefs;
            result.Resumen["n_hallazgos"] = result.Hallazgos.Count;
            return result;
        }

        /// <summary>Handles de las inserciones (orden de handle ≈ orden de base de datos), su caja y su espacio común.</summary>
        private static void AddInserts(Finding finding, BlockTableRecord btr, Transaction tr)
        {
            var ids = new List<ObjectId>();
            AcadUtil.CollectInsertions(btr, tr, ids);
            ids.Sort((a, b) => a.Handle.Value.CompareTo(b.Handle.Value));

            BBox2? box = null;
            string? space = null;
            bool mixedSpaces = false;
            int opened = 0;
            foreach (ObjectId id in ids)
            {
                finding.Handles.Add(AcadUtil.HandleToString(id.Handle));
                if (opened >= Finding.MaxHandles)
                {
                    continue;
                }

                opened++;
                try
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity e))
                    {
                        continue;
                    }

                    BBox2? b = AcadUtil.TryBBox2(e);
                    if (b != null)
                    {
                        box = box == null ? b : box.Value.Union(b.Value);
                    }

                    string? owner = SpaceResolver.OwnerName(e, tr);
                    if (space == null && !mixedSpaces)
                    {
                        space = owner;
                    }
                    else if (!string.Equals(space, owner, StringComparison.Ordinal))
                    {
                        mixedSpaces = true;
                        space = null;
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    // Inserción ilegible: se lista su handle sin caja.
                }
            }

            finding.Bbox = box;
            // Espacio solo si todas las inserciones leídas están en el mismo modelo/layout (no dentro de un bloque).
            finding.Espacio = mixedSpaces || (space != null && space.StartsWith(SpaceResolver.BlockPrefix, StringComparison.Ordinal)) ? null : space;
        }

        /// <summary>XrefStatus → estado de §5B.6.</summary>
        private static string MapStatus(BlockTableRecord btr)
        {
            XrefStatus status;
            try
            {
                status = btr.XrefStatus;
            }
            catch (Exception)
            {
                return "desconocido";
            }

            return status switch
            {
                XrefStatus.Resolved => "resuelta",
                XrefStatus.FileNotFound => "no_encontrada",
                XrefStatus.Unloaded => "descargada",
                XrefStatus.Unresolved => "sin_cargar",
                XrefStatus.Unreferenced => "sin_referencias",
                _ => "desconocido",
            };
        }

        /// <summary>FindFile(PathName, db, XRefDrawing); null si lanza o devuelve "".</summary>
        private static string? Resolve(string? saved, Database db)
        {
            if (string.IsNullOrEmpty(saved))
            {
                return null;
            }

            try
            {
                string found = HostApplicationServices.Current.FindFile(saved, db, FindFileHint.XRefDrawing);
                return string.IsNullOrEmpty(found) ? null : found;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? SafeString(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
