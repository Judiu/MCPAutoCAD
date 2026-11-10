using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MCPAutoCAD.GeometryCore.Documentos;

namespace MCPAutoCAD.GeometryCore.Imagenes
{
    /// <summary>
    /// Marco de una imagen raster en el plano (WCS). El origen es la esquina inferior izquierda (la posición del cliente);
    /// (UX, UY) es el eje del ancho (módulo = ancho en unidades del dibujo) y (VX, VY) el eje de la altura (módulo = alto).
    /// </summary>
    public readonly record struct MarcoImagen(double UX, double UY, double VX, double VY, double Ancho, double Alto)
    {
        /// <summary>Caja envolvente [mínimo, máximo] de la imagen si su origen es (<paramref name="ox"/>, <paramref name="oy"/>).</summary>
        public (double MinX, double MinY, double MaxX, double MaxY) Caja(double ox, double oy)
        {
            double x1 = ox;
            double x2 = ox + UX;
            double x3 = ox + VX;
            double x4 = ox + UX + VX;
            double y1 = oy;
            double y2 = oy + UY;
            double y3 = oy + VY;
            double y4 = oy + UY + VY;
            return (
                Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)),
                Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)),
                Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)),
                Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)));
        }
    }

    /// <summary>Decisión sobre el archivo de destino de una copia: nombre final y si ya existe con el mismo contenido.</summary>
    public readonly record struct DecisionCopia(string Nombre, bool Reutilizar);

    /// <summary>
    /// Reglas puras de insertar_imagen (fallo 24): extensiones admitidas, validación de la ruta de origen, tope de tamaño,
    /// nombre libre al copiar junto al DWG (nunca se sobrescribe), ruta relativa de referencia, clave de diccionario de
    /// definiciones de imagen y marco (ancho → alto por proporción, rotación). Sin AutoCAD ni disco.
    /// </summary>
    public static class ImagenRules
    {
        /// <summary>Extensiones admitidas (sin distinguir mayúsculas).</summary>
        public static readonly string[] Extensiones = { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp" };

        /// <summary>Tope de tamaño del archivo de origen (50 MB).</summary>
        public const long TamanoMaximoBytes = 50L * 1024 * 1024;

        /// <summary>Carpeta, junto al DWG, donde se copian las imágenes.</summary>
        public const string CarpetaImagenes = "imagenes";

        /// <summary>Número máximo de sufijos "_n" que se prueban al buscar un nombre libre.</summary>
        public const int MaxIntentosCopia = 999;

        private const string Prefijo = "MCP_IMG_";
        private const int MaxLongitudClave = 100;

        /// <summary>
        /// Valida la ruta de origen SIN tocar el disco. Debe ser absoluta con unidad (C:\...), no UNC (\\servidor\... ni
        /// \\?\...: se usa la unidad asignada), sin '..' ni caracteres no válidos ni comodines, y terminar en una extensión
        /// admitida. Devuelve null si es válida, o el detalle "ruta: ..." para INVALID_PARAMS.
        /// </summary>
        public static string? ValidarRutaOrigen(string? ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                return "ruta: obligatoria";
            }

            string r = ruta;
            if (r.StartsWith(@"\\", StringComparison.Ordinal) || r.StartsWith("//", StringComparison.Ordinal))
            {
                return "ruta: no se admiten rutas de red (UNC); use la unidad asignada (p. ej. O:\\...)";
            }

            if (!(r.Length >= 3 && IsAsciiLetter(r[0]) && r[1] == ':' && (r[2] == '\\' || r[2] == '/')))
            {
                return "ruta: debe ser una ruta absoluta con unidad (p. ej. C:\\imagenes\\logo.png)";
            }

            for (int i = 0; i < r.Length; i++)
            {
                char c = r[i];
                if (char.IsControl(c) || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' || (c == ':' && i != 1))
                {
                    return "ruta: contiene caracteres no válidos";
                }
            }

            foreach (string segmento in r.Split('\\', '/'))
            {
                if (segmento == "..")
                {
                    return "ruta: no se admiten '..'";
                }
            }

            string nombre = NombreArchivo(r);
            if (nombre.Length == 0)
            {
                return "ruta: debe apuntar a un archivo, no a una carpeta";
            }

            return ExtensionPermitida(nombre)
                ? null
                : "ruta: extensión no admitida (" + string.Join(", ", Extensiones) + ")";
        }

        /// <summary>true si la extensión de <paramref name="nombreOruta"/> es una admitida (sin distinguir mayúsculas).</summary>
        public static bool ExtensionPermitida(string nombreOruta)
        {
            string ext = Path.GetExtension(nombreOruta ?? string.Empty);
            foreach (string e in Extensiones)
            {
                if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Tamaño del archivo de origen: vacío o mayor que <see cref="TamanoMaximoBytes"/> → detalle "ruta: ...". null si vale.</summary>
        public static string? ValidarTamano(long bytes)
        {
            if (bytes <= 0)
            {
                return "ruta: el archivo está vacío";
            }

            if (bytes > TamanoMaximoBytes)
            {
                return "ruta: el archivo pesa " + (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)
                    + " MB; el máximo admitido es " + (TamanoMaximoBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB";
            }

            return null;
        }

        /// <summary>Nombre de archivo de una ruta (acepta '\' y '/'); cadena vacía si la ruta termina en separador.</summary>
        public static string NombreArchivo(string ruta)
        {
            string r = (ruta ?? string.Empty).Replace('/', '\\');
            int i = r.LastIndexOf('\\');
            return i < 0 ? r : r.Substring(i + 1);
        }

        /// <summary>
        /// Elige el nombre del archivo de destino dentro de la carpeta de imágenes: <paramref name="nombre"/> si está libre;
        /// si está ocupado con el mismo contenido, se reutiliza; si está ocupado con otro, "nombre_2.ext", "nombre_3.ext"…
        /// Nunca sobrescribe. Devuelve null si se agotan <see cref="MaxIntentosCopia"/> sufijos.
        /// </summary>
        /// <param name="existe">true si ya hay un archivo con ese nombre en el destino.</param>
        /// <param name="mismoContenido">true si ese archivo existente tiene el mismo SHA-256 que el origen.</param>
        public static DecisionCopia? ResolverCopia(string nombre, Func<string, bool> existe, Func<string, bool> mismoContenido)
        {
            string stem = Path.GetFileNameWithoutExtension(nombre);
            string ext = Path.GetExtension(nombre);
            string candidato = nombre;
            for (int n = 2; ; n++)
            {
                if (!existe(candidato))
                {
                    return new DecisionCopia(candidato, false);
                }

                if (mismoContenido(candidato))
                {
                    return new DecisionCopia(candidato, true);
                }

                if (n - 1 > MaxIntentosCopia)
                {
                    return null;
                }

                candidato = stem + "_" + n.ToString(CultureInfo.InvariantCulture) + ext;
            }
        }

        /// <summary>Ruta relativa de referencia para el dibujo: <c>.\imagenes\&lt;nombre&gt;</c>.</summary>
        public static string RutaRelativa(string nombre) => @".\" + CarpetaImagenes + @"\" + nombre;

        /// <summary>
        /// Clave de la definición de imagen en el diccionario ACAD_IMAGE_DICT: "MCP_IMG_" + nombre sin extensión con los caracteres
        /// no admitidos sustituidos por "_", y un sufijo "_2", "_3"… si ya existe (sin distinguir mayúsculas).
        /// </summary>
        public static string ClaveDiccionario(string nombreArchivo, IEnumerable<string> existentes)
        {
            var ocupadas = new HashSet<string>(existentes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string stem = Path.GetFileNameWithoutExtension(nombreArchivo ?? string.Empty);
            var sb = new StringBuilder(stem.Length);
            foreach (char c in stem)
            {
                sb.Append(IsAsciiLetter(c) || (c >= '0' && c <= '9') || c == '_' || c == '-' ? c : '_');
            }

            string limpio = sb.Length > MaxLongitudClave ? sb.ToString(0, MaxLongitudClave) : sb.ToString();
            string basePlena = Prefijo + (limpio.Length == 0 ? "IMG" : limpio);
            string clave = basePlena;
            for (int n = 2; ocupadas.Contains(clave); n++)
            {
                clave = basePlena + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            return clave;
        }

        /// <summary>true si dos rutas designan el mismo archivo lexicográficamente (sin mayúsculas, '/' = '\').</summary>
        public static bool MismaRuta(string? a, string? b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            return string.Equals(DocumentoRules.Canonica(a), DocumentoRules.Canonica(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Marco de la imagen: ancho en unidades del dibujo, alto por la proporción de píxeles y rotación antihoraria en grados.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Ancho no finito o ≤ 0, o píxeles ≤ 0.</exception>
        public static MarcoImagen Marco(double ancho, double rotacionGrados, int pxAncho, int pxAlto)
        {
            if (!double.IsFinite(ancho) || ancho <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ancho));
            }

            if (pxAncho <= 0 || pxAlto <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pxAncho));
            }

            double alto = ancho * pxAlto / pxAncho;
            double rad = rotacionGrados * Math.PI / 180.0;
            double c = Snap(Math.Cos(rad));
            double s = Snap(Math.Sin(rad));
            return new MarcoImagen(ancho * c, ancho * s, -alto * s, alto * c, ancho, alto);
        }

        /// <summary>SHA-256 en hexadecimal mayúsculas (para comparar contenidos).</summary>
        public static string Sha256Hex(byte[] datos) => Convert.ToHexString(SHA256.HashData(datos ?? Array.Empty<byte>()));

        /// <summary>Redondea a cero los residuos de coseno/seno (p. ej. sin(180°)) para que 90°, 180° y 270° den ejes exactos.</summary>
        private static double Snap(double v) => Math.Abs(v) < 1e-12 ? 0.0 : v;

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }
}
