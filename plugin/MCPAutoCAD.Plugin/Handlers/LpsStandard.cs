using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Estándar LPS (protocol.md §2.2): lee UNA vez lps.json (junto a la DLL, o la ruta de MCP_AUTOCAD_LPS) y expone
    /// solo unidades.insunits y unidades.nombre. Si falta o es inválido se registra una advertencia en el log y
    /// <see cref="Disponible"/> = false. Thread-safe y nunca lanza.
    /// </summary>
    internal static class LpsStandard
    {
        public const string EnvVar = "MCP_AUTOCAD_LPS";
        public const string FileName = "lps.json";

        private static readonly Lazy<State> Loaded = new Lazy<State>(Load, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>true si se cargó lps.json con unidades válidas.</summary>
        public static bool Disponible => Loaded.Value.Available;

        /// <summary>INSUNITS del estándar, o null si no está disponible.</summary>
        public static int? Insunits => Loaded.Value.Available ? Loaded.Value.Insunits : null;

        /// <summary>Nombre corto de la unidad del estándar (unidades.nombre), o null si no está disponible.</summary>
        public static string? Unidades => Loaded.Value.Available ? Loaded.Value.Name : null;

        /// <summary>Ruta de la que se intentó leer el estándar (diagnóstico).</summary>
        public static string? Ruta => Loaded.Value.Path;

        /// <summary>
        /// Documento JSON completo de lps.json (clonado, inmutable), o null si no se pudo leer/parsear como objeto
        /// (protocol.md §2.6). Las secciones de capas (capas_permitidas, capas_preferidas, capas_mcp,
        /// grupos_externos_permitidos y, desde el estándar v2 del 2026-10-28, la opcional mapa_normalizacion) las
        /// interpreta LpsLayerRules; aquí no se validan.
        /// </summary>
        public static JsonElement? Root => Loaded.Value.Root;

        /// <summary>
        /// Estilo de texto preferido para un concepto (<c>lps.json → "estilos_texto": {"ejes": "75"}</c>), o null si
        /// lps.json no está, no tiene la clave o el valor no es una cadena no vacía. La clave es OPCIONAL: hoy lps.json no la
        /// trae y el plugin usa su constante (<c>AxisLabelStyle.OfficeDefault</c>, corrección del gate en vivo de Fase 4A,
        /// 2026-10-27). Nunca lanza.
        /// </summary>
        public static string? TextStyleFor(string concepto)
        {
            try
            {
                JsonElement? root = Loaded.Value.Root;
                if (root == null
                    || !root.Value.TryGetProperty("estilos_texto", out JsonElement estilos)
                    || estilos.ValueKind != JsonValueKind.Object
                    || !estilos.TryGetProperty(concepto, out JsonElement valor)
                    || valor.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                string? s = valor.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Advertencia LPS de §2.2 para el INSUNITS dado, o null (coincide o estándar no disponible).</summary>
        public static string? Warning(int insunits)
        {
            try
            {
                State s = Loaded.Value;
                if (!s.Available)
                {
                    return null;
                }

                return LpsUnits.Warning(insunits, s.Insunits, s.Name ?? "");
            }
            catch
            {
                return null;
            }
        }

        private static State Load()
        {
            string? path = null;
            try
            {
                path = ResolvePath();
                if (path == null || !File.Exists(path))
                {
                    return Unavailable(path ?? "(ruta no determinada)");
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
                JsonElement root = doc.RootElement;
                JsonElement? rootClone = root.ValueKind == JsonValueKind.Object ? root.Clone() : null;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("unidades", out JsonElement units)
                    || units.ValueKind != JsonValueKind.Object
                    || !units.TryGetProperty("insunits", out JsonElement insEl)
                    || insEl.ValueKind != JsonValueKind.Number
                    || !insEl.TryGetInt32(out int ins)
                    || !units.TryGetProperty("nombre", out JsonElement nameEl)
                    || nameEl.ValueKind != JsonValueKind.String)
                {
                    PluginLog.Warn(FileName + " no encontrado/inválido en " + path + ": se omite la comparación LPS");
                    return new State(false, 0, null, path, rootClone);
                }

                string name = nameEl.GetString() ?? "";
                PluginLog.Info("Estándar LPS cargado de " + path + ": INSUNITS=" + ins + " (" + name + ")");
                return new State(true, ins, name, path, rootClone);
            }
            catch (Exception ex)
            {
                PluginLog.Warn(FileName + " no encontrado/inválido en " + (path ?? "(ruta no determinada)")
                    + ": se omite la comparación LPS (" + ex.GetType().Name + ": " + ex.Message + ")");
                return new State(false, 0, null, path, null);
            }
        }

        private static State Unavailable(string path)
        {
            PluginLog.Warn(FileName + " no encontrado/inválido en " + path + ": se omite la comparación LPS");
            return new State(false, 0, null, path, null);
        }

        private static string? ResolvePath()
        {
            string? env = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            string? dir = Path.GetDirectoryName(typeof(PluginApplication).Assembly.Location);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, FileName);
        }

        private sealed class State
        {
            public State(bool available, int insunits, string? name, string? path, JsonElement? root)
            {
                Available = available;
                Insunits = insunits;
                Name = name;
                Path = path;
                Root = root;
            }

            public bool Available { get; }
            public int Insunits { get; }
            public string? Name { get; }
            public string? Path { get; }
            public JsonElement? Root { get; }
        }
    }
}
