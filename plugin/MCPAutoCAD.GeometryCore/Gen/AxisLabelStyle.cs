using System;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Estilo de texto de los rótulos de <c>ejes_rejilla</c> cuando el cliente no da <c>estilo</c> (protocol.md §5E.3.2;
    /// corrección del gate en vivo de Fase 4A, 2026-10-27). La oficina rotula los ejes con el estilo "75" (evidencia:
    /// TEXT 859F0 de tests/fixtures/copia_real.dwg); antes se usaba el estilo actual del dibujo (Standard).
    /// Regla: <c>estilo</c> dado → ese (inexistente → NOT_FOUND, lo decide el llamador); sin <c>estilo</c> → el estilo
    /// preferido (lps.json <c>estilos_texto.ejes</c> si existe la clave; si no <see cref="OfficeDefault"/>) cuando existe
    /// en el dibujo; si no existe, el estilo actual con la advertencia <see cref="MissingWarning"/>.
    /// </summary>
    public static class AxisLabelStyle
    {
        /// <summary>
        /// Estilo de rótulo de ejes de la oficina. TODO: mover a lps.json como <c>"estilos_texto": {"ejes": "75"}</c>
        /// cuando el usuario autorice cambiar standards/ (el plugin ya lee esa clave si existe:
        /// LpsStandard.TextStyleFor("ejes")).
        /// </summary>
        public const string OfficeDefault = "75";

        /// <summary>Advertencia cuando el estilo preferido no existe en el dibujo y se usa el actual.</summary>
        public static string MissingWarning(string preferido, string actual) =>
            "ejes_rejilla: el estilo de texto '" + preferido + "' de los rótulos de eje de la oficina no existe en el dibujo; "
            + "los rótulos usan el estilo actual '" + actual + "' (indique estilo para otro).";

        /// <summary>
        /// Decide el estilo. <paramref name="pedido"/> = parámetro <c>estilo</c> (o null); <paramref name="configurado"/> =
        /// lps.json <c>estilos_texto.ejes</c> (o null/vacío → <see cref="OfficeDefault"/>); <paramref name="resolver"/>
        /// devuelve el nombre real del estilo en el dibujo (sin distinguir mayúsculas) o null si no existe;
        /// <paramref name="actual"/> = nombre del estilo actual. Devuelve el estilo a usar (null = el actual) y la
        /// advertencia (o null).
        /// </summary>
        public static (string? Estilo, string? Advertencia) Choose(string? pedido, string? configurado, Func<string, string?> resolver, string actual)
        {
            if (resolver == null)
            {
                throw new ArgumentNullException(nameof(resolver));
            }

            if (pedido != null)
            {
                return (pedido, null);
            }

            string preferido = string.IsNullOrWhiteSpace(configurado) ? OfficeDefault : configurado!;
            string? real = resolver(preferido);
            if (real != null)
            {
                return (real, null);
            }

            return (null, MissingWarning(preferido, actual));
        }
    }
}
