using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Reglas puras de <c>insertar_export</c> (protocol.md §5G.4): validación de la ruta del cliente (SEGURIDAD),
    /// nombre del bloque derivado del archivo, factor de unidades y clasificación de las capas que trae el export.
    /// </summary>
    public static class ExportRules
    {
        /// <summary>
        /// Lámina detectada (§5G.4): <see cref="Formatos"/> normalizados encontrados en los recuadros, en orden A0…A4, y
        /// <see cref="Escalas"/> N de los textos "Escala 1:N", sin repetir y de menor a mayor.
        /// </summary>
        public sealed record SheetDetection(IReadOnlyList<string> Formatos, IReadOnlyList<int> Escalas)
        {
            public bool EsLamina => Formatos.Count > 0;
        }

        /// <summary>Unidad que se supone en una lámina: 5 = centímetros (INSUNITS), porque sus recuadros miden A1…A4 en cm.</summary>
        public const int SheetUnits = 5;

        /// <summary>Tolerancia en cm al comparar un recuadro con un formato normalizado (A1 viene de 84 × 59,4, no de 84,1).</summary>
        public const double SheetTol = 0.5;

        private static readonly (string Nombre, double Largo, double Corto)[] SheetsCm =
        {
            ("A0", 118.9, 84.1), ("A1", 84.1, 59.4), ("A2", 59.4, 42.0), ("A3", 42.0, 29.7), ("A4", 29.7, 21.0),
        };

        private static readonly string[] SheetLayers = { "RECUAD_LIMITES", "RECUAD_MARCO" };

        /// <summary>El texto de un plano es dato: solo se le extrae el número de "Escala 1:N" o "Escala: 1:N".</summary>
        private static readonly Regex ScaleText = new Regex(
            @"escala\s*:?\s*1\s*[:/]\s*(\d{1,6})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>true si la capa es de los recuadros de una lámina de CYPE.</summary>
        public static bool IsSheetLayer(string capa) => SheetLayers.Contains(capa, StringComparer.OrdinalIgnoreCase);

        /// <summary>true si merece la pena conservar el texto para <see cref="DetectSheet"/>.</summary>
        public static bool IsScaleText(string? texto) => texto != null && ScaleText.IsMatch(texto);

        /// <summary>
        /// Detección pura de lámina: <paramref name="recuadros"/> = ancho y alto de las entidades de las capas de recuadro,
        /// en unidades del archivo; un formato cuenta si coincide en cm (<see cref="SheetTol"/>).
        /// </summary>
        public static SheetDetection DetectSheet(IReadOnlyList<(double ancho, double alto)> recuadros, IReadOnlyList<string> textos)
        {
            var formatos = new List<string>();
            foreach ((string nombre, double largo, double corto) in SheetsCm)
            {
                foreach ((double ancho, double alto) in recuadros ?? Array.Empty<(double, double)>())
                {
                    double mayor = Math.Max(ancho, alto);
                    double menor = Math.Min(ancho, alto);
                    if (Math.Abs(mayor - largo) <= SheetTol && Math.Abs(menor - corto) <= SheetTol)
                    {
                        formatos.Add(nombre);
                        break;
                    }
                }
            }

            var escalas = new SortedSet<int>();
            foreach (string texto in textos ?? Array.Empty<string>())
            {
                foreach (Match m in ScaleText.Matches(texto ?? string.Empty))
                {
                    if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1)
                    {
                        escalas.Add(n);
                    }
                }
            }

            return new SheetDetection(formatos, escalas.ToList());
        }

        /// <summary>Escala que lleva una lámina 1:<paramref name="n"/> al dibujo: N × (cm → unidad del dibujo). En m, N/100.</summary>
        public static double SuggestedScale(int n, double factorCm) => n * factorCm;

        /// <summary>
        /// Rechazo de §5G.4 de una lámina con escala_unidades 'auto': su INSUNITS no es la unidad real y el factor la dejaría
        /// mal sin ningún aviso (500 veces pequeña en el export real). <paramref name="factorCm"/> = cm → unidad del dibujo.
        /// </summary>
        public static string SheetRejection(SheetDetection d, double factorCm)
        {
            string inicio = "escala_unidades: el export es una lámina de papel en centímetros (" + string.Join(", ", d.Formatos) + ")";
            const string Motivo = ", no un modelo a escala real: su INSUNITS no sirve y el factor automático la dejaría a una escala equivocada.";
            if (d.Escalas.Count == 1)
            {
                return inicio + " a escala 1:" + Int(d.Escalas[0]) + Motivo
                    + " Pase escala: " + Number(SuggestedScale(d.Escalas[0], factorCm)) + " y escala_unidades: 'ninguna'";
            }

            if (d.Escalas.Count > 1)
            {
                string lista = string.Join(", ", d.Escalas.Select(n => "1:" + Int(n) + " → " + Number(SuggestedScale(n, factorCm))));
                return inicio + " con varias escalas (" + lista + ")" + Motivo + " Elija la del plano que vaya a usar y pase esa escala y escala_unidades: 'ninguna'";
            }

            return inicio + " sin ningún texto 'Escala 1:N' legible" + Motivo
                + " Pase escala: N × " + Number(factorCm) + " con la escala 1:N de la lámina y escala_unidades: 'ninguna'";
        }

        /// <summary>
        /// Caja en el dibujo de un contenido de caja <paramref name="caja"/> insertado como bloque de punto base
        /// <paramref name="basePunto"/>: escala, rotación y posición sobre sus cuatro esquinas (§5G.4; la extensión de un INSERT
        /// muy grande puede no calcularse, y R4 debe mirar la caja real y no solo el punto de inserción).
        /// </summary>
        public static BBox2 PlacedBox(BBox2 caja, (double x, double y) basePunto, IReadOnlyList<double> escala, double rotacionGrados, Point3 posicion)
        {
            double a = rotacionGrados * Math.PI / 180.0;
            (double cos, double sin) = (Math.Cos(a), Math.Sin(a));
            double xmin = double.PositiveInfinity, ymin = double.PositiveInfinity, xmax = double.NegativeInfinity, ymax = double.NegativeInfinity;
            foreach ((double x, double y) in new[] { (caja.Xmin, caja.Ymin), (caja.Xmax, caja.Ymin), (caja.Xmax, caja.Ymax), (caja.Xmin, caja.Ymax) })
            {
                double sx = (x - basePunto.x) * escala[0];
                double sy = (y - basePunto.y) * escala[1];
                double px = posicion.X + (sx * cos) - (sy * sin);
                double py = posicion.Y + (sx * sin) + (sy * cos);
                xmin = Math.Min(xmin, px);
                ymin = Math.Min(ymin, py);
                xmax = Math.Max(xmax, px);
                ymax = Math.Max(ymax, py);
            }

            return BBox2.FromCorners(xmin, ymin, xmax, ymax);
        }

        private static string Int(int n) => n.ToString(CultureInfo.InvariantCulture);

        private static string Number(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        public const string LpsKeyFolders = "carpetas_export_permitidas";

        public const string Prefix = "CYPE_";

        public const int MaxBlockName = 200;

        private const string NotAbsolute = "ruta: debe ser una ruta absoluta";
        private const string NotDwg = "ruta: solo se admiten archivos .dwg";
        private const string Unsafe = "ruta: no se admiten '..' ni comodines";
        private const string OutsideFolders = "ruta: fuera de las carpetas permitidas de lps.json (carpetas_export_permitidas)";

        private const string NoUnitsWarning =
            "El export no declara unidades (INSUNITS=0): no se aplicó factor; pase escala explícita si el export está en mm.";

        private const string BlockNameForbidden = "<>/\\\":;?*|,=`";

        /// <summary>Máximo de nombres de capa citados en <see cref="ForeignLayersWarning"/>.</summary>
        private const int MaxNamedLayers = 5;

        private static readonly char[] Separators = { '\\', '/' };

        /// <summary>Comodines de Win32 (incluidos los DOS &lt;, &gt; y comilla) y la barra vertical.</summary>
        private static readonly char[] WildcardChars = { '*', '?', '<', '>', '"', '|' };

        /// <summary>
        /// true si <paramref name="ruta"/> es absoluta (unidad o UNC) y no trae '..', comodines, comillas ni caracteres de
        /// control (las mismas reglas que <see cref="ValidatePath"/>, sin extensión ni carpetas). Lo usa cargar_lisp (0.8.4).
        /// </summary>
        public static bool IsSafeAbsolutePath(string ruta) =>
            !string.IsNullOrWhiteSpace(ruta) && IsAbsolute(ruta) && !HasUnsafeContent(ruta);

        /// <summary>Ruta con '\' como separador y sin separadores repetidos (conserva el prefijo UNC).</summary>
        public static string NormalizePath(string ruta) => NormalizeSeparators(ruta);

        /// <summary>
        /// Valida <paramref name="ruta"/> SIN tocar el disco: debe ser absoluta (raíz de unidad o UNC), terminar en ".dwg"
        /// (sin mayúsculas), no contener ".." ni caracteres de control ni comodines, y, si <paramref name="carpetasPermitidas"/>
        /// no es null ni vacía, estar dentro de alguna de ellas (comparación de prefijo de ruta normalizada, sin mayúsculas).
        /// Una clave de lps.json vacía o mal formada no llega aquí: el handler la rechaza antes para no abrir todas las rutas.
        /// Devuelve null si es válida o el detalle exacto: "ruta: debe ser una ruta absoluta", "ruta: solo se admiten
        /// archivos .dwg", "ruta: no se admiten '..' ni comodines", "ruta: fuera de las carpetas permitidas de lps.json
        /// (carpetas_export_permitidas)". La existencia del archivo la comprueba el plugin después (NOT_FOUND).
        /// </summary>
        public static string? ValidatePath(string ruta, IReadOnlyList<string>? carpetasPermitidas)
        {
            if (string.IsNullOrWhiteSpace(ruta) || !IsAbsolute(ruta))
            {
                return NotAbsolute;
            }

            if (!ruta.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                return NotDwg;
            }

            if (HasUnsafeContent(ruta))
            {
                return Unsafe;
            }

            if (carpetasPermitidas == null || carpetasPermitidas.Count == 0)
            {
                return null;
            }

            string normalizada = NormalizeSeparators(ruta);
            foreach (string carpeta in carpetasPermitidas)
            {
                if (string.IsNullOrWhiteSpace(carpeta))
                {
                    continue;
                }

                // Con separador final: la carpeta CYPE no debe admitir CYPE2. Una entrada que es solo barras no es una carpeta.
                string carpetaNormal = NormalizeSeparators(carpeta.Trim()).TrimEnd('\\');
                if (carpetaNormal.Length > 0 && normalizada.StartsWith(carpetaNormal + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return OutsideFolders;
        }

        /// <summary>
        /// Nombre de bloque derivado del archivo: "CYPE_" + nombre sin extensión con los caracteres no admitidos en un
        /// símbolo de AutoCAD (&lt;&gt;/\":;?*|,=` y espacios iniciales/finales) sustituidos por "_", recortado a
        /// <see cref="MaxBlockName"/>. Nunca vacío ("CYPE_EXPORT" si no queda nada). Si <paramref name="existentes"/> ya
        /// contiene el nombre (sin mayúsculas) se añade "_2", "_3", …
        /// </summary>
        public static string BlockNameFor(string ruta, IReadOnlyCollection<string> existentes)
        {
            string stem = Sanitize(StemOf(ruta ?? string.Empty));
            string baseName = Truncate(stem.Length == 0 ? Prefix + "EXPORT" : Prefix + stem, MaxBlockName);

            var usados = new HashSet<string>(existentes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (!usados.Contains(baseName))
            {
                return baseName;
            }

            for (int i = 2; ; i++)
            {
                string sufijo = "_" + i.ToString(CultureInfo.InvariantCulture);
                string candidato = Truncate(baseName, MaxBlockName - sufijo.Length) + sufijo;
                if (!usados.Contains(candidato))
                {
                    return candidato;
                }
            }
        }

        /// <summary>
        /// Factor de escala origen → destino con <c>UnitsScale.Factor</c>: null si alguna unidad no tiene equivalencia
        /// métrica, también con <paramref name="insunitsOrigen"/> 0, y entonces la respuesta informa factor_unidades = 1 y
        /// no se escala (§5G.4). Con origen 0 añade la advertencia exacta "El export no declara unidades (INSUNITS=0): no se
        /// aplicó factor; pase escala explícita si el export está en mm.".
        /// </summary>
        public static (double? factor, string? advertencia) Factor(int insunitsOrigen, int insunitsDestino)
        {
            return (UnitsScale.Factor(insunitsOrigen, insunitsDestino), insunitsOrigen == 0 ? NoUnitsWarning : null);
        }

        /// <summary>
        /// Clasifica las capas del export: "cype" si coinciden con algún patrón de <paramref name="patronesCype"/>
        /// (lps.json grupos_externos_permitidos.cype, comodín '*' final; sin mayúsculas), "0" para la capa 0, "otra" el
        /// resto. Devuelve (cype, otras) en el orden dado, sin la capa 0.
        /// </summary>
        public static (List<string> cype, List<string> otras) ClassifyLayers(IReadOnlyList<string> capas, IReadOnlyList<string> patronesCype)
        {
            var cype = new List<string>();
            var otras = new List<string>();
            if (capas == null)
            {
                return (cype, otras);
            }

            foreach (string capa in capas)
            {
                if (capa == "0")
                {
                    continue;
                }

                (MatchesAny(capa, patronesCype) ? cype : otras).Add(capa);
            }

            return (cype, otras);
        }

        /// <summary>Advertencia de lo que entra: "El export trae &lt;n&gt; capa(s) fuera del grupo CYPE: A, B (y &lt;k&gt; más); pase filtrar_capas: \"cype\" para dejarlas fuera." (hasta 5 nombres). null si no hay otras.</summary>
        public static string? ForeignLayersWarning(IReadOnlyList<string> otras)
        {
            if (otras == null || otras.Count == 0)
            {
                return null;
            }

            var citadas = new List<string>(MaxNamedLayers);
            for (int i = 0; i < otras.Count && i < MaxNamedLayers; i++)
            {
                citadas.Add(otras[i]);
            }

            int resto = otras.Count - citadas.Count;
            return "El export trae " + otras.Count.ToString(CultureInfo.InvariantCulture) + " capa(s) fuera del grupo CYPE: "
                + string.Join(", ", citadas)
                + (resto > 0 ? " (y " + resto.ToString(CultureInfo.InvariantCulture) + " más)" : string.Empty)
                + "; pase filtrar_capas: \"cype\" para dejarlas fuera.";
        }

        /// <summary>
        /// Raíz de unidad o UNC con servidor. Más estricto que Path.IsPathRooted, que da por buenas "C:x.dwg" (relativa a
        /// la carpeta actual de la unidad) y una barra inicial suelta. Los espacios de dispositivo de Win32 (servidor "?" o
        /// ".") no son rutas de archivo admitidas.
        /// </summary>
        private static bool IsAbsolute(string r)
        {
            if (r.Length >= 3 && char.IsAsciiLetter(r[0]) && r[1] == ':' && IsSeparator(r[2]))
            {
                return true;
            }

            if (r.Length < 5 || !IsSeparator(r[0]) || !IsSeparator(r[1]))
            {
                return false;
            }

            int fin = r.IndexOfAny(Separators, 2);
            if (fin <= 2 || fin == r.Length - 1)
            {
                return false;
            }

            string servidor = r.Substring(2, fin - 2);
            return servidor != "." && servidor != "?";
        }

        private static bool IsSeparator(char c) => c == '\\' || c == '/';

        /// <summary>
        /// ".." en cualquier parte (Windows quita puntos y espacios finales de cada segmento: ".. " también sube de carpeta),
        /// comodines, caracteres de control y ':' fuera de la unidad (flujos alternativos de NTFS).
        /// </summary>
        private static bool HasUnsafeContent(string r)
        {
            if (r.Contains("..", StringComparison.Ordinal) || r.IndexOfAny(WildcardChars) >= 0)
            {
                return true;
            }

            for (int i = 0; i < r.Length; i++)
            {
                if (char.IsControl(r[i]) || (r[i] == ':' && i != 1))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>'/' pasa a barra invertida y los separadores repetidos se colapsan (salvo los dos iniciales de una UNC).</summary>
        private static string NormalizeSeparators(string r)
        {
            string s = r.Replace('/', '\\');
            bool unc = s.StartsWith("\\\\", StringComparison.Ordinal);
            while (s.Contains("\\\\", StringComparison.Ordinal))
            {
                s = s.Replace("\\\\", "\\", StringComparison.Ordinal);
            }

            return unc ? "\\" + s : s;
        }

        /// <summary>Nombre del archivo sin carpeta ni extensión; admite los dos separadores en cualquier sistema.</summary>
        private static string StemOf(string ruta)
        {
            string nombre = ruta.Substring(ruta.LastIndexOfAny(Separators) + 1);
            int punto = nombre.LastIndexOf('.');
            return punto >= 0 ? nombre.Substring(0, punto) : nombre;
        }

        private static string Sanitize(string stem)
        {
            char[] c = stem.ToCharArray();
            for (int i = 0; i < c.Length; i++)
            {
                if (BlockNameForbidden.IndexOf(c[i]) >= 0 || char.IsControl(c[i]))
                {
                    c[i] = '_';
                }
            }

            for (int i = 0; i < c.Length && c[i] == ' '; i++)
            {
                c[i] = '_';
            }

            for (int i = c.Length - 1; i >= 0 && c[i] == ' '; i--)
            {
                c[i] = '_';
            }

            return new string(c);
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        /// <summary>Patrón exacto o con '*' final (prefijo), sin mayúsculas.</summary>
        private static bool MatchesAny(string capa, IReadOnlyList<string> patrones)
        {
            if (patrones == null)
            {
                return false;
            }

            foreach (string p in patrones)
            {
                if (string.IsNullOrEmpty(p))
                {
                    continue;
                }

                bool coincide = p.EndsWith('*')
                    ? capa.StartsWith(p.Substring(0, p.Length - 1), StringComparison.OrdinalIgnoreCase)
                    : string.Equals(capa, p, StringComparison.OrdinalIgnoreCase);
                if (coincide)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
