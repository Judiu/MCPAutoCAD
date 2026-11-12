using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Datos de una entidad pedida por handle en mover/copiar/rotar/escalar/borrar (protocol.md §5D.8), ya leídos de
    /// AutoCAD por el plugin. <see cref="Espacio"/> = "modelo", nombre del layout o "bloque:&lt;nombre&gt;".
    /// </summary>
    public sealed record EntityInfo(
        string Handle,
        string Capa,
        bool CapaBloqueada,
        bool CapaXref,
        bool CapaCongeladaOApagada,
        bool EsAtributo,
        bool EsViewportPapel,
        bool EnBloque,
        string Espacio);

    /// <summary>Rechazo de la protección (§5D.8 pasos 2–5): <see cref="Detalle"/> exacto y datos para <c>error.data</c>.</summary>
    public sealed class ProtectionError
    {
        public ProtectionError(string detalle, string? handle, string? capa, IReadOnlyList<string>? handles = null)
        {
            Detalle = detalle;
            Handle = handle;
            Capa = capa;
            Handles = handles;
        }

        /// <summary>Texto exacto de <c>data.detalle</c>.</summary>
        public string Detalle { get; }

        /// <summary><c>data.handle</c> (null si no aplica).</summary>
        public string? Handle { get; }

        /// <summary><c>data.capa</c> (solo pasos de capa bloqueada / xref).</summary>
        public string? Capa { get; }

        /// <summary><c>data.handles</c> (solo espacio distinto: los dos handles citados).</summary>
        public IReadOnlyList<string>? Handles { get; }
    }

    /// <summary>
    /// Protección de las entidades del usuario y comprobaciones previas de §5D.8 (pasos 2–5). Puro, sin AutoCAD:
    /// el plugin (HandleGuard) lee cada entidad, construye <see cref="EntityInfo"/> y aplica estas reglas ANTES de
    /// modificar nada (todo o nada).
    /// </summary>
    public static class EntityProtection
    {
        /// <summary>Prefijo de las capas del propio MCP (sin distinguir mayúsculas).</summary>
        public const string McpPrefix = "MCP-";

        /// <summary>Máximo de ejemplos citados en el mensaje de confirmación.</summary>
        public const int MaxExamples = 5;

        /// <summary>
        /// Primer error en el orden de §5D.8 (paso 2 sobre todos los handles, después el paso 3, después el 4), o null
        /// si todo es válido. Paso 2 por handle: atributo, dentro de una definición de bloque, viewport de papel. Paso 3:
        /// capa bloqueada, capa dependiente de xref. Paso 4: todas en el mismo espacio.
        /// <paramref name="permitirCapaBloqueada"/> (fallo 43, solo copiar con permitir_origen_bloqueado): la capa bloqueada
        /// deja de ser motivo de rechazo; la capa de xref lo sigue siendo.
        /// </summary>
        public static ProtectionError? Classify(IReadOnlyList<EntityInfo> entities, bool permitirCapaBloqueada = false)
        {
            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            for (int i = 0; i < entities.Count; i++)
            {
                EntityInfo e = entities[i];
                if (e.EsAtributo)
                {
                    return new ProtectionError(Prefix(i) + "es un atributo; use el handle del INSERT", e.Handle, null);
                }

                if (e.EnBloque)
                {
                    return new ProtectionError(Prefix(i) + "la entidad está dentro de una definición de bloque", e.Handle, null);
                }

                if (e.EsViewportPapel)
                {
                    return new ProtectionError(Prefix(i) + "es un viewport de papel", e.Handle, null);
                }
            }

            for (int i = 0; i < entities.Count; i++)
            {
                EntityInfo e = entities[i];
                if (e.CapaBloqueada && !permitirCapaBloqueada)
                {
                    return new ProtectionError(Prefix(i) + "la entidad está en una capa bloqueada", e.Handle, e.Capa);
                }

                if (e.CapaXref)
                {
                    return new ProtectionError(Prefix(i) + "la entidad está en una capa dependiente de xref", e.Handle, e.Capa);
                }
            }

            if (entities.Count > 1)
            {
                EntityInfo first = entities[0];
                for (int i = 1; i < entities.Count; i++)
                {
                    EntityInfo e = entities[i];
                    if (!string.Equals(e.Espacio, first.Espacio, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ProtectionError(
                            "handles: las entidades deben estar en el mismo espacio (" + first.Handle + " en " + first.Espacio
                            + ", " + e.Handle + " en " + e.Espacio + ")",
                            null,
                            null,
                            new[] { first.Handle, e.Handle });
                    }
                }
            }

            return null;
        }

        /// <summary>true si la capa es del MCP (empieza por "MCP-", sin distinguir mayúsculas).</summary>
        public static bool IsMcpLayer(string? capa) =>
            capa != null && capa.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>n_usuario: entidades cuya capa NO empieza por MCP- (§5D.8 paso 5).</summary>
        public static int CountUser(IReadOnlyList<EntityInfo> entities)
        {
            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            int n = 0;
            foreach (EntityInfo e in entities)
            {
                if (!IsMcpLayer(e.Capa))
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>Hasta <see cref="MaxExamples"/> entidades del usuario (handle, capa), en el orden de handles.</summary>
        public static List<(string handle, string capa)> UserExamples(IReadOnlyList<EntityInfo> entities)
        {
            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            var list = new List<(string handle, string capa)>();
            foreach (EntityInfo e in entities)
            {
                if (list.Count >= MaxExamples)
                {
                    break;
                }

                if (!IsMcpLayer(e.Capa))
                {
                    list.Add((e.Handle, e.Capa));
                }
            }

            return list;
        }

        /// <summary>Entidades en capas congeladas o apagadas (paso 3: se modifican con advertencia).</summary>
        public static int CountFrozenOrOff(IReadOnlyList<EntityInfo> entities)
        {
            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            int n = 0;
            foreach (EntityInfo e in entities)
            {
                if (e.CapaCongeladaOApagada)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>Advertencia exacta del paso 3 de §5D.8.</summary>
        public static string FrozenOrOffWarning(int n) =>
            n.ToString(CultureInfo.InvariantCulture) + " entidad(es) en capas congeladas o apagadas: el cambio no será visible.";

        /// <summary>
        /// Texto exacto del paso 5 de §5D.8: "confirmar: &lt;n_usuario&gt; de las &lt;n&gt; entidades no están en capas
        /// MCP-* (p. ej. 2A3F en MUROS, 2A40 en Cotas); pase confirmar: true para modificarlas". Se citan como mucho
        /// <see cref="MaxExamples"/> ejemplos.
        /// </summary>
        public static string ConfirmMessage(int nUsuario, int n, IReadOnlyList<(string handle, string capa)> ejemplos)
        {
            if (ejemplos == null)
            {
                throw new ArgumentNullException(nameof(ejemplos));
            }

            var sb = new StringBuilder();
            sb.Append("confirmar: ").Append(nUsuario.ToString(CultureInfo.InvariantCulture)).Append(" de las ")
                .Append(n.ToString(CultureInfo.InvariantCulture)).Append(" entidades no están en capas MCP-* (p. ej. ");
            for (int k = 0; k < ejemplos.Count && k < MaxExamples; k++)
            {
                if (k > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(ejemplos[k].handle).Append(" en ").Append(ejemplos[k].capa);
            }

            sb.Append("); pase confirmar: true para modificarlas");
            return sb.ToString();
        }

        /// <summary>
        /// Paso 5 completo: null si no hace falta confirmación (todas MCP-* o <paramref name="confirmar"/> = true);
        /// si no, el mensaje de <see cref="ConfirmMessage"/>.
        /// </summary>
        public static string? RequireConfirmation(IReadOnlyList<EntityInfo> entities, bool confirmar)
        {
            int nUsuario = CountUser(entities);
            if (nUsuario == 0 || confirmar)
            {
                return null;
            }

            return ConfirmMessage(nUsuario, entities.Count, UserExamples(entities));
        }

        private static string Prefix(int i) => "handles[" + i.ToString(CultureInfo.InvariantCulture) + "]: ";
    }
}
