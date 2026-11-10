using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>
    /// Comprobación previa de marcar_hallazgos (protocol.md §5C.9, F9 de 0.3.2): cada marca debe intersecar la extensión
    /// del espacio destino ampliada un 10 % de su tamaño. Gate en vivo 0.3.1: con Layout1 activo y sin 'espacio' se
    /// escribió en el papel (extensión ≈ x 319…321, y 59…60) una marca con coordenadas de modelo
    /// (≈ [[3273,3989],[3278,3994]]) sin ningún aviso. Puro: sin AutoCAD.
    /// </summary>
    public static class MarkPlacement
    {
        /// <summary>Margen por defecto: 10 % del lado mayor de la extensión, por cada lado.</summary>
        public const double DefaultMarginFraction = 0.10;

        /// <summary>Máximo de índices que se citan en el mensaje.</summary>
        private const int MaxListed = 5;

        /// <summary>
        /// Extensión ampliada por cada lado en <paramref name="fraction"/> × max(ancho, alto) (así una extensión degenerada
        /// en un eje —p. ej. una sola línea horizontal— también gana margen en ese eje). fraction negativa o no finita →
        /// ArgumentException.
        /// </summary>
        public static BBox2 Expand(BBox2 extents, double fraction)
        {
            if (!double.IsFinite(fraction) || fraction < 0)
            {
                throw new ArgumentException("margen: debe ser un número finito ≥ 0");
            }

            double m = fraction * Math.Max(extents.Width, extents.Height);
            return BBox2.FromCorners(extents.Xmin - m, extents.Ymin - m, extents.Xmax + m, extents.Ymax + m);
        }

        /// <summary>
        /// true si la marca interseca la extensión ampliada. Sin extensión (<paramref name="extents"/> null: espacio vacío)
        /// no se bloquea nada → true.
        /// </summary>
        public static bool Fits(BBox2 mark, BBox2? extents, double fraction = DefaultMarginFraction) =>
            extents == null || mark.Intersects(Expand(extents.Value, fraction));

        /// <summary>Índices (en orden) de las marcas que NO intersecan la extensión ampliada; vacío si todas caben o no hay extensión.</summary>
        public static List<int> OutsideIndices(IReadOnlyList<BBox2> marks, BBox2? extents, double fraction = DefaultMarginFraction)
        {
            if (marks == null)
            {
                throw new ArgumentNullException(nameof(marks));
            }

            var outside = new List<int>();
            if (extents == null)
            {
                return outside;
            }

            BBox2 expanded = Expand(extents.Value, fraction);
            for (int i = 0; i < marks.Count; i++)
            {
                if (!marks[i].Intersects(expanded))
                {
                    outside.Add(i);
                }
            }

            return outside;
        }

        /// <summary>
        /// Mensaje (detalle de INVALID_PARAMS) cuando hay marcas fuera: cita el espacio destino, su extensión, las marcas
        /// fuera (hasta 5, con su bbox) y la causa probable. <paramref name="destinoEsModelo"/> decide la sugerencia:
        /// destino papel → probablemente se quiso espacio:"modelo"; destino modelo → probablemente se quiso un layout.
        /// </summary>
        public static string OutsideMessage(
            string espacio, bool destinoEsModelo, BBox2 extents, IReadOnlyList<int> outside, IReadOnlyList<BBox2> marks) =>
            OutsideMessage(espacio, destinoEsModelo, extents, outside, marks, "marcas");

        /// <summary>
        /// Variante con etiqueta (0.4, R4 de §5D.0): <paramref name="etiqueta"/> = nombre del array que se cita en el
        /// mensaje ("marcas" en marcar_hallazgos; "entidades", "vertices", etc. en las herramientas de dibujo).
        /// </summary>
        public static string OutsideMessage(
            string espacio, bool destinoEsModelo, BBox2 extents, IReadOnlyList<int> outside, IReadOnlyList<BBox2> marks, string etiqueta)
        {
            if (outside == null || outside.Count == 0)
            {
                throw new ArgumentException("sin marcas fuera");
            }

            var sb = new StringBuilder();
            for (int k = 0; k < outside.Count && k < MaxListed; k++)
            {
                if (k > 0)
                {
                    sb.Append(", ");
                }

                int i = outside[k];
                sb.Append(etiqueta).Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ").Append(Format(marks[i]));
            }

            if (outside.Count > MaxListed)
            {
                sb.Append(" y ").Append((outside.Count - MaxListed).ToString(CultureInfo.InvariantCulture)).Append(" más");
            }

            string sugerencia = destinoEsModelo
                ? "probablemente las coordenadas son de un layout (espacio papel): indique en 'espacio' el layout correcto"
                : "probablemente las coordenadas son del modelo: use espacio:\"modelo\" (o el layout correcto)";

            return sb + " fuera de la extensión del espacio destino '" + espacio + "' " + Format(extents)
                + " (ampliada un 10 %): " + sugerencia + ". No se escribió nada; pase forzar: true para escribir igualmente.";
        }

        /// <summary>[[xmin, ymin], [xmax, ymax]] con hasta 3 decimales, cultura invariante.</summary>
        public static string Format(BBox2 b) =>
            "[[" + N(b.Xmin) + ", " + N(b.Ymin) + "], [" + N(b.Xmax) + ", " + N(b.Ymax) + "]]";

        private static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
