using System;

namespace MCPAutoCAD.GeometryCore.Imagenes
{
    /// <summary>
    /// Lectura de las dimensiones en píxeles desde la cabecera de un archivo de imagen (PNG, JPEG, BMP, TIFF), sin
    /// decodificar la imagen. Sirve para validar que el archivo corresponde a su extensión y para calcular el marco antes de
    /// copiar o escribir nada. Cualquier dato fuera de rango devuelve false (nunca lanza).
    /// </summary>
    public static class ImageHeader
    {
        /// <summary>
        /// Dimensiones de la imagen contenida en <paramref name="datos"/> si su firma corresponde a <paramref name="extension"/>
        /// (".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"; sin distinguir mayúsculas). false si no coincide o no se puede leer.
        /// </summary>
        public static bool TryDimensiones(string extension, byte[] datos, out int ancho, out int alto)
        {
            ancho = 0;
            alto = 0;
            if (datos == null)
            {
                return false;
            }

            string ext = (extension ?? string.Empty).ToLowerInvariant();
            bool ok;
            switch (ext)
            {
                case ".png":
                    ok = Png(datos, out ancho, out alto);
                    break;
                case ".jpg":
                case ".jpeg":
                    ok = Jpeg(datos, out ancho, out alto);
                    break;
                case ".bmp":
                    ok = Bmp(datos, out ancho, out alto);
                    break;
                case ".tif":
                case ".tiff":
                    ok = Tiff(datos, out ancho, out alto);
                    break;
                default:
                    ok = false;
                    break;
            }

            if (!ok || ancho <= 0 || alto <= 0)
            {
                ancho = 0;
                alto = 0;
                return false;
            }

            return true;
        }

        private static bool Png(byte[] d, out int ancho, out int alto)
        {
            ancho = 0;
            alto = 0;
            byte[] firma = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (d.Length < 24)
            {
                return false;
            }

            for (int i = 0; i < firma.Length; i++)
            {
                if (d[i] != firma[i])
                {
                    return false;
                }
            }

            // Primer fragmento: longitud (4), "IHDR" (4), ancho (4, big-endian), alto (4, big-endian).
            if (d[12] != 'I' || d[13] != 'H' || d[14] != 'D' || d[15] != 'R')
            {
                return false;
            }

            long w = U32Be(d, 16);
            long h = U32Be(d, 20);
            return ToInt(w, out ancho) && ToInt(h, out alto);
        }

        private static bool Bmp(byte[] d, out int ancho, out int alto)
        {
            ancho = 0;
            alto = 0;
            if (d.Length < 26 || d[0] != (byte)'B' || d[1] != (byte)'M')
            {
                return false;
            }

            long cabecera = U32Le(d, 14);
            if (cabecera == 12)
            {
                // Cabecera OS/2 antigua: dimensiones sin signo de 16 bits.
                ancho = U16Le(d, 18);
                alto = U16Le(d, 20);
                return true;
            }

            if (cabecera < 40 || d.Length < 26)
            {
                return false;
            }

            // Cabecera Windows: alto negativo = filas de arriba abajo (se usa el valor absoluto).
            long w = (int)U32Le(d, 18);
            long h = (int)U32Le(d, 22);
            return ToInt(Math.Abs(w), out ancho) && ToInt(Math.Abs(h), out alto);
        }

        private static bool Jpeg(byte[] d, out int ancho, out int alto)
        {
            ancho = 0;
            alto = 0;
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8)
            {
                return false;
            }

            int pos = 2;
            while (pos < d.Length)
            {
                if (d[pos] != 0xFF)
                {
                    return false;
                }

                while (pos < d.Length && d[pos] == 0xFF)
                {
                    pos++;
                }

                if (pos >= d.Length)
                {
                    return false;
                }

                byte marcador = d[pos];
                pos++;
                if (marcador == 0xD9)
                {
                    return false;
                }

                if (marcador == 0x01 || (marcador >= 0xD0 && marcador <= 0xD7))
                {
                    continue;   // marcadores sin longitud
                }

                if (pos + 2 > d.Length)
                {
                    return false;
                }

                int longitud = (d[pos] << 8) | d[pos + 1];
                if (longitud < 2)
                {
                    return false;
                }

                if (EsSof(marcador))
                {
                    if (pos + 7 > d.Length)
                    {
                        return false;
                    }

                    // Segmento SOF: longitud (2), precisión (1), alto (2), ancho (2), big-endian.
                    alto = (d[pos + 3] << 8) | d[pos + 4];
                    ancho = (d[pos + 5] << 8) | d[pos + 6];
                    return ancho > 0 && alto > 0;
                }

                pos += longitud;
            }

            return false;
        }

        private static bool Tiff(byte[] d, out int ancho, out int alto)
        {
            ancho = 0;
            alto = 0;
            if (d.Length < 8)
            {
                return false;
            }

            bool le;
            if (d[0] == (byte)'I' && d[1] == (byte)'I' && d[2] == 0x2A && d[3] == 0x00)
            {
                le = true;
            }
            else if (d[0] == (byte)'M' && d[1] == (byte)'M' && d[2] == 0x00 && d[3] == 0x2A)
            {
                le = false;
            }
            else
            {
                return false;
            }

            long ifd = le ? U32Le(d, 4) : U32Be(d, 4);
            if (ifd < 0 || ifd + 2 > d.Length)
            {
                return false;
            }

            int n = le ? U16Le(d, (int)ifd) : U16Be(d, (int)ifd);
            long? w = null;
            long? h = null;
            for (int i = 0; i < n; i++)
            {
                long e = ifd + 2 + 12L * i;
                if (e + 12 > d.Length)
                {
                    break;
                }

                int etiqueta = le ? U16Le(d, (int)e) : U16Be(d, (int)e);
                int tipo = le ? U16Le(d, (int)e + 2) : U16Be(d, (int)e + 2);
                if (etiqueta != 256 && etiqueta != 257)
                {
                    continue;
                }

                long valor;
                if (tipo == 3)
                {
                    valor = le ? U16Le(d, (int)e + 8) : U16Be(d, (int)e + 8);
                }
                else if (tipo == 4)
                {
                    valor = le ? U32Le(d, (int)e + 8) : U32Be(d, (int)e + 8);
                }
                else
                {
                    continue;
                }

                if (etiqueta == 256)
                {
                    w = valor;
                }
                else
                {
                    h = valor;
                }
            }

            if (w == null || h == null)
            {
                return false;
            }

            return ToInt(w.Value, out ancho) && ToInt(h.Value, out alto);
        }

        private static bool EsSof(byte m) =>
            m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC;

        private static bool ToInt(long v, out int r)
        {
            if (v >= 1 && v <= int.MaxValue)
            {
                r = (int)v;
                return true;
            }

            r = 0;
            return false;
        }

        private static int U16Le(byte[] d, int o) => d[o] | (d[o + 1] << 8);

        private static int U16Be(byte[] d, int o) => (d[o] << 8) | d[o + 1];

        private static long U32Le(byte[] d, int o) =>
            (long)d[o] | ((long)d[o + 1] << 8) | ((long)d[o + 2] << 16) | ((long)d[o + 3] << 24);

        private static long U32Be(byte[] d, int o) =>
            ((long)d[o] << 24) | ((long)d[o + 1] << 16) | ((long)d[o + 2] << 8) | d[o + 3];
    }
}
