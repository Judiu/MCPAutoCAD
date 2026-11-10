using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>Texto de entrada (TEXT o MTEXT ya convertido por el handler, protocol.md §5C.5).</summary>
    public sealed class TextItem
    {
        public TextItem(string handle, string texto, double x, double y, double altura)
            : this(handle, texto, x, y, altura, 0.0, null)
        {
        }

        /// <summary>
        /// Texto con rotación (grados) y extensión geométrica 2D (desde 0.3.1). <paramref name="bbox"/> null = sin
        /// extensión conocida: se usa el punto de inserción y <c>y + altura</c> (conducta 0.3.0).
        /// </summary>
        public TextItem(string handle, string texto, double x, double y, double altura, double rotacion, BBox2? bbox)
        {
            Handle = handle ?? "";
            Texto = texto ?? "";
            X = x;
            Y = y;
            Altura = altura;
            Rotacion = rotacion;
            Bbox = bbox;
        }

        public string Handle { get; }
        public string Texto { get; }
        public double X { get; }
        public double Y { get; }
        public double Altura { get; }

        /// <summary>Rotación en grados (0 por defecto).</summary>
        public double Rotacion { get; }

        /// <summary>Caja 2D de GeometricExtents o null.</summary>
        public BBox2? Bbox { get; }
    }

    /// <summary>Fila de un cuadro: etiqueta canónica, su texto y (si se emparejó) el texto del valor.</summary>
    public sealed class AreaTableRow
    {
        public AreaTableRow(string etiqueta, TextItem label, TextItem? valueText, double? valor)
        {
            Etiqueta = etiqueta;
            Label = label;
            ValueText = valueText;
            Valor = valor;
        }

        /// <summary>Clave canónica (LOTE, AREA PISO 1, TITULO…).</summary>
        public string Etiqueta { get; }

        public TextItem Label { get; }

        /// <summary>Texto del valor; null si no hay valor o si es el mismo texto que la etiqueta (fila combinada).</summary>
        public TextItem? ValueText { get; }

        public double? Valor { get; }

        /// <summary>Etiqueta y valor en un mismo texto ("LOTE: 72.00 m2").</summary>
        public bool Combinada => Valor != null && ValueText == null;
    }

    /// <summary>Cuadro de áreas detectado.</summary>
    public sealed class AreaTableBox
    {
        public string Id { get; set; } = "";

        public string? Titulo { get; set; }

        public BBox2? Bbox { get; set; }

        /// <summary>Etiqueta → valor declarado, en orden de filas (de arriba abajo). Si una etiqueta se repite gana la primera.</summary>
        public Dictionary<string, double> Valores { get; } = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Etiqueta → (handle de la etiqueta, handle del valor o null si la fila es combinada/sin valor).</summary>
        public Dictionary<string, (string etiqueta, string? valor)> Handles { get; } =
            new Dictionary<string, (string etiqueta, string? valor)>(StringComparer.Ordinal);

        public List<string> EtiquetasSinValor { get; } = new List<string>();

        /// <summary>Filas del cuadro (de arriba abajo), incluido el título.</summary>
        public List<AreaTableRow> Filas { get; } = new List<AreaTableRow>();

        /// <summary>Fila que aportó el valor de la etiqueta (la primera con valor).</summary>
        public AreaTableRow? RowFor(string etiqueta)
        {
            foreach (AreaTableRow r in Filas)
            {
                if (r.Valor != null && string.Equals(r.Etiqueta, etiqueta, StringComparison.Ordinal))
                {
                    return r;
                }
            }

            return null;
        }

        /// <summary>
        /// true si la caja del cuadro interseca la ventana de modelo de algún viewport de papel, o si no se evaluó
        /// (espacio de papel, sin ventanas). Desde 0.3.1 (F3).
        /// </summary>
        public bool EnLayout { get; set; } = true;

        /// <summary>Reglas internas cuyas etiquetas necesarias están presentes (haya hallazgo o no). Desde 0.3.1 (F2).</summary>
        public List<string> ReglasEvaluadas { get; } = new List<string>();

        /// <summary>Reglas internas no evaluables con su motivo ("falta LOTE"…). Desde 0.3.1 (F2).</summary>
        public List<(string regla, string motivo)> ReglasNoEvaluables { get; } = new List<(string regla, string motivo)>();

        internal double AnchorY { get; set; }

        internal double AnchorX { get; set; }
    }

    /// <summary>Resultado de <see cref="AreaTable.Parse"/>.</summary>
    public sealed class AreaTableResult
    {
        public List<AreaTableBox> Cuadros { get; } = new List<AreaTableBox>();

        /// <summary>Cuadros con <see cref="AreaTableBox.EnLayout"/> = false.</summary>
        public int NCuadrosFueraDeLayout => Cuadros.Count(c => !c.EnLayout);

        /// <summary>Ids de los cuadros fuera de los viewports de los layouts, en orden.</summary>
        public List<string> IdsFueraDeLayout => Cuadros.Where(c => !c.EnLayout).Select(c => c.Id).ToList();

        /// <summary>Hallazgos internos y de discrepancia (Regla = verificar_cuadro_areas, sin id).</summary>
        public List<Finding> Hallazgos { get; } = new List<Finding>();

        /// <summary>Advertencias (p. ej. más de 20 cuadros).</summary>
        public List<string> Advertencias { get; } = new List<string>();
    }

    /// <summary>
    /// Detección y verificación aritmética de cuadros de áreas a partir de textos sueltos (protocol.md §5C.5).
    /// Puro: sin AutoCAD.
    /// </summary>
    public static class AreaTable
    {
        public const string Regla = "verificar_cuadro_areas";

        public const string Titulo = "TITULO";
        public const string TotalConstruido = "TOTAL CONSTRUIDO";
        public const string RematePuntoFijo = "REMATE PUNTO FIJO";
        public const string Lote = "LOTE";
        public const string Libre = "LIBRE";
        public const string Ocupacion = "OCUPACION";
        public const string Construccion = "CONSTRUCCION";
        public const string AreaPisoPrefix = "AREA PISO ";

        public const string RuleSumaPisos = "suma_pisos";
        public const string RuleLibre = "libre";
        public const string RuleOcupacion = "ocupacion";
        public const string RuleConstruccion = "construccion";
        public const string RuleDiscrepantes = "cuadros_discrepantes";
        public const string RuleContorno = "contorno_vs_declarado";

        public const int MaxBoxes = 20;
        public const int MaxTextLength = 120;
        public const string WarningTooManyBoxes = "Se detectaron más de 20 cuadros; se evaluaron los 20 primeros.";

        /// <summary>Sufijo del mensaje de la pasada (ii) de cuadros_discrepantes (F3, 0.3.1).</summary>
        public const string SuffixOutsideLayout = " (cuadro fuera de los layouts: posible versión anterior)";

        public const string AmbitoEnLayout = "en_layout";
        public const string AmbitoConFueraDeLayout = "con_fuera_de_layout";

        /// <summary>Tolerancia de rotación (grados) para aceptar un texto como horizontal (0° o 180°).</summary>
        public const double RotationToleranceDeg = 1.0;

        /// <summary>Distancia vertical máxima de anexión de índices, en alturas medianas del cuadro receptor (F2).</summary>
        public const double AnnexMaxHeights = 15.0;

        private static readonly Regex FloorRegex = new Regex(@"^AREA(S)?PISO0*(\d+)$", RegexOptions.CultureInvariant);

        // Plugin 0.8.1 (B5): LOTE solo si el texto ES la etiqueta del área del lote ("LOTE", "ÁREA LOTE", "ÁREA DEL LOTE",
        // "ÁREA TOTAL LOTE"). Antes bastaba con contener LOTE: "AISLAMIENTO LOTE" (un export de Revit, junto a cotas
        // de 2 y 14) se tomaba por LOTE y daba "LOTE difiere entre cuadros" con números que no eran áreas.
        private static readonly Regex LoteLabelRegex = new Regex(@"^(AREAS?(DEL?)?(TOTAL)?(DEL?)?)?LOTE$", RegexOptions.CultureInvariant);

        private static readonly Regex NumberRegex = new Regex(@"^[+-]?\d+([.,]\d+)?$", RegexOptions.CultureInvariant);

        // Etiqueta + separadores + número (+ sufijo m2/m²) en el mismo texto, sobre el texto en mayúsculas sin acentos.
        private static readonly Regex CombinedRegex = new Regex(
            @"^\s*(?<lab>.*?[A-Z].*?)[\s:=]+(?<num>[+-]?\d+(?<dec>[.,]\d+)?)\s*(?<unit>M2|M²)?\s*$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        private static readonly Regex ContourLabelRegex = new Regex(
            @"^(LOTE|LIBRE|TOTAL CONSTRUIDO|REMATE PUNTO FIJO|AREA PISO [1-9][0-9]{0,2})$", RegexOptions.CultureInvariant);

        /// <summary>Patrón JSON Schema de las etiquetas admitidas en <c>contornos</c>.</summary>
        public const string ContourLabelPattern = "^(LOTE|LIBRE|TOTAL CONSTRUIDO|REMATE PUNTO FIJO|AREA PISO [1-9][0-9]{0,2})$";

        /// <summary>true si la etiqueta canónica se admite en <c>contornos</c> (etiquetas con área).</summary>
        public static bool IsContourLabel(string etiqueta) => etiqueta != null && ContourLabelRegex.IsMatch(etiqueta);

        // ------------------------------------------------------------------ normalización

        /// <summary>
        /// Normalización de etiqueta: mayúsculas invariantes, sin acentos, sin espacios, ':' ni '.', sin sufijo M2/M².
        /// </summary>
        public static string NormalizeLabel(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            string s = LayerClassifier.RemoveDiacritics(text.ToUpperInvariant());
            var chars = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (!char.IsWhiteSpace(c) && c != ':' && c != '.')
                {
                    chars.Append(c);
                }
            }

            return StripAreaSuffix(chars.ToString());
        }

        /// <summary>Clave canónica de la etiqueta (orden de la tabla de §5C.5) o null si no se reconoce.</summary>
        public static string? CanonicalLabel(string text)
        {
            string n = NormalizeLabel(text);
            if (n.Length == 0)
            {
                return null;
            }

            if (n.StartsWith("AREAS", StringComparison.Ordinal) && n.Contains("PROYECTO", StringComparison.Ordinal))
            {
                return Titulo;
            }

            Match m = FloorRegex.Match(n);
            if (m.Success)
            {
                string digits = m.Groups[2].Value.TrimStart('0');
                if (digits.Length == 0 || digits.Length > 3)
                {
                    return null;
                }

                return AreaPisoPrefix + digits;
            }

            if (n.StartsWith("TOTAL", StringComparison.Ordinal))
            {
                return TotalConstruido;
            }

            if (n.Contains("REMATE", StringComparison.Ordinal))
            {
                return RematePuntoFijo;
            }

            if (LoteLabelRegex.IsMatch(n))
            {
                return Lote;
            }

            if (n.Contains("LIBRE", StringComparison.Ordinal))
            {
                return Libre;
            }

            if (n.Contains("OCUPACION", StringComparison.Ordinal))
            {
                return Ocupacion;
            }

            if (n.Contains("CONSTRUCCION", StringComparison.Ordinal))
            {
                return Construccion;
            }

            return null;
        }

        /// <summary>
        /// Valor numérico: tras quitar espacios y el sufijo M2/M², cumple ^[+-]?\d+([.,]\d+)?$ (coma → punto).
        /// </summary>
        public static bool TryParseValue(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text.ToUpperInvariant())
            {
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
            }

            string s = StripAreaSuffix(sb.ToString());
            if (!NumberRegex.IsMatch(s))
            {
                return false;
            }

            return double.TryParse(s.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }

        /// <summary>
        /// Texto con etiqueta y valor juntos ("LOTE: 72.00 m2"). El título no admite valor. Desde 0.3.1 (F1) el número
        /// debe llevar parte decimal o unidad m2/m²: "LOTE 20" es un número de lote, no un área, y devuelve false.
        /// </summary>
        public static bool TryParseCombined(string text, out string etiqueta, out double value) =>
            ClassifyCombined(text, out etiqueta, out value) == CombinedKind.Ok;

        private enum CombinedKind
        {
            /// <summary>No tiene forma de etiqueta reconocida + número.</summary>
            NoCombinado,

            /// <summary>Etiqueta reconocida + número entero sin unidad: se descarta el texto entero (F1).</summary>
            Rechazado,

            Ok,
        }

        private static CombinedKind ClassifyCombined(string text, out string etiqueta, out double value)
        {
            etiqueta = "";
            value = 0;
            if (string.IsNullOrEmpty(text))
            {
                return CombinedKind.NoCombinado;
            }

            string s = LayerClassifier.RemoveDiacritics(text.ToUpperInvariant());
            Match m = CombinedRegex.Match(s);
            if (!m.Success)
            {
                return CombinedKind.NoCombinado;
            }

            string? label = CanonicalLabel(m.Groups["lab"].Value);
            if (label == null || label == Titulo)
            {
                return CombinedKind.NoCombinado;
            }

            if (!TryParseValue(m.Groups["num"].Value, out double v))
            {
                return CombinedKind.NoCombinado;
            }

            if (!m.Groups["dec"].Success && !m.Groups["unit"].Success)
            {
                return CombinedKind.Rechazado;
            }

            etiqueta = label;
            value = v;
            return CombinedKind.Ok;
        }

        /// <summary>
        /// true si el texto es horizontal: |rotación mod 360| ≤ 1° o a 180° ± 1° (F1, 0.3.1). Los demás no son
        /// candidatos a etiqueta, valor ni unidad.
        /// </summary>
        public static bool IsHorizontal(double rotacionGrados)
        {
            if (!double.IsFinite(rotacionGrados))
            {
                return false;
            }

            double r = rotacionGrados % 360.0;
            if (r < 0)
            {
                r += 360.0;
            }

            return r <= RotationToleranceDeg || r >= 360.0 - RotationToleranceDeg
                || Math.Abs(r - 180.0) <= RotationToleranceDeg;
        }

        /// <summary>Texto que es solo la unidad ("m²", "M2"): sin espacios ni puntos queda exactamente M2/M².</summary>
        private static bool IsUnitText(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text.ToUpperInvariant())
            {
                if (!char.IsWhiteSpace(c) && c != '.')
                {
                    sb.Append(c);
                }
            }

            string s = sb.ToString();
            return s == "M2" || s == "M²";
        }

        private static string StripAreaSuffix(string s)
        {
            if (s.EndsWith("M2", StringComparison.Ordinal) || s.EndsWith("M²", StringComparison.Ordinal))
            {
                return s.Substring(0, s.Length - 2);
            }

            return s;
        }

        // ------------------------------------------------------------------ análisis

        private sealed class LabelCandidate
        {
            public LabelCandidate(TextItem item, string etiqueta, int order)
            {
                Item = item;
                Etiqueta = etiqueta;
                Order = order;
            }

            public TextItem Item { get; }
            public string Etiqueta { get; }
            public int Order { get; }
            public TextItem? ValueText { get; set; }
            public double? Valor { get; set; }
            public bool Combined { get; set; }
        }

        private sealed class ValueCandidate
        {
            public ValueCandidate(TextItem item, double valor, int order)
            {
                Item = item;
                Valor = valor;
                Order = order;
            }

            public TextItem Item { get; }
            public double Valor { get; }
            public int Order { get; }
            public bool Used { get; set; }
        }

        /// <summary>
        /// Detecta los cuadros y aplica las reglas internas y de discrepancia. <paramref name="ventanasLayout"/> =
        /// ventanas de modelo de los viewports de papel (F3, 0.3.1): null o vacía → todos los cuadros
        /// <see cref="AreaTableBox.EnLayout"/> = true; si no, EnLayout = la caja del cuadro interseca alguna ventana.
        /// </summary>
        public static AreaTableResult Parse(IReadOnlyList<TextItem> textos, IReadOnlyList<BBox2>? ventanasLayout = null)
        {
            if (textos == null)
            {
                throw new ArgumentNullException(nameof(textos));
            }

            var labels = new List<LabelCandidate>();
            var values = new List<ValueCandidate>();
            var units = new List<TextItem>();
            int order = 0;
            foreach (TextItem t in textos)
            {
                order++;
                if (t == null || string.IsNullOrWhiteSpace(t.Texto) || t.Texto.Length > MaxTextLength
                    || t.Texto.IndexOf('\n') >= 0 || t.Texto.IndexOf('\r') >= 0
                    || !double.IsFinite(t.X) || !double.IsFinite(t.Y) || !double.IsFinite(t.Altura))
                {
                    continue;
                }

                // F1: textos rotados (p. ej. rótulos del plano de localización) no son candidatos.
                if (!IsHorizontal(t.Rotacion))
                {
                    continue;
                }

                if (TryParseValue(t.Texto, out double v))
                {
                    values.Add(new ValueCandidate(t, v, order));
                    continue;
                }

                if (IsUnitText(t.Texto))
                {
                    units.Add(t);
                    continue;
                }

                CombinedKind kind = ClassifyCombined(t.Texto, out string combinedLabel, out double cv);
                if (kind == CombinedKind.Ok)
                {
                    labels.Add(new LabelCandidate(t, combinedLabel, order) { Valor = cv, Combined = true });
                    continue;
                }

                if (kind == CombinedKind.Rechazado)
                {
                    // F1: "LOTE 20" se descarta entero (no queda como etiqueta suelta que capture un número vecino).
                    continue;
                }

                string? label = CanonicalLabel(t.Texto);
                if (label != null)
                {
                    labels.Add(new LabelCandidate(t, label, order));
                }
            }

            PairValues(labels, values);

            var result = new AreaTableResult();
            List<List<LabelCandidate>> parts = Group(labels);
            AnnexIndexRows(parts);
            units.Sort((a, b) => a.Y.CompareTo(b.Y));
            var boxes = new List<AreaTableBox>();
            foreach (List<LabelCandidate> part in parts)
            {
                AreaTableBox? box = BuildBox(part, units);
                if (box != null)
                {
                    boxes.Add(box);
                }
            }

            bool evaluateLayout = ventanasLayout != null && ventanasLayout.Count > 0;
            foreach (AreaTableBox box in boxes)
            {
                box.EnLayout = !evaluateLayout || (box.Bbox != null && ventanasLayout!.Any(w => w.Intersects(box.Bbox.Value)));
            }

            boxes.Sort((a, b) =>
            {
                int c = b.AnchorY.CompareTo(a.AnchorY);
                return c != 0 ? c : a.AnchorX.CompareTo(b.AnchorX);
            });

            if (boxes.Count > MaxBoxes)
            {
                boxes.RemoveRange(MaxBoxes, boxes.Count - MaxBoxes);
                result.Advertencias.Add(WarningTooManyBoxes);
            }

            for (int k = 0; k < boxes.Count; k++)
            {
                boxes[k].Id = "cuadro-" + (k + 1).ToString(CultureInfo.InvariantCulture);
            }

            result.Cuadros.AddRange(boxes);
            foreach (AreaTableBox box in boxes)
            {
                CheckBox(box, result.Hallazgos);
            }

            CheckDiscrepancies(boxes, result.Hallazgos);
            return result;
        }

        /// <summary>
        /// Emparejamiento etiqueta → valor: |Δy| ≤ 0.6·altura_etiqueta y x_valor &gt; x_etiqueta; menor Δx primero;
        /// cada valor y cada etiqueta se usan una sola vez.
        /// </summary>
        private static void PairValues(List<LabelCandidate> labels, List<ValueCandidate> values)
        {
            if (labels.Count == 0 || values.Count == 0)
            {
                return;
            }

            // Valores ordenados por y para buscar por rango (sin recorrer todos los pares).
            var byY = new List<ValueCandidate>(values);
            byY.Sort((a, b) => a.Item.Y.CompareTo(b.Item.Y));
            var ys = new double[byY.Count];
            for (int i = 0; i < byY.Count; i++)
            {
                ys[i] = byY[i].Item.Y;
            }

            var pairs = new List<(double dx, LabelCandidate l, ValueCandidate v)>();
            foreach (LabelCandidate l in labels)
            {
                if (l.Combined || l.Etiqueta == Titulo)
                {
                    continue;
                }

                double tolY = 0.6 * Math.Abs(l.Item.Altura);
                int start = LowerBound(ys, l.Item.Y - tolY);
                for (int i = start; i < byY.Count && ys[i] <= l.Item.Y + tolY; i++)
                {
                    ValueCandidate v = byY[i];
                    double dx = v.Item.X - l.Item.X;
                    if (dx > 0)
                    {
                        pairs.Add((dx, l, v));
                    }
                }
            }

            pairs.Sort((a, b) =>
            {
                int c = a.dx.CompareTo(b.dx);
                if (c != 0)
                {
                    return c;
                }

                c = a.l.Order.CompareTo(b.l.Order);
                return c != 0 ? c : a.v.Order.CompareTo(b.v.Order);
            });

            foreach ((_, LabelCandidate l, ValueCandidate v) in pairs)
            {
                if (l.Valor != null || v.Used)
                {
                    continue;
                }

                l.Valor = v.Valor;
                l.ValueText = v.Item;
                v.Used = true;
            }
        }

        private static int LowerBound(double[] sorted, double value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid] < value)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        /// <summary>
        /// Agrupación: componentes conexas con enlace |Δx| ≤ 25·h y |Δy| ≤ 6·h (h = mayor altura de las dos);
        /// división por anclas (TITULO o LOTE) cuando hay ≥ 2 en una componente.
        /// </summary>
        private static List<List<LabelCandidate>> Group(List<LabelCandidate> labels)
        {
            var boxes = new List<List<LabelCandidate>>();
            int n = labels.Count;
            if (n == 0)
            {
                return boxes;
            }

            var parent = new int[n];
            for (int i = 0; i < n; i++)
            {
                parent[i] = i;
            }

            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }

                return i;
            }

            // Barrido por y: solo se comparan filas dentro de la ventana 6·hmax global (no O(n²) en la práctica).
            int[] idx = Enumerable.Range(0, n).OrderBy(i => labels[i].Item.Y).ToArray();
            double hmax = labels.Max(l => Math.Abs(l.Item.Altura));
            for (int a = 0; a < n; a++)
            {
                LabelCandidate la = labels[idx[a]];
                for (int b = a + 1; b < n; b++)
                {
                    LabelCandidate lb = labels[idx[b]];
                    double dy = lb.Item.Y - la.Item.Y;
                    if (dy > 6 * hmax)
                    {
                        break;
                    }

                    double h = Math.Max(Math.Abs(la.Item.Altura), Math.Abs(lb.Item.Altura));
                    if (Math.Abs(lb.Item.X - la.Item.X) <= 25 * h && Math.Abs(dy) <= 6 * h)
                    {
                        int ra = Find(idx[a]);
                        int rb = Find(idx[b]);
                        if (ra != rb)
                        {
                            parent[ra] = rb;
                        }
                    }
                }
            }

            var components = new Dictionary<int, List<LabelCandidate>>();
            var componentOrder = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!components.TryGetValue(r, out List<LabelCandidate>? list))
                {
                    list = new List<LabelCandidate>();
                    components.Add(r, list);
                    componentOrder.Add(r);
                }

                list.Add(labels[i]);
            }

            foreach (int r in componentOrder)
            {
                boxes.AddRange(SplitByAnchors(components[r]));
            }

            return boxes;
        }

        /// <summary>
        /// Anexión de las filas de índices (F2, 0.3.1, protocol.md §5C.5 paso 3). Un cuadro provisional sin LOTE cuyas
        /// etiquetas con valor sean solo OCUPACION/CONSTRUCCION (y sin título) se une al cuadro con LOTE (con valor)
        /// que: solapa en X (caja de etiquetas y valores de cada uno), está por encima (y de su fila inferior ≥ y de la
        /// fila superior del de índices − 0.6·h) y a una distancia vertical entre esas filas ≤ 15·hmed (hmed = altura
        /// mediana de etiquetas y valores del receptor). Con varios candidatos gana el más cercano en y. Sin candidato
        /// se conserva. El orden de filas lo rehace <see cref="BuildBox"/> (de arriba abajo; gana la fila de arriba).
        /// </summary>
        private static void AnnexIndexRows(List<List<LabelCandidate>> parts)
        {
            if (parts.Count < 2)
            {
                return;
            }

            var receivers = new List<(List<LabelCandidate> rows, double xmin, double xmax, double bottomY, double hmed)>();
            var indexParts = new List<(List<LabelCandidate> rows, double xmin, double xmax, double topY, double topH)>();
            foreach (List<LabelCandidate> p in parts)
            {
                bool hasLoteValue = p.Any(r => r.Etiqueta == Lote && r.Valor != null);
                (double xmin, double xmax) = XRange(p);
                if (hasLoteValue)
                {
                    LabelCandidate bottom = p.OrderBy(r => r.Item.Y).First();
                    receivers.Add((p, xmin, xmax, bottom.Item.Y, MedianHeight(p)));
                    continue;
                }

                bool isIndex = !p.Any(r => r.Etiqueta == Lote || r.Etiqueta == Titulo)
                    && p.Any(r => r.Valor != null)
                    && p.Where(r => r.Valor != null).All(r => r.Etiqueta == Ocupacion || r.Etiqueta == Construccion);
                if (isIndex)
                {
                    LabelCandidate top = p.OrderByDescending(r => r.Item.Y).First();
                    indexParts.Add((p, xmin, xmax, top.Item.Y, Math.Abs(top.Item.Altura)));
                }
            }

            if (receivers.Count == 0 || indexParts.Count == 0)
            {
                return;
            }

            foreach (var ix in indexParts)
            {
                int best = -1;
                double bestDist = double.PositiveInfinity;
                for (int i = 0; i < receivers.Count; i++)
                {
                    var rc = receivers[i];
                    bool overlapX = rc.xmin <= ix.xmax && ix.xmin <= rc.xmax;
                    if (!overlapX || rc.bottomY < ix.topY - 0.6 * ix.topH)
                    {
                        continue;
                    }

                    double dist = Math.Abs(rc.bottomY - ix.topY);
                    if (dist <= AnnexMaxHeights * rc.hmed && dist < bestDist)
                    {
                        bestDist = dist;
                        best = i;
                    }
                }

                if (best >= 0)
                {
                    receivers[best].rows.AddRange(ix.rows);
                    parts.Remove(ix.rows);
                }
            }
        }

        /// <summary>Rango X de las etiquetas y valores de un cuadro provisional (extensión geométrica si existe).</summary>
        private static (double xmin, double xmax) XRange(List<LabelCandidate> rows)
        {
            double xmin = double.PositiveInfinity, xmax = double.NegativeInfinity;
            void Add(TextItem t)
            {
                if (t.Bbox != null)
                {
                    xmin = Math.Min(xmin, t.Bbox.Value.Xmin);
                    xmax = Math.Max(xmax, t.Bbox.Value.Xmax);
                }
                else
                {
                    xmin = Math.Min(xmin, t.X);
                    xmax = Math.Max(xmax, t.X);
                }
            }

            foreach (LabelCandidate r in rows)
            {
                Add(r.Item);
                if (r.ValueText != null)
                {
                    Add(r.ValueText);
                }
            }

            return (xmin, xmax);
        }

        /// <summary>Altura mediana (valor absoluto) de etiquetas y valores.</summary>
        private static double MedianHeight(List<LabelCandidate> rows)
        {
            var hs = new List<double>();
            foreach (LabelCandidate r in rows)
            {
                hs.Add(Math.Abs(r.Item.Altura));
                if (r.ValueText != null)
                {
                    hs.Add(Math.Abs(r.ValueText.Altura));
                }
            }

            hs.Sort();
            int n = hs.Count;
            return n == 0 ? 0 : (n % 2 == 1 ? hs[n / 2] : 0.5 * (hs[n / 2 - 1] + hs[n / 2]));
        }

        /// <summary>
        /// División por anclas. Si la componente tiene ≥ 2 títulos se divide por títulos; si no, con ≥ 2 LOTE se
        /// divide por LOTE (así un título y un LOTE del mismo cuadro no lo parten en dos). Cada fila va al ancla más
        /// cercana por encima (y_ancla ≥ y_fila − 0.6·h) o a la primera si está por encima de todas.
        /// </summary>
        private static IEnumerable<List<LabelCandidate>> SplitByAnchors(List<LabelCandidate> rows)
        {
            List<LabelCandidate> anchors = rows.Where(r => r.Etiqueta == Titulo).ToList();
            if (anchors.Count < 2)
            {
                anchors = rows.Where(r => r.Etiqueta == Lote).ToList();
            }

            if (anchors.Count < 2)
            {
                yield return rows;
                yield break;
            }

            // De arriba abajo.
            anchors.Sort((a, b) =>
            {
                int c = b.Item.Y.CompareTo(a.Item.Y);
                return c != 0 ? c : a.Item.X.CompareTo(b.Item.X);
            });

            var parts = new List<LabelCandidate>[anchors.Count];
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = new List<LabelCandidate>();
            }

            foreach (LabelCandidate row in rows)
            {
                int anchorIndex = anchors.IndexOf(row);
                if (anchorIndex < 0)
                {
                    double tol = 0.6 * Math.Abs(row.Item.Altura);
                    anchorIndex = 0;
                    double best = double.PositiveInfinity;
                    for (int i = 0; i < anchors.Count; i++)
                    {
                        double ay = anchors[i].Item.Y;
                        if (ay >= row.Item.Y - tol && ay - row.Item.Y < best)
                        {
                            best = ay - row.Item.Y;
                            anchorIndex = i;
                        }
                    }
                }

                parts[anchorIndex].Add(row);
            }

            foreach (List<LabelCandidate> p in parts)
            {
                if (p.Count > 0)
                {
                    yield return p;
                }
            }
        }

        /// <summary>Construye el cuadro; null si no tiene ninguna etiqueta con valor (se descarta).</summary>
        private static AreaTableBox? BuildBox(List<LabelCandidate> rows, List<TextItem> unitsByY)
        {
            if (!rows.Any(r => r.Valor != null && r.Etiqueta != Titulo))
            {
                return null;
            }

            // Filas de arriba abajo, luego izquierda a derecha, luego orden de entrada.
            List<LabelCandidate> sorted = rows
                .OrderByDescending(r => r.Item.Y)
                .ThenBy(r => r.Item.X)
                .ThenBy(r => r.Order)
                .ToList();

            var box = new AreaTableBox();
            var points = new List<Point3>();
            foreach (LabelCandidate r in sorted)
            {
                box.Filas.Add(new AreaTableRow(r.Etiqueta, r.Item, r.ValueText, r.Valor));
                AddTextPoints(points, r.Item);
                if (r.ValueText != null)
                {
                    AddTextPoints(points, r.ValueText);
                    AddUnitPoints(points, r.ValueText, unitsByY);
                }

                if (r.Etiqueta == Titulo)
                {
                    box.Titulo ??= r.Item.Texto.Trim();
                    continue;
                }

                if (r.Valor != null)
                {
                    if (!box.Valores.ContainsKey(r.Etiqueta))
                    {
                        box.Valores.Add(r.Etiqueta, r.Valor.Value);
                        box.Handles.Add(r.Etiqueta, (r.Item.Handle, r.ValueText?.Handle));
                    }
                }
                else if (!box.EtiquetasSinValor.Contains(r.Etiqueta))
                {
                    box.EtiquetasSinValor.Add(r.Etiqueta);
                }
            }

            box.Bbox = points.Count > 0 ? BBox2.FromPoints(points) : (BBox2?)null;

            // Ancla para ordenar: título, si no LOTE, si no la fila superior.
            LabelCandidate anchor = sorted.FirstOrDefault(r => r.Etiqueta == Titulo)
                ?? sorted.FirstOrDefault(r => r.Etiqueta == Lote)
                ?? sorted[0];
            box.AnchorY = anchor.Item.Y;
            box.AnchorX = anchor.Item.X;
            return box;
        }

        /// <summary>
        /// Puntos de la caja de un texto (F6, 0.3.1): las 4 esquinas de su extensión geométrica si existe; si no, el
        /// punto de inserción y <c>y + altura</c> (conducta 0.3.0).
        /// </summary>
        private static void AddTextPoints(List<Point3> points, TextItem t)
        {
            if (t.Bbox != null)
            {
                BBox2 b = t.Bbox.Value;
                points.Add(new Point3(b.Xmin, b.Ymin, 0));
                points.Add(new Point3(b.Xmax, b.Ymin, 0));
                points.Add(new Point3(b.Xmin, b.Ymax, 0));
                points.Add(new Point3(b.Xmax, b.Ymax, 0));
                return;
            }

            points.Add(new Point3(t.X, t.Y, 0));
            if (t.Altura > 0)
            {
                points.Add(new Point3(t.X, t.Y + t.Altura, 0));
            }
        }

        /// <summary>
        /// Unidades sueltas ("m²") de la fila del valor (F6, 0.3.1): |y_unidad − y_valor| ≤ 0.6·altura_valor y
        /// x_unidad a la derecha del valor (x_unidad &gt; x_valor) a menos de 2·altura de su borde derecho
        /// (Xmax de su extensión, o x si no la tiene). Solo amplían la caja del cuadro.
        /// </summary>
        private static void AddUnitPoints(List<Point3> points, TextItem value, List<TextItem> unitsByY)
        {
            if (unitsByY.Count == 0)
            {
                return;
            }

            double h = Math.Abs(value.Altura);
            double tolY = 0.6 * h;
            double right = value.Bbox?.Xmax ?? value.X;
            int lo = 0, hi = unitsByY.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (unitsByY[mid].Y < value.Y - tolY)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (int i = lo; i < unitsByY.Count && unitsByY[i].Y <= value.Y + tolY; i++)
            {
                TextItem u = unitsByY[i];
                if (u.X > value.X && u.X - right <= 2 * h)
                {
                    AddTextPoints(points, u);
                }
            }
        }

        // ------------------------------------------------------------------ reglas

        private static bool Exceeds(double declared, double expected, double reference) =>
            Math.Abs(declared - expected) > Math.Max(0.01, 0.001 * Math.Abs(reference));

        private static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

        private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

        private static void CheckBox(AreaTableBox box, List<Finding> findings)
        {
            var v = box.Valores;
            List<string> floors = v.Keys.Where(k => k.StartsWith(AreaPisoPrefix, StringComparison.Ordinal)).ToList();
            string piso1 = AreaPisoPrefix + "1";
            DeclareRules(box, floors.Count > 0, piso1);

            // suma_pisos
            if (v.TryGetValue(TotalConstruido, out double total) && floors.Count > 0)
            {
                double sum = floors.Sum(k => v[k]) + (v.TryGetValue(RematePuntoFijo, out double rem) ? rem : 0.0);
                if (Exceeds(total, sum, total))
                {
                    var involved = new List<string>(floors);
                    if (v.ContainsKey(RematePuntoFijo))
                    {
                        involved.Add(RematePuntoFijo);
                    }

                    involved.Add(TotalConstruido);
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "En {0}, la suma de AREA PISO n y REMATE PUNTO FIJO es {1} y TOTAL CONSTRUIDO declara {2} (diferencia {3}).",
                        box.Id, F2(sum), F2(total), F2(total - sum));
                    findings.Add(BoxFinding(box, Severity.Alta, msg, RuleSumaPisos, TotalConstruido, sum, total, involved));
                }
            }

            bool hasLote = v.TryGetValue(Lote, out double lote);

            // libre
            if (hasLote && v.TryGetValue(piso1, out double p1) && v.TryGetValue(Libre, out double libre))
            {
                double expected = lote - p1;
                if (Exceeds(libre, expected, lote))
                {
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "En {0}, LOTE − AREA PISO 1 = {1} y LIBRE declara {2}.", box.Id, F2(expected), F2(libre));
                    findings.Add(BoxFinding(box, Severity.Media, msg, RuleLibre, Libre, expected, libre,
                        new List<string> { Lote, piso1, Libre }));
                }
            }

            // ocupacion
            if (hasLote && lote > 0 && v.TryGetValue(piso1, out double p1o) && v.TryGetValue(Ocupacion, out double ocup))
            {
                double expected = Round2(p1o / lote);
                if (Math.Abs(Round2(ocup) - expected) > 0.005 + 1e-9)
                {
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "En {0}, AREA PISO 1 / LOTE = {1} y OCUPACION declara {2}.", box.Id, F2(expected), F2(ocup));
                    findings.Add(BoxFinding(box, Severity.Media, msg, RuleOcupacion, Ocupacion, expected, ocup,
                        new List<string> { piso1, Lote, Ocupacion }));
                }
            }

            // construccion
            if (hasLote && lote > 0 && v.TryGetValue(TotalConstruido, out double tot) && v.TryGetValue(Construccion, out double cons))
            {
                double expected = Round2(tot / lote);
                if (Math.Abs(Round2(cons) - expected) > 0.005 + 1e-9)
                {
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "En {0}, TOTAL CONSTRUIDO / LOTE = {1} y CONSTRUCCION declara {2}.", box.Id, F2(expected), F2(cons));
                    findings.Add(BoxFinding(box, Severity.Media, msg, RuleConstruccion, Construccion, expected, cons,
                        new List<string> { TotalConstruido, Lote, Construccion }));
                }
            }
        }

        /// <summary>
        /// Rellena ReglasEvaluadas / ReglasNoEvaluables (F2, 0.3.1). Etiquetas necesarias en el orden de la tabla de
        /// reglas de §5C.5: suma_pisos = TOTAL CONSTRUIDO, AREA PISO n; libre = LOTE, AREA PISO 1, LIBRE;
        /// ocupacion = OCUPACION, AREA PISO 1, LOTE &gt; 0; construccion = CONSTRUCCION, TOTAL CONSTRUIDO, LOTE &gt; 0.
        /// Motivo = "falta &lt;ETIQUETA&gt;" (la primera que falta) o "LOTE no es mayor que 0".
        /// </summary>
        private static void DeclareRules(AreaTableBox box, bool hasFloors, string piso1)
        {
            var v = box.Valores;
            box.ReglasEvaluadas.Clear();
            box.ReglasNoEvaluables.Clear();

            string? Missing(params string[] needed)
            {
                foreach (string k in needed)
                {
                    if (k == "AREA PISO n")
                    {
                        if (!hasFloors)
                        {
                            return "falta AREA PISO n";
                        }
                    }
                    else if (k == "LOTE>0")
                    {
                        if (!v.TryGetValue(Lote, out double l))
                        {
                            return "falta LOTE";
                        }

                        if (!(l > 0))
                        {
                            return "LOTE no es mayor que 0";
                        }
                    }
                    else if (!v.ContainsKey(k))
                    {
                        return "falta " + k;
                    }
                }

                return null;
            }

            void Declare(string regla, string? motivo)
            {
                if (motivo == null)
                {
                    box.ReglasEvaluadas.Add(regla);
                }
                else
                {
                    box.ReglasNoEvaluables.Add((regla, motivo));
                }
            }

            Declare(RuleSumaPisos, Missing(TotalConstruido, "AREA PISO n"));
            Declare(RuleLibre, Missing(Lote, piso1, Libre));
            Declare(RuleOcupacion, Missing(Ocupacion, piso1, "LOTE>0"));
            Declare(RuleConstruccion, Missing(Construccion, TotalConstruido, "LOTE>0"));
        }

        private static Finding BoxFinding(
            AreaTableBox box, string severidad, string mensaje, string regla, string etiqueta,
            double esperado, double declarado, List<string> involved)
        {
            var f = new Finding(Regla, severidad, mensaje);
            var points = new List<Point3>();
            foreach (string label in involved)
            {
                AddRow(f, points, box.RowFor(label));
            }

            f.Bbox = points.Count > 0 ? BBox2.FromPoints(points) : (BBox2?)null;
            f.Datos["regla"] = regla;
            f.Datos["cuadro"] = box.Id;
            f.Datos["etiqueta"] = etiqueta;
            f.Datos["esperado"] = esperado;
            f.Datos["declarado"] = declarado;
            f.Datos["diferencia"] = declarado - esperado;
            return f;
        }

        private static void AddRow(Finding f, List<Point3> points, AreaTableRow? row)
        {
            if (row == null)
            {
                return;
            }

            AddHandle(f, row.Label.Handle);
            AddTextPoints(points, row.Label);
            if (row.ValueText != null)
            {
                AddHandle(f, row.ValueText.Handle);
                AddTextPoints(points, row.ValueText);
            }
        }

        private static void AddHandle(Finding f, string handle)
        {
            if (!string.IsNullOrEmpty(handle) && !f.Handles.Contains(handle))
            {
                f.Handles.Add(handle);
            }
        }

        /// <summary>Orden canónico de etiquetas para los hallazgos de discrepancia.</summary>
        private static int LabelRank(string etiqueta)
        {
            if (etiqueta == Lote)
            {
                return 0;
            }

            if (etiqueta.StartsWith(AreaPisoPrefix, StringComparison.Ordinal)
                && int.TryParse(etiqueta.Substring(AreaPisoPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                return 100 + n;
            }

            return etiqueta switch
            {
                RematePuntoFijo => 2000,
                TotalConstruido => 2001,
                Libre => 2002,
                Ocupacion => 2003,
                Construccion => 2004,
                _ => 3000,
            };
        }

        private static void CheckDiscrepancies(List<AreaTableBox> boxes, List<Finding> findings)
        {
            if (boxes.Count < 2)
            {
                return;
            }

            var labels = new List<string>();
            foreach (AreaTableBox b in boxes)
            {
                foreach (string k in b.Valores.Keys)
                {
                    if (!labels.Contains(k))
                    {
                        labels.Add(k);
                    }
                }
            }

            labels.Sort((a, b) =>
            {
                int c = LabelRank(a).CompareTo(LabelRank(b));
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });

            foreach (string label in labels)
            {
                double tol = (label == Ocupacion || label == Construccion) ? 0.005 : 0.01;
                List<AreaTableBox> all = boxes.Where(b => b.Valores.ContainsKey(label)).ToList();
                List<AreaTableBox> inLayout = all.Where(b => b.EnLayout).ToList();
                List<AreaTableBox> outside = all.Where(b => !b.EnLayout).ToList();

                // (i) solo cuadros en layout → alta.
                if (inLayout.Count >= 2 && Differ(inLayout, label, tol))
                {
                    findings.Add(DiscrepancyFinding(inLayout, label, Severity.Alta, AmbitoEnLayout, null));
                }

                // (ii) con cuadros fuera de layout → media, solo si alguno aporta un valor distinto de todos los de (i).
                if (outside.Count == 0 || all.Count < 2 || !Differ(all, label, tol))
                {
                    continue;
                }

                bool aportaNuevo = outside.Any(o =>
                    inLayout.All(i => Math.Abs(o.Valores[label] - i.Valores[label]) > tol + 1e-9));
                if (aportaNuevo)
                {
                    findings.Add(DiscrepancyFinding(all, label, Severity.Media, AmbitoConFueraDeLayout,
                        outside.Select(b => b.Id).ToList()));
                }
            }
        }

        private static bool Differ(List<AreaTableBox> with, string label, double tol)
        {
            double min = with.Min(b => b.Valores[label]);
            double max = with.Max(b => b.Valores[label]);
            return max - min > tol + 1e-9;
        }

        private static Finding DiscrepancyFinding(
            List<AreaTableBox> with, string label, string severidad, string ambito, List<string>? fuera)
        {
            string list = string.Join(", ", with.Select(b => b.Id + " = " + F2(b.Valores[label])));
            string msg = label + " difiere entre cuadros: " + list + ".";
            if (fuera != null)
            {
                msg += SuffixOutsideLayout;
            }

            var f = new Finding(Regla, severidad, msg);
            var points = new List<Point3>();
            var valores = new List<Dictionary<string, object?>>();
            foreach (AreaTableBox b in with)
            {
                AddRow(f, points, b.RowFor(label));
                valores.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cuadro"] = b.Id,
                    ["valor"] = b.Valores[label],
                });
            }

            f.Bbox = points.Count > 0 ? BBox2.FromPoints(points) : (BBox2?)null;
            f.Datos["regla"] = RuleDiscrepantes;
            f.Datos["etiqueta"] = label;
            f.Datos["valores"] = valores;
            f.Datos["ambito"] = ambito;
            if (fuera != null)
            {
                f.Datos["cuadros_fuera_de_layout"] = fuera;
            }

            return f;
        }

        /// <summary>
        /// Regla contorno_vs_declarado: por cada cuadro que declara <paramref name="etiqueta"/>,
        /// |área geométrica − declarado| &gt; max(0.01, 0.001·declarado) → hallazgo alta. Lista vacía si ningún cuadro
        /// la declara (el handler añade la advertencia). <paramref name="contornoBbox"/> se une a la caja del hallazgo.
        /// </summary>
        public static List<Finding> CompareContour(
            AreaTableResult r, string etiqueta, string handle, double areaGeometrica, BBox2? contornoBbox = null)
        {
            if (r == null)
            {
                throw new ArgumentNullException(nameof(r));
            }

            var findings = new List<Finding>();
            if (string.IsNullOrEmpty(etiqueta) || !double.IsFinite(areaGeometrica))
            {
                return findings;
            }

            foreach (AreaTableBox box in r.Cuadros)
            {
                if (!box.Valores.TryGetValue(etiqueta, out double declared))
                {
                    continue;
                }

                if (!Exceeds(declared, areaGeometrica, declared))
                {
                    continue;
                }

                string msg = string.Format(CultureInfo.InvariantCulture,
                    "En {0}, el área geométrica de la entidad {1} es {2} y {3} declara {4}.",
                    box.Id, handle, F2(areaGeometrica), etiqueta, F2(declared));
                var f = new Finding(Regla, Severity.Alta, msg);
                var points = new List<Point3>();
                AddRow(f, points, box.RowFor(etiqueta));
                AddHandle(f, handle);
                BBox2? bbox = points.Count > 0 ? BBox2.FromPoints(points) : (BBox2?)null;
                if (contornoBbox != null)
                {
                    bbox = bbox == null ? contornoBbox : bbox.Value.Union(contornoBbox.Value);
                }

                f.Bbox = bbox;
                f.Datos["regla"] = RuleContorno;
                f.Datos["cuadro"] = box.Id;
                f.Datos["etiqueta"] = etiqueta;
                f.Datos["esperado"] = areaGeometrica;
                f.Datos["declarado"] = declared;
                f.Datos["diferencia"] = declared - areaGeometrica;
                findings.Add(f);
            }

            return findings;
        }
    }
}
