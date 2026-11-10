using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>
    /// Ventana de espacio modelo que muestra un viewport de papel (protocol.md §5C.5, F3 de 0.3.1). Puro: sin AutoCAD.
    /// </summary>
    public static class ViewportWindow
    {
        private const double TwistTolerance = 1e-9;

        /// <summary>
        /// Caja de modelo de un viewport: centro (<paramref name="cx"/>, <paramref name="cy"/>) en coordenadas de modelo
        /// (= ViewTarget + ViewCenter; véase <see cref="FromViewportTarget"/>), alto =
        /// <paramref name="viewHeight"/>, ancho = viewHeight · <paramref name="width"/> / <paramref name="height"/>
        /// (proporción del viewport en papel). Si <paramref name="twistRad"/> ≠ 0 (mod 2π, tolerancia 1e-9) devuelve la
        /// caja envolvente de las cuatro esquinas giradas alrededor del centro. <paramref name="height"/> ≤ 0,
        /// <paramref name="viewHeight"/> ≤ 0, <paramref name="width"/> &lt; 0 o valores no finitos → ArgumentException.
        /// </summary>
        public static BBox2 FromViewport(double cx, double cy, double viewHeight, double width, double height, double twistRad)
        {
            if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(viewHeight) || !double.IsFinite(width)
                || !double.IsFinite(height) || !double.IsFinite(twistRad))
            {
                throw new ArgumentException("viewport: valor no finito");
            }

            if (height <= 0)
            {
                throw new ArgumentException("viewport: alto en papel no positivo");
            }

            if (viewHeight <= 0)
            {
                throw new ArgumentException("viewport: ViewHeight no positivo");
            }

            if (width < 0)
            {
                throw new ArgumentException("viewport: ancho en papel negativo");
            }

            double halfW = 0.5 * viewHeight * width / height;
            double halfH = 0.5 * viewHeight;

            double t = twistRad % (2 * Math.PI);
            if (t < 0)
            {
                t += 2 * Math.PI;
            }

            if (t <= TwistTolerance || 2 * Math.PI - t <= TwistTolerance)
            {
                return BBox2.FromCorners(cx - halfW, cy - halfH, cx + halfW, cy + halfH);
            }

            double c = Math.Cos(twistRad);
            double s = Math.Sin(twistRad);
            var points = new List<Point3>(4);
            foreach ((double dx, double dy) in new[] { (-halfW, -halfH), (halfW, -halfH), (halfW, halfH), (-halfW, halfH) })
            {
                points.Add(new Point3(cx + dx * c - dy * s, cy + dx * s + dy * c, 0));
            }

            return BBox2.FromPoints(points);
        }

        /// <summary>
        /// Igual que <see cref="FromViewport"/> pero partiendo de los valores crudos del Viewport: ViewCenter está en DCS,
        /// cuyo origen es ViewTarget, así que el centro en modelo (vista en planta) = ViewTarget + ViewCenter. Caso real
        /// (Layout1 nº3 de EXP 26-XXXX): ViewTarget (2590.96, 1494.53) + ViewCenter (703.4832, 2500.5022) ≈
        /// (3294.44, 3995.03). Con ViewTarget = 0 coincide con <see cref="FromViewport"/>.
        /// </summary>
        public static BBox2 FromViewportTarget(
            double targetX, double targetY, double viewCenterX, double viewCenterY, double viewHeight, double width, double height,
            double twistRad)
        {
            if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
            {
                throw new ArgumentException("viewport: ViewTarget no finito");
            }

            return FromViewport(targetX + viewCenterX, targetY + viewCenterY, viewHeight, width, height, twistRad);
        }

        /// <summary>
        /// Criterio de §5C.5 (F3, 0.3.2) para decidir si un Viewport de papel aporta ventana de modelo:
        /// <list type="bullet">
        /// <item><paramref name="number"/> = 1 es el propio espacio papel → no.</item>
        /// <item>Si Number no está inicializado (≤ 0) y es el primer viewport del layout
        /// (<paramref name="firstInLayout"/>), también es el de papel → no.</item>
        /// <item><paramref name="on"/> solo se respeta en el layout ACTUAL (<paramref name="isCurrentLayout"/>): AutoCAD
        /// informa Viewport.On = false para los viewports de los layouts que no se han activado en la sesión (gate en vivo
        /// 0.3.1: el viewport nº2 de Layout2, que muestra el cuadro B, salía apagado porque Layout2 no se había abierto y
        /// el cuadro B quedaba en_layout:false).</item>
        /// </list>
        /// </summary>
        public static bool IncludeViewport(bool isCurrentLayout, bool on, int number, bool firstInLayout = false)
        {
            if (IsPaperSpaceViewport(number, firstInLayout))
            {
                return false;
            }

            return !isCurrentLayout || on;
        }

        /// <summary>
        /// Criterio por número: true si <paramref name="number"/> = 1, o si Number ≤ 0 (AutoCAD informa −1 en los
        /// viewports de los layouts que NO son el actual) y es el primer viewport del layout en el orden del registro de
        /// bloque (<paramref name="firstInLayout"/>). Es el respaldo de <see cref="TryResolvePaperViewport{T}"/>, que es
        /// el criterio que usan LayoutViewports (§5C.5) y normalizar_capas (§5F.3) desde 0.6.1.
        /// </summary>
        public static bool IsPaperSpaceViewport(int number, bool firstInLayout) =>
            number == 1 || (firstInLayout && number <= 0);

        /// <summary>
        /// Viewport de papel (n.º 1, el propio espacio papel) de un layout, uno como máximo (§5C.5, §5F.3; 0.6.1). Fuentes,
        /// en orden: (1) el primero de <paramref name="fromLayout"/> (Layout.GetViewports(): AutoCAD lo devuelve primero
        /// cuando el layout está inicializado; vacía si no lo está) si además está entre los viewports del registro de
        /// bloque; (2) el primero de <paramref name="inBtrOrder"/> con Number = 1 (layout actual); (3) respaldo: el
        /// primero del registro de bloque (en los layouts no actuales Number = −1 en todos). false si el layout no tiene
        /// viewports. <paramref name="inBtrOrder"/> = (id, Number) de cada viewport en el orden del registro de bloque.
        /// </summary>
        public static bool TryResolvePaperViewport<T>(
            IReadOnlyList<T> fromLayout, IReadOnlyList<KeyValuePair<T, int>> inBtrOrder, out T paper)
            where T : struct
        {
            if (fromLayout == null)
            {
                throw new ArgumentNullException(nameof(fromLayout));
            }

            if (inBtrOrder == null)
            {
                throw new ArgumentNullException(nameof(inBtrOrder));
            }

            paper = default;
            if (inBtrOrder.Count == 0)
            {
                return false;
            }

            EqualityComparer<T> eq = EqualityComparer<T>.Default;
            if (fromLayout.Count > 0)
            {
                foreach (KeyValuePair<T, int> kv in inBtrOrder)
                {
                    if (eq.Equals(kv.Key, fromLayout[0]))
                    {
                        paper = kv.Key;
                        return true;
                    }
                }
            }

            foreach (KeyValuePair<T, int> kv in inBtrOrder)
            {
                if (kv.Value == 1)
                {
                    paper = kv.Key;
                    return true;
                }
            }

            paper = inBtrOrder[0].Key;
            return true;
        }
    }
}
