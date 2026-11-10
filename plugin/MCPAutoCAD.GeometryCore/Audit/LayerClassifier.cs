using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>Resultado de clasificar una capa (protocol.md §5C.1).</summary>
    public sealed class LayerClassification
    {
        public LayerClassification(string clasificacion, List<string> diferencias, string? sugerencia)
            : this(clasificacion, diferencias, sugerencia, null, null)
        {
        }

        /// <summary>Desde 0.6 (§5C.1): con el patrón ajeno detectado aunque la capa sea del estándar y su sugerencia.</summary>
        public LayerClassification(string clasificacion, List<string> diferencias, string? sugerencia, string? patronAjeno, string? sugerenciaPatron)
        {
            Clasificacion = clasificacion;
            Diferencias = diferencias;
            Sugerencia = sugerencia;
            PatronAjeno = patronAjeno;
            SugerenciaPatron = sugerenciaPatron;
        }

        /// <summary>Una de las constantes de <see cref="LayerClassifier"/>.</summary>
        public string Clasificacion { get; }

        /// <summary>"color: 46 ≠ 155" etc.; vacía salvo en estandar_con_diferencias.</summary>
        public List<string> Diferencias { get; }

        /// <summary>
        /// Nombre de capa preferida (solo clasificaciones 5–8) o null. Desde el estándar v2 (2026-10-28), si la capa
        /// está en lps.json.mapa_normalizacion es el destino decidido allí; si no, la palabra clave (§5C.1).
        /// </summary>
        public string? Sugerencia { get; }

        /// <summary>
        /// Desde 0.6 (protocol.md §5C.1, campo <c>patron_ajeno</c>): "revit_aia" | "exportacion" | "generica" | null, según
        /// el NOMBRE de la capa y con independencia de la clasificación (también para capas que lps.json legitima, como
        /// Q-SPCQ-____-OTLN, P-SANR-FIXT-OTLN, S-COLS o …_Pen_No__7). Para las clasificaciones 5–7 coincide con ellas.
        /// </summary>
        public string? PatronAjeno { get; }

        /// <summary>
        /// Desde 0.6 (campo <c>sugerencia_patron</c>): capa preferida de lps.json deducible del patrón ajeno (códigos
        /// AIA como WALL → muros, COLS → columnas, o las palabras clave de <see cref="LayerClassifier.Suggest"/>), solo
        /// cuando <see cref="PatronAjeno"/> no es null; si no, null. <see cref="Sugerencia"/> no cambia (null en 1–4).
        /// Desde el estándar v2 (2026-10-28) el destino de lps.json.mapa_normalizacion, si lo hay, gana sobre esta heurística.
        /// </summary>
        public string? SugerenciaPatron { get; }
    }

    /// <summary>
    /// Clasificación de capas frente al estándar LPS (protocol.md §5C.1). Orden exacto de evaluación:
    /// mcp → estandar → estandar_con_diferencias → cype → ajena_revit_aia → ajena_exportacion → generica →
    /// fuera_de_estandar (la primera que cumple gana).
    /// </summary>
    public static class LayerClassifier
    {
        public const string Mcp = "mcp";
        public const string Estandar = "estandar";
        public const string EstandarConDiferencias = "estandar_con_diferencias";
        public const string Cype = "cype";
        public const string AjenaRevitAia = "ajena_revit_aia";
        public const string AjenaExportacion = "ajena_exportacion";
        public const string Generica = "generica";
        public const string FueraDeEstandar = "fuera_de_estandar";

        /// <summary>Valores de <c>patron_ajeno</c> (§5C.1, desde 0.6).</summary>
        public const string PatronRevitAia = "revit_aia";
        public const string PatronExportacion = "exportacion";
        public const string PatronGenerica = "generica";

        /// <summary>Las 8 clasificaciones en el orden de evaluación (y de <c>por_clasificacion</c>).</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Mcp, Estandar, EstandarConDiferencias, Cype, AjenaRevitAia, AjenaExportacion, Generica, FueraDeEstandar,
        };

        /// <summary>Prefijo de las capas MCP; solo se usa si el estándar no está disponible (§2.6).</summary>
        public const string McpPrefix = "MCP-";

        private static readonly Regex RevitAia = new Regex(@"^[A-Z]-[A-Z0-9]{3,4}(-|$)", RegexOptions.CultureInvariant);

        private static readonly Regex GenericLayer = new Regex(
            @"^(Layer|Capa)\d+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Palabras clave de sugerencia → clave de capas_preferidas, en el orden de §5C.1.</summary>
        private static readonly (string[] keywords, string concept)[] SuggestionTable =
        {
            (new[] { "MURO" }, "muros"),
            (new[] { "COTA" }, "cotas"),
            (new[] { "TEXT" }, "textos"),
            (new[] { "EJE" }, "ejes"),
            (new[] { "PUERTA" }, "puertas"),
            (new[] { "VENTANA" }, "ventanas"),
            (new[] { "ACHUR", "HATCH" }, "achurados"),
            (new[] { "COLUMN" }, "columnas"),
            (new[] { "VIGA" }, "vigas"),
            (new[] { "NIVEL" }, "niveles"),
            (new[] { "ESCALER" }, "escaleras"),
            (new[] { "SANIT" }, "sanitarios"),
            (new[] { "MUEBLE", "MOBIL" }, "mobiliario_fijo"),
        };

        /// <summary>Número de orden 1–8 de la clasificación (0 si no es válida).</summary>
        public static int Rank(string clasificacion)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (string.Equals(All[i], clasificacion, StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }

            return 0;
        }

        /// <summary>true para las clasificaciones 5–8 (ajenas, genérica, fuera de estándar).</summary>
        public static bool IsForeign(string clasificacion) => Rank(clasificacion) >= 5;

        /// <summary>
        /// Clasifica la capa. <paramref name="color"/> = ACI; <paramref name="tipoLinea"/> = nombre del tipo de
        /// línea; <paramref name="grosor"/> = LineWeight entero. Con reglas no disponibles, mcp se reconoce por el
        /// prefijo "MCP-" y no hay estandar/cype ni sugerencias.
        /// </summary>
        public static LayerClassification Classify(string nombre, int color, string tipoLinea, int grosor, LpsLayerRules reglas)
        {
            if (nombre == null)
            {
                throw new ArgumentNullException(nameof(nombre));
            }

            reglas ??= LpsLayerRules.Empty;
            tipoLinea ??= "";

            // 1. mcp
            bool isMcp = reglas.Disponible
                ? reglas.CapasMcp.ContainsKey(nombre)
                : nombre.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase);
            string? patron = ForeignPattern(nombre);

            // Estándar v2 (2026-10-28): el destino decidido en lps.json.mapa_normalizacion gana sobre la heurística.
            reglas.MapaNormalizacion.TryGetValue(nombre, out string? decidida);
            string? sugerenciaPatron = patron == null ? null : decidida ?? SuggestForPattern(nombre, reglas);
            if (isMcp)
            {
                return new LayerClassification(Mcp, new List<string>(), null, patron, sugerenciaPatron);
            }

            // 2–3. estandar / estandar_con_diferencias
            if (reglas.CapasPermitidas.TryGetValue(nombre, out LayerSpec? spec))
            {
                var diffs = new List<string>();
                if (color != spec.Color)
                {
                    diffs.Add(string.Format(CultureInfo.InvariantCulture, "color: {0} ≠ {1}", color, spec.Color));
                }

                if (!string.Equals(tipoLinea, spec.TipoLinea, StringComparison.OrdinalIgnoreCase))
                {
                    diffs.Add("tipo_linea: " + tipoLinea + " ≠ " + spec.TipoLinea);
                }

                if (grosor != spec.Grosor)
                {
                    diffs.Add(string.Format(CultureInfo.InvariantCulture, "grosor: {0} ≠ {1}", grosor, spec.Grosor));
                }

                return diffs.Count == 0
                    ? new LayerClassification(Estandar, diffs, null, patron, sugerenciaPatron)
                    : new LayerClassification(EstandarConDiferencias, diffs, null, patron, sugerenciaPatron);
            }

            // 4. cype (grupos externos permitidos)
            foreach (string pattern in reglas.PatronesExternos)
            {
                if (LpsLayerRules.WildcardMatch(nombre, pattern))
                {
                    return new LayerClassification(Cype, new List<string>(), null, patron, sugerenciaPatron);
                }
            }

            string clasif = ForeignClass(nombre);
            return new LayerClassification(clasif, new List<string>(), decidida ?? Suggest(nombre, reglas), patron, sugerenciaPatron);
        }

        /// <summary>
        /// Patrón ajeno del NOMBRE (§5C.1, desde 0.6), con independencia de lps.json: "revit_aia" si cumple la regex
        /// de la clasificación 5, "exportacion" si cumple la 6, "generica" si cumple la 7; null si ninguna. Mismo orden
        /// que <see cref="ForeignClass"/>.
        /// </summary>
        public static string? ForeignPattern(string nombre)
        {
            if (string.IsNullOrEmpty(nombre))
            {
                return null;
            }

            string clasif = ForeignClass(nombre);
            return clasif switch
            {
                AjenaRevitAia => PatronRevitAia,
                AjenaExportacion => PatronExportacion,
                Generica => PatronGenerica,
                _ => null,
            };
        }

        /// <summary>Códigos AIA/Revit por campo del nombre → clave de capas_preferidas (§5F.5).</summary>
        private static readonly Dictionary<string, string> AiaCodes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WALL"] = "muros",
            ["COLS"] = "columnas",
            ["COLM"] = "columnas",
            ["DOOR"] = "puertas",
            ["GLAZ"] = "ventanas",
            ["WIND"] = "ventanas",
            ["FURN"] = "mobiliario_fijo",
            ["CASE"] = "mobiliario_fijo",
            ["SANR"] = "sanitarios",
            ["PLMB"] = "sanitarios",
            ["FIXT"] = "sanitarios",
            ["DIMS"] = "cotas",
            ["TEXT"] = "textos",
            ["ANNO"] = "textos",
            ["IDEN"] = "textos",
            ["GRID"] = "ejes",
            ["BEAM"] = "vigas",
            ["STRS"] = "escaleras",
            ["PATT"] = "achurados",
            ["HATCH"] = "achurados",
            ["LEVL"] = "niveles",
            ["ELEV"] = "niveles",
        };

        private static readonly char[] AiaSeparators = { '-', '_' };

        /// <summary>
        /// Sugerencia de capa preferida para un nombre con patrón ajeno (campo <c>sugerencia_patron</c>, §5F.5):
        /// primero <see cref="Suggest"/> (palabras clave en español, que gana si coincide); si no, la tabla de códigos
        /// AIA/Revit (WALL → muros, COLS/COLM → columnas, DOOR → puertas, GLAZ/WIND → ventanas, FURN/CASE → mobiliario_fijo,
        /// SANR/PLMB/FIXT → sanitarios, DIMS → cotas, TEXT/ANNO/IDEN → textos, GRID → ejes, BEAM → vigas, STRS → escaleras,
        /// PATT/HATCH → achurados, LEVL/ELEV → niveles) aplicada, en orden, a los campos del nombre en mayúsculas
        /// separados por '-' y '_': el primer campo que esté en la tabla y cuyo concepto tenga capa preferida en
        /// lps.json decide. Sin coincidencia → null.
        /// </summary>
        public static string? SuggestForPattern(string nombre, LpsLayerRules reglas)
        {
            if (string.IsNullOrEmpty(nombre) || reglas == null)
            {
                return null;
            }

            string? porPalabra = Suggest(nombre, reglas);
            if (porPalabra != null)
            {
                return porPalabra;
            }

            string upper = RemoveDiacritics(nombre.ToUpperInvariant());
            foreach (string field in upper.Split(AiaSeparators))
            {
                string f = field.Trim();
                if (f.Length > 0
                    && AiaCodes.TryGetValue(f, out string? concept)
                    && reglas.CapasPreferidas.TryGetValue(concept, out string? layer))
                {
                    return layer;
                }
            }

            return null;
        }

        /// <summary>Clasificaciones 5–8, que no dependen de lps.json.</summary>
        private static string ForeignClass(string nombre)
        {
            string upper = nombre.ToUpperInvariant();
            if (RevitAia.IsMatch(upper))
            {
                return AjenaRevitAia;
            }

            if (nombre.IndexOf("_Pen_No__", StringComparison.OrdinalIgnoreCase) >= 0
                || nombre.IndexOf("_Pluma_N", StringComparison.OrdinalIgnoreCase) >= 0
                || upper.StartsWith("PDF_", StringComparison.Ordinal)
                || (upper.Length > 3 && upper.StartsWith("PDF", StringComparison.Ordinal) && upper[3] >= '0' && upper[3] <= '9')
                || nombre.StartsWith("Dibujo 2D - General", StringComparison.OrdinalIgnoreCase)
                || nombre.StartsWith("2D Drafting - General", StringComparison.OrdinalIgnoreCase)
                || nombre.StartsWith("3D ", StringComparison.OrdinalIgnoreCase))
            {
                return AjenaExportacion;
            }

            if (GenericLayer.IsMatch(nombre))
            {
                return Generica;
            }

            return FueraDeEstandar;
        }

        /// <summary>Capa preferida por palabra clave (primera coincidencia de la tabla) o null.</summary>
        public static string? Suggest(string nombre, LpsLayerRules reglas)
        {
            if (string.IsNullOrEmpty(nombre) || reglas == null || reglas.CapasPreferidas.Count == 0)
            {
                return null;
            }

            string key = RemoveDiacritics(nombre.ToUpperInvariant());
            foreach ((string[] keywords, string concept) in SuggestionTable)
            {
                foreach (string kw in keywords)
                {
                    if (key.IndexOf(kw, StringComparison.Ordinal) >= 0)
                    {
                        if (reglas.CapasPreferidas.TryGetValue(concept, out string? layer))
                        {
                            return layer;
                        }

                        // Concepto sin capa preferida en el estándar: se prueba la siguiente palabra clave.
                        break;
                    }
                }
            }

            return null;
        }

        /// <summary>Quita las marcas diacríticas (Á → A, Ñ → N, Ü → U). No toca otros caracteres.</summary>
        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? "";
            }

            string decomposed = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
