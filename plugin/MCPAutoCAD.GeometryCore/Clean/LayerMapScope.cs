using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MCPAutoCAD.GeometryCore.Clean
{
    /// <summary>Qué se hace con una entidad de una capa origen de normalizar_capas según su espacio (§5F.3, 0.6.1).</summary>
    public enum ScopeDecision
    {
        /// <summary>Se reasigna a la capa destino.</summary>
        Reasignar,

        /// <summary>Vive en un espacio que no está en <c>espacios</c>: no se reasigna (cuenta en excluidas.por_espacio).</summary>
        ExcluidaEspacio,

        /// <summary>VIEWPORT de layout (no el n.º 1) sin <c>incluir_viewports</c>: no se reasigna y se avisa.</summary>
        ExcluidaViewport,

        /// <summary>Viewport n.º 1 de un layout (el propio espacio papel): nunca se reasigna.</summary>
        ViewportPapel,
    }

    /// <summary>
    /// Ámbito de normalizar_capas (protocol.md §5F.3, correcciones del gate en vivo 0.6.1): parámetro <c>espacios</c>
    /// ("modelo" y/o nombres de layout, sin distinguir mayúsculas; por defecto todos) y exclusión de los VIEWPORT de
    /// los layouts (el n.º 1 nunca; los demás salvo <c>incluir_viewports: true</c>). Lógica pura: el plugin lee los
    /// nombres de los layouts y el número de cada viewport y decide aquí. Las definiciones de bloque
    /// (<c>incluir_bloques</c>) no dependen de <c>espacios</c>.
    /// </summary>
    public static class LayerMapScope
    {
        /// <summary>Nombre del espacio modelo en <c>espacios</c> y en las respuestas (como SpaceResolver.ModelName).</summary>
        public const string Modelo = "modelo";

        /// <summary>Máximo de elementos de <c>espacios</c>.</summary>
        public const int MaxEspacios = 100;

        /// <summary>Máximo de caracteres de cada elemento de <c>espacios</c>.</summary>
        public const int MaxNombre = 255;

        /// <summary>Detalle INVALID_PARAMS de la forma del parámetro.</summary>
        public static readonly string ShapeError =
            "espacios: debe ser un array de 1 a " + MaxEspacios.ToString(CultureInfo.InvariantCulture)
            + " nombres (\"modelo\" o nombres de layout)";

        /// <summary>Detalle INVALID_PARAMS de un elemento fuera de 1–255 caracteres o que no es cadena.</summary>
        public static string ItemError(int i) =>
            "espacios[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe ser una cadena de 1 a "
            + MaxNombre.ToString(CultureInfo.InvariantCulture) + " caracteres";

        /// <summary>Detalle INVALID_PARAMS de un espacio repetido (sin distinguir mayúsculas): "espacios: espacio repetido '&lt;x&gt;'".</summary>
        public static string DuplicateError(string nombre) => "espacios: espacio repetido '" + nombre + "'";

        /// <summary>
        /// Primer elemento repetido (sin distinguir mayúsculas) de <paramref name="espacios"/> → detalle de
        /// <see cref="DuplicateError"/> con el elemento tal como lo escribió el cliente (la segunda aparición); null si no hay.
        /// </summary>
        public static string? FindDuplicate(IReadOnlyList<string> espacios)
        {
            if (espacios == null)
            {
                throw new ArgumentNullException(nameof(espacios));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string e in espacios)
            {
                if (!seen.Add(e ?? ""))
                {
                    return DuplicateError(e ?? "");
                }
            }

            return null;
        }

        /// <summary>
        /// Resuelve <paramref name="espacios"/> contra los layouts del dibujo (<paramref name="layouts"/>, nombres reales en
        /// orden de pestañas). null → todos (<paramref name="resueltos"/> = null). Cada elemento: "modelo" (sin mayúsculas)
        /// → "modelo"; si no, el nombre real del layout que coincide sin distinguir mayúsculas. Devuelve false con
        /// <paramref name="inexistentes"/> = TODOS los que no existen (tal como los escribió el cliente, en su orden).
        /// <paramref name="resueltos"/> conserva el orden del cliente.
        /// </summary>
        public static bool TryResolve(
            IReadOnlyList<string>? espacios,
            IReadOnlyList<string> layouts,
            out IReadOnlyList<string>? resueltos,
            out IReadOnlyList<string> inexistentes)
        {
            if (layouts == null)
            {
                throw new ArgumentNullException(nameof(layouts));
            }

            inexistentes = Array.Empty<string>();
            resueltos = null;
            if (espacios == null)
            {
                return true;
            }

            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string l in layouts)
            {
                if (!string.IsNullOrEmpty(l) && !lookup.ContainsKey(l))
                {
                    lookup[l] = l;
                }
            }

            // "modelo" (sin mayúsculas) es siempre el espacio modelo, aunque exista un layout con ese nombre (O2).
            var ok = new List<string>(espacios.Count);
            var missing = new List<string>();
            foreach (string e in espacios)
            {
                string name = e ?? "";
                if (string.Equals(name, Modelo, StringComparison.OrdinalIgnoreCase))
                {
                    ok.Add(Modelo);
                }
                else if (lookup.TryGetValue(name, out string? real))
                {
                    ok.Add(real);
                }
                else
                {
                    missing.Add(name);
                }
            }

            if (missing.Count > 0)
            {
                inexistentes = missing;
                return false;
            }

            resueltos = ok;
            return true;
        }

        /// <summary>
        /// Detalle (y mensaje) del NOT_FOUND de <c>espacios</c>: "espacios: layouts inexistentes: A, B (espacios del dibujo:
        /// modelo, Layout1, Layout2)".
        /// </summary>
        public static string MissingDetail(IReadOnlyList<string> inexistentes, IReadOnlyList<string> layouts)
        {
            var sb = new StringBuilder("espacios: layouts inexistentes: ");
            sb.Append(string.Join(", ", inexistentes));
            sb.Append(" (espacios del dibujo: ").Append(Modelo);
            foreach (string l in layouts)
            {
                sb.Append(", ").Append(l);
            }

            return sb.Append(')').ToString();
        }

        /// <summary>
        /// true si el espacio <paramref name="espacio"/> ("modelo" o nombre de layout) está en el ámbito: todos si
        /// <paramref name="resueltos"/> es null; si no, coincidencia sin distinguir mayúsculas. Equivale a
        /// <see cref="Includes(IReadOnlyList{string}, string, bool)"/> con esModelo = (espacio es "modelo").
        /// </summary>
        public static bool Includes(IReadOnlyList<string>? resueltos, string espacio) =>
            Includes(resueltos, espacio, string.Equals(espacio, Modelo, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Como <see cref="Includes(IReadOnlyList{string}, string)"/> pero sabiendo si el espacio es el modelo
        /// (<paramref name="esModelo"/>): el modelo solo coincide con la entrada "modelo"; un layout solo con las entradas
        /// resueltas a layouts. Limitación documentada (O2): un layout llamado literalmente "modelo" (sin distinguir
        /// mayúsculas) no es direccionable con <c>espacios</c>, porque "modelo" siempre significa el espacio modelo; ese
        /// layout solo entra cuando se omite <c>espacios</c>.
        /// </summary>
        public static bool Includes(IReadOnlyList<string>? resueltos, string espacio, bool esModelo)
        {
            if (resueltos == null)
            {
                return true;
            }

            foreach (string r in resueltos)
            {
                bool rModelo = string.Equals(r, Modelo, StringComparison.Ordinal);
                if (esModelo ? rModelo : (!rModelo && string.Equals(r, espacio, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Decisión para una entidad (o ATTRIB) de una capa origen que vive en un espacio (modelo o layout; no dentro de
        /// una definición de bloque). Orden: espacio fuera del ámbito → <see cref="ScopeDecision.ExcluidaEspacio"/>
        /// (también los viewports de ese layout); viewport n.º 1 → <see cref="ScopeDecision.ViewportPapel"/> (siempre);
        /// otro VIEWPORT de layout sin <paramref name="incluirViewports"/> → <see cref="ScopeDecision.ExcluidaViewport"/>;
        /// el resto → <see cref="ScopeDecision.Reasignar"/>.
        /// </summary>
        public static ScopeDecision Classify(bool espacioIncluido, bool esViewport, bool esViewportPapel, bool incluirViewports)
        {
            if (!espacioIncluido)
            {
                return ScopeDecision.ExcluidaEspacio;
            }

            if (esViewport && esViewportPapel)
            {
                return ScopeDecision.ViewportPapel;
            }

            if (esViewport && !incluirViewports)
            {
                return ScopeDecision.ExcluidaViewport;
            }

            return ScopeDecision.Reasignar;
        }

        /// <summary>
        /// Advertencia de viewports excluidos (sin incluir_viewports): "&lt;n&gt; viewport(s) de layout en capas origen no se
        /// reasignaron (Layout1 2, Layout2 1); pase incluir_viewports: true para reasignarlos." null si no hay ninguno.
        /// <paramref name="porLayout"/> en el orden de los layouts; los de conteo 0 se omiten.
        /// </summary>
        public static string? ExcludedViewportsWarning(IReadOnlyList<KeyValuePair<string, int>> porLayout)
        {
            if (porLayout == null)
            {
                throw new ArgumentNullException(nameof(porLayout));
            }

            int total = 0;
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in porLayout)
            {
                if (kv.Value <= 0)
                {
                    continue;
                }

                total += kv.Value;
                parts.Add(kv.Key + " " + kv.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (total == 0)
            {
                return null;
            }

            return total.ToString(CultureInfo.InvariantCulture) + " viewport(s) de layout en capas origen no se reasignaron ("
                + string.Join(", ", parts) + "); pase incluir_viewports: true para reasignarlos.";
        }
    }
}
