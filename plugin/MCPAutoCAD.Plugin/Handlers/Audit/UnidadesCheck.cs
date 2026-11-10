using System.Collections.Generic;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// Regla 8 de auditar_dibujo, <c>unidades</c> (protocol.md §5C.8): como mucho un hallazgo. <c>alta</c> si
    /// INSUNITS = 0; <c>media</c> si INSUNITS ≠ el del estándar LPS (cuando está disponible) o si MEASUREMENT e
    /// INSUNITS son inconsistentes (§5.2; mismos textos que estado). datos = {insunits, measurement, insunits_lps};
    /// handles = []; espacio = null. Solo lectura.
    /// </summary>
    internal sealed class UnidadesCheck : IAuditCheck
    {
        public const string RuleName = "unidades";

        public string Regla => RuleName;

        public int Orden => 8;

        public AuditCheckResult Run(AuditContext ctx)
        {
            var result = new AuditCheckResult();
            int ins = AcadUtil.InsUnits(ctx.Db);
            int measurement = AcadUtil.Measurement(ctx.Db);
            int? lps = LpsStandard.Insunits;

            string? severidad = null;
            var facts = new List<string>();
            if (ins == 0)
            {
                severidad = Severity.Alta;
                facts.Add("El dibujo no declara unidades (INSUNITS=0).");
                if (lps != null)
                {
                    facts.Add("El estándar LPS es " + UnitsTable.SpanishName(lps.Value) + " (INSUNITS=" + lps.Value + ").");
                }
            }
            else
            {
                // Diferencia con el estándar: mismo texto que la advertencia LPS de §2.2.
                string? lpsWarning = LpsStandard.Warning(ins);
                if (lpsWarning != null)
                {
                    facts.Add(lpsWarning);
                }

                // Inconsistencia MEASUREMENT/INSUNITS: mismos textos que estado (§5.2), sin la de INSUNITS=0.
                foreach (string w in UnitsTable.Warnings(ins, measurement))
                {
                    if (w != UnitsTable.WarningUnitsUnknown)
                    {
                        facts.Add(w);
                    }
                }

                if (facts.Count > 0)
                {
                    severidad = Severity.Media;
                }
            }

            if (severidad != null)
            {
                var finding = new Finding(RuleName, severidad, string.Join(" ", facts));
                finding.Datos["insunits"] = ins;
                finding.Datos["measurement"] = measurement;
                finding.Datos["insunits_lps"] = lps;
                result.Hallazgos.Add(finding);
            }

            result.Resumen["insunits"] = ins;
            result.Resumen["measurement"] = measurement;
            result.Resumen["insunits_lps"] = lps;
            return result;
        }
    }
}
