using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>Fila de capa de auditar_capas (protocol.md §5C.1).</summary>
    internal sealed class LayerAuditRow
    {
        public LayerAuditRow(string nombre, int color, string tipoLinea, int grosor, ObjectId id)
        {
            Nombre = nombre;
            Color = color;
            TipoLinea = tipoLinea;
            Grosor = grosor;
            Id = id;
        }

        public string Nombre { get; }
        public int Color { get; }
        public string TipoLinea { get; }
        public int Grosor { get; }
        public ObjectId Id { get; }
        public int NEntidades { get; set; }
        public LayerClassification Clasificacion { get; set; } = new LayerClassification(LayerClassifier.FueraDeEstandar, new List<string>(), null);

        public Dictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["nombre"] = Nombre,
            ["color"] = Color,
            ["tipo_linea"] = TipoLinea,
            ["grosor"] = Grosor,
            ["n_entidades"] = NEntidades,
            ["vacia"] = NEntidades == 0,
            ["clasificacion"] = Clasificacion.Clasificacion,
            ["diferencias"] = Clasificacion.Diferencias,
            ["sugerencia"] = Clasificacion.Sugerencia,

            // Desde 0.6 (protocol.md §5C.1, §5F.5): patrón ajeno del NOMBRE con independencia de la clasificación.
            ["patron_ajeno"] = Clasificacion.PatronAjeno,
            ["sugerencia_patron"] = Clasificacion.SugerenciaPatron,
        };
    }

    /// <summary>Análisis completo de capas.</summary>
    internal sealed class LayerAuditResult
    {
        /// <summary>Capas no borradas y no dependientes de xref, por nombre (ordinal sin mayúsculas).</summary>
        public List<LayerAuditRow> Capas { get; } = new List<LayerAuditRow>();

        public int NCapasXref { get; set; }

        public bool EstandarDisponible { get; set; }

        /// <summary>false si el plazo se agotó durante el conteo de entidades (conteos parciales).</summary>
        public bool Completo { get; set; } = true;
    }

    /// <summary>
    /// Regla 1 de auditar_dibujo, <c>auditar_capas</c> (protocol.md §5C.1): clasifica cada capa frente a lps.json con
    /// GeometryCore (<see cref="LayerClassifier"/>). Solo propone; no modifica nada. Cuenta entidades por capa en todos
    /// los espacios (nunca dentro de definiciones de bloque).
    /// </summary>
    internal sealed class CapasCheck : IAuditCheck
    {
        public const string RuleName = "auditar_capas";

        public string Regla => RuleName;

        public int Orden => 1;

        /// <summary>Lectura completa: filas de capa con clasificación y conteo.</summary>
        public LayerAuditResult Analyze(AuditContext ctx)
        {
            Transaction tr = ctx.Tr;
            LpsLayerRules rules = AuditSupport.LayerRules;
            var result = new LayerAuditResult { EstandarDisponible = rules.Disponible };
            var byId = new Dictionary<ObjectId, LayerAuditRow>();

            var table = (LayerTable)tr.GetObject(ctx.Db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                LayerTableRecord? ltr;
                try
                {
                    ltr = tr.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                }
                catch (Exception)
                {
                    ltr = null;
                }

                if (ltr == null)
                {
                    continue;
                }

                string name = ltr.Name ?? "";
                if (name.IndexOf('|') >= 0)
                {
                    result.NCapasXref++;
                    continue;
                }

                var row = new LayerAuditRow(name, SafeColor(ltr), ResolveLinetype(ltr, tr), SafeLineWeight(ltr), id);
                result.Capas.Add(row);
                byId[id] = row;
            }

            // Conteo de entidades por LayerId en todos los espacios (una pasada; los ATTRIB no están en el espacio).
            int seen = 0;
            foreach (SpaceResolver.ResolvedSpace space in AuditSupport.AllSpaces(ctx.Db, tr))
            {
                foreach (ObjectId id in space.Btr)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    if (++seen % AuditSupport.CheckEvery == 0 && ctx.TimeExceeded)
                    {
                        result.Completo = false;
                        break;
                    }

                    Entity? e = AuditSupport.Open(tr, id);
                    if (e == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (byId.TryGetValue(e.LayerId, out LayerAuditRow? row))
                        {
                            row.NEntidades++;
                        }
                    }
                    catch (Exception)
                    {
                        // Capa ilegible: no se cuenta.
                    }
                }

                if (!result.Completo)
                {
                    break;
                }
            }

            foreach (LayerAuditRow row in result.Capas)
            {
                row.Clasificacion = LayerClassifier.Classify(row.Nombre, row.Color, row.TipoLinea, row.Grosor, rules);
            }

            result.Capas.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Nombre, b.Nombre));
            return result;
        }

        public AuditCheckResult Run(AuditContext ctx)
        {
            LayerAuditResult analysis = Analyze(ctx);
            var result = new AuditCheckResult { Completo = analysis.Completo };
            if (!analysis.EstandarDisponible)
            {
                result.Advertencias.Add(AuditSupport.WarningNoStandard);
            }

            foreach (LayerAuditRow row in analysis.Capas)
            {
                Finding? f = ToFinding(row);
                if (f != null)
                {
                    result.Hallazgos.Add(f);
                }
            }

            result.Resumen["n_capas"] = analysis.Capas.Count;
            result.Resumen["n_capas_xref"] = analysis.NCapasXref;
            return result;
        }

        /// <summary>
        /// Hallazgo de la capa: estandar_con_diferencias → baja; clasificaciones 5–8 con entidades → media, vacías →
        /// baja; mcp, estandar y cype → ninguno.
        /// </summary>
        public static Finding? ToFinding(LayerAuditRow row)
        {
            string clasif = row.Clasificacion.Clasificacion;
            string? message;
            string severidad;
            if (clasif == LayerClassifier.EstandarConDiferencias)
            {
                severidad = Severity.Baja;
                message = DifferencesMessage(row);
            }
            else if (LayerClassifier.IsForeign(clasif))
            {
                severidad = row.NEntidades > 0 ? Severity.Media : Severity.Baja;
                string content = row.NEntidades == 0
                    ? "no tiene entidades"
                    : "tiene " + AuditSupport.Int(row.NEntidades) + (row.NEntidades == 1 ? " entidad" : " entidades");
                message = "La capa '" + row.Nombre + "' (" + clasif + ") " + content
                    + (row.Clasificacion.Sugerencia != null ? "; capa preferida: " + row.Clasificacion.Sugerencia + "." : ".");
            }
            else
            {
                return null;
            }

            var f = new Finding(RuleName, severidad, message);
            f.Datos["clasificacion"] = clasif;
            f.Datos["n_entidades"] = row.NEntidades;
            f.Datos["diferencias"] = row.Clasificacion.Diferencias;
            f.Datos["sugerencia"] = row.Clasificacion.Sugerencia;
            f.Datos["patron_ajeno"] = row.Clasificacion.PatronAjeno;
            f.Datos["sugerencia_patron"] = row.Clasificacion.SugerenciaPatron;
            return f;
        }

        /// <summary>"La capa 'MUROS' tiene color 46; el estándar LPS define 155." (o la lista de diferencias).</summary>
        private static string DifferencesMessage(LayerAuditRow row)
        {
            LpsLayerRules rules = AuditSupport.LayerRules;
            if (!rules.CapasPermitidas.TryGetValue(row.Nombre, out LayerSpec? spec))
            {
                return "La capa '" + row.Nombre + "' difiere del estándar LPS: " + string.Join("; ", row.Clasificacion.Diferencias) + ".";
            }

            var drawing = new List<string>();
            var standard = new List<string>();
            if (row.Color != spec.Color)
            {
                drawing.Add("color " + AuditSupport.Int(row.Color));
                standard.Add("color " + AuditSupport.Int(spec.Color));
            }

            if (!string.Equals(row.TipoLinea, spec.TipoLinea, StringComparison.OrdinalIgnoreCase))
            {
                drawing.Add("tipo de línea " + row.TipoLinea);
                standard.Add("tipo de línea " + spec.TipoLinea);
            }

            if (row.Grosor != spec.Grosor)
            {
                drawing.Add("grosor " + AuditSupport.Int(row.Grosor));
                standard.Add("grosor " + AuditSupport.Int(spec.Grosor));
            }

            if (drawing.Count == 1)
            {
                // Una sola diferencia: "tiene color 46; el estándar LPS define 155."
                string std = standard[0].Substring(standard[0].LastIndexOf(' ') + 1);
                return "La capa '" + row.Nombre + "' tiene " + drawing[0] + "; el estándar LPS define " + std + ".";
            }

            return "La capa '" + row.Nombre + "' tiene " + JoinY(drawing) + "; el estándar LPS define " + JoinY(standard) + ".";
        }

        private static string JoinY(List<string> parts) =>
            parts.Count <= 1 ? string.Join("", parts) : string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " y " + parts[parts.Count - 1];

        private static int SafeColor(LayerTableRecord ltr)
        {
            try
            {
                return ltr.Color.ColorIndex;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static int SafeLineWeight(LayerTableRecord ltr)
        {
            try
            {
                return (int)ltr.LineWeight;
            }
            catch (Exception)
            {
                return -3;
            }
        }

        /// <summary>Nombre del tipo de línea de la capa (como ListarCapasHandler); "" si no se puede leer.</summary>
        private static string ResolveLinetype(LayerTableRecord ltr, Transaction tr)
        {
            try
            {
                ObjectId ltId = ltr.LinetypeObjectId;
                if (ltId.IsNull || ltId.IsErased)
                {
                    return "";
                }

                return tr.GetObject(ltId, OpenMode.ForRead, false) is LinetypeTableRecord lt ? lt.Name : "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
