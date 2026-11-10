using System;
using System.Collections.Generic;
using System.Globalization;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.GeometryCore.Clean
{
    /// <summary>Estado de una capa del dibujo que el validador necesita (lo lee el plugin).</summary>
    public sealed record LayerState(string Nombre, bool Bloqueada, bool Congelada, bool Xref, bool EsActual);

    /// <summary>Entrada del mapa ya validada: capa origen real del dibujo → plan de destino (R3).</summary>
    public sealed record LayerMapEntry(string Origen, LayerPlan Destino, bool DestinoExiste);

    /// <summary>Rechazo del mapa (§5F.3): código, detalle exacto y datos para error.data.</summary>
    public sealed record LayerMapError(string Codigo, string Detalle, IReadOnlyList<string>? Capas, string? Capa);

    /// <summary>
    /// Validación pura del parámetro <c>mapa</c> de normalizar_capas (protocol.md §5F.3): orígenes existentes,
    /// destinos resueltos por R3 (<see cref="LayerResolver"/>), sin ciclos, sin destino que sea a la vez origen, sin
    /// origen = destino, sin capas bloqueadas/congeladas/xref, sin "0" como origen con incluir_bloques, nunca
    /// DEFPOINTS como origen y nunca una capa del MCP (MCP-* o capas_mcp) como origen ni como destino. Nunca lanza por los datos del mapa: todo rechazo vuelve como <see cref="LayerMapError"/>.
    /// </summary>
    public static class LayerMapValidator
    {
        public const string CodigoNotFound = "NOT_FOUND";
        public const string CodigoInvalidParams = "INVALID_PARAMS";

        public const string DefpointsOrigin = "mapa: la capa DEFPOINTS no se normaliza";
        public const string ZeroWithBlocks =
            "mapa: la capa 0 no puede ser origen con incluir_bloques: true (la capa 0 dentro de los bloques es correcta)";
        public const string LockedLayer = "capa bloqueada";
        public const string FrozenLayer = "capa congelada";
        public const string XrefLayer = "capa dependiente de xref";
        public const string EmptyOrigin = "mapa: la capa origen no puede estar vacía";

        /// <summary>
        /// Caracteres que SymbolUtilityServices.ValidateSymbolName rechaza en un nombre de capa nuevo
        /// (la barra vertical se trata aparte: es la de las capas dependientes de xref).
        /// </summary>
        private const string InvalidNameChars = "<>/\\\":;?*,=`";

        /// <summary>
        /// Valida y normaliza el mapa. <paramref name="mapa"/> = pares origen → destino tal como los escribió el
        /// cliente (claves distintas; una clave repetida sin distinguir mayúsculas → INVALID_PARAMS
        /// "mapa: origen repetido '&lt;x&gt;'"); <paramref name="capas"/> = capas existentes del dibujo (sin distinguir
        /// mayúsculas); <paramref name="reglas"/> = lps.json; <paramref name="incluirBloques"/> = parámetro.
        /// Orden de comprobación (la primera que falla devuelve el error):
        /// 1. Origen vacío o solo espacios → INVALID_PARAMS <see cref="EmptyOrigin"/> (antes que los inexistentes: un
        ///    NOT_FOUND con un nombre vacío no diría nada). Orígenes inexistentes (todos) → NOT_FOUND, Detalle =
        ///    "mapa: capas de origen inexistentes: A, B", Capas = lista.
        /// 2. Origen "DEFPOINTS" → INVALID_PARAMS "mapa: la capa DEFPOINTS no se normaliza"; origen del MCP (empieza
        ///    por "MCP-" sin distinguir mayúsculas o está en capas_mcp) → INVALID_PARAMS <see cref="McpOrigin"/> (Capa =
        ///    nombre real): las entidades del MCP (previsualizaciones, observaciones, dibujo del MCP) no se convierten en
        ///    entidades del usuario; origen "0" con incluirBloques → "mapa: la capa 0 no puede ser origen con
        ///    incluir_bloques: true (la capa 0 dentro de los bloques es correcta)".
        /// 3. Destino, entrada por entrada: nombre no válido como símbolo (vacío, solo espacios, espacios al principio
        ///    o al final, alguno de &lt;&gt;/\":;?*,=` o una barra vertical en un nombre que no es una capa existente, como
        ///    SymbolUtilityServices.ValidateSymbolName) → INVALID_PARAMS <see cref="LayerResolver.InvalidName"/> (Capa =
        ///    destino tal como lo escribió el cliente), antes que el rechazo del MCP y que el NOT_FOUND; una capa
        ///    existente con barra vertical (de xref) sigue a la comprobación 6. Después se resuelve con
        ///    <see cref="LayerResolver.Resolve"/>(destino, null, reglas) (concepto → su capa; nombre existente → tal cual). Destino del MCP (empieza por "MCP-" sin distinguir mayúsculas o está en capas_mcp; exista
        ///    o no, por nombre o por concepto) → INVALID_PARAMS <see cref="McpDestination"/> (Capa = d): las entidades
        ///    del usuario no pasan a capas del MCP (§5D.8). Después, inexistente creable (capas_permitidas) → se
        ///    creará; inexistente no creable → NOT_FOUND <see cref="MissingDestination"/> (Capa = d).
        /// 4. Origen = destino resuelto (sin mayúsculas) → INVALID_PARAMS "mapa['&lt;origen&gt;']: origen y destino son la misma capa".
        /// 5. Destino resuelto que es también origen → INVALID_PARAMS "mapa: el destino '&lt;d&gt;' es también origen
        ///    (cadena o ciclo); aplane el mapa para que cada origen vaya directamente a su capa final".
        /// 6. Origen o destino existente bloqueado → INVALID_PARAMS "capa bloqueada" (Capa); congelado → "capa congelada";
        ///    xref → "capa dependiente de xref".
        /// Devuelve las entradas en el orden del mapa, con el nombre REAL de la capa origen (mayúsculas del dibujo).
        /// Detalles de implementación: dentro de la comprobación 2 se evalúan DEFPOINTS, luego el origen del MCP,
        /// luego "0" y luego los repetidos, cada una sobre todo el mapa; en los mensajes "mapa['&lt;origen&gt;']" el origen va tal como lo
        /// escribió el cliente; si el destino existe, el plan lleva el nombre real de la capa del dibujo; en 6 se
        /// revisa cada entrada en orden (origen y después destino) y, por capa, bloqueada → congelada → xref. Con
        /// error, <paramref name="entradas"/> queda vacía.
        /// </summary>
        public static LayerMapError? Validate(
            IReadOnlyList<KeyValuePair<string, string>> mapa,
            IReadOnlyDictionary<string, LayerState> capas,
            LpsLayerRules reglas,
            bool incluirBloques,
            out IReadOnlyList<LayerMapEntry> entradas)
        {
            if (mapa == null)
            {
                throw new ArgumentNullException(nameof(mapa));
            }

            if (capas == null)
            {
                throw new ArgumentNullException(nameof(capas));
            }

            reglas ??= LpsLayerRules.Empty;
            entradas = Array.Empty<LayerMapEntry>();

            var lookup = new Dictionary<string, LayerState>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in capas)
            {
                if (kv.Value == null)
                {
                    continue;
                }

                string name = string.IsNullOrEmpty(kv.Value.Nombre) ? kv.Key : kv.Value.Nombre;
                if (name != null)
                {
                    lookup[name] = kv.Value with { Nombre = name };
                }
            }

            // 1. Origen vacío; después orígenes inexistentes (todos, sin repetir).
            foreach (var kv in mapa)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    return Invalid(EmptyOrigin, null);
                }
            }

            var missing = new List<string>();
            var missingSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in mapa)
            {
                string origen = kv.Key ?? "";
                if (!lookup.ContainsKey(origen) && missingSeen.Add(origen))
                {
                    missing.Add(origen);
                }
            }

            if (missing.Count > 0)
            {
                return new LayerMapError(
                    CodigoNotFound, "mapa: capas de origen inexistentes: " + string.Join(", ", missing), missing, null);
            }

            // 2. DEFPOINTS, "0" con incluir_bloques, repetidos.
            foreach (var kv in mapa)
            {
                if (string.Equals(kv.Key, "DEFPOINTS", StringComparison.OrdinalIgnoreCase))
                {
                    return Invalid(DefpointsOrigin, lookup[kv.Key].Nombre);
                }
            }

            // Corrección de auditoría (ronda 2): un origen del MCP hacia una capa del usuario convertiría
            // previsualizaciones u observaciones en entidades del usuario (y el destino MCP-* ya está prohibido).
            foreach (var kv in mapa)
            {
                LayerState origenState = lookup[kv.Key];
                if (IsMcpLayer(origenState.Nombre, reglas))
                {
                    return Invalid(McpOrigin(kv.Key, origenState.Nombre), origenState.Nombre);
                }
            }

            if (incluirBloques)
            {
                foreach (var kv in mapa)
                {
                    if (string.Equals(kv.Key, "0", StringComparison.Ordinal))
                    {
                        return Invalid(ZeroWithBlocks, "0");
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in mapa)
            {
                if (!seen.Add(kv.Key))
                {
                    return Invalid("mapa: origen repetido '" + kv.Key + "'", null);
                }
            }

            // 3. Destinos por R3.
            var origins = new List<string>(mapa.Count);
            var plans = new List<LayerPlan>(mapa.Count);
            var exists = new List<bool>(mapa.Count);
            foreach (var kv in mapa)
            {
                origins.Add(lookup[kv.Key].Nombre);
                string destinoCliente = kv.Value ?? "";
                if (!IsValidDestinationName(destinoCliente, lookup.ContainsKey(destinoCliente)))
                {
                    return Invalid(LayerResolver.InvalidName, destinoCliente);
                }

                LayerPlan plan;
                try
                {
                    plan = LayerResolver.Resolve(kv.Value ?? "", null, reglas);
                }
                catch (ArgumentException ex)
                {
                    return Invalid(ex.Message, null);
                }

                bool destExists = lookup.TryGetValue(plan.Nombre, out LayerState? destState);
                if (destExists && !string.Equals(destState!.Nombre, plan.Nombre, StringComparison.Ordinal))
                {
                    plan = new LayerPlan(destState.Nombre, plan.Origen, plan.Especificacion, plan.Concepto);
                }

                // Corrección de auditoría (ronda 1): mover entidades del usuario a una capa del MCP les quitaría la
                // protección de §5D.8 (borrar/mover sin confirmar, limpiar_observaciones en masa, huérfanas de
                // MCP-PREVIEW). Se rechaza cualquier destino MCP-* o de capas_mcp, exista o no.
                if (IsMcpLayer(plan.Nombre, reglas))
                {
                    return Invalid(McpDestination(kv.Key, plan.Nombre), plan.Nombre);
                }

                if (destExists)
                {
                    exists.Add(true);
                }
                else
                {
                    if (!plan.Creable)
                    {
                        return new LayerMapError(CodigoNotFound, MissingDestination(kv.Key, plan.Nombre), null, plan.Nombre);
                    }

                    exists.Add(false);
                }

                plans.Add(plan);
            }

            // 4. Origen = destino.
            for (int i = 0; i < mapa.Count; i++)
            {
                if (string.Equals(origins[i], plans[i].Nombre, StringComparison.OrdinalIgnoreCase))
                {
                    return Invalid("mapa['" + mapa[i].Key + "']: origen y destino son la misma capa", origins[i]);
                }
            }

            // 5. Destino que es también origen (cadena o ciclo).
            var originSet = new HashSet<string>(origins, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < mapa.Count; i++)
            {
                if (originSet.Contains(plans[i].Nombre))
                {
                    return Invalid(
                        "mapa: el destino '" + plans[i].Nombre
                            + "' es también origen (cadena o ciclo); aplane el mapa para que cada origen vaya directamente a su capa final",
                        plans[i].Nombre);
                }
            }

            // 6. Bloqueadas, congeladas, xref (origen y destino existente).
            for (int i = 0; i < mapa.Count; i++)
            {
                LayerMapError? err = StateError(lookup[origins[i]]);
                if (err != null)
                {
                    return err;
                }

                if (exists[i])
                {
                    err = StateError(lookup[plans[i].Nombre]);
                    if (err != null)
                    {
                        return err;
                    }
                }
            }

            var result = new List<LayerMapEntry>(mapa.Count);
            for (int i = 0; i < mapa.Count; i++)
            {
                result.Add(new LayerMapEntry(origins[i], plans[i], exists[i]));
            }

            entradas = result;
            return null;
        }

        /// <summary>Advertencia de propiedades forzadas (§5F.3): "&lt;n&gt; entidad(es) reasignada(s) conservan color, tipo de línea o grosor propios (no ByLayer); no se cambiaron."</summary>
        public static string ForcedPropertiesWarning(int n)
        {
            if (n < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(n));
            }

            return n.ToString(CultureInfo.InvariantCulture)
                + " entidad(es) reasignada(s) conservan color, tipo de línea o grosor propios (no ByLayer); no se cambiaron.";
        }

        /// <summary>
        /// Detalle del rechazo de un destino del MCP (§5F.3, comprobación 3): "mapa['&lt;origen&gt;']: la capa destino
        /// '&lt;d&gt;' es una capa del MCP (MCP-*); las entidades del usuario no se mueven a capas del MCP".
        /// </summary>
        public static string McpDestination(string origen, string destino) =>
            "mapa['" + origen + "']: la capa destino '" + destino
                + "' es una capa del MCP (MCP-*); las entidades del usuario no se mueven a capas del MCP";

        /// <summary>
        /// Detalle del rechazo de un origen del MCP (§5F.3, comprobación 2): "mapa['&lt;origen&gt;']: la capa origen
        /// '&lt;o&gt;' es una capa del MCP (MCP-*); sus entidades no se mueven a capas del usuario".
        /// </summary>
        public static string McpOrigin(string origen, string nombreReal) =>
            "mapa['" + origen + "']: la capa origen '" + nombreReal
                + "' es una capa del MCP (MCP-*); sus entidades no se mueven a capas del usuario";

        /// <summary>
        /// Detalle del NOT_FOUND de un destino inexistente no creable (§5F.3, comprobación 3): "mapa['&lt;origen&gt;']: la
        /// capa destino '&lt;d&gt;' no existe y no está en lps.json (capas_permitidas)". Propio de normalizar_capas: aquí
        /// solo se crean capas de capas_permitidas (las MCP-* están prohibidas como destino); el texto de las
        /// herramientas de dibujo (DrawLayerFactory.NotCreatable, donde MCP-* sí se crea) no cambia.
        /// </summary>
        public static string MissingDestination(string origen, string destino) =>
            "mapa['" + origen + "']: la capa destino '" + destino + "' no existe y no está en lps.json (capas_permitidas)";

        /// <summary>
        /// Nombre de destino válido como símbolo de AutoCAD (mismo criterio que SymbolUtilityServices.ValidateSymbolName):
        /// no vacío, sin espacios al principio ni al final y sin &lt;&gt;/\":;?*,=`; la barra vertical solo se admite si el
        /// nombre es una capa existente (dependiente de xref: la rechaza la comprobación 6 con su propio mensaje).
        /// </summary>
        private static bool IsValidDestinationName(string nombre, bool existe)
        {
            if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length != nombre.Length)
            {
                return false;
            }

            if (nombre.IndexOfAny(InvalidNameChars.ToCharArray()) >= 0)
            {
                return false;
            }

            return existe || nombre.IndexOf('|') < 0;
        }

        /// <summary>Capa del MCP: empieza por "MCP-" (sin distinguir mayúsculas) o está en capas_mcp de lps.json.</summary>
        private static bool IsMcpLayer(string nombre, LpsLayerRules reglas)
        {
            if (nombre.StartsWith(LayerResolver.McpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (reglas.CapasMcp != null)
            {
                foreach (string k in reglas.CapasMcp.Keys)
                {
                    if (string.Equals(k, nombre, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static LayerMapError Invalid(string detalle, string? capa) =>
            new LayerMapError(CodigoInvalidParams, detalle, null, capa);

        private static LayerMapError? StateError(LayerState s)
        {
            if (s.Bloqueada)
            {
                return Invalid(LockedLayer, s.Nombre);
            }

            if (s.Congelada)
            {
                return Invalid(FrozenLayer, s.Nombre);
            }

            return s.Xref ? Invalid(XrefLayer, s.Nombre) : null;
        }
    }
}
