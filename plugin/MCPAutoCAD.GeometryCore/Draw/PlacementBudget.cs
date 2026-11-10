using System;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Tope de coste de la comprobación de ubicación (R4, protocol.md §5D.0; pendiente del gate de Fase 2 para
    /// marcar_hallazgos): recorrer las extensiones de ~40 000 entidades puede costar segundos, así que la pasada se
    /// corta por número de entidades o por tiempo. Si se corta ANTES de que todas las cajas quepan, la comprobación
    /// es PARCIAL: no se bloquea la escritura (no hay certeza de que estén fuera) y se añade una advertencia. Puro.
    /// </summary>
    public static class PlacementBudget
    {
        /// <summary>Máximo de entidades cuya caja se consulta en una comprobación.</summary>
        public const int DefaultMaxEntities = 60000;

        /// <summary>Tiempo máximo de la pasada, en milisegundos.</summary>
        public const int DefaultMaxMs = 2500;

        /// <summary>Cada cuántas entidades se mira el reloj.</summary>
        public const int CheckEvery = 500;

        /// <summary>true si la pasada debe detenerse (se alcanzó el tope de entidades o de tiempo).</summary>
        public static bool Exhausted(int entitiesScanned, double elapsedMs, int maxEntities = DefaultMaxEntities, int maxMs = DefaultMaxMs)
        {
            if (maxEntities < 0 || maxMs < 0 || !double.IsFinite(elapsedMs))
            {
                throw new ArgumentException("tope: valores no válidos");
            }

            return entitiesScanned >= maxEntities || elapsedMs >= maxMs;
        }

        /// <summary>
        /// Advertencia cuando la pasada se cortó con alguna caja aún fuera de la extensión parcial: la escritura
        /// sigue adelante porque no se pudo confirmar que esté fuera.
        /// </summary>
        public static string PartialWarning(string espacio, int entitiesScanned, double elapsedMs) =>
            "La comprobación de ubicación en '" + espacio + "' se interrumpió por el tope de coste ("
            + entitiesScanned.ToString(CultureInfo.InvariantCulture) + " entidades, "
            + Math.Round(elapsedMs).ToString(CultureInfo.InvariantCulture)
            + " ms): no se pudo confirmar si la geometría queda dentro de la extensión del espacio; se escribió igualmente. Revise con vista_png.";
    }
}
