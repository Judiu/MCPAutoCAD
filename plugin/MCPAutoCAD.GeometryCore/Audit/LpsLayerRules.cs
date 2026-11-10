using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>Propiedades de una capa definida en lps.json (capas_permitidas / capas_mcp).</summary>
    public sealed class LayerSpec
    {
        public LayerSpec(int color, string tipoLinea, int grosor, bool plot)
        {
            Color = color;
            TipoLinea = tipoLinea ?? "";
            Grosor = grosor;
            Plot = plot;
        }

        /// <summary>Índice ACI.</summary>
        public int Color { get; }

        /// <summary>Nombre del tipo de línea (se compara sin mayúsculas).</summary>
        public string TipoLinea { get; }

        /// <summary>LineWeight en centésimas de mm (-3 = Default, -2 = ByBlock, -1 = ByLayer).</summary>
        public int Grosor { get; }

        public bool Plot { get; }
    }

    /// <summary>
    /// Reglas de capas del estándar LPS (protocol.md §2.6) leídas del JsonElement completo de lps.json.
    /// Tolerante: las secciones ausentes o mal formadas se ignoran (quedan vacías); nunca lanza.
    /// </summary>
    public sealed class LpsLayerRules
    {
        private LpsLayerRules(
            bool disponible,
            Dictionary<string, LayerSpec> permitidas,
            Dictionary<string, LayerSpec> mcp,
            Dictionary<string, string> preferidas,
            List<string> patrones,
            Dictionary<string, string> mapa)
        {
            Disponible = disponible;
            CapasPermitidas = permitidas;
            CapasMcp = mcp;
            CapasPreferidas = preferidas;
            PatronesExternos = patrones;
            MapaNormalizacion = mapa;
        }

        /// <summary>true si se construyó desde un objeto JSON (el estándar está disponible).</summary>
        public bool Disponible { get; }

        /// <summary>capas_permitidas: nombre (sin distinguir mayúsculas) → propiedades.</summary>
        public IReadOnlyDictionary<string, LayerSpec> CapasPermitidas { get; }

        /// <summary>capas_mcp: nombre (sin distinguir mayúsculas) → propiedades.</summary>
        public IReadOnlyDictionary<string, LayerSpec> CapasMcp { get; }

        /// <summary>capas_preferidas: concepto (clave tal cual, p. ej. "muros") → nombre de capa.</summary>
        public IReadOnlyDictionary<string, string> CapasPreferidas { get; }

        /// <summary>Todos los patrones de grupos_externos_permitidos (con '*'), en orden de aparición.</summary>
        public IReadOnlyList<string> PatronesExternos { get; }

        /// <summary>
        /// Clave OPCIONAL <c>mapa_normalizacion</c> (estándar v2, 2026-10-28): capa origen (sin distinguir mayúsculas) →
        /// capa destino decidida por el usuario al normalizar un plano (docs/mapa_capas_6x12.json). auditar_capas la usa
        /// como <c>sugerencia</c>/<c>sugerencia_patron</c> con prioridad sobre la heurística (§5C.1). Se ignoran los pares
        /// con valor que no sea cadena no vacía y los de origen que esté en capas_permitidas o capas_mcp (esas capas no
        /// tienen sugerencia). Un destino que sea un concepto de capas_preferidas (p. ej. "muros") se traduce a su capa.
        /// </summary>
        public IReadOnlyDictionary<string, string> MapaNormalizacion { get; }

        /// <summary>Reglas vacías (estándar no disponible).</summary>
        public static LpsLayerRules Empty { get; } = new LpsLayerRules(
            false,
            new Dictionary<string, LayerSpec>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, LayerSpec>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new List<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Construye las reglas desde la raíz de lps.json (<c>LpsStandard.Root</c>). null o no objeto → <see cref="Empty"/>.
        /// </summary>
        public static LpsLayerRules FromJson(JsonElement? root)
        {
            if (root == null || root.Value.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            JsonElement r = root.Value;
            var permitidas = ReadSpecs(r, "capas_permitidas");
            var mcp = ReadSpecs(r, "capas_mcp");

            var preferidas = new Dictionary<string, string>(StringComparer.Ordinal);
            if (r.TryGetProperty("capas_preferidas", out JsonElement pref) && pref.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in pref.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && !preferidas.ContainsKey(p.Name))
                    {
                        string? v = p.Value.GetString();
                        if (!string.IsNullOrEmpty(v))
                        {
                            preferidas.Add(p.Name, v);
                        }
                    }
                }
            }

            var patrones = new List<string>();
            if (r.TryGetProperty("grupos_externos_permitidos", out JsonElement groups) && groups.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty g in groups.EnumerateObject())
                {
                    if (g.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement item in g.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string? s = item.GetString();
                            if (!string.IsNullOrEmpty(s))
                            {
                                patrones.Add(s);
                            }
                        }
                    }
                }
            }

            var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (r.TryGetProperty("mapa_normalizacion", out JsonElement map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in map.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Name)
                        || mapa.ContainsKey(p.Name) || permitidas.ContainsKey(p.Name) || mcp.ContainsKey(p.Name))
                    {
                        continue;
                    }

                    string? destino = p.Value.GetString();
                    if (string.IsNullOrWhiteSpace(destino))
                    {
                        continue;
                    }

                    // Destino dado como concepto (clave de capas_preferidas, sin distinguir mayúsculas) → su capa.
                    foreach (var kv in preferidas)
                    {
                        if (string.Equals(kv.Key, destino, StringComparison.OrdinalIgnoreCase))
                        {
                            destino = kv.Value;
                            break;
                        }
                    }

                    mapa.Add(p.Name, destino);
                }
            }

            return new LpsLayerRules(true, permitidas, mcp, preferidas, patrones, mapa);
        }

        /// <summary>
        /// true si el nombre cumple el patrón con '*' (cualquier secuencia, incluida la vacía), sin distinguir
        /// mayúsculas y anclado a toda la cadena.
        /// </summary>
        public static bool WildcardMatch(string name, string pattern)
        {
            if (name == null || pattern == null)
            {
                return false;
            }

            string n = name.ToUpperInvariant();
            string p = pattern.ToUpperInvariant();

            // Algoritmo voraz con retroceso al último '*': O(n·m) en el peor caso, sin regex.
            int ni = 0, pi = 0, star = -1, mark = 0;
            while (ni < n.Length)
            {
                if (pi < p.Length && p[pi] != '*' && p[pi] == n[ni])
                {
                    ni++;
                    pi++;
                }
                else if (pi < p.Length && p[pi] == '*')
                {
                    star = pi++;
                    mark = ni;
                }
                else if (star >= 0)
                {
                    pi = star + 1;
                    ni = ++mark;
                }
                else
                {
                    return false;
                }
            }

            while (pi < p.Length && p[pi] == '*')
            {
                pi++;
            }

            return pi == p.Length;
        }

        private static Dictionary<string, LayerSpec> ReadSpecs(JsonElement root, string section)
        {
            var specs = new Dictionary<string, LayerSpec>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty(section, out JsonElement obj) || obj.ValueKind != JsonValueKind.Object)
            {
                return specs;
            }

            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object || specs.ContainsKey(p.Name))
                {
                    continue;
                }

                JsonElement v = p.Value;
                if (!TryInt(v, "color", out int color) || !TryInt(v, "grosor", out int grosor)
                    || !v.TryGetProperty("tipo_linea", out JsonElement lt) || lt.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                bool plot = true;
                if (v.TryGetProperty("plot", out JsonElement pl))
                {
                    if (pl.ValueKind == JsonValueKind.False)
                    {
                        plot = false;
                    }
                    else if (pl.ValueKind == JsonValueKind.Number && pl.TryGetDouble(out double d))
                    {
                        plot = d != 0;
                    }
                }

                specs.Add(p.Name, new LayerSpec(color, lt.GetString() ?? "", grosor, plot));
            }

            return specs;
        }

        private static bool TryInt(JsonElement obj, string name, out int value)
        {
            value = 0;
            if (!obj.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            if (el.TryGetInt32(out value))
            {
                return true;
            }

            if (el.TryGetDouble(out double d) && double.IsFinite(d) && Math.Floor(d) == d && d >= int.MinValue && d <= int.MaxValue)
            {
                value = (int)d;
                return true;
            }

            return false;
        }
    }
}
