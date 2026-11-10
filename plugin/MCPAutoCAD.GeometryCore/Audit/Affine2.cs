using System;
using System.Collections.Generic;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>
    /// Transformación afín 2D (x' = A·x + B·y + Tx, y' = C·x + D·y + Ty): la parte XY de un BlockTransform en vista en
    /// planta. Sirve para llevar la caja de una inserción ANIDADA (en coordenadas de la definición que la contiene) al
    /// espacio, componiendo el BlockTransform de cada contenedor (protocol.md §5C.3, corrección de 0.3.2 para el bloque
    /// 'C' del plano real: 5 464 inserciones anidadas en bloques anónimos a escala 0.045). Puro e inmutable.
    /// </summary>
    public readonly struct Affine2
    {
        public Affine2(double a, double b, double c, double d, double tx, double ty)
        {
            if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c) || !double.IsFinite(d)
                || !double.IsFinite(tx) || !double.IsFinite(ty))
            {
                throw new ArgumentException("transformación: valor no finito");
            }

            A = a;
            B = b;
            C = c;
            D = d;
            Tx = tx;
            Ty = ty;
        }

        public double A { get; }
        public double B { get; }
        public double C { get; }
        public double D { get; }
        public double Tx { get; }
        public double Ty { get; }

        public static Affine2 Identity => new Affine2(1, 0, 0, 1, 0, 0);

        /// <summary>Inserción típica: escala (sx, sy), rotación (rad) alrededor del origen y traslación al punto de inserción.</summary>
        public static Affine2 FromInsert(double x, double y, double sx, double sy, double rotationRad)
        {
            double cos = Math.Cos(rotationRad);
            double sin = Math.Sin(rotationRad);
            return new Affine2(cos * sx, -sin * sy, sin * sx, cos * sy, x, y);
        }

        /// <summary>Aplica la transformación al punto.</summary>
        public (double x, double y) Apply(double x, double y) => ((A * x) + (B * y) + Tx, (C * x) + (D * y) + Ty);

        /// <summary>
        /// Composición <c>this ∘ inner</c>: primero <paramref name="inner"/> y después this. Para una cadena anidada se
        /// sube nivel a nivel con <c>acumulada = transformContenedor.Compose(acumulada)</c>.
        /// </summary>
        public Affine2 Compose(Affine2 inner) => new Affine2(
            (A * inner.A) + (B * inner.C),
            (A * inner.B) + (B * inner.D),
            (C * inner.A) + (D * inner.C),
            (C * inner.B) + (D * inner.D),
            (A * inner.Tx) + (B * inner.Ty) + Tx,
            (C * inner.Tx) + (D * inner.Ty) + Ty);

        /// <summary>Caja envolvente de las cuatro esquinas transformadas de <paramref name="box"/>.</summary>
        public BBox2 TransformBox(BBox2 box)
        {
            var pts = new List<Point3>(4);
            foreach ((double x, double y) in new[] { (box.Xmin, box.Ymin), (box.Xmax, box.Ymin), (box.Xmax, box.Ymax), (box.Xmin, box.Ymax) })
            {
                (double px, double py) = Apply(x, y);
                pts.Add(new Point3(px, py, 0));
            }

            return BBox2.FromPoints(pts);
        }

        /// <summary>Caja en el espacio de una inserción anidada: compone de dentro afuera las transformaciones de los contenedores.</summary>
        /// <param name="localBox">Caja de la inserción anidada en coordenadas de la definición que la contiene.</param>
        /// <param name="containers">BlockTransform de cada contenedor, del más interior al más exterior (el que está en el espacio).</param>
        public static BBox2 BoxInSpace(BBox2 localBox, IEnumerable<Affine2> containers)
        {
            if (containers == null)
            {
                throw new ArgumentNullException(nameof(containers));
            }

            Affine2 acc = Identity;
            foreach (Affine2 t in containers)
            {
                acc = t.Compose(acc);
            }

            return acc.TransformBox(localBox);
        }
    }
}
