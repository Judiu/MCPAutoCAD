using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// Bucle de reglas compartido por auditar_dibujo (§5C.8) y auditar_archivo (§5H.1): descubrimiento, orden, plazo,
    /// reglas_omitidas, reglas_con_error, por_regla, n_cuadros_fuera_de_layout y advertencias propias. Solo hilo principal;
    /// solo lectura. Trabaja sobre la base y la transacción de quien llama, que resuelve el espacio y confirma.
    /// </summary>
    internal static class SideAudit
    {
        /// <summary>
        /// Ejecuta <paramref name="reglas"/> (nombres del contrato; por_regla sale en este orden) con plazo absoluto
        /// <paramref name="deadlineUtc"/>. Una regla que falla no tumba la llamada. <paramref name="tool"/> solo da nombre a
        /// las líneas de plugin.log y <paramref name="budgetMs"/> es el presupuesto que citan las advertencias de
        /// parcialidad. Devuelve los hallazgos ya ordenados y con id (§5C.0).
        /// </summary>
        public static SideAuditResult Run(
            Database db,
            Transaction tr,
            SpaceResolver.ResolvedSpace espacio,
            IReadOnlyList<string> reglas,
            DateTime deadlineUtc,
            string tool = "auditar_dibujo",
            int budgetMs = PluginInfo.AuditBudgetMs)
        {
            var audit = new SideAuditResult();
            var omittedForTime = new List<string>();
            var auditCtx = new AuditContext(db, tr, espacio, deadlineUtc);
            Dictionary<string, IAuditCheck> checks = AuditCheckRegistry.Discover();

            // Orden de ejecución: el de las reglas descubiertas (Orden, nombre), restringido a las pedidas.
            var runOrder = new List<string>(reglas);
            runOrder.Sort((a, b) =>
            {
                int oa = checks.TryGetValue(a, out IAuditCheck? ca) ? SafeOrder(ca) : int.MaxValue;
                int ob = checks.TryGetValue(b, out IAuditCheck? cb) ? SafeOrder(cb) : int.MaxValue;
                int c = oa.CompareTo(ob);
                return c != 0 ? c : IndexInContract(a).CompareTo(IndexInContract(b));
            });

            foreach (string rule in runOrder)
            {
                var st = new RuleStats { Completo = false };
                audit.Stats[rule] = st;

                if (!checks.TryGetValue(rule, out IAuditCheck? check))
                {
                    audit.Omitted.Add(rule);
                    audit.OwnWarnings.Add("La regla " + rule + " no está implementada en este ensamblado.");
                    continue;
                }

                if (auditCtx.TimeExceeded)
                {
                    audit.Omitted.Add(rule);
                    omittedForTime.Add(rule);
                    continue;
                }

                AuditCheckResult result;
                try
                {
                    result = check.Run(auditCtx) ?? throw new InvalidOperationException("La regla devolvió null.");
                }
                catch (Exception ex)
                {
                    PluginLog.Error("La regla de auditoría " + rule + " falló", ex);
                    audit.Errors.Add(new Dictionary<string, object?>
                    {
                        ["regla"] = rule,
                        ["excepcion"] = ex.GetType().Name,
                    });
                    audit.OwnWarnings.Add("La regla " + rule + " falló (" + ex.GetType().Name + "); se continuó con las demás.");
                    continue;
                }

                st.Completo = result.Completo;
                foreach (Finding f in result.Hallazgos)
                {
                    if (f == null)
                    {
                        continue;
                    }

                    audit.All.Add(f);
                    st.N++;
                    switch (f.Severidad)
                    {
                        case Severity.Alta:
                            st.Alta++;
                            break;
                        case Severity.Media:
                            st.Media++;
                            break;
                        default:
                            st.Baja++;
                            break;
                    }
                }

                foreach (string warning in result.Advertencias)
                {
                    // Las reglas citan los 8 s por defecto; aquí se sabe el presupuesto real de la llamada.
                    audit.OwnWarnings.Add(
                        string.Equals(warning, AuditSupport.WarningPartial, StringComparison.Ordinal)
                            ? FileAuditRules.PartialWarning(budgetMs)
                            : warning);
                }

                if (!result.Completo)
                {
                    audit.OwnWarnings.Add(FileAuditRules.PartialRuleWarning(rule, budgetMs));
                }

                if (string.Equals(rule, CuadroAreasCheck.RuleName, StringComparison.Ordinal))
                {
                    audit.NCuadrosFueraDeLayout = ReadNonNegativeInt(result.Resumen, "n_cuadros_fuera_de_layout");
                }

                if (string.Equals(rule, TextosEscalaCheck.RuleName, StringComparison.Ordinal) && result.Completo)
                {
                    audit.MedianaTexto = FileAuditRules.FiniteOrNull(
                        result.Resumen.TryGetValue("mediana", out object? mediana) ? mediana as double? : null);
                }

                LogSummary(tool, rule, result);
            }

            if (omittedForTime.Count > 0)
            {
                audit.OwnWarnings.Add("Se omitieron por falta de tiempo: " + string.Join(", ", omittedForTime) + ".");
            }

            // Orden y ids del contrato (§5C.0): severidad, regla (tabla §5C.8), llegada.
            Finding.Sort(audit.All, AuditCheckRegistry.ContractRules);
            Finding.AssignIds(audit.All);

            // Omitidas en el orden del contrato.
            audit.Omitted.Sort((a, b) => IndexInContract(a).CompareTo(IndexInContract(b)));
            return audit;
        }

        private static int IndexInContract(string rule)
        {
            for (int i = 0; i < AuditCheckRegistry.ContractRules.Count; i++)
            {
                if (string.Equals(AuditCheckRegistry.ContractRules[i], rule, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return AuditCheckRegistry.ContractRules.Count;
        }

        private static int SafeOrder(IAuditCheck check)
        {
            try
            {
                return check.Orden;
            }
            catch (Exception)
            {
                return int.MaxValue;
            }
        }

        /// <summary>Entero ≥ 0 de un resumen de regla; 0 si falta o no es numérico.</summary>
        private static int ReadNonNegativeInt(Dictionary<string, object?> resumen, string key)
        {
            if (resumen == null || !resumen.TryGetValue(key, out object? v) || v == null)
            {
                return 0;
            }

            try
            {
                int n = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                return n < 0 ? 0 : n;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void LogSummary(string tool, string rule, AuditCheckResult result)
        {
            try
            {
                var parts = new List<string>();
                foreach (KeyValuePair<string, object?> kv in result.Resumen)
                {
                    parts.Add(kv.Key + "=" + Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
                }

                PluginLog.Info(tool + ": regla " + rule + " → " + result.Hallazgos.Count + " hallazgo(s), completo="
                    + (result.Completo ? "sí" : "no") + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : ""));
            }
            catch (Exception)
            {
                // El log nunca tumba la auditoría.
            }
        }
    }

    /// <summary>Resultado de <see cref="SideAudit.Run"/>; no guarda ningún objeto de la base.</summary>
    internal sealed class SideAuditResult
    {
        /// <summary>Hallazgos ordenados y con id.</summary>
        public List<Finding> All { get; } = new List<Finding>();

        public Dictionary<string, RuleStats> Stats { get; } = new Dictionary<string, RuleStats>(StringComparer.Ordinal);

        /// <summary>reglas_omitidas, en el orden del contrato.</summary>
        public List<string> Omitted { get; } = new List<string>();

        /// <summary>reglas_con_error.</summary>
        public List<Dictionary<string, object?>> Errors { get; } = new List<Dictionary<string, object?>>();

        public List<string> OwnWarnings { get; } = new List<string>();

        /// <summary>§5C.8: resumen.n_cuadros_fuera_de_layout (0 si verificar_cuadro_areas no se ejecutó o falló).</summary>
        public int NCuadrosFueraDeLayout { get; set; }

        /// <summary>Mediana de alturas de texto del espacio; null si textos_fuera_de_escala no se ejecutó completa.</summary>
        public double? MedianaTexto { get; set; }

        /// <summary>Bloque <c>resumen</c> de §5C.8; por_regla lleva solo <paramref name="reglas"/>, en ese orden.</summary>
        public Dictionary<string, object?> BuildResumen(IReadOnlyList<string> reglas)
        {
            int alta = 0, media = 0, baja = 0;
            foreach (Finding f in All)
            {
                if (f.Severidad == Severity.Alta)
                {
                    alta++;
                }
                else if (f.Severidad == Severity.Media)
                {
                    media++;
                }
                else
                {
                    baja++;
                }
            }

            var perRule = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string rule in reglas)
            {
                perRule[rule] = (Stats.TryGetValue(rule, out RuleStats? st) ? st : new RuleStats()).ToDictionary();
            }

            return new Dictionary<string, object?>
            {
                ["total_hallazgos"] = All.Count,
                ["por_severidad"] = new Dictionary<string, object?>
                {
                    ["alta"] = alta,
                    ["media"] = media,
                    ["baja"] = baja,
                },
                ["por_regla"] = perRule,
                ["n_cuadros_fuera_de_layout"] = NCuadrosFueraDeLayout,
            };
        }

        /// <summary>Página de hallazgos (§2.4) y cursor siguiente.</summary>
        public (List<Dictionary<string, object?>> page, string? next) Page(int start, int limit)
        {
            (int count, string? next) = ParamReader.Slice(All.Count, start, limit);
            var page = new List<Dictionary<string, object?>>(count);
            for (int i = start; i < start + count; i++)
            {
                page.Add(All[i].ToDictionary());
            }

            return (page, next);
        }
    }

    /// <summary>Conteos de una regla para resumen.por_regla.</summary>
    internal sealed class RuleStats
    {
        public int N;
        public int Alta;
        public int Media;
        public int Baja;
        public bool Completo;

        public Dictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["n"] = N,
            ["alta"] = Alta,
            ["media"] = Media,
            ["baja"] = Baja,
            ["completo"] = Completo,
        };
    }
}
