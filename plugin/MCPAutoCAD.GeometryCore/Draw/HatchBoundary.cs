using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Validación pura del contorno de un sombreado (protocol.md §5D.7). Textos de error del contrato: no cambiarlos.
    /// </summary>
    public static class HatchBoundary
    {
        public const double DefaultTolerance = 1e-9;

        public const string ContourExclusive = "contorno: indique handles o vertices (solo uno)";

        /// <summary>Prefijo con que se citan los errores del contorno por vértices.</summary>
        public const string VerticesLabel = "contorno.vertices";

        public static string NotClosed(int i, string tipo) =>
            "contorno.handles[" + i.ToString(CultureInfo.InvariantCulture) + "]: la entidad no es un contorno cerrado (" + tipo + ")";

        public static string OtherSpace(int i, string espacio) =>
            "contorno.handles[" + i.ToString(CultureInfo.InvariantCulture) + "]: la entidad no está en el espacio '" + espacio + "'";

        /// <summary>
        /// Entidad de contorno en capa bloqueada (0.4.0): el sombreado asociativo tiene que abrir el contorno para
        /// escribir su reactor y AutoCAD lo impediría a mitad de la escritura (eOnLockedLayer).
        /// </summary>
        public static string LockedLayer(int i) =>
            "contorno.handles[" + i.ToString(CultureInfo.InvariantCulture) + "]: la entidad está en una capa bloqueada; desbloquéela o use contorno.vertices";

        /// <summary>Entidad de contorno en capa dependiente de xref (0.4.0; no se puede asociar el sombreado).</summary>
        public static string XrefLayer(int i) =>
            "contorno.handles[" + i.ToString(CultureInfo.InvariantCulture) + "]: la entidad está en una capa dependiente de xref";

        /// <summary>
        /// Criterio de cierre de una LWPOLYLINE / POLYLINE 2D usada como contorno: <paramref name="closedFlag"/> con
        /// al menos 2 vértices (dos vértices con bulge forman un contorno), o primer vértice = último (distancia ≤ tol)
        /// con al menos 3 vértices.
        /// </summary>
        public static bool IsClosedPolyline(bool closedFlag, IReadOnlyList<(double x, double y)> vertices, double tol = DefaultTolerance)
        {
            if (vertices == null)
            {
                throw new ArgumentNullException(nameof(vertices));
            }

            int n = vertices.Count;
            if (closedFlag)
            {
                return n >= 2;
            }

            if (n < 3)
            {
                return false;
            }

            double dx = vertices[0].x - vertices[n - 1].x;
            double dy = vertices[0].y - vertices[n - 1].y;
            return Math.Sqrt(dx * dx + dy * dy) <= tol;
        }

        /// <summary>
        /// Contorno por vértices = polígono cerrado implícito: <see cref="PolylineValidator.Validate"/> con
        /// cerrada = true y sin bulges; los errores se citan con la etiqueta <see cref="VerticesLabel"/>
        /// ("contorno.vertices[2]: vértice duplicado con el anterior", "contorno.vertices: …").
        /// </summary>
        public static IReadOnlyList<string> ValidateVertices(IReadOnlyList<(double x, double y)> vertices, double tol = DefaultTolerance)
        {
            IReadOnlyList<string> raw = PolylineValidator.Validate(vertices, true, null, tol);
            var errors = new List<string>(raw.Count);
            foreach (string e in raw)
            {
                errors.Add(Relabel(e));
            }

            return errors;
        }

        /// <summary>"vertices…" → "contorno.vertices…" (solo el prefijo inicial).</summary>
        public static string Relabel(string error)
        {
            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            const string prefix = "vertices";
            return error.StartsWith(prefix, StringComparison.Ordinal) ? VerticesLabel + error.Substring(prefix.Length) : error;
        }
    }
}
