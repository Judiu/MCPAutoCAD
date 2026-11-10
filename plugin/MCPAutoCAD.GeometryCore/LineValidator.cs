using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Validación de líneas previa a crear entidades (protocol.md §5.4).
    /// Los textos de error forman parte del contrato: no cambiarlos.
    /// </summary>
    public static class LineValidator
    {
        /// <summary>Tolerancia por defecto de longitud cero, en unidades de dibujo.</summary>
        public const double DefaultTolerance = 1e-9;

        /// <summary>
        /// Valida una lista de líneas. Devuelve los errores (lista vacía si todo es válido) con el formato exacto
        /// del contrato: "lineas[i]: longitud cero" o "lineas[i]: coordenada no finita".
        /// Una línea tiene longitud cero cuando su distancia 3D es menor o igual que <paramref name="tolerance"/>.
        /// La lista vacía es válida para este validador (el mínimo de 1 elemento lo controla el schema).
        /// </summary>
        public static IReadOnlyList<string> ValidateLines(
            IReadOnlyList<(Point3 start, Point3 end)> lines,
            double tolerance = DefaultTolerance)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            if (!double.IsFinite(tolerance) || tolerance < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tolerance), "La tolerancia debe ser un número finito no negativo.");
            }

            var errors = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                Point3 start = lines[i].start;
                Point3 end = lines[i].end;
                string prefix = string.Format(CultureInfo.InvariantCulture, "lineas[{0}]: ", i);

                if (!start.IsFinite || !end.IsFinite)
                {
                    errors.Add(prefix + "coordenada no finita");
                    continue;
                }

                double length = start.Distance(end);
                if (!double.IsFinite(length))
                {
                    // Desbordamiento al calcular la distancia con coordenadas enormes.
                    errors.Add(prefix + "coordenada no finita");
                    continue;
                }

                if (length <= tolerance)
                {
                    errors.Add(prefix + "longitud cero");
                }
            }

            return errors;
        }
    }
}
