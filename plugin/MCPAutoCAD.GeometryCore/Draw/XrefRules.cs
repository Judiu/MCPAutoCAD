using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Reglas puras de adjuntar_xref y gestionar_xref (fallo 37, plugin 0.10.3). Sin AutoCAD: validación de la ruta y del
    /// nombre de la referencia, ruta relativa entre el dibujo anfitrión y la referencia, decisión ante un nombre ya usado y
    /// las comprobaciones de gestionar_xref. Las rutas se tratan como texto Windows ('\' o '/'); los llamadores pasan rutas
    /// absolutas (unidad o UNC) y la comparación no toca el disco.
    /// </summary>
    public static class XrefRules
    {
        /// <summary>Longitud máxima del nombre de la referencia (como el de los bloques).</summary>
        public const int MaxNombre = 200;

        /// <summary>Tipos de referencia admitidos por adjuntar_xref.</summary>
        public static readonly string[] Tipos = { "attach", "overlay" };

        /// <summary>Acciones admitidas por gestionar_xref.</summary>
        public static readonly string[] Acciones = { "recargar", "descargar", "separar" };

        /// <summary>Límite de rotación en grados (±10 vueltas), suficiente para cualquier plano.</summary>
        public const double MaxRotacionGrados = 3600.0;

        /// <summary>Aviso cuando el dibujo anfitrión no está guardado y no hay carpeta a la que hacer relativa la ruta.</summary>
        public const string HostNoGuardado =
            "El dibujo activo no está guardado: la referencia se guarda con ruta absoluta (guarde el dibujo para usar rutas relativas).";

        /// <summary>Aviso cuando no existe ruta relativa (distinta unidad o red): se guarda absoluta.</summary>
        public const string SinRutaRelativa =
            "No hay ruta relativa entre el dibujo y la referencia (distinta unidad o servidor): se guarda con ruta absoluta.";

        /// <summary>Caracteres que no pueden aparecer en la ruta de un .dwg de referencia (comodines y los de Windows).</summary>
        private const string CaracteresProhibidos = "*?\"<>|";

        /// <summary>Caracteres que no se admiten en el nombre de una referencia, además de los de BlockNameRules.</summary>
        private static readonly char[] CaracteresNombreProhibidos = { ',', '=', ';', '`' };

        private static readonly Regex UnidadAbsoluta = new Regex(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant);

        /// <summary>
        /// null si la ruta vale para una referencia; si no, el detalle con el prefijo "ruta: ". Misma forma que
        /// ExportRules.ValidatePath, sin la lista de carpetas (eso lo comprueba el plugin): absoluta en unidad (no UNC), sin
        /// ':' salvo la unidad, sin '..', sin comodines ni caracteres de control y con extensión .dwg.
        /// </summary>
        public static string? ValidateRuta(string ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta))
            {
                return "ruta: no puede estar vacía";
            }

            if (ruta.StartsWith("\\\\", StringComparison.Ordinal) || ruta.StartsWith("//", StringComparison.Ordinal))
            {
                return "ruta: no se admiten rutas UNC (\\\\servidor\\...): use una unidad (local o asignada)";
            }

            if (!UnidadAbsoluta.IsMatch(ruta))
            {
                return "ruta: debe ser absoluta en una unidad (por ejemplo C:\\... o Z:\\...)";
            }

            if (ruta.IndexOf(':', 2) >= 0)
            {
                return "ruta: ':' solo puede aparecer como unidad (no se admiten flujos de datos NTFS)";
            }

            foreach (char c in ruta)
            {
                if (char.IsControl(c) || CaracteresProhibidos.IndexOf(c) >= 0)
                {
                    return "ruta: no se admiten comodines ni caracteres de control";
                }
            }

            if (Array.Exists(Split(ruta), seg => seg == ".."))
            {
                return "ruta: no se admiten '..'";
            }

            if (!ruta.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                return "ruta: solo se admiten archivos .dwg";
            }

            return null;
        }

        /// <summary>
        /// Nombre por defecto de la referencia: el nombre del archivo sin la extensión (<c>C:\x\PLANTA 01.dwg</c> →
        /// <c>PLANTA 01</c>). Puede no ser un nombre válido; el llamador lo valida con <see cref="ValidateNombre"/>.
        /// </summary>
        public static string DefaultName(string ruta)
        {
            string[] partes = Split(ruta ?? string.Empty);
            string archivo = partes.Length == 0 ? string.Empty : partes[partes.Length - 1];
            return archivo.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                ? archivo.Substring(0, archivo.Length - 4)
                : archivo;
        }

        /// <summary>
        /// null si el nombre sirve para una definición de referencia; si no, el detalle con el prefijo "nombre: ". Reutiliza
        /// <see cref="BlockNameRules.Validate"/> (sin rutas, sin anónimos, sin extensiones ni nombres de dispositivo) y añade
        /// el límite de longitud. La validación contra la tabla de símbolos de AutoCAD la hace el handler.
        /// </summary>
        public static string? ValidateNombre(string? nombre)
        {
            if (string.IsNullOrEmpty(nombre))
            {
                return "nombre: no puede estar vacío (indíquelo o use un archivo con nombre válido)";
            }

            if (BlockNameRules.Validate(nombre) is string error)
            {
                return error;
            }

            if (nombre.IndexOfAny(CaracteresNombreProhibidos) >= 0)
            {
                return "nombre: no se admiten las comas, signos de igual, punto y coma ni comillas invertidas";
            }

            if (nombre.Length > MaxNombre)
            {
                return "nombre: debe tener entre 1 y " + MaxNombre + " caracteres";
            }

            return null;
        }

        /// <summary>
        /// Ruta de <paramref name="target"/> relativa a la carpeta de <paramref name="hostFile"/> (el dibujo anfitrión), con
        /// '\' como separador, en el formato de AutoCAD: <c>plano.dwg</c> (misma carpeta), <c>sub\plano.dwg</c> (subcarpeta),
        /// <c>..\plano.dwg</c> (carpeta superior), <c>..\otra\plano.dwg</c> (carpeta hermana). Null si no hay ruta relativa
        /// (rutas no absolutas en unidad, distinta unidad o UNC, o alguna ruta con '..'): el llamador guarda la absoluta.
        /// </summary>
        public static string? RelativePath(string hostFile, string target)
        {
            if (!IsAbsoluteDrive(hostFile) || !IsAbsoluteDrive(target))
            {
                return null;
            }

            string[] host = Split(hostFile);
            string[] dest = Split(target);
            if (Array.Exists(host, seg => seg == "..") || Array.Exists(dest, seg => seg == ".."))
            {
                return null;
            }

            if (!string.Equals(host[0], dest[0], StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Las carpetas son los segmentos entre la unidad y el nombre del archivo.
            int hostDirs = host.Length - 2;
            int destDirs = dest.Length - 2;
            int comunes = 0;
            while (comunes < hostDirs && comunes < destDirs
                && string.Equals(host[1 + comunes], dest[1 + comunes], StringComparison.OrdinalIgnoreCase))
            {
                comunes++;
            }

            var partes = new List<string>();
            for (int i = comunes; i < hostDirs; i++)
            {
                partes.Add("..");
            }

            for (int i = 1 + comunes; i < dest.Length; i++)
            {
                partes.Add(dest[i]);
            }

            // Sin subir de carpeta, AutoCAD guarda el prefijo ".\" (".\x.dwg", ".\sub\x.dwg").
            if (hostDirs - comunes == 0)
            {
                partes.Insert(0, ".");
            }

            return string.Join("\\", partes);
        }

        /// <summary>
        /// Ruta absoluta de una referencia guardada en el dibujo: la misma si ya es absoluta; si es relativa, se resuelve
        /// desde la carpeta de <paramref name="hostFile"/> (con '..' ya normalizados). Null si no se puede resolver.
        /// </summary>
        public static string? AbsoluteFrom(string? hostFile, string? guardada)
        {
            if (string.IsNullOrWhiteSpace(guardada))
            {
                return null;
            }

            if (IsAbsoluteDrive(guardada))
            {
                string[]? absoluta = Normalize(Split(guardada));
                return absoluta == null ? null : string.Join("\\", absoluta);
            }

            if (hostFile == null || !IsAbsoluteDrive(hostFile))
            {
                return null;
            }

            string[] host = Split(hostFile);
            var combinada = new List<string>(host.Length + 4);
            combinada.AddRange(host);
            combinada.RemoveAt(combinada.Count - 1);   // nombre del dibujo anfitrión
            combinada.AddRange(Split(guardada));
            string[]? resuelta = Normalize(combinada.ToArray());
            return resuelta == null ? null : string.Join("\\", resuelta);
        }

        /// <summary>true si ambas rutas (absolutas, en unidad) apuntan al mismo archivo, sin distinguir mayúsculas.</summary>
        public static bool SamePath(string? a, string? b)
        {
            if (!IsAbsoluteDrive(a) || !IsAbsoluteDrive(b))
            {
                return false;
            }

            string[]? na = Normalize(Split(a!));
            string[]? nb = Normalize(Split(b!));
            return na != null && nb != null
                && string.Equals(string.Join("\\", na), string.Join("\\", nb), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Resultado de <see cref="DecideName"/>.</summary>
        public enum Decision
        {
            /// <summary>Se adjunta una referencia nueva con ese nombre.</summary>
            Adjuntar,

            /// <summary>Ya existe la referencia de ese archivo y solo_insertar pide otro INSERT de ella.</summary>
            Reutilizar,

            /// <summary>No se puede: ver el mensaje.</summary>
            Rechazar,
        }

        /// <summary>
        /// Qué hacer con el nombre de la referencia. <paramref name="existe"/> = ya hay una definición de bloque con ese nombre
        /// (sin distinguir mayúsculas); <paramref name="esReferencia"/> = esa definición es una xref; <paramref name="rutaExistente"/>
        /// = su ruta absoluta resuelta (null si no se pudo resolver); <paramref name="soloInsertar"/> = el cliente pide solo
        /// insertar otra referencia de un archivo ya adjuntado. Sin AutoCAD.
        /// </summary>
        public static Decision DecideName(bool existe, bool esReferencia, string? rutaExistente, string ruta, bool soloInsertar, out string? error)
        {
            error = null;
            if (!existe)
            {
                if (soloInsertar)
                {
                    error = "solo_insertar: no hay ninguna referencia con ese nombre en el dibujo; quite solo_insertar para adjuntarla";
                    return Decision.Rechazar;
                }

                return Decision.Adjuntar;
            }

            if (!esReferencia)
            {
                error = "nombre: ya existe un bloque con ese nombre que no es una referencia externa";
                return Decision.Rechazar;
            }

            if (!SamePath(rutaExistente, ruta))
            {
                error = "nombre: ya existe una referencia con ese nombre que no apunta a este archivo";
                return Decision.Rechazar;
            }

            if (soloInsertar)
            {
                return Decision.Reutilizar;
            }

            error = "nombre: ya existe una referencia con ese nombre apuntando a este mismo archivo; use solo_insertar: true para insertar otra referencia de ella";
            return Decision.Rechazar;
        }

        /// <summary>
        /// Comprobación de gestionar_xref antes de actuar: null si la acción tiene sentido para el estado actual, o el detalle
        /// con el prefijo "accion: ". Descargar una referencia ya descargada no hace nada y se rechaza.
        /// </summary>
        public static string? ErrorAccion(string accion, string estado)
        {
            if (Array.IndexOf(Acciones, accion) < 0)
            {
                return "accion: debe ser recargar, descargar o separar";
            }

            if (accion == "descargar" && estado == "descargada")
            {
                return "accion: la referencia ya está descargada";
            }

            return null;
        }

        /// <summary>
        /// Aviso si la referencia declara unidades (INSUNITS) distintas de las del dibujo anfitrión; null si no hay
        /// diferencia o alguna no está declarada (0). La escala pedida no convierte unidades: lo avisa, no lo corrige.
        /// </summary>
        public static string? UnidadesDistintas(int? insunitsReferencia, int insunitsHost)
        {
            if (insunitsReferencia == null || insunitsReferencia.Value == 0 || insunitsHost == 0 || insunitsReferencia.Value == insunitsHost)
            {
                return null;
            }

            return "La referencia declara INSUNITS " + insunitsReferencia.Value + " (" + UnitsTable.Name(insunitsReferencia.Value)
                + ") y el dibujo " + insunitsHost + " (" + UnitsTable.Name(insunitsHost)
                + "): escala no convierte unidades; ajuste 'escala' si la referencia no está en las unidades del dibujo.";
        }

        /// <summary>true si la ruta es absoluta en una unidad (C:\..., no UNC).</summary>
        private static bool IsAbsoluteDrive(string? ruta)
        {
            return !string.IsNullOrEmpty(ruta) && UnidadAbsoluta.IsMatch(ruta!) && ruta!.IndexOf(':', 2) < 0;
        }

        /// <summary>Segmentos de una ruta ('\' o '/'), sin vacíos.</summary>
        private static string[] Split(string ruta)
        {
            return ruta.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Resuelve los '.' y '..' de una lista de segmentos. Null si un '..' sube por encima de la unidad (ruta inválida).
        /// </summary>
        private static string[]? Normalize(string[] segmentos)
        {
            var salida = new List<string>(segmentos.Length);
            foreach (string seg in segmentos)
            {
                if (seg == ".")
                {
                    continue;
                }

                if (seg == "..")
                {
                    if (salida.Count <= 1)
                    {
                        return null;
                    }

                    salida.RemoveAt(salida.Count - 1);
                    continue;
                }

                salida.Add(seg);
            }

            return salida.ToArray();
        }
    }
}
