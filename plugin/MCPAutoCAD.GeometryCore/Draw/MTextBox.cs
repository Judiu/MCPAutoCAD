using System;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Caja de un MTEXT en su plano (fallo 30, editar_mtext mantener_centro). El punto de inserción (Location) es el punto
    /// de la caja que indica la justificación: columna 0 / 0,5 / 1 = izquierda / centro / derecha; fila 0 / 0,5 / 1 =
    /// superior / medio / inferior. La caja mide ancho × alto reales y gira con la dirección del texto.
    /// </summary>
    public static class MTextBox
    {
        /// <summary>Código TL…BR → (columna, fila). Cualquier otro código → ArgumentException.</summary>
        public static (double Col, double Row) Cell(string code)
        {
            if (code == null || code.Length != 2)
            {
                throw new ArgumentException("justificación MTEXT inválida: " + code);
            }

            double row = code[0] switch
            {
                'T' => 0.0,
                'M' => 0.5,
                'B' => 1.0,
                _ => throw new ArgumentException("justificación MTEXT inválida: " + code),
            };
            double col = code[1] switch
            {
                'L' => 0.0,
                'C' => 0.5,
                'R' => 1.0,
                _ => throw new ArgumentException("justificación MTEXT inválida: " + code),
            };
            return (col, row);
        }

        /// <summary>Centro de la caja a partir del punto de inserción.</summary>
        public static (double X, double Y) Center(double locX, double locY, double angle, string code, double width, double height)
        {
            (double dx, double dy) = LocalOffset(code, width, height);
            (double rx, double ry) = Rotate(dx, dy, angle);
            return (locX + rx, locY + ry);
        }

        /// <summary>Punto de inserción que deja el centro de la caja en (centerX, centerY). Inversa de <see cref="Center"/>.</summary>
        public static (double X, double Y) LocationFor(double centerX, double centerY, double angle, string code, double width, double height)
        {
            (double dx, double dy) = LocalOffset(code, width, height);
            (double rx, double ry) = Rotate(dx, dy, angle);
            return (centerX - rx, centerY - ry);
        }

        /// <summary>Vector del punto de inserción al centro, en ejes locales del texto (x a lo largo, y hacia arriba).</summary>
        private static (double, double) LocalOffset(string code, double width, double height)
        {
            if (!(width >= 0) || !(height >= 0) || double.IsInfinity(width) || double.IsInfinity(height))
            {
                throw new ArgumentException("ancho y alto de la caja deben ser finitos y ≥ 0");
            }

            (double col, double row) = Cell(code);
            return ((0.5 - col) * width, (row - 0.5) * height);
        }

        private static (double, double) Rotate(double x, double y, double angle)
        {
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            return (x * c - y * s, x * s + y * c);
        }
    }
}
