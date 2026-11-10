using System;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Paginación común (protocol.md §2.4): cursor = índice decimal de la primera fila de la página siguiente.
    /// Los errores se lanzan como <see cref="ArgumentException"/> con el detalle exacto del contrato; los handlers
    /// los convierten en INVALID_PARAMS.
    /// </summary>
    public static class Paging
    {
        public const string InvalidCursor = "cursor: valor inválido";
        public const string CursorOutOfRange = "cursor: fuera de rango";
        public const int MaxCursorDigits = 9;

        /// <summary>
        /// Interpreta el cursor: null → 0. Vacío, no numérico (solo dígitos ASCII) o con más de 9 dígitos →
        /// ArgumentException("cursor: valor inválido").
        /// </summary>
        public static int ParseCursor(string? cursor)
        {
            if (cursor == null)
            {
                return 0;
            }

            if (cursor.Length == 0 || cursor.Length > MaxCursorDigits)
            {
                throw new ArgumentException(InvalidCursor);
            }

            foreach (char c in cursor)
            {
                if (c < '0' || c > '9')
                {
                    throw new ArgumentException(InvalidCursor);
                }
            }

            return int.Parse(cursor, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Calcula la página [start, start + count) de una lista de <paramref name="total"/> filas.
        /// start &gt; 0 y start ≥ total → ArgumentException("cursor: fuera de rango").
        /// nextCursor es null cuando no quedan filas.
        /// </summary>
        public static (int count, string? nextCursor) Slice(int total, int start, int limit)
        {
            if (total < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(total), "total no puede ser negativo.");
            }

            if (limit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "limit debe ser al menos 1.");
            }

            if (start < 0 || (start > 0 && start >= total))
            {
                throw new ArgumentException(CursorOutOfRange);
            }

            int count = Math.Max(0, Math.Min(limit, total - start));
            int next = start + count;
            string? nextCursor = next < total ? next.ToString(CultureInfo.InvariantCulture) : null;
            return (count, nextCursor);
        }
    }
}
