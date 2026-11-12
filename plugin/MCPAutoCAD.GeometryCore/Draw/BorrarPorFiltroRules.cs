using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Reglas puras de borrar_por_filtro (fallo 40a): validación de la combinación de filtros, tope por llamada,
    /// comprobación de n_esperado y plan de borrado (conteos por tipo y capa, omitidas por motivo, muestra de handles).
    /// Sin AutoCAD: el plugin lee cada entidad del espacio, construye <see cref="Candidato"/> y delega aquí la decisión.
    /// </summary>
    public static class BorrarPorFiltroRules
    {
        /// <summary>Máximo de entidades que coinciden con el filtro en una llamada (lectura y escritura).</summary>
        public const int MaxEntities = 20000;

        /// <summary>Handles de muestra en la respuesta.</summary>
        public const int MaxSample = 20;

        /// <summary>Motivos por los que una entidad que cumple el filtro NO se borra (se cuenta, no falla la llamada).</summary>
        public enum SkipReason
        {
            /// <summary>Atributo (ATTRIB): se borra el INSERT, no el atributo. Guarda interna: el recorrido no lo produce.</summary>
            Atributo,

            /// <summary>Dentro de una definición de bloque. Guarda interna: el recorrido solo mira el primer nivel.</summary>
            DentroDeBloque,

            /// <summary>Viewport: nunca se borra.</summary>
            Viewport,

            /// <summary>INSERT de un bloque definido en un xref externo u overlay (usar la herramienta de xref).</summary>
            XrefBloque,

            /// <summary>Capa bloqueada.</summary>
            CapaBloqueada,

            /// <summary>Capa dependiente de xref.</summary>
            CapaXref,
        }

        /// <summary>Nombre del motivo en la respuesta (snake_case, como el resto de campos del protocolo).</summary>
        public static string SkipReasonName(SkipReason reason)
        {
            switch (reason)
            {
                case SkipReason.Atributo:
                    return "atributo";
                case SkipReason.DentroDeBloque:
                    return "dentro_de_bloque";
                case SkipReason.Viewport:
                    return "viewport";
                case SkipReason.XrefBloque:
                    return "xref";
                case SkipReason.CapaBloqueada:
                    return "capa_bloqueada";
                case SkipReason.CapaXref:
                    return "capa_xref";
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }

        /// <summary>
        /// true si el patrón coincide con cualquier nombre no vacío: solo '*' y '?' y al menos un '*' (p. ej. "*", "?*", "*?").
        /// </summary>
        public static bool IsUniversalPattern(string? pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            bool hasStar = false;
            foreach (char c in pattern)
            {
                if (c == '*')
                {
                    hasStar = true;
                }
                else if (c != '?')
                {
                    return false;
                }
            }

            return hasStar;
        }

        /// <summary>
        /// Combinación de filtros (sin AutoCAD). Reglas, en orden:
        /// al menos uno de capas, tipos o bbox (nunca se borra el espacio entero por omisión); con bbox, modo_bbox es
        /// obligatorio; 'dentro' exige bbox; un filtro de capas que coincide con todas las capas (todos sus patrones son
        /// universales) sin tipos ni bbox se rechaza. Devuelve el <c>detalle</c> de INVALID_PARAMS o null.
        /// </summary>
        public static string? ValidateFilters(
            IReadOnlyList<string>? capas, bool hasTipos, bool hasBbox, string? modoBbox, string accion = "borra")
        {
            bool hasCapas = capas != null;
            if (!hasCapas && !hasTipos && !hasBbox)
            {
                return "capas, tipos o bbox: al menos uno es obligatorio (no se " + accion + " el espacio entero por omisión)";
            }

            if (hasBbox && modoBbox == null)
            {
                return "modo_bbox: obligatorio con bbox (use 'interseca' o 'dentro')";
            }

            if (string.Equals(modoBbox, EntityFilter.BboxModeInside, StringComparison.Ordinal) && !hasBbox)
            {
                return "modo_bbox: 'dentro' requiere bbox";
            }

            if (capas != null && capas.Count > 0 && !hasTipos && !hasBbox)
            {
                bool todoUniversal = true;
                foreach (string patron in capas)
                {
                    if (!IsUniversalPattern(patron))
                    {
                        todoUniversal = false;
                        break;
                    }
                }

                if (todoUniversal)
                {
                    return "capas: el filtro coincide con todas las capas; añada tipos o bbox, o use un patrón más preciso";
                }
            }

            return null;
        }

        /// <summary>Tope de <see cref="MaxEntities"/>: detalle de INVALID_PARAMS si <paramref name="nCoincidentes"/> lo supera, o null.</summary>
        public static string? CheckCap(int nCoincidentes)
        {
            if (nCoincidentes <= MaxEntities)
            {
                return null;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "coinciden {0} entidades y el máximo por llamada es {1}; acote el filtro (capas, tipos o bbox)",
                nCoincidentes,
                MaxEntities);
        }

        /// <summary>
        /// n_esperado (con confirmar: true): null si no se pasó o coincide con <paramref name="nBorrar"/>; si no, el detalle
        /// de INVALID_PARAMS (el filtro cambió desde la previsualización; no se borra nada). <paramref name="participio"/> =
        /// "borró" (borrar_por_filtro) o "movió" (mover_por_filtro).
        /// </summary>
        public static string? CheckExpected(int? esperado, int nBorrar, string participio = "borró")
        {
            if (esperado == null || esperado.Value == nBorrar)
            {
                return null;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "la previsualización mostró {0}, ahora coinciden {1}; no se {2} nada: repita la previsualización",
                esperado.Value,
                nBorrar,
                participio);
        }

        /// <summary>
        /// Motivo por el que una entidad del espacio no se borra, en orden de precedencia (atributo, dentro de bloque,
        /// viewport, capa bloqueada, capa de xref). null = se puede borrar. Congelada o apagada NO es motivo: se borra con
        /// advertencia (como borrar). El INSERT de bloque xref se evalúa en <see cref="Build"/> tras estas reglas.
        /// </summary>
        public static SkipReason? SkipReasonFor(EntityInfo e)
        {
            if (e == null)
            {
                throw new ArgumentNullException(nameof(e));
            }

            if (e.EsAtributo)
            {
                return SkipReason.Atributo;
            }

            if (e.EnBloque)
            {
                return SkipReason.DentroDeBloque;
            }

            if (e.EsViewportPapel)
            {
                return SkipReason.Viewport;
            }

            if (e.CapaBloqueada)
            {
                return SkipReason.CapaBloqueada;
            }

            if (e.CapaXref)
            {
                return SkipReason.CapaXref;
            }

            return null;
        }

        /// <summary>
        /// Entidad que cumple el filtro: datos de protección (<see cref="EntityInfo"/>), tipo DXF normalizado y si es un
        /// INSERT de un bloque definido en un xref externo u overlay (<paramref name="insertXref"/>).
        /// </summary>
        public sealed record Candidato(EntityInfo Info, string Tipo, bool InsertXref = false);

        /// <summary>
        /// Plan de borrado sobre los candidatos (en su orden). <see cref="Borrar"/> son los índices de los candidatos que
        /// se borran; el resto se cuenta en <see cref="OmitidasPorMotivo"/>.
        /// </summary>
        public sealed class Plan
        {
            public Plan(
                int nCoincidentes,
                List<int> borrar,
                Dictionary<string, int> omitidasPorMotivo,
                Dictionary<string, int> porTipo,
                Dictionary<string, int> porCapa,
                int nUsuario,
                List<EntityInfo> infoBorrar,
                List<string> muestra)
            {
                NCoincidentes = nCoincidentes;
                Borrar = borrar;
                OmitidasPorMotivo = omitidasPorMotivo;
                PorTipo = porTipo;
                PorCapa = porCapa;
                NUsuario = nUsuario;
                InfoBorrar = infoBorrar;
                Muestra = muestra;
            }

            /// <summary>Entidades que cumplen el filtro (antes de las exclusiones).</summary>
            public int NCoincidentes { get; }

            /// <summary>Índices (en la lista de candidatos) de las entidades a borrar, en orden.</summary>
            public List<int> Borrar { get; }

            /// <summary>Omitidas por motivo (solo los motivos con n &gt; 0).</summary>
            public Dictionary<string, int> OmitidasPorMotivo { get; }

            /// <summary>Entidades a borrar por tipo DXF.</summary>
            public Dictionary<string, int> PorTipo { get; }

            /// <summary>Entidades a borrar por capa (nombre tal como lo devuelve AutoCAD).</summary>
            public Dictionary<string, int> PorCapa { get; }

            /// <summary>Entidades a borrar cuya capa NO empieza por MCP-.</summary>
            public int NUsuario { get; }

            /// <summary>Datos de las entidades a borrar (para las advertencias de capas congeladas o apagadas).</summary>
            public List<EntityInfo> InfoBorrar { get; }

            /// <summary>Hasta <see cref="MaxSample"/> handles de las entidades a borrar.</summary>
            public List<string> Muestra { get; }

            /// <summary>Total omitidas (suma de <see cref="OmitidasPorMotivo"/>).</summary>
            public int NOmitidas
            {
                get
                {
                    int n = 0;
                    foreach (int v in OmitidasPorMotivo.Values)
                    {
                        n += v;
                    }

                    return n;
                }
            }
        }

        /// <summary>
        /// Construye el plan. Los candidatos ya cumplen capas, tipos y bbox; aquí solo se decide qué se borra y se cuenta
        /// lo omitido. No aplica el tope: el llamador lo comprueba antes con <see cref="CheckCap"/>.
        /// </summary>
        public static Plan Build(IReadOnlyList<Candidato> candidatos)
        {
            if (candidatos == null)
            {
                throw new ArgumentNullException(nameof(candidatos));
            }

            var borrar = new List<int>();
            var infoBorrar = new List<EntityInfo>();
            var muestra = new List<string>();
            var omitidas = new Dictionary<SkipReason, int>();
            var porTipo = new Dictionary<string, int>(StringComparer.Ordinal);
            var porCapa = new Dictionary<string, int>(StringComparer.Ordinal);
            int nUsuario = 0;

            for (int i = 0; i < candidatos.Count; i++)
            {
                Candidato c = candidatos[i];
                SkipReason? motivo = SkipReasonFor(c.Info);
                if (!motivo.HasValue && c.InsertXref)
                {
                    motivo = SkipReason.XrefBloque;
                }

                if (motivo.HasValue)
                {
                    omitidas.TryGetValue(motivo.Value, out int n);
                    omitidas[motivo.Value] = n + 1;
                    continue;
                }

                borrar.Add(i);
                infoBorrar.Add(c.Info);
                Increment(porTipo, c.Tipo);
                Increment(porCapa, c.Info.Capa);
                if (!EntityProtection.IsMcpLayer(c.Info.Capa))
                {
                    nUsuario++;
                }

                if (muestra.Count < MaxSample)
                {
                    muestra.Add(c.Info.Handle);
                }
            }

            // Motivos en orden de enum, solo los que tienen entidades (estable para el cliente).
            var omitidasPorMotivo = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SkipReason r in (SkipReason[])Enum.GetValues(typeof(SkipReason)))
            {
                if (omitidas.TryGetValue(r, out int n) && n > 0)
                {
                    omitidasPorMotivo[SkipReasonName(r)] = n;
                }
            }

            return new Plan(candidatos.Count, borrar, omitidasPorMotivo, porTipo, porCapa, nUsuario, infoBorrar, muestra);
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }
    }
}
