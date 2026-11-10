using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>Geometría de una nube de revisión ya calculada (protocol.md §5C.9). Inmutable.</summary>
    public sealed class RevisionCloudShape
    {
        public RevisionCloudShape(
            IReadOnlyList<(double X, double Y)> vertices,
            IReadOnlyList<double> bulges,
            BBox2 bbox,
            double margen,
            double cuerda,
            int arcosHorizontal,
            int arcosVertical)
        {
            Vertices = vertices;
            Bulges = bulges;
            Bbox = bbox;
            Margen = margen;
            Cuerda = cuerda;
            ArcosHorizontal = arcosHorizontal;
            ArcosVertical = arcosVertical;
        }

        /// <summary>Vértices en sentido antihorario empezando en (xminR, yminR); la polilínea se cierra sola.</summary>
        public IReadOnlyList<(double X, double Y)> Vertices { get; }

        /// <summary>Bulge del tramo que sale de cada vértice (mismo número que <see cref="Vertices"/>).</summary>
        public IReadOnlyList<double> Bulges { get; }

        /// <summary>Rectángulo ampliado R (la caja pedida más el margen por cada lado).</summary>
        public BBox2 Bbox { get; }

        /// <summary>Margen m aplicado a cada lado de la caja.</summary>
        public double Margen { get; }

        /// <summary>Cuerda objetivo c de cada arco.</summary>
        public double Cuerda { get; }

        /// <summary>Arcos en cada lado horizontal de R (inferior y superior).</summary>
        public int ArcosHorizontal { get; }

        /// <summary>Arcos en cada lado vertical de R (derecho e izquierdo).</summary>
        public int ArcosVertical { get; }
    }

    /// <summary>
    /// Cálculo de la nube de revisión de <c>marcar_hallazgos</c> (protocol.md §5C.9), sin AutoCAD:
    /// margen m = max(alturaTexto, 0.05·max(ancho, alto)); R = caja ampliada en m por cada lado;
    /// cuerda c = max(1.5·alturaTexto, 0.08·max(anchoR, altoR)); cada lado de R se divide en
    /// n = max(4, ceil(L / c)) arcos iguales; vértices antihorarios desde (xminR, yminR); bulge = tan(110°/4).
    /// </summary>
    public static class RevisionCloud
    {
        /// <summary>Bulge de un arco de 110° (tan(110°/4) ≈ 0.5206). Positivo: hacia fuera en un recorrido antihorario.</summary>
        public static readonly double BulgeArc110 = Math.Tan(110.0 / 4.0 * Math.PI / 180.0);

        /// <summary>Mínimo de arcos por lado.</summary>
        public const int MinArcsPerSide = 4;

        /// <summary>Mensaje exacto del contrato (protocol.md §6) para una altura de texto inválida.</summary>
        public const string InvalidTextHeight = "altura_texto: debe ser un número mayor que 0";

        /// <summary>Tolerancia relativa para que ceil(L/c) no salte por ruido de coma flotante (p. ej. 4.0000000001 → 4).</summary>
        private const double CeilEpsilon = 1e-9;

        /// <summary>
        /// Calcula la nube alrededor de <paramref name="caja"/>. Una caja de tamaño cero se trata como punto
        /// (R = cuadrado de lado 2m). <paramref name="alturaTexto"/> ≤ 0 o no finita → ArgumentException
        /// ("altura_texto: debe ser un número mayor que 0").
        /// </summary>
        public static RevisionCloudShape Build(BBox2 caja, double alturaTexto)
        {
            if (!double.IsFinite(alturaTexto) || alturaTexto <= 0)
            {
                throw new ArgumentException(InvalidTextHeight);
            }

            double margin = Math.Max(alturaTexto, 0.05 * Math.Max(caja.Width, caja.Height));
            BBox2 r = BBox2.FromCorners(caja.Xmin - margin, caja.Ymin - margin, caja.Xmax + margin, caja.Ymax + margin);
            double chord = Math.Max(1.5 * alturaTexto, 0.08 * Math.Max(r.Width, r.Height));
            if (!double.IsFinite(margin) || !double.IsFinite(chord) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height))
            {
                throw new ArgumentException("bbox: coordenada no finita");
            }

            int nh = ArcsForSide(r.Width, chord);
            int nv = ArcsForSide(r.Height, chord);

            var vertices = new List<(double X, double Y)>(2 * (nh + nv));

            // Inferior: de (xmin, ymin) hacia +X.
            for (int i = 0; i < nh; i++)
            {
                vertices.Add((Lerp(r.Xmin, r.Xmax, i, nh), r.Ymin));
            }

            // Derecho: de (xmax, ymin) hacia +Y.
            for (int i = 0; i < nv; i++)
            {
                vertices.Add((r.Xmax, Lerp(r.Ymin, r.Ymax, i, nv)));
            }

            // Superior: de (xmax, ymax) hacia −X.
            for (int i = 0; i < nh; i++)
            {
                vertices.Add((Lerp(r.Xmax, r.Xmin, i, nh), r.Ymax));
            }

            // Izquierdo: de (xmin, ymax) hacia −Y (el último tramo cierra en el primer vértice).
            for (int i = 0; i < nv; i++)
            {
                vertices.Add((r.Xmin, Lerp(r.Ymax, r.Ymin, i, nv)));
            }

            var bulges = new double[vertices.Count];
            for (int i = 0; i < bulges.Length; i++)
            {
                bulges[i] = BulgeArc110;
            }

            return new RevisionCloudShape(vertices, bulges, r, margin, chord, nh, nv);
        }

        /// <summary>n = max(4, ceil(L / c)).</summary>
        private static int ArcsForSide(double length, double chord)
        {
            double ratio = length / chord;
            int n = (int)Math.Ceiling(ratio - CeilEpsilon * Math.Max(1.0, ratio));
            return Math.Max(MinArcsPerSide, n);
        }

        /// <summary>Punto i de n divisiones iguales de [a, b] (i = 0 → a exactamente).</summary>
        private static double Lerp(double a, double b, int i, int n) => i == 0 ? a : a + (b - a) * i / n;
    }
}
