using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Métricas básicas de polígonos/polilíneas (puro). La tarea A amplía esta clase con centroide, punto interior y
    /// autointersección (ver el reparto de Fase 4A); aquí solo lo que necesita el código compartido.
    /// </summary>
    public static partial class PolygonMetrics
    {
        /// <summary>Longitud de las cuerdas (tramos rectos) y, si es cerrada (≥ 3 vértices), área por fórmula del cordón (|valor|).</summary>
        public static (double longitud, double? area) LengthAndArea(IReadOnlyList<(double x, double y)> v, bool cerrada)
        {
            if (v == null)
            {
                throw new ArgumentNullException(nameof(v));
            }

            double len = 0;
            for (int i = 1; i < v.Count; i++)
            {
                len += Dist(v[i - 1], v[i]);
            }

            if (!cerrada || v.Count < 3)
            {
                return (len, null);
            }

            len += Dist(v[v.Count - 1], v[0]);
            return (len, Math.Abs(SignedArea(v)));
        }

        /// <summary>Área con signo (positiva = antihoraria) del polígono cerrado implícito.</summary>
        public static double SignedArea(IReadOnlyList<(double x, double y)> v)
        {
            double a = 0;
            for (int i = 0; i < v.Count; i++)
            {
                (double x1, double y1) = v[i];
                (double x2, double y2) = v[(i + 1) % v.Count];
                a += (x1 * y2) - (x2 * y1);
            }

            return a / 2.0;
        }

        public static double Dist((double x, double y) a, (double x, double y) b) =>
            Math.Sqrt(((b.x - a.x) * (b.x - a.x)) + ((b.y - a.y) * (b.y - a.y)));
    }
}
