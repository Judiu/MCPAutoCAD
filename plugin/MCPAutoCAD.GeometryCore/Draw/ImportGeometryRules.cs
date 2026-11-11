using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Reglas puras de <c>importar_geometria</c> (fallo 32): validación de la ruta de un DXF local, tipos de entidad que se
    /// importan, filtro de capas, plan de conteos (previsualización), comparación de unidades y bloqueos de capa. Sin AutoCAD.
    /// Los tipos llegan ya normalizados por el plugin (nombres DXF en mayúsculas; POLYLINE = 2D, LWPOLYLINE, 3D aparte).
    /// </summary>
    public static class ImportGeometryRules
    {
        /// <summary>Tamaño máximo del DXF de entrada (200 MB).</summary>
        public const long MaxFileBytes = 200L * 1024 * 1024;

        /// <summary>Máximo de entidades importables que pasan el filtro en una llamada.</summary>
        public const int MaxEntities = 100000;

        /// <summary>Handles devueltos en la respuesta de escritura (el resto, solo como total).</summary>
        public const int MaxSampleHandles = 50;

        /// <summary>Máximo de nombres en filtrar_capas.</summary>
        public const int MaxFilterLayers = 200;

        /// <summary>Tipos DXF que se importan (en orden de presentación).</summary>
        public static readonly IReadOnlyList<string> ImportableTypes = new[]
        {
            "LINE", "LWPOLYLINE", "POLYLINE", "CIRCLE", "ARC", "ELLIPSE", "SPLINE", "POINT", "TEXT", "MTEXT", "HATCH", "SOLID",
        };

        private static readonly HashSet<string> Importable = new HashSet<string>(ImportableTypes, StringComparer.OrdinalIgnoreCase);

        private const string WildcardChars = "*?<>\"|";

        /// <summary>true si el tipo DXF (LINE, LWPOLYLINE…) es de los que se importan.</summary>
        public static bool IsImportable(string tipo) => !string.IsNullOrEmpty(tipo) && Importable.Contains(tipo);

        /// <summary>true si la entidad pasa el filtro: tipo importable y, si hay filtro, capa incluida (sin distinguir mayúsculas).</summary>
        public static bool Selecciona(string tipo, string capa, IReadOnlySet<string>? filtro) =>
            IsImportable(tipo) && (filtro == null || filtro.Contains(capa ?? string.Empty));

        /// <summary>
        /// Valida la ruta SIN tocar el disco: unidad local absoluta (C:\…), sin UNC ni rutas de red, extensión .dxf, sin '..',
        /// sin comodines, sin caracteres de control ni flujos NTFS (':' fuera de la unidad). Null si es válida.
        /// </summary>
        public static string? ValidatePath(string ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                return "ruta: no puede estar vacía";
            }

            // Una unidad asignada (Z:\...) se admite: solo se lee. Lo que no se admite es UNC (\\servidor\...).
            if (ruta.StartsWith("\\\\", StringComparison.Ordinal) || ruta.StartsWith("//", StringComparison.Ordinal))
            {
                return "ruta: no se admiten rutas UNC (\\\\servidor\\...): use una unidad (local o asignada)";
            }

            if (!Regex.IsMatch(ruta, @"^[A-Za-z]:[\\/]"))
            {
                return "ruta: debe ser absoluta en una unidad (por ejemplo C:\\... o Z:\\...)";
            }

            if (ruta.IndexOf(':', 2) >= 0)
            {
                return "ruta: ':' solo puede aparecer como unidad (no se admiten flujos de datos NTFS)";
            }

            if (ruta.Any(c => char.IsControl(c)) || ruta.IndexOfAny(WildcardChars.ToCharArray()) >= 0)
            {
                return "ruta: no se admiten comodines ni caracteres de control";
            }

            if (ruta.Split('\\', '/').Any(seg => seg == ".."))
            {
                return "ruta: no se admiten '..'";
            }

            if (!ruta.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
            {
                return "ruta: solo se admiten archivos .dxf";
            }

            return null;
        }

        /// <summary>Null si el archivo cabe en <see cref="MaxFileBytes"/>; si no, el detalle con el tamaño real en MB.</summary>
        public static string? ValidateSize(long bytes)
        {
            if (bytes <= MaxFileBytes)
            {
                return null;
            }

            double mb = bytes / 1048576.0;
            return "ruta: el archivo pesa " + mb.ToString("0.0", CultureInfo.InvariantCulture)
                + " MB; el máximo es " + (MaxFileBytes / 1048576L).ToString(CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>
        /// Normaliza filtrar_capas: null si no hay filtro (importar todas); si lo hay, nombres sin repetir (sin distinguir
        /// mayúsculas). Lista vacía, nombre en blanco o más de <see cref="MaxFilterLayers"/> → <paramref name="error"/>.
        /// </summary>
        public static HashSet<string>? NormalizeFilter(IReadOnlyList<string>? capas, out string? error)
        {
            error = null;
            if (capas == null)
            {
                return null;
            }

            if (capas.Count == 0)
            {
                error = "filtrar_capas: la lista está vacía; omítala para importar todas las capas";
                return null;
            }

            if (capas.Count > MaxFilterLayers)
            {
                error = "filtrar_capas: máximo " + MaxFilterLayers.ToString(CultureInfo.InvariantCulture) + " capas";
                return null;
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string capa in capas)
            {
                if (string.IsNullOrWhiteSpace(capa))
                {
                    error = "filtrar_capas: hay un nombre de capa vacío";
                    return null;
                }

                set.Add(capa.Trim());
            }

            return set;
        }

        /// <summary>Mensaje de INVALID_PARAMS cuando hay demasiadas entidades para una llamada.</summary>
        public static string TooManyEntities(int n) =>
            "el archivo tiene " + n.ToString(CultureInfo.InvariantCulture) + " entidades importables que pasan el filtro (máximo "
            + MaxEntities.ToString(CultureInfo.InvariantCulture) + " por llamada): use filtrar_capas para importar por partes";

        /// <summary>Una entidad del archivo: tipo DXF normalizado y capa tal como viene en el archivo.</summary>
        public sealed record EntidadArchivo(string Tipo, string Capa);

        /// <summary>
        /// Plan de importación (previsualización y comprobaciones). <see cref="Seleccionadas"/> cuenta las importables que
        /// pasan el filtro; <see cref="Omitidas"/> las no importables de capas incluidas; <see cref="ExcluidasPorCapa"/> todo
        /// lo que queda fuera del filtro. Total = Seleccionadas + Σ Omitidas + ExcluidasPorCapa.
        /// </summary>
        public sealed class ImportPlan
        {
            public ImportPlan(
                int total, int seleccionadas, IReadOnlyDictionary<string, int> porTipo, IReadOnlyDictionary<string, int> porCapa,
                IReadOnlyDictionary<string, int> omitidas, int excluidasPorCapa, IReadOnlyList<string> capasNuevas,
                IReadOnlyList<string> capasExistentes, IReadOnlyList<string> filtroSinCoincidencia)
            {
                Total = total;
                Seleccionadas = seleccionadas;
                PorTipo = porTipo;
                PorCapa = porCapa;
                Omitidas = omitidas;
                ExcluidasPorCapa = excluidasPorCapa;
                CapasNuevas = capasNuevas;
                CapasExistentes = capasExistentes;
                FiltroSinCoincidencia = filtroSinCoincidencia;
            }

            public int Total { get; }
            public int Seleccionadas { get; }
            public IReadOnlyDictionary<string, int> PorTipo { get; }
            public IReadOnlyDictionary<string, int> PorCapa { get; }
            public IReadOnlyDictionary<string, int> Omitidas { get; }
            public int ExcluidasPorCapa { get; }

            /// <summary>Capas (nombre del archivo) que usan entidades seleccionadas y no existen en el dibujo.</summary>
            public IReadOnlyList<string> CapasNuevas { get; }

            /// <summary>Capas que usan entidades seleccionadas y ya existen en el dibujo (conservan su definición).</summary>
            public IReadOnlyList<string> CapasExistentes { get; }

            /// <summary>Nombres de filtrar_capas que no existen en el archivo (advertencia, no error).</summary>
            public IReadOnlyList<string> FiltroSinCoincidencia { get; }

            /// <summary>true si hay más entidades seleccionadas que <see cref="MaxEntities"/>.</summary>
            public bool Excede => Seleccionadas > MaxEntities;
        }

        /// <summary>
        /// Cuenta por tipo, capa y omitidas. <paramref name="filtro"/> null = todas las capas. <paramref name="capasDestino"/> =
        /// capas que ya existen en el dibujo (sin distinguir mayúsculas).
        /// </summary>
        public static ImportPlan Plan(IEnumerable<EntidadArchivo> entidades, IReadOnlySet<string>? filtro, IReadOnlySet<string> capasDestino)
        {
            var porTipo = new Dictionary<string, int>(StringComparer.Ordinal);
            var porCapa = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var omitidas = new Dictionary<string, int>(StringComparer.Ordinal);
            var capasVistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var capasOrden = new List<string>();
            var filtroVisto = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0, seleccionadas = 0, excluidas = 0;

            foreach (EntidadArchivo e in entidades)
            {
                total++;
                string capa = e.Capa ?? string.Empty;
                if (filtro != null)
                {
                    filtroVisto.Add(capa);
                }

                if (filtro != null && !filtro.Contains(capa))
                {
                    excluidas++;
                    continue;
                }

                if (!IsImportable(e.Tipo))
                {
                    Add(omitidas, e.Tipo);
                    continue;
                }

                seleccionadas++;
                Add(porTipo, e.Tipo);
                Add(porCapa, capa);
                if (capasVistas.Add(capa))
                {
                    capasOrden.Add(capa);
                }
            }

            var nuevas = capasOrden.Where(c => !capasDestino.Contains(c)).ToList();
            var existentes = capasOrden.Where(c => capasDestino.Contains(c)).ToList();
            var sinCoincidencia = filtro == null
                ? new List<string>()
                : filtro.Where(f => !filtroVisto.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

            return new ImportPlan(
                total,
                seleccionadas,
                porTipo,
                porCapa,
                omitidas,
                excluidas,
                nuevas,
                existentes,
                sinCoincidencia);
        }

        private static void Add(IDictionary<string, int> dict, string clave)
        {
            dict.TryGetValue(clave, out int n);
            dict[clave] = n + 1;
        }

        /// <summary>Estado de la comparación de unidades entre el DXF y el dibujo.</summary>
        public enum EstadoUnidades
        {
            /// <summary>Iguales y con unidades declaradas.</summary>
            Correcto,

            /// <summary>Alguna de las dos no declara unidades: se importa sin escalar, con aviso.</summary>
            Advertencia,

            /// <summary>Unidades distintas y ambas declaradas: se rechaza (no hay escalado automático).</summary>
            Incompatible,
        }

        /// <summary>Resultado de <see cref="CompareUnits"/>.</summary>
        public sealed record VeredictoUnidades(EstadoUnidades Estado, string Mensaje);

        /// <summary>
        /// Compara INSUNITS del DXF (<paramref name="origen"/>) con el dibujo (<paramref name="destino"/>). Iguales → correcto;
        /// origen 0 o destino 0 → advertencia; distintas y no nulas → incompatible (sin escalado automático en esta versión).
        /// <paramref name="nombreUnidad"/> traduce el código INSUNITS al nombre en español.
        /// </summary>
        public static VeredictoUnidades CompareUnits(int origen, int destino, Func<int, string> nombreUnidad)
        {
            if (origen == destino && origen != 0)
            {
                return new VeredictoUnidades(EstadoUnidades.Correcto, "unidades: el archivo y el dibujo están en " + nombreUnidad(origen) + ".");
            }

            if (origen == 0 && destino == 0)
            {
                return new VeredictoUnidades(
                    EstadoUnidades.Advertencia, "unidades: ni el archivo ni el dibujo declaran unidades (INSUNITS=0): no se escala nada.");
            }

            if (origen == 0)
            {
                return new VeredictoUnidades(
                    EstadoUnidades.Advertencia,
                    "unidades: el DXF no declara unidades (INSUNITS=0); se importa sin escalar. Compruebe que está en " + nombreUnidad(destino) + ".");
            }

            if (destino == 0)
            {
                return new VeredictoUnidades(
                    EstadoUnidades.Advertencia,
                    "unidades: el dibujo no declara unidades (INSUNITS=0); el DXF está en " + nombreUnidad(origen) + " y se importa sin escalar.");
            }

            return new VeredictoUnidades(
                EstadoUnidades.Incompatible,
                "unidades: el DXF está en " + nombreUnidad(origen) + " y el dibujo en " + nombreUnidad(destino)
                + ". importar_geometria no escala automáticamente en esta versión: convierta el DXF a las unidades del dibujo.");
        }

        /// <summary>
        /// Motivo por el que una capa existente del dibujo no admite entidades nuevas (R3: bloqueada, congelada o dependiente de
        /// xref), o null si es escribible. Apagada no bloquea (solo avisa).
        /// </summary>
        public static string? LayerBlockReason(bool bloqueada, bool congelada, bool dependienteXref)
        {
            if (dependienteXref)
            {
                return "capa dependiente de xref";
            }

            if (congelada)
            {
                return "capa congelada";
            }

            return bloqueada ? "capa bloqueada" : null;
        }

        /// <summary>Mensaje de INVALID_PARAMS con las capas que no admiten entidades (pares nombre + motivo).</summary>
        public static string BlockedLayersMessage(IReadOnlyList<(string Nombre, string Motivo)> capas) =>
            "capas del DXF que no admiten entidades en el dibujo: "
            + string.Join(", ", capas.Select(c => "'" + c.Nombre + "' (" + c.Motivo + ")"))
            + ". Desbloquéelas o excluyalas con filtrar_capas.";

        /// <summary>
        /// Aviso de entidades importables fuera del plano XY: normal distinta de +Z o cota Z distinta de cero. Se importan igual
        /// (sin transformar); el aviso pide comprobar su posición. Null si <paramref name="n"/> es 0.
        /// </summary>
        public static string? AvisoFueraDePlanoXY(int n) =>
            n <= 0
                ? null
                : "importar_geometria: " + n.ToString(CultureInfo.InvariantCulture)
                  + " entidades tienen normal distinta de +Z o una cota Z distinta de 0: se importan tal cual, sin transformar; compruebe su posición.";

        /// <summary>Capa nueva del DXF con su estado (congelada, apagada o bloqueada) tal como llega en el archivo.</summary>
        public sealed record EstadoCapaNueva(string Nombre, bool Congelada, bool Apagada, bool Bloqueada);

        /// <summary>
        /// Aviso con las capas nuevas que llegan congeladas, apagadas o bloqueadas: sus entidades quedarán ocultas o no
        /// editables. Null si ninguna llega con estado.
        /// </summary>
        public static string? AvisoCapasNuevasConEstado(IEnumerable<EstadoCapaNueva> capas)
        {
            var partes = new List<string>();
            foreach (EstadoCapaNueva c in capas)
            {
                var estados = new List<string>();
                if (c.Congelada)
                {
                    estados.Add("congelada");
                }

                if (c.Apagada)
                {
                    estados.Add("apagada");
                }

                if (c.Bloqueada)
                {
                    estados.Add("bloqueada");
                }

                if (estados.Count > 0)
                {
                    partes.Add("'" + c.Nombre + "' (" + string.Join(", ", estados) + ")");
                }
            }

            return partes.Count == 0
                ? null
                : "capas nuevas que llegan con estado: " + string.Join(", ", partes)
                  + ". Las entidades importadas en ellas quedarán ocultas (congeladas o apagadas) o no editables (bloqueadas).";
        }

        /// <summary>Tablas de definiciones que existen en cualquier dibujo: repetirlas no merece aviso.</summary>
        private static readonly HashSet<string> DefinicionesEstandar = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Standard", "Continuous", "ByLayer", "ByBlock",
        };

        /// <summary>Máximo de nombres citados en <see cref="AvisoDefinicionesExistentes"/>.</summary>
        private const int MaxNombresAviso = 10;

        /// <summary>
        /// Aviso de estilos de texto o tipos de línea del DXF que ya existen en el dibujo: con DuplicateRecordCloning.Ignore se
        /// conserva la definición del dibujo, no la del archivo. <paramref name="etiqueta"/> = "estilos de texto" o "tipos de
        /// línea". Ignora Standard, Continuous, ByLayer y ByBlock. Null si no hay coincidencias.
        /// </summary>
        public static string? AvisoDefinicionesExistentes(string etiqueta, IEnumerable<string> delDxf, IEnumerable<string> existentesEnDibujo)
        {
            var enDibujo = new HashSet<string>(existentesEnDibujo ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var coincidentes = (delDxf ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n) && !DefinicionesEstandar.Contains(n) && enDibujo.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (coincidentes.Count == 0)
            {
                return null;
            }

            string lista = string.Join(", ", coincidentes.Take(MaxNombresAviso).Select(n => "'" + n + "'"));
            if (coincidentes.Count > MaxNombresAviso)
            {
                lista += " y " + (coincidentes.Count - MaxNombresAviso).ToString(CultureInfo.InvariantCulture) + " más";
            }

            return "importar_geometria: " + etiqueta + " del DXF que ya existen en el dibujo (" + lista
                + "): se conserva la definición del dibujo, no la del archivo.";
        }

        /// <summary>Aviso de HATCH importados: el orden de dibujo (SORTENTS) del DXF no se conserva. Null si n es 0.</summary>
        public static string? AvisoOrdenDeDibujoHatch(int n) =>
            n <= 0
                ? null
                : "importar_geometria: " + n.ToString(CultureInfo.InvariantCulture)
                  + " HATCH importados: el orden de dibujo (SORTENTS) del DXF no se conserva; revise el orden si se solapan.";

        /// <summary>Aviso de HATCH que quedaron no asociativos porque su contorno no se importó. Null si n es 0.</summary>
        public static string? AvisoHatchNoAsociativos(int n) =>
            n <= 0
                ? null
                : "importar_geometria: " + n.ToString(CultureInfo.InvariantCulture)
                  + " HATCH quedaron no asociativos: su contorno asociado no se importó, así que son relleno fijo.";

        /// <summary>Aviso cuando ninguna entidad importada tiene extensión calculable: R4 no pudo comprobar la colocación.</summary>
        public const string AvisoSinExtension =
            "importar_geometria: ninguna entidad importada tiene extensión calculable: no se pudo comprobar la colocación (R4).";
    }
}
