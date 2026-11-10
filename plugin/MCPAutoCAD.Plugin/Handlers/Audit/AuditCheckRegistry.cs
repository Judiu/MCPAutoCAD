using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// Reglas de auditoría por descubrimiento (mismo patrón que <see cref="ToolRegistry"/>): toda clase concreta del
    /// ensamblado que implementa <see cref="IAuditCheck"/> con constructor público sin parámetros. Ninguna regla se
    /// referencia por nombre de clase: si una no existe, auditar_dibujo la reporta como omitida.
    /// </summary>
    internal static class AuditCheckRegistry
    {
        /// <summary>Las 8 reglas del contrato en el orden fijo de protocol.md §5C.8.</summary>
        public static readonly IReadOnlyList<string> ContractRules = new[]
        {
            "auditar_capas",
            "entidades_en_capa_0",
            "auditar_bloques",
            "verificar_cuadro_areas",
            "duplicados",
            "textos_fuera_de_escala",
            "xrefs_no_resueltas",
            "unidades",
        };

        /// <summary>Tipos descubiertos una sola vez (la reflexión no cambia mientras el ensamblado esté cargado).</summary>
        private static readonly Lazy<Type[]> CheckTypes = new Lazy<Type[]>(FindTypes, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        public static bool IsContractRule(string regla)
        {
            foreach (string r in ContractRules)
            {
                if (string.Equals(r, regla, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Instancias nuevas de las reglas, indexadas por <see cref="IAuditCheck.Regla"/> y ordenables por
        /// <see cref="IAuditCheck.Orden"/> y nombre. Un tipo que lance al instanciarse se registra y se omite;
        /// una regla duplicada conserva la primera (orden por nombre completo del tipo) y se avisa en el log.
        /// </summary>
        public static Dictionary<string, IAuditCheck> Discover()
        {
            var found = new Dictionary<string, IAuditCheck>(StringComparer.Ordinal);
            foreach (Type type in CheckTypes.Value)
            {
                IAuditCheck check;
                string regla;
                try
                {
                    check = (IAuditCheck)Activator.CreateInstance(type)!;
                    regla = check.Regla;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("No se pudo crear la regla de auditoría " + type.FullName + "; se omite", ex);
                    continue;
                }

                if (string.IsNullOrEmpty(regla))
                {
                    PluginLog.Error("La regla de auditoría " + type.FullName + " tiene un nombre vacío; se omite");
                    continue;
                }

                if (found.ContainsKey(regla))
                {
                    PluginLog.Warn("Regla de auditoría '" + regla + "' duplicada (" + type.FullName + "); se conserva la primera");
                    continue;
                }

                if (!IsContractRule(regla))
                {
                    PluginLog.Warn("Regla de auditoría '" + regla + "' (" + type.FullName + ") no figura en el contrato; no se ejecutará");
                }

                found.Add(regla, check);
            }

            return found;
        }

        private static Type[] FindTypes()
        {
            Type[] types;
            try
            {
                types = typeof(AuditCheckRegistry).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                PluginLog.Error("No se pudieron cargar todos los tipos del plugin; se registran las reglas disponibles", ex);
                types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
            }

            return types
                .Where(t => !t.IsAbstract && !t.IsInterface && !t.ContainsGenericParameters
                    && typeof(IAuditCheck).IsAssignableFrom(t)
                    && t.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
