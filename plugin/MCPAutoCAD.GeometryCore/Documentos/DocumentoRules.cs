using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Documentos
{
    /// <summary>Documento abierto en AutoCAD, lo mínimo para resolver una consulta del cliente.</summary>
    /// <param name="Nombre">Nombre de archivo (p. ej. "PLANTA.dwg"), o el nombre provisional de un dibujo sin guardar.</param>
    /// <param name="Ruta">Ruta completa, o null si el dibujo nunca se ha guardado.</param>
    /// <param name="Activo">true si es el documento activo.</param>
    public sealed record DocumentoRef(string Nombre, string? Ruta, bool Activo);

    /// <summary>Resultado de <see cref="DocumentoRules.Buscar"/>.</summary>
    public enum ResultadoBusqueda
    {
        Encontrado,
        NoEncontrado,
        Ambiguo,
    }

    /// <param name="Estado">Encontrado, no encontrado o ambiguo.</param>
    /// <param name="Indice">Índice en la lista de entrada si Encontrado; null en otro caso.</param>
    /// <param name="Candidatos">Índices de todos los documentos que coinciden (en Ambiguo, para mostrar la lista).</param>
    public sealed record ResultadoConsulta(ResultadoBusqueda Estado, int? Indice, IReadOnlyList<int> Candidatos);

    /// <summary>
    /// Reglas puras de los handlers de documentos (guardar, abrir_documento, activar_documento): resolver una consulta
    /// (nombre o ruta) entre los documentos abiertos, canonicalizar rutas de forma léxica y detectar si una ruta está
    /// dentro de una carpeta de solo lectura. Sin AutoCAD ni Win32: los alias (8.3, unidades mapeadas) los resuelve el plugin
    /// y pasa aquí todas las formas de la ruta.
    /// </summary>
    public static class DocumentoRules
    {
        /// <summary>
        /// Forma canónica léxica de una ruta: '/' → '\', separadores repetidos colapsados, segmentos "." eliminados y ".."
        /// resueltos (sin subir por encima de la raíz), y mayúsculas/minúsculas sin tocar. Conserva la raíz de unidad
        /// ("C:\") o el prefijo UNC ("\\servidor\recurso"). No toca el disco.
        /// </summary>
        public static string Canonica(string ruta)
        {
            string r = (ruta ?? string.Empty).Trim().Replace('/', '\\');
            string prefijo;
            if (r.StartsWith(@"\\", StringComparison.Ordinal))
            {
                prefijo = @"\\";
                r = r.Substring(2);
            }
            else if (r.Length >= 2 && r[1] == ':')
            {
                prefijo = r.Substring(0, 2) + @"\";
                r = r.Substring(2);
            }
            else
            {
                prefijo = string.Empty;
            }

            var partes = new List<string>();
            foreach (string s in r.Split('\\'))
            {
                if (s.Length == 0 || s == ".")
                {
                    continue;
                }

                if (s == "..")
                {
                    if (partes.Count > 0)
                    {
                        partes.RemoveAt(partes.Count - 1);
                    }

                    continue;
                }

                partes.Add(s);
            }

            return prefijo + string.Join(@"\", partes);
        }

        /// <summary>
        /// Busca UN documento por ruta (si la consulta es una ruta: contiene '\', '/' o empieza por "X:") o por nombre
        /// de archivo (sin distinguir mayúsculas; sin extensión también vale si es única). La ruta se canonicaliza antes de
        /// comparar. Coincidencias ≠ 1 se devuelven como NoEncontrado (0) o Ambiguo (más de uno, con todos los candidatos).
        /// </summary>
        public static ResultadoConsulta Buscar(IReadOnlyList<DocumentoRef> documentos, string consulta)
        {
            string q = (consulta ?? string.Empty).Trim();
            var hits = new List<int>();
            if (q.Length > 0)
            {
                bool esRuta = q.IndexOf('\\') >= 0 || q.IndexOf('/') >= 0 || (q.Length >= 2 && q[1] == ':');
                string qRuta = SinBarraFinal(Canonica(q));
                for (int i = 0; i < documentos.Count; i++)
                {
                    DocumentoRef d = documentos[i];
                    bool coincide = esRuta
                        ? d.Ruta != null && Igual(SinBarraFinal(Canonica(d.Ruta)), qRuta)
                        : CoincideNombre(d.Nombre, q);
                    if (coincide)
                    {
                        hits.Add(i);
                    }
                }
            }

            if (hits.Count == 1)
            {
                return new ResultadoConsulta(ResultadoBusqueda.Encontrado, hits[0], hits);
            }

            return hits.Count == 0
                ? new ResultadoConsulta(ResultadoBusqueda.NoEncontrado, null, hits)
                : new ResultadoConsulta(ResultadoBusqueda.Ambiguo, null, hits);
        }

        /// <summary>
        /// true si <paramref name="ruta"/> está dentro de alguna carpeta de <paramref name="carpetas"/> (comparación de
        /// prefijo canónico, sin mayúsculas). Acepta barra final o no, '/' y raíces de unidad ("C:\" cubre todo C:) y UNC.
        /// "O:\Planos" no cubre "O:\Planos2\x.dwg". Una ruta igual a la carpeta NO cuenta como dentro (es un directorio).
        /// Entradas vacías se ignoran.
        /// </summary>
        public static bool EstaEnCarpeta(string ruta, IReadOnlyList<string> carpetas)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                return false;
            }

            string normalizada = Canonica(ruta);
            foreach (string carpeta in carpetas)
            {
                if (string.IsNullOrWhiteSpace(carpeta))
                {
                    continue;
                }

                string carpetaCanon = SinBarraFinal(Canonica(carpeta));
                if (carpetaCanon.Length == 0)
                {
                    continue;
                }

                string prefijo = carpetaCanon.EndsWith('\\') ? carpetaCanon : carpetaCanon + "\\";
                if (normalizada.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase) && normalizada.Length > prefijo.Length)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>true si la ruta contiene un nombre corto 8.3 (una componente con "~" seguido de dígito, p. ej. "PLANOS~1").</summary>
        public static bool TieneNombreCorto(string? ruta) =>
            ruta != null && System.Text.RegularExpressions.Regex.IsMatch(ruta, @"~\d", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Nombres de archivo que AutoCAD carga automáticamente desde la carpeta del dibujo, sin pasar por el diálogo de LISP:
        /// acaddoc.lsp/.fas/.vlx, acad.lsp/.fas/.vlx, acad.mnl y &lt;stem&gt;.mnl. El plugin comprueba cuáles existen.
        /// </summary>
        public static IReadOnlyList<string> NombresAutoCarga(string stem)
        {
            return new List<string>
            {
                "acaddoc.lsp", "acaddoc.fas", "acaddoc.vlx", "acad.lsp", "acad.fas", "acad.vlx", "acad.mnl", stem + ".mnl",
            };
        }

        private static string SinBarraFinal(string s) => s.TrimEnd('\\');

        private static bool CoincideNombre(string? nombre, string consulta)
        {
            if (string.IsNullOrEmpty(nombre))
            {
                return false;
            }

            if (Igual(nombre, consulta))
            {
                return true;
            }

            // Sin extensión: "PLANTA" encuentra "PLANTA.dwg" (solo si la consulta no lleva extensión).
            bool consultaSinExt = System.IO.Path.GetExtension(consulta).Length == 0;
            return consultaSinExt && Igual(System.IO.Path.GetFileNameWithoutExtension(nombre), consulta);
        }

        private static bool Igual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
