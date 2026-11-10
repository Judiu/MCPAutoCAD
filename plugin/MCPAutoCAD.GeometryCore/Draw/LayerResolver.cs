using System;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Plan de capa destino de una escritura (protocol.md §5D.0, regla R3). Puro: no sabe si la capa existe en el
    /// dibujo; eso lo decide el plugin (<c>DrawLayerFactory</c>). El plan dice QUÉ nombre se busca y, si no existe,
    /// CON QUÉ propiedades se crearía (o que no se puede crear).
    /// </summary>
    public sealed class LayerPlan
    {
        public const string OrigenConcepto = "concepto";
        public const string OrigenNombre = "nombre";
        public const string OrigenDefecto = "defecto";

        public LayerPlan(string nombre, string origen, LayerSpec? especificacion, string? concepto)
        {
            Nombre = nombre;
            Origen = origen;
            Especificacion = especificacion;
            Concepto = concepto;
        }

        /// <summary>Nombre de capa que se busca en el dibujo (tal como está en lps.json o como lo escribió el cliente).</summary>
        public string Nombre { get; }

        /// <summary>"concepto" (clave de capas_preferidas), "nombre" (nombre dado por el cliente) o "defecto" (no se dio 'capa').</summary>
        public string Origen { get; }

        /// <summary>Propiedades con que se crearía si no existe; null = no se puede crear (NOT_FOUND si no existe).</summary>
        public LayerSpec? Especificacion { get; }

        /// <summary>Concepto resuelto (p. ej. "muros") o null.</summary>
        public string? Concepto { get; }

        /// <summary>true si, de no existir, el plugin puede crearla.</summary>
        public bool Creable => Especificacion != null;
    }

    /// <summary>
    /// Resolución pura del parámetro 'capa' (R3): (a) un CONCEPTO de lps.json.capas_preferidas (sin distinguir
    /// mayúsculas) → su capa; (b) un nombre cualquiera → se buscará tal cual; si no existe, solo se crea cuando está
    /// en capas_permitidas o capas_mcp (con sus propiedades) o empieza por "MCP-" (color 3, Continuous, grosor
    /// Default, imprimible). Sin 'capa': el concepto por defecto de la herramienta (p. ej. "cotas") si lps.json lo
    /// define, o MCP-DIBUJO. El concepto tiene prioridad sobre una capa existente con el mismo nombre que la clave.
    /// </summary>
    public static class LayerResolver
    {
        /// <summary>Capa por defecto de todas las escrituras (protocol.md §5.4, §5D.0).</summary>
        public const string DefaultLayer = "MCP-DIBUJO";

        /// <summary>Prefijo de las capas del MCP: cualquier nombre con este prefijo es creable.</summary>
        public const string McpPrefix = "MCP-";

        /// <summary>Propiedades de respaldo de una capa MCP-* no definida en lps.json: color 3, Continuous, Default, imprimible.</summary>
        public static readonly LayerSpec McpFallback = new LayerSpec(3, "Continuous", -3, true);

        public const string InvalidName = "capa: nombre de capa no válido";

        /// <summary>
        /// Plan para <paramref name="capa"/> (null = no se dio). <paramref name="conceptoPorDefecto"/> = concepto que usa la
        /// herramienta cuando no se da 'capa' (null = MCP-DIBUJO). <paramref name="reglas"/> = reglas de lps.json
        /// (<see cref="LpsLayerRules.Empty"/> si el estándar no está disponible: entonces solo MCP-* es creable).
        /// Nombre vacío o solo espacios → ArgumentException(<see cref="InvalidName"/>).
        /// </summary>
        public static LayerPlan Resolve(string? capa, string? conceptoPorDefecto, LpsLayerRules reglas)
        {
            if (reglas == null)
            {
                throw new ArgumentNullException(nameof(reglas));
            }

            if (capa == null)
            {
                if (conceptoPorDefecto != null && TryConcept(conceptoPorDefecto, reglas, out string preferida))
                {
                    return new LayerPlan(preferida, LayerPlan.OrigenDefecto, SpecFor(preferida, reglas), conceptoPorDefecto);
                }

                return new LayerPlan(DefaultLayer, LayerPlan.OrigenDefecto, SpecFor(DefaultLayer, reglas), null);
            }

            string trimmed = capa.Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException(InvalidName);
            }

            if (TryConcept(trimmed, reglas, out string nombre))
            {
                return new LayerPlan(nombre, LayerPlan.OrigenConcepto, SpecFor(nombre, reglas), ConceptKey(trimmed, reglas));
            }

            return new LayerPlan(trimmed, LayerPlan.OrigenNombre, SpecFor(trimmed, reglas), null);
        }

        /// <summary>Propiedades de creación para un nombre: capas_permitidas, capas_mcp, respaldo MCP-* o null.</summary>
        public static LayerSpec? SpecFor(string nombre, LpsLayerRules reglas)
        {
            if (reglas.CapasPermitidas.TryGetValue(nombre, out LayerSpec? permitida))
            {
                return permitida;
            }

            if (reglas.CapasMcp.TryGetValue(nombre, out LayerSpec? mcp))
            {
                return mcp;
            }

            return nombre.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase) ? McpFallback : null;
        }

        /// <summary>true si <paramref name="clave"/> es un concepto de capas_preferidas (sin mayúsculas); devuelve su capa.</summary>
        public static bool TryConcept(string clave, LpsLayerRules reglas, out string capa)
        {
            foreach (var kv in reglas.CapasPreferidas)
            {
                if (string.Equals(kv.Key, clave, StringComparison.OrdinalIgnoreCase))
                {
                    capa = kv.Value;
                    return true;
                }
            }

            capa = "";
            return false;
        }

        private static string? ConceptKey(string clave, LpsLayerRules reglas)
        {
            foreach (var kv in reglas.CapasPreferidas)
            {
                if (string.Equals(kv.Key, clave, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Key;
                }
            }

            return null;
        }
    }
}
