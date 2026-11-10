using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Audit
{
    /// <summary>Severidades del contrato (protocol.md §5C.0) y su orden de prioridad.</summary>
    public static class Severity
    {
        public const string Alta = "alta";
        public const string Media = "media";
        public const string Baja = "baja";

        /// <summary>0 = alta, 1 = media, 2 = baja; otro texto → 3 (nunca debería ocurrir).</summary>
        public static int Rank(string severidad) => severidad switch
        {
            Alta => 0,
            Media => 1,
            Baja => 2,
            _ => 3,
        };

        public static bool IsValid(string? severidad) => severidad is Alta or Media or Baja;
    }

    /// <summary>
    /// Hallazgo de auditoría con el formato común de protocol.md §5C.0. Independiente de AutoCAD.
    /// <see cref="ToDictionary"/> produce exactamente los campos JSON del contrato.
    /// </summary>
    public sealed class Finding
    {
        /// <summary>Máximo de handles por hallazgo (§5C.0); el resto se resume en datos.</summary>
        public const int MaxHandles = 50;

        public Finding(string regla, string severidad, string mensaje)
        {
            if (string.IsNullOrEmpty(regla))
            {
                throw new ArgumentException("regla: obligatoria", nameof(regla));
            }

            if (!Severity.IsValid(severidad))
            {
                throw new ArgumentException("severidad: debe ser alta, media o baja", nameof(severidad));
            }

            if (string.IsNullOrEmpty(mensaje))
            {
                throw new ArgumentException("mensaje: obligatorio", nameof(mensaje));
            }

            Regla = regla;
            Severidad = severidad;
            Mensaje = mensaje;
        }

        /// <summary>"&lt;regla&gt;-&lt;n&gt;"; lo asigna <see cref="AssignIds"/> al final de la llamada.</summary>
        public string Id { get; set; } = "";

        public string Regla { get; }

        public string Severidad { get; }

        public string Mensaje { get; }

        /// <summary>Handles implicados (hex mayúsculas), en orden de base de datos. Se recorta a 50 al serializar.</summary>
        public List<string> Handles { get; } = new List<string>();

        /// <summary>Caja 2D [[xmin, ymin], [xmax, ymax]] o null.</summary>
        public BBox2? Bbox { get; set; }

        /// <summary>"modelo", nombre de layout o null si no aplica.</summary>
        public string? Espacio { get; set; }

        /// <summary>Datos específicos de la regla (siempre serializado; {} si está vacío).</summary>
        public Dictionary<string, object?> Datos { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <summary>Orden del contrato: severidad (alta → baja), luego orden de regla, luego id numérico.</summary>
        public static void Sort(List<Finding> findings, IReadOnlyList<string> ruleOrder)
        {
            if (findings == null)
            {
                throw new ArgumentNullException(nameof(findings));
            }

            int RuleIndex(string regla)
            {
                for (int i = 0; i < ruleOrder.Count; i++)
                {
                    if (string.Equals(ruleOrder[i], regla, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }

                return ruleOrder.Count;
            }

            // Orden estable: se conserva el orden de llegada dentro de cada (severidad, regla).
            var indexed = new List<(Finding f, int i)>(findings.Count);
            for (int i = 0; i < findings.Count; i++)
            {
                indexed.Add((findings[i], i));
            }

            indexed.Sort((a, b) =>
            {
                int c = Severity.Rank(a.f.Severidad).CompareTo(Severity.Rank(b.f.Severidad));
                if (c != 0)
                {
                    return c;
                }

                c = RuleIndex(a.f.Regla).CompareTo(RuleIndex(b.f.Regla));
                return c != 0 ? c : a.i.CompareTo(b.i);
            });

            findings.Clear();
            foreach ((Finding f, _) in indexed)
            {
                findings.Add(f);
            }
        }

        /// <summary>Asigna ids "&lt;regla&gt;-&lt;n&gt;" correlativos por regla, en el orden actual de la lista.</summary>
        public static void AssignIds(IEnumerable<Finding> findings)
        {
            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Finding f in findings)
            {
                counters.TryGetValue(f.Regla, out int n);
                n++;
                counters[f.Regla] = n;
                f.Id = f.Regla + "-" + n.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Objeto JSON de §5C.0 (handles recortados a 50 con datos.handles_truncados/n_handles).</summary>
        public Dictionary<string, object?> ToDictionary()
        {
            var datos = new Dictionary<string, object?>(Datos, StringComparer.Ordinal);
            List<string> handles = Handles;
            if (Handles.Count > MaxHandles)
            {
                handles = Handles.GetRange(0, MaxHandles);
                datos["handles_truncados"] = true;
                datos["n_handles"] = Handles.Count;
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Id,
                ["regla"] = Regla,
                ["severidad"] = Severidad,
                ["mensaje"] = Mensaje,
                ["handles"] = handles,
                ["bbox"] = Bbox?.ToArray2D(),
                ["espacio"] = Espacio,
                ["datos"] = datos,
            };
        }
    }
}
