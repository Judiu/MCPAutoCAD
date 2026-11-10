using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Elección del resultado de <c>offset</c> (protocol.md §5E.3.4) entre los candidatos Curve.GetOffsetCurves(+d) y
    /// (−d). Lógica pura compartida por la LECTURA (OffsetHandler.Plan, que calcula la caja del resultado para
    /// numeros.bbox y R4) y el DIBUJO (GeometryDrawer.DrawOffset), para que ambos elijan siempre el mismo candidato.
    /// Corrección del gate en vivo de Fase 4A (2026-10-27): numeros.bbox abarcaba las DOS paralelas candidatas (caja de
    /// la original ampliada la distancia); ahora es la caja de la curva elegida, igual que entidades[0].bbox.
    /// </summary>
    public static class OffsetChoice
    {
        /// <summary>
        /// Índice del candidato elegido: el de menor distancia FINITA al punto de lado; en empate, el primero (el orden de
        /// prueba es +distancia y luego −distancia). Un candidato sin distancia (NaN/infinito: sin piezas o sin punto más
        /// cercano) no decide. Ninguno válido → −1.
        /// </summary>
        public static int Choose(IReadOnlyList<double> distancias)
        {
            if (distancias == null)
            {
                throw new ArgumentNullException(nameof(distancias));
            }

            int best = -1;
            double bestDist = double.PositiveInfinity;
            for (int i = 0; i < distancias.Count; i++)
            {
                double d = distancias[i];
                if (!double.IsFinite(d))
                {
                    continue;
                }

                if (d < bestDist)
                {
                    best = i;
                    bestDist = d;
                }
            }

            return best;
        }

        /// <summary>
        /// Punto lateral esperado (O3 de la revisión del gate, 2026-10-27): el punto a <paramref name="distancia"/> de la
        /// curva original, en la dirección que va de <paramref name="proyeccion"/> (punto más cercano de la original al
        /// punto de lado) hacia <paramref name="lado"/>. La paralela del lado pedido pasa por él (exacto en líneas y arcos;
        /// en esquinas de polilínea, a menos de distancia/2). null si el punto de lado está sobre la curva (≤ 1e-9: lado
        /// ambiguo, no se comprueba).
        /// </summary>
        public static Point3? LateralPoint(Point3 lado, Point3 proyeccion, double distancia)
        {
            double dx = lado.X - proyeccion.X, dy = lado.Y - proyeccion.Y;
            double n = Math.Sqrt((dx * dx) + (dy * dy));
            if (!double.IsFinite(n) || n <= 1e-9)
            {
                return null;
            }

            double d = Math.Abs(distancia);
            return new Point3(proyeccion.X + (d * dx / n), proyeccion.Y + (d * dy / n), proyeccion.Z);
        }

        /// <summary>
        /// true si la paralela elegida está del lado pedido: su distancia al punto lateral esperado es ≤ distancia/2. Si es
        /// mayor, AutoCAD no construyó la paralela de ese lado (p. ej. la distancia hacia dentro supera el radio) y se eligió
        /// la del otro lado: el llamador responde INVALID_GEOMETRY (<see cref="WrongSideDetail"/>) sin escribir.
        /// </summary>
        public static bool IsOnRequestedSide(double distanciaAlLateral, double distancia) =>
            double.IsFinite(distanciaAlLateral) && distanciaAlLateral <= Math.Abs(distancia) / 2.0;

        /// <summary>Detalle exacto de §5E.3.4: "lado: no se puede construir la paralela a &lt;d&gt; del lado pedido (&lt;motivo&gt;)".</summary>
        public static string WrongSideDetail(double distancia, string motivo) =>
            "lado: no se puede construir la paralela a " + Math.Abs(distancia).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
            + " del lado pedido (" + motivo + ")";

        /// <summary>Motivo para un arco o círculo: la distancia hacia dentro alcanza o supera el radio.</summary>
        public static string RadiusReason(double radio) =>
            "la distancia supera o iguala el radio " + radio.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Motivo genérico (otras curvas).</summary>
        public const string GenericReason = "AutoCAD no construye la curva paralela de ese lado";

        /// <summary>Caja del resultado elegido: unión de las cajas de sus piezas (las null se ignoran); null si ninguna.</summary>
        public static BBox2? UnionOf(IEnumerable<BBox2?> cajas)
        {
            if (cajas == null)
            {
                throw new ArgumentNullException(nameof(cajas));
            }

            BBox2? total = null;
            foreach (BBox2? b in cajas)
            {
                if (b == null)
                {
                    continue;
                }

                total = total == null ? b : total.Value.Union(b.Value);
            }

            return total;
        }
    }
}
