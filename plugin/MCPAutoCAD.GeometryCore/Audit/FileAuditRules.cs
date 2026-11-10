using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>
    /// Reglas puras de auditar_archivo (protocol.md §5H.1, §5H.5, §5H.6): comprobaciones admitidas, nombre de la copia
    /// temporal, espacio en disco necesario y textos literales. Sin AutoCAD ni disco.
    /// </summary>
    public static class FileAuditRules
    {
        /// <summary>Comprobación que devuelve el bloque de §5B.8; no es una regla de §5C.8.</summary>
        public const string Resumen = "resumen";

        /// <summary>Clave opcional de lps.json con las carpetas auditables.</summary>
        public const string LpsKeyFolders = "carpetas_auditoria_permitidas";

        /// <summary>Motivo de <c>no_disponible</c> para xrefs_no_resueltas al leer una copia (§5H.0.3).</summary>
        public const string XrefsUnavailableReason =
            "se leyó una copia temporal: las rutas relativas de las xrefs se resolverían contra la carpeta temporal; pase copiar: false para comprobarlas";

        public const string NoFoldersWarning =
            "lps.json no define carpetas_auditoria_permitidas: se admite cualquier ruta absoluta .dwg legible; añada la clave para limitar las carpetas.";

        /// <summary>Consejo que sigue al texto común del timeout de 60 s (§3, regla 7; §5H.1).</summary>
        public const string TimeoutHint =
            "La auditoría del archivo no escribe nada; reintente con menos comprobaciones o un presupuesto menor.";

        private const long BytesPerMb = 1024L * 1024L;

        /// <summary>Margen de 100 MiB sobre el doble del archivo (§5H.1).</summary>
        private const long FreeSpaceMarginBytes = 100L * BytesPerMb;

        /// <summary>
        /// Tamaño máximo con copiar: true (50 MiB). La copia corre en el hilo principal, y un archivo mayor desde la red
        /// congelaría AutoCAD más allá de los 60 s del pipe sin poder cancelarse (§3, regla 7). auditar_carpeta copia fuera.
        /// </summary>
        public const long MaxCopyBytes = 50L * BytesPerMb;

        /// <summary>
        /// Plazo máximo de la medición auxiliar (<see cref="FileAuxMeasure"/>). Es aparte del de las reglas: corre después de
        /// ellas y un archivo que agotó el presupuesto no debe quedarse sin ella.
        /// </summary>
        private const int AuxBudgetMaxMs = 4000;

        /// <summary>Solo el formato exacto de <see cref="TempCopyName"/>; cualquier otro archivo de la carpeta no es nuestro.</summary>
        private static readonly Regex TempCopyPattern = new Regex(
            "\\Aauditar_([0-9]+)_[0-9]+\\.dwg\\z",
            RegexOptions.CultureInvariant);

        /// <summary>Las 9 comprobaciones: las 8 reglas en el orden fijo de §5C.8 y "resumen" al final.</summary>
        public static readonly IReadOnlyList<string> Comprobaciones = Array.AsReadOnly(new[]
        {
            "auditar_capas",
            "entidades_en_capa_0",
            "auditar_bloques",
            "verificar_cuadro_areas",
            "duplicados",
            "textos_fuera_de_escala",
            "xrefs_no_resueltas",
            "unidades",
            Resumen,
        });

        /// <summary>
        /// Comprobaciones a ejecutar en el orden fijo de <see cref="Comprobaciones"/>; null = todas. Lanza
        /// <see cref="ArgumentException"/> con el texto de §5H.5 ante un nombre desconocido o repetido (distingue mayúsculas),
        /// en el orden de la lista pedida.
        /// </summary>
        public static List<string> NormalizeChecks(IReadOnlyList<string>? pedidas)
        {
            if (pedidas == null)
            {
                return new List<string>(Comprobaciones);
            }

            if (pedidas.Count == 0)
            {
                throw new ArgumentException("comprobaciones: debe ser un array de 1 a 9 cadenas");
            }

            var vistas = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < pedidas.Count; i++)
            {
                string pedida = pedidas[i];
                if (!IsKnown(pedida))
                {
                    throw new ArgumentException(
                        "comprobaciones[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe ser uno de " + string.Join(", ", Comprobaciones));
                }

                if (!vistas.Add(pedida))
                {
                    throw new ArgumentException("comprobaciones: comprobación repetida '" + pedida + "'");
                }
            }

            var resultado = new List<string>(pedidas.Count);
            foreach (string comprobacion in Comprobaciones)
            {
                if (vistas.Contains(comprobacion))
                {
                    resultado.Add(comprobacion);
                }
            }

            return resultado;
        }

        /// <summary>Nombre de la copia temporal del plugin: "auditar_&lt;pid&gt;_&lt;n&gt;.dwg".</summary>
        public static string TempCopyName(int pid, int n) =>
            "auditar_" + pid.ToString(CultureInfo.InvariantCulture) + "_" + n.ToString(CultureInfo.InvariantCulture) + ".dwg";

        /// <summary>
        /// Pid dueño de una copia temporal según su nombre. false si el nombre no es exactamente
        /// "auditar_&lt;pid&gt;_&lt;n&gt;.dwg" o el pid no cabe en un entero positivo.
        /// </summary>
        public static bool TryParseTempCopyPid(string? nombre, out int pid)
        {
            pid = 0;
            Match match = TempCopyPattern.Match(nombre ?? string.Empty);
            return match.Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out pid)
                && pid > 0;
        }

        /// <summary>
        /// Carpetas admitidas con la clave de lps.json definida: las de la clave más la temporal propia, de donde el lote
        /// lee sus copias (§5H.2); sin ella, definir la clave rechazaría todas las filas con origen plugin.
        /// </summary>
        public static List<string> WithOwnTempFolder(IReadOnlyList<string> carpetas, string carpetaTemporal) =>
            new List<string>(carpetas) { carpetaTemporal };

        /// <summary>Texto de INVALID_PARAMS para copiar: true por encima de <see cref="MaxCopyBytes"/>.</summary>
        public static string CopyTooLargeDetail(long bytes) =>
            "ruta: el archivo pesa " + WholeMb(bytes) + " MB y con copiar: true el máximo es " + WholeMb(MaxCopyBytes)
            + " MB, porque la copia bloquearía AutoCAD; use auditar_carpeta con patron igual al nombre del archivo, que copia fuera de AutoCAD";

        /// <summary>Texto de INVALID_PARAMS para copiar: false por encima de <see cref="MaxCopyBytes"/> fuera de la carpeta temporal.</summary>
        public static string DirectTooLargeDetail(long bytes) =>
            "ruta: el archivo pesa " + WholeMb(bytes) + " MB y con copiar: false, fuera de la carpeta temporal del plugin, el máximo es "
            + WholeMb(MaxCopyBytes) + " MB, porque la lectura bloquearía AutoCAD sin poder cancelarse; use auditar_carpeta con patron igual al nombre del archivo, que copia fuera de AutoCAD";

        /// <summary>
        /// Detalle de INVALID_PARAMS si el tamaño no cabe en el hilo principal; null si se admite. Hasta
        /// <see cref="MaxCopyBytes"/> se admite todo. Por encima, solo copiar: false sobre la carpeta temporal propia, donde el
        /// lote deja sus copias locales (§5H.2): leer desde la red (hasta 227 MB en la carpeta real) congelaría AutoCAD más
        /// allá de los 60 s del pipe (§3, regla 7).
        /// </summary>
        public static string? SizeRejection(long bytes, bool copiar, string ruta, string carpetaTemporal)
        {
            if (bytes <= MaxCopyBytes)
            {
                return null;
            }

            if (copiar)
            {
                return CopyTooLargeDetail(bytes);
            }

            return ExportRules.ValidatePath(ruta, new[] { carpetaTemporal }) == null ? null : DirectTooLargeDetail(bytes);
        }

        /// <summary>data.detalle de un fallo de lectura que no es una excepción de AutoCAD: tipo y mensaje (§5B.8).</summary>
        public static string ReadFailureDetail(string tipoExcepcion, string mensaje) => tipoExcepcion + ": " + mensaje;

        /// <summary>Plazo de la medición auxiliar: el de la llamada, con el tope propio.</summary>
        public static int AuxBudgetMs(int presupuestoMs) => Math.Min(presupuestoMs, AuxBudgetMaxMs);

        /// <summary>El VIEWPORT de cada layout lo crea AutoCAD, no el autor: no hace que su capa cuente como capa con entidades.</summary>
        public static bool CountsAsLayerContent(string? nombreDxf) =>
            !string.Equals(nombreDxf, "VIEWPORT", StringComparison.OrdinalIgnoreCase);

        /// <summary>Número finito, o null: System.Text.Json no serializa NaN ni infinitos.</summary>
        public static double? FiniteOrNull(double? valor) => valor != null && double.IsFinite(valor.Value) ? valor : null;

        /// <summary>Advertencia cuando la medición auxiliar no cabe en su plazo: no se publica una medida parcial.</summary>
        public static string AuxTimedOutWarning(int plazoMs) =>
            "La medición de capas y láminas no terminó en " + Seconds(plazoMs) + " s: _cype y _lamina quedan en null (sin medir).";

        /// <summary>Advertencia cuando la medición auxiliar falla: los hallazgos ya calculados se conservan.</summary>
        public static string AuxFailedWarning(string tipoExcepcion) =>
            "No se pudieron medir los datos auxiliares del archivo (" + tipoExcepcion + "): quedan en null (sin medir); los hallazgos son válidos.";

        /// <summary>Advertencia de una regla que no terminó dentro del presupuesto real de la llamada.</summary>
        public static string PartialRuleWarning(string regla, int presupuestoMs) =>
            "La regla " + regla + " no terminó dentro del plazo de " + Seconds(presupuestoMs) + " s: resultado parcial.";

        /// <summary>
        /// Advertencia parcial genérica de las reglas con el presupuesto real; con 8000 ms es
        /// <c>AuditSupport.WarningPartial</c>, el texto de §5C.
        /// </summary>
        public static string PartialWarning(int presupuestoMs) =>
            "Se agotó el plazo de " + Seconds(presupuestoMs) + " s: el resultado es parcial.";

        /// <summary>Advertencia cuando el bloque de resumen falla: los hallazgos ya calculados se conservan.</summary>
        public static string SummaryFailedWarning(string tipoExcepcion) =>
            "No se pudo calcular el resumen del archivo (" + tipoExcepcion + "): resumen_archivo queda en null; los hallazgos son válidos.";

        /// <summary>Espacio libre exigido antes de copiar: 2 × tamaño + 100 MiB; satura en long.MaxValue.</summary>
        public static long RequiredFreeBytes(long fileBytes)
        {
            if (fileBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fileBytes));
            }

            return fileBytes > (long.MaxValue - FreeSpaceMarginBytes) / 2 ? long.MaxValue : (2 * fileBytes) + FreeSpaceMarginBytes;
        }

        /// <summary>"libres &lt;n&gt; MB, necesarios &lt;m&gt; MB en &lt;unidad&gt;": MB (1024²) enteros redondeados, cultura invariante.</summary>
        public static string DiskSpaceDetail(long libres, long necesarios, string unidad) =>
            "libres " + WholeMb(libres) + " MB, necesarios " + WholeMb(necesarios) + " MB en " + unidad;

        /// <summary>Advertencia cuando la copia temporal no se pudo borrar.</summary>
        public static string CopyNotDeletedWarning(string ruta) => "No se pudo borrar la copia temporal " + ruta + ".";

        private static bool IsKnown(string? comprobacion)
        {
            foreach (string conocida in Comprobaciones)
            {
                if (string.Equals(conocida, comprobacion, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Segundos sin ceros de sobra y con punto decimal, cultura invariante: 8000 → "8", 2500 → "2.5".</summary>
        private static string Seconds(int milisegundos) =>
            (milisegundos / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

        private static string WholeMb(long bytes)
        {
            decimal mb = Math.Round((decimal)bytes / BytesPerMb, MidpointRounding.AwayFromZero);
            return mb.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Campos auxiliares de la fila del lote (§5H.2) medidos sobre la base lateral: capas con entidades, recuadros de lámina y
    /// escalas. Quien llama solo pasa valores ya leídos; aquí se deduplica y se acota. De los textos del plano solo se conserva
    /// el número de la escala: la respuesta nunca reenvía el texto de terceros.
    /// </summary>
    public sealed class FileAuxMeasure
    {
        /// <summary>Tope de recuadros y de escalas: un archivo con miles de entidades en esas capas no debe inflar la respuesta.</summary>
        public const int MaxItems = 500;

        private static readonly (double ancho, double alto)[] NoSheets = Array.Empty<(double ancho, double alto)>();

        private readonly HashSet<string> seenLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<int> scales = new SortedSet<int>();

        /// <summary>Capas sin repetir (sin distinguir mayúsculas), con el nombre de la primera aparición.</summary>
        public List<string> Capas { get; } = new List<string>();

        /// <summary>[ancho, alto] de las entidades de las capas de recuadro, en unidades del archivo.</summary>
        public List<double[]> Recuadros { get; } = new List<double[]>();

        /// <summary>Forma canónica "Escala 1:N", de menor a mayor N.</summary>
        public List<string> TextosEscala
        {
            get
            {
                var textos = new List<string>(scales.Count);
                foreach (int n in scales)
                {
                    textos.Add("Escala 1:" + n.ToString(CultureInfo.InvariantCulture));
                }

                return textos;
            }
        }

        public void AddLayer(string? capa)
        {
            if (!string.IsNullOrEmpty(capa) && seenLayers.Add(capa))
            {
                Capas.Add(capa);
            }
        }

        public void AddRecuadro(double ancho, double alto)
        {
            if (double.IsFinite(ancho) && double.IsFinite(alto) && Recuadros.Count < MaxItems)
            {
                Recuadros.Add(new[] { ancho, alto });
            }
        }

        public void AddText(string? texto)
        {
            if (!ExportRules.IsScaleText(texto))
            {
                return;
            }

            foreach (int n in ExportRules.DetectSheet(NoSheets, new[] { texto! }).Escalas)
            {
                if (scales.Count < MaxItems)
                {
                    scales.Add(n);
                }
            }
        }
    }
}
