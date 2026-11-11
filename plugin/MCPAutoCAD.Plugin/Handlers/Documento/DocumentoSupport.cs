using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.GeometryCore.Documentos;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Documento
{
    /// <summary>
    /// Soporte común de guardar, abrir_documento y activar_documento (0.9.x). Hilo principal. Lee los documentos abiertos,
    /// resuelve una consulta (nombre o ruta) con <see cref="DocumentoRules"/>, canonicaliza rutas (alias 8.3 y unidades
    /// mapeadas) y lee <c>carpetas_solo_lectura</c> de lps.json RELEYÉNDOLO en cada llamada (sin modificarlo).
    /// </summary>
    internal static class DocumentoSupport
    {
        /// <summary>Clave opcional de lps.json: lista de carpetas cuyos dibujos nunca se guardan ni se abren en escritura desde el puente.</summary>
        public const string LpsKeyReadOnlyFolders = "carpetas_solo_lectura";

        private const string ReadOnlyFoldersMalformed =
            "lps.json: carpetas_solo_lectura debe ser una lista de rutas absolutas (p. ej. [\"O:\\Planos\\Cerrados\"]); "
            + "por seguridad no se guarda ni se abre nada desde el puente hasta corregirla";

        private const string LpsIlegible =
            "lps.json existe pero no se puede leer como JSON: no se puede comprobar carpetas_solo_lectura; "
            + "por seguridad no se guarda ni se abre nada desde el puente hasta corregirlo";

        private const uint FileFlagBackupSemantics = 0x02000000;   // necesario para abrir directorios
        private const uint FileShareAll = 0x7;                     // read | write | delete
        private const uint OpenExisting = 3;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathName(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string lpszLocalName, StringBuilder lpszRemoteName, ref int cchRemoteName);

        /// <summary>Documentos abiertos en orden de colección.</summary>
        public static List<Document> Abiertos(DocumentCollection docs)
        {
            var list = new List<Document>();
            foreach (Document d in docs)
            {
                list.Add(d);
            }

            return list;
        }

        /// <summary>Lista para la respuesta: nombre, ruta (null si nunca se guardó) y si es el activo.</summary>
        public static List<DocumentoRef> Describir(IReadOnlyList<Document> abiertos, Document? activo)
        {
            var refs = new List<DocumentoRef>(abiertos.Count);
            foreach (Document d in abiertos)
            {
                refs.Add(new DocumentoRef(Path.GetFileName(d.Name ?? ""), RutaDe(d), ReferenceEquals(d, activo)));
            }

            return refs;
        }

        /// <summary>Forma JSON de la lista de documentos (mismo formato que estado.documentos).</summary>
        public static List<object> Filas(IReadOnlyList<DocumentoRef> refs)
        {
            var filas = new List<object>(refs.Count);
            foreach (DocumentoRef r in refs)
            {
                filas.Add(new { nombre = r.Nombre, ruta = r.Ruta, activo = r.Activo });
            }

            return filas;
        }

        /// <summary>
        /// Ruta completa de un documento con nombre, o null si nunca se guardó. Formato único en todo el puente (estado,
        /// documentos, guardar, abrir): ruta completa absoluta, sin canonicalizar alias.
        /// </summary>
        public static string? RutaDe(Document d)
        {
            if (!d.IsNamedDrawing || string.IsNullOrEmpty(d.Name))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(d.Name);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return d.Name;
            }
        }

        /// <summary>
        /// Resuelve <paramref name="consulta"/> entre los documentos abiertos. Sin coincidencia → NOT_FOUND con la lista de
        /// abiertos; varias → INVALID_PARAMS con los candidatos (para que el cliente pida la ruta completa).
        /// </summary>
        public static Document Resolver(DocumentCollection docs, string consulta)
        {
            List<Document> abiertos = Abiertos(docs);
            List<DocumentoRef> refs = Describir(abiertos, docs.MdiActiveDocument);
            ResultadoConsulta r = DocumentoRules.Buscar(refs, consulta);
            if (r.Estado == ResultadoBusqueda.Encontrado)
            {
                return abiertos[r.Indice!.Value];
            }

            var lista = Filas(refs);
            if (r.Estado == ResultadoBusqueda.NoEncontrado)
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "No hay ningún documento abierto que coincida con '" + consulta + "'. Use el nombre o la ruta de uno de los documentos abiertos.",
                    new Dictionary<string, object?> { ["documento"] = consulta, ["documentos"] = lista });
            }

            var candidatos = new List<object>();
            foreach (int i in r.Candidatos)
            {
                candidatos.Add(lista[i]);
            }

            throw ToolException.InvalidParams(
                "documento: '" + consulta + "' coincide con " + r.Candidatos.Count + " documentos abiertos; indique la ruta completa",
                new Dictionary<string, object?> { ["documento"] = consulta, ["documentos"] = candidatos });
        }

        /// <summary>Documento abierto cuya ruta coincide con <paramref name="rutaCompleta"/> en alguna forma canónica (alias incluidos), o null.</summary>
        public static Document? PorRuta(DocumentCollection docs, string rutaCompleta)
        {
            List<string> buscada = Formas(rutaCompleta);
            foreach (Document d in Abiertos(docs))
            {
                string? r = RutaDe(d);
                if (r == null)
                {
                    continue;
                }

                foreach (string f in Formas(r))
                {
                    if (buscada.Exists(b => string.Equals(b, f, StringComparison.OrdinalIgnoreCase)))
                    {
                        return d;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Formas canónicas de una ruta: la léxica, la de nombre largo (GetLongPathName resuelve alias 8.3) y, si está en una
        /// unidad mapeada, la UNC (WNetGetConnection). Sin duplicados. No exige que exista.
        /// </summary>
        public static List<string> Formas(string ruta)
        {
            var formas = new List<string>();
            string lexica = DocumentoRules.Canonica(ruta);
            AnadirSinDuplicar(formas, lexica);
            string largo = NombreLargo(lexica) ?? lexica;
            AnadirSinDuplicar(formas, DocumentoRules.Canonica(largo));
            // Ruta final real (GetFinalPathNameByHandle): cubre SUBST, junctions, symlinks y prefijos \\?\. Solo si existe.
            string? final = RutaFinal(lexica);
            if (final != null)
            {
                AnadirSinDuplicar(formas, DocumentoRules.Canonica(final));
            }

            foreach (string? basePlena in new string?[] { largo, final })
            {
                if (basePlena != null && basePlena.Length >= 2 && char.IsLetter(basePlena[0]) && basePlena[1] == ':')
                {
                    string? unc = UncDeUnidad(basePlena.Substring(0, 2));
                    if (unc != null)
                    {
                        AnadirSinDuplicar(formas, DocumentoRules.Canonica(unc + basePlena.Substring(2)));
                    }
                }
            }

            return formas;
        }

        /// <summary>true si la ruta contiene un nombre corto 8.3 (p. ej. "PLANOS~1"): no se acepta en abrir_documento.</summary>
        public static bool TieneNombreCorto(string ruta) => DocumentoRules.TieneNombreCorto(ruta);

        /// <summary>
        /// true si alguna forma de <paramref name="ruta"/> está dentro de alguna carpeta de <paramref name="carpetas"/>
        /// (también en todas sus formas: letra, nombre largo y UNC).
        /// </summary>
        public static bool EnCarpetaSoloLectura(string ruta, IReadOnlyList<string> carpetas)
        {
            var formasCarpetas = new List<string>();
            foreach (string c in carpetas)
            {
                foreach (string f in Formas(c))
                {
                    formasCarpetas.Add(f);
                }
            }

            foreach (string f in Formas(ruta))
            {
                if (DocumentoRules.EstaEnCarpeta(f, formasCarpetas))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Los métodos que cambian el documento activo o abren dibujos solo pueden hacerlo en contexto de aplicación. Con un
        /// comando activo el handler no debe llegar aquí (BUSY); esto es la comprobación de seguridad.
        /// </summary>
        public static void RequerirContextoAplicacion(DocumentCollection docs)
        {
            if (!docs.IsApplicationContext)
            {
                throw ToolException.BusyCommand("");
            }
        }

        /// <summary>
        /// Activa <paramref name="doc"/> como documento activo. Devuelve true si después es el activo. Si
        /// DocumentActivationEnabled es false (AutoCAD no permite cambiar la activación ahora) no hace nada y devuelve false.
        /// </summary>
        public static bool Activar(DocumentCollection docs, Document doc)
        {
            if (!docs.DocumentActivationEnabled)
            {
                return false;
            }

            docs.MdiActiveDocument = doc;
            return ReferenceEquals(docs.MdiActiveDocument, doc);
        }

        /// <summary>
        /// Avisos de los diálogos que AutoCAD puede mostrar al abrir un dibujo (DWGCHECK, PROXYNOTICE, SECURELOAD). Los usan
        /// abrir_documento y nuevo_documento: solo añaden advertencias; el rechazo por archivos de carga automática va aparte.
        /// </summary>
        public static void AnadirAvisosDeApertura(List<string> warnings)
        {
            int? dwgCheck = SafeIntSysVar("DWGCHECK");
            // DWGCHECK 1 o 3: AutoCAD pregunta al abrir un dibujo que puede estar dañado. Otros valores no bloquean.
            if (dwgCheck == 1 || dwgCheck == 3)
            {
                warnings.Add("DWGCHECK=" + dwgCheck.Value + ": AutoCAD puede abrir un diálogo al abrir el dibujo que bloquee el puente hasta que el usuario responda.");
            }

            int? proxyNotice = SafeIntSysVar("PROXYNOTICE");
            if (proxyNotice != null && proxyNotice.Value != 0)
            {
                warnings.Add("PROXYNOTICE=" + proxyNotice.Value + ": AutoCAD puede abrir un diálogo por objetos proxy que bloquee el puente hasta que el usuario responda.");
            }

            if (SafeIntSysVar("SECURELOAD") == 0)
            {
                warnings.Add("SECURELOAD=0: AutoCAD carga código de la carpeta del dibujo sin avisar (aquí se rechaza si hay archivos de carga automática).");
            }
        }

        /// <summary>Primer archivo de bloqueo de AutoCAD (.dwl / .dwl2) de un dibujo, o null. Su presencia haría que AutoCAD pregunte.</summary>
        public static string? BloqueoDe(string rutaDwg)
        {
            foreach (string ext in new[] { ".dwl", ".dwl2" })
            {
                string candidato = Path.ChangeExtension(rutaDwg, ext);
                if (File.Exists(candidato))
                {
                    return candidato;
                }
            }

            return null;
        }

        /// <summary>
        /// Archivos de carga automática presentes en la carpeta del dibujo (acaddoc.*, acad.*, acad.mnl y &lt;nombre&gt;.mnl).
        /// Si existe alguno, AutoCAD ejecuta código sin pasar por el diálogo de aprobación de LISP: abrir_documento rechaza.
        /// </summary>
        public static List<string> AutoCargadosEn(string rutaDwg)
        {
            var encontrados = new List<string>();
            string dir = Path.GetDirectoryName(rutaDwg) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(rutaDwg);
            foreach (string n in DocumentoRules.NombresAutoCarga(stem))
            {
                string p = Path.Combine(dir, n);
                if (File.Exists(p))
                {
                    encontrados.Add(p);
                }
            }

            return encontrados;
        }

        /// <summary>Sysvar entera, o null si AutoCAD no la devuelve.</summary>
        public static int? SafeIntSysVar(string name)
        {
            try
            {
                return AcadUtil.GetIntSysVar(name);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("sysvar " + name + " no legible (" + ex.GetType().Name + ").");
                return null;
            }
        }

        /// <summary>
        /// Carpetas de solo lectura de lps.json (<see cref="LpsKeyReadOnlyFolders"/>). Se RELEE el archivo en cada llamada
        /// (no hace falta reiniciar AutoCAD). Sin lps.json o sin la clave → lista vacía. Si el archivo existe y no es JSON
        /// legible, o la clave no es un array de rutas absolutas no vacías → INVALID_PARAMS (fail-closed).
        /// </summary>
        public static List<string> CarpetasSoloLectura()
        {
            var carpetas = new List<string>();
            string? ruta = LpsStandard.Ruta;
            if (string.IsNullOrEmpty(ruta) || !File.Exists(ruta))
            {
                return carpetas;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllBytes(ruta));
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                PluginLog.Error("lps.json ilegible en " + ruta, ex);
                throw ToolException.InvalidParams(LpsIlegible);
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams(LpsIlegible);
                }

                if (!root.TryGetProperty(LpsKeyReadOnlyFolders, out JsonElement el))
                {
                    return carpetas;
                }

                if (el.ValueKind != JsonValueKind.Array)
                {
                    throw ToolException.InvalidParams(ReadOnlyFoldersMalformed);
                }

                foreach (JsonElement item in el.EnumerateArray())
                {
                    string? carpeta = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (string.IsNullOrWhiteSpace(carpeta) || !Path.IsPathFullyQualified(carpeta))
                    {
                        throw ToolException.InvalidParams(ReadOnlyFoldersMalformed);
                    }

                    carpetas.Add(carpeta);
                }
            }

            return carpetas;
        }

        private static void AnadirSinDuplicar(List<string> lista, string valor)
        {
            foreach (string v in lista)
            {
                if (string.Equals(v, valor, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            lista.Add(valor);
        }

        /// <summary>
        /// Ruta final real de un archivo o directorio EXISTENTE (CreateFile solo con atributos + GetFinalPathNameByHandle, VOLUME_NAME_DOS),
        /// sin el prefijo \\?\. Resuelve SUBST, junctions y symlinks. null si no existe o Win32 falla.
        /// </summary>
        private static string? RutaFinal(string ruta)
        {
            if (!File.Exists(ruta) && !Directory.Exists(ruta))
            {
                return null;
            }

            try
            {
                IntPtr h = CreateFile(ruta, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
                if (h == InvalidHandle || h == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var sb = new StringBuilder(4096);
                    uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                    if (n == 0 || n >= sb.Capacity)
                    {
                        return null;
                    }

                    string s = sb.ToString();
                    if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    {
                        return @"\\" + s.Substring(8);
                    }

                    return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s.Substring(4) : s;
                }
                finally
                {
                    CloseHandle(h);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is SEHException)
            {
                PluginLog.Warn("GetFinalPathNameByHandle no disponible (" + ex.GetType().Name + ").");
                return null;
            }
        }

        /// <summary>Nombre largo de una ruta existente (GetLongPathName), o null si Win32 no puede resolverla.</summary>
        private static string? NombreLargo(string ruta)
        {
            try
            {
                var sb = new StringBuilder(4096);
                uint n = GetLongPathName(ruta, sb, (uint)sb.Capacity);
                return n == 0 || n >= sb.Capacity ? null : sb.ToString();
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is SEHException)
            {
                PluginLog.Warn("GetLongPathName no disponible (" + ex.GetType().Name + ").");
                return null;
            }
        }

        /// <summary>Ruta UNC de una unidad mapeada ("O:" → "\\srv\obras"), o null si no es unidad de red.</summary>
        private static string? UncDeUnidad(string unidad)
        {
            try
            {
                var sb = new StringBuilder(512);
                int len = sb.Capacity;
                int rc = WNetGetConnection(unidad, sb, ref len);
                return rc == 0 ? sb.ToString() : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is SEHException)
            {
                PluginLog.Warn("WNetGetConnection no disponible (" + ex.GetType().Name + ").");
                return null;
            }
        }
    }
}
