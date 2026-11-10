using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Colocación y orientación del bloque de puerta/ventana dentro de un hueco (protocol.md §5G.1). Puro.
    /// Convención del catálogo (<see cref="VanoBlockSpec"/>, lps.json "biblioteca_vanos", evidencia §5G.0): cada bloque
    /// tiene un vector local <c>u</c> a lo largo del vano (eje_ancho: x → (1,0); y → (0,1)) y un vector local <c>v</c> que
    /// entra en el muro desde la cara donde está el punto base (direccion_espesor: "+y" → (0,1), "-x" → (−1,0)…). La
    /// hoja de la puerta abre hacia el lado al que apunta <c>v</c> una vez cruzado el muro: por eso la cara de apertura es
    /// la OPUESTA a la cara del punto base.
    /// </summary>
    public static class OpeningPlacement
    {
        private const string CaraIzquierda = "izquierda";
        private const string CaraDerecha = "derecha";
        private const string BisagraInicio = "inicio";
        private const string BisagraFin = "fin";

        private const string InvalidFace = "cara_apertura: debe ser izquierda o derecha";

        private const double VectorTol = 1e-9;

        /// <summary>Signos de escala en orden de preferencia: sin espejo, espejo en y, espejo en x, doble espejo.</summary>
        private static readonly (double sx, double sy)[] SignOrder = { (1, 1), (1, -1), (-1, 1), (-1, -1) };

        /// <summary>Resultado: punto de inserción, rotación en grados [0, 360), escala [sx, sy, sz] (±1 o el factor de ancho) y si hubo espejo.</summary>
        public sealed record Placement(
            (double x, double y) Posicion,
            double RotacionGrados,
            IReadOnlyList<double> Escala,
            bool Espejo,
            double? FactorAncho);

        /// <summary>
        /// Resuelve rotación y signos de escala (sx, sy ∈ {+1, −1}) tales que el vector local <paramref name="u"/> quede en
        /// la dirección <paramref name="aLoLargo"/> y el vector local <paramref name="v"/> en la dirección
        /// <paramref name="haciaDentro"/> (ambos unitarios, perpendiculares entre sí). Preferencia: sin espejo (sx = sy = 1)
        /// si existe solución; si no, un solo espejo. Lanza ArgumentException "colocacion: u y v deben ser perpendiculares"
        /// si no lo son (tolerancia 1e-9).
        /// </summary>
        public static (double rotacionGrados, double sx, double sy) Solve(
            (double x, double y) u,
            (double x, double y) v,
            (double x, double y) aLoLargo,
            (double x, double y) haciaDentro)
        {
            if (Math.Abs((u.x * v.x) + (u.y * v.y)) > VectorTol)
            {
                throw new ArgumentException("colocacion: u y v deben ser perpendiculares");
            }

            foreach ((double sx, double sy) in SignOrder)
            {
                (double x, double y) su = (sx * u.x, sy * u.y);
                (double x, double y) sv = (sx * v.x, sy * v.y);
                double rot = Math.Atan2(aLoLargo.y, aLoLargo.x) - Math.Atan2(su.y, su.x);
                (double cos, double sin) = (Math.Cos(rot), Math.Sin(rot));
                (double x, double y) girado = ((cos * sv.x) - (sin * sv.y), (sin * sv.x) + (cos * sv.y));
                if (Math.Abs(girado.x - haciaDentro.x) <= VectorTol && Math.Abs(girado.y - haciaDentro.y) <= VectorTol)
                {
                    return (NormalizeDegrees(Angles.ToDegrees(rot)), sx, sy);
                }
            }

            throw new ArgumentException("colocacion: la dirección del vano y la de entrada al muro deben ser perpendiculares");
        }

        /// <summary>
        /// Colocación completa del bloque en el hueco <paramref name="hueco"/> (de <see cref="WallOpening.Cut"/>):
        /// <paramref name="caraApertura"/> "izquierda" | "derecha" respecto a <c>hueco.Direccion</c> (la cara hacia la que abre
        /// la hoja; el punto base va en la cara opuesta); <paramref name="bisagra"/> "inicio" | "fin" (esquina del hueco
        /// donde va el punto base: la más cercana al inicio o al fin del eje). Escala: si <paramref name="anchoPorPropiedad"/>
        /// es true, ±1 en ambos ejes (el ancho lo fija la propiedad dinámica); si no, el eje <c>u</c> se escala
        /// <c>hueco ancho / spec.AnchoNominal</c> (<see cref="Placement.FactorAncho"/>) y el otro queda en ±1.
        /// Lanza ArgumentException "cara_apertura: debe ser izquierda o derecha" / "bisagra: debe ser inicio o fin".
        /// </summary>
        public static Placement Place(
            WallOpening.OpeningResult hueco,
            VanoBlockSpec spec,
            string caraApertura,
            string bisagra,
            bool anchoPorPropiedad)
        {
            if (hueco == null)
            {
                throw new ArgumentNullException(nameof(hueco));
            }

            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            if (caraApertura != CaraIzquierda && caraApertura != CaraDerecha)
            {
                throw new ArgumentException(InvalidFace);
            }

            if (bisagra != BisagraInicio && bisagra != BisagraFin)
            {
                throw new ArgumentException("bisagra: debe ser inicio o fin");
            }

            (double x, double y) dir = hueco.Direccion;
            (double x, double y) normalIzquierda = (-dir.y, dir.x);

            // El punto base va en la cara opuesta a la de apertura; desde ella, "hacia dentro" cruza el muro hacia la apertura.
            bool baseDerecha = caraApertura == CaraIzquierda;
            bool enInicio = bisagra == BisagraInicio;
            (double x, double y) posicion = baseDerecha ? (enInicio ? hueco.A2 : hueco.B2) : (enInicio ? hueco.A1 : hueco.B1);
            (double x, double y) aLoLargo = enInicio ? dir : (-dir.x, -dir.y);
            (double x, double y) haciaDentro = baseDerecha ? normalIzquierda : (-normalIzquierda.x, -normalIzquierda.y);

            (double rotacion, double sx, double sy) = Solve(
                VanoCatalog.AxisVector(spec.EjeAncho), VanoCatalog.ThicknessVector(spec.DireccionEspesor), aLoLargo, haciaDentro);

            double? factor = anchoPorPropiedad ? null : PolygonMetrics.Dist(hueco.A1, hueco.B1) / spec.AnchoNominal;
            double escalaX = spec.EjeAncho == "x" ? sx * (factor ?? 1.0) : sx;
            double escalaY = spec.EjeAncho == "y" ? sy * (factor ?? 1.0) : sy;
            return new Placement(posicion, rotacion, new[] { escalaX, escalaY, 1.0 }, sx * sy < 0, factor);
        }

        /// <summary>
        /// Caja estimada del bloque colocado, para R4: el rectángulo del hueco ampliado hacia la cara de apertura en
        /// <paramref name="alcanceApertura"/> (p. ej. el ancho del hueco para una puerta, 0 para una ventana).
        /// </summary>
        public static BBox2 EstimatedBox(WallOpening.OpeningResult hueco, string caraApertura, double alcanceApertura)
        {
            if (hueco == null)
            {
                throw new ArgumentNullException(nameof(hueco));
            }

            if (caraApertura != CaraIzquierda && caraApertura != CaraDerecha)
            {
                throw new ArgumentException(InvalidFace);
            }

            (double x, double y) n = (-hueco.Direccion.y, hueco.Direccion.x);
            bool izquierda = caraApertura == CaraIzquierda;
            double alcance = izquierda ? alcanceApertura : -alcanceApertura;
            (double x, double y) esquina1 = izquierda ? hueco.A1 : hueco.A2;
            (double x, double y) esquina2 = izquierda ? hueco.B1 : hueco.B2;
            (double x, double y)[] puntos =
            {
                hueco.A1,
                hueco.A2,
                hueco.B1,
                hueco.B2,
                (esquina1.x + (alcance * n.x), esquina1.y + (alcance * n.y)),
                (esquina2.x + (alcance * n.x), esquina2.y + (alcance * n.y)),
            };

            double xmin = double.PositiveInfinity;
            double ymin = double.PositiveInfinity;
            double xmax = double.NegativeInfinity;
            double ymax = double.NegativeInfinity;
            foreach ((double x, double y) p in puntos)
            {
                xmin = Math.Min(xmin, p.x);
                ymin = Math.Min(ymin, p.y);
                xmax = Math.Max(xmax, p.x);
                ymax = Math.Max(ymax, p.y);
            }

            return BBox2.FromCorners(xmin, ymin, xmax, ymax);
        }

        /// <summary>[0, 360) sin residuos de redondeo junto a 0 (−1e-13 no debe convertirse en 359.9999999999).</summary>
        private static double NormalizeDegrees(double grados)
        {
            double d = Angles.Normalize360(grados);
            return d >= 360.0 - VectorTol ? 0.0 : d;
        }
    }
}
