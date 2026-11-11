using System;
using System.Collections.Generic;
using System.Linq;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Reglas puras de <c>estado_capas</c> (fallo 39): comodines estilo AutoCAD ('*' y '?', sin distinguir mayúsculas),
    /// validación de parámetros y plan de cambios de estado (apagar/encender, congelar/descongelar, bloquear/desbloquear,
    /// plot on/off). Sin AutoCAD: la herramienta lee las capas, llama a <see cref="Plan"/> y escribe solo lo planificado.
    /// </summary>
    public static class LayerStateRules
    {
        /// <summary>Máximo de patrones por llamada.</summary>
        public const int MaxPatrones = 200;

        /// <summary>Longitud máxima de un patrón (como el límite de nombre de capa de AutoCAD).</summary>
        public const int MaxLongitudPatron = 255;

        /// <summary>Caracteres que AutoCAD no admite en un nombre de capa (los comodines '*' y '?' sí se admiten en patrones).</summary>
        private const string CaracteresNoPermitidos = "<>/\\\":;=`";

        /// <summary>Detalle de INVALID_PARAMS cuando no se pasa ninguna bandera.</summary>
        public const string SinBanderas =
            "estado: indique al menos una de apagada, congelada, bloqueada o imprimir";

        /// <summary>Detalle de INVALID_PARAMS cuando ningún patrón coincide con una capa del dibujo.</summary>
        public const string SinCoincidencias =
            "capas: ningún patrón coincide con una capa del dibujo";

        /// <summary>Advertencia cuando se pide congelar la capa actual (AutoCAD lo prohíbe).</summary>
        public const string CapaActualNoCongelable =
            "no se puede congelar la capa actual (AutoCAD lo prohíbe): se omite el congelado";

        /// <summary>Motivo de omisión de una capa de xref (dependiente).</summary>
        public const string MotivoXref =
            "capa de referencia externa (xref): no se modifica";

        /// <summary>
        /// Valida la lista de patrones: entre 1 y <see cref="MaxPatrones"/>, sin patrones vacíos ni demasiado largos, sin
        /// caracteres de control ni caracteres que AutoCAD no admite en nombres de capa. Devuelve null si es válida o el
        /// detalle del error (para INVALID_PARAMS).
        /// </summary>
        public static string? ValidatePatterns(IReadOnlyList<string>? patrones)
        {
            if (patrones == null || patrones.Count == 0)
            {
                return "capas: obligatorio, de 1 a " + MaxPatrones + " patrones";
            }

            if (patrones.Count > MaxPatrones)
            {
                return "capas: como máximo " + MaxPatrones + " patrones";
            }

            for (int i = 0; i < patrones.Count; i++)
            {
                string? p = patrones[i];
                string ruta = "capas[" + i + "]";
                if (p == null || p.Trim().Length == 0)
                {
                    return ruta + ": no puede estar vacío";
                }

                string t = p.Trim();
                if (t.Length > MaxLongitudPatron)
                {
                    return ruta + ": como máximo " + MaxLongitudPatron + " caracteres";
                }

                if (t.Any(char.IsControl))
                {
                    return ruta + ": no admite caracteres de control";
                }

                if (t.IndexOf(',') >= 0)
                {
                    // Auditoría: AutoCAD admite listas "A*,B*"; aquí cada patrón va en su propio elemento del array.
                    return ruta + ": no admite ',' (ponga cada patrón en su propio elemento de 'capas')";
                }

                if (t.IndexOfAny(CaracteresNoPermitidos.ToCharArray()) >= 0)
                {
                    return ruta + ": contiene caracteres que no admiten los nombres de capa";
                }
            }

            return null;
        }

        /// <summary>
        /// Valida que se pida al menos una bandera (apagada, congelada, bloqueada o imprimir). Null si es válida o el detalle.
        /// </summary>
        public static string? ValidateChange(LayerStateChange cambio)
        {
            if (cambio == null)
            {
                throw new ArgumentNullException(nameof(cambio));
            }

            return cambio.HasAny ? null : SinBanderas;
        }

        /// <summary>
        /// Coincidencia de nombre de capa con un patrón de AutoCAD: '*' = cualquier secuencia (también vacía), '?' = exactamente
        /// un carácter; el patrón debe cubrir todo el nombre y no distingue mayúsculas. Algoritmo voraz con retroceso al último
        /// '*' (sin regex, O(n·m) en el peor caso).
        /// </summary>
        public static bool WildcardMatch(string nombre, string patron)
        {
            if (nombre == null || patron == null)
            {
                return false;
            }

            string n = nombre.ToUpperInvariant();
            string p = patron.ToUpperInvariant();

            int ni = 0, pi = 0, star = -1, mark = 0;
            while (ni < n.Length)
            {
                if (pi < p.Length && (p[pi] == '?' || (p[pi] != '*' && p[pi] == n[ni])))
                {
                    ni++;
                    pi++;
                }
                else if (pi < p.Length && p[pi] == '*')
                {
                    star = pi++;
                    mark = ni;
                }
                else if (star >= 0)
                {
                    pi = star + 1;
                    ni = ++mark;
                }
                else
                {
                    return false;
                }
            }

            while (pi < p.Length && p[pi] == '*')
            {
                pi++;
            }

            return pi == p.Length;
        }

        /// <summary>
        /// Plan de <c>estado_capas</c>. Para cada capa que coincide con algún patrón (en el orden de <paramref name="capas"/>,
        /// sin repetir) calcula el estado antes y después:
        /// <list type="bullet">
        /// <item>Capa de xref (dependiente): se omite con advertencia.</item>
        /// <item>Capa actual: nunca se congela (se descarta esa parte con advertencia; el resto de banderas sí se aplica; si
        /// no queda nada que cambiar, la capa se omite). Apagarla sí se permite, con advertencia.</item>
        /// <item>Banderas no pedidas (null) se conservan.</item>
        /// </list>
        /// Los patrones que no coinciden con ninguna capa se devuelven en <see cref="LayerStatePlan.PatronesSinCoincidencia"/>.
        /// Lectura pura: no modifica nada.
        /// </summary>
        public static LayerStatePlan Plan(
            IReadOnlyList<LayerStateSnapshot> capas,
            IReadOnlyList<string> patrones,
            LayerStateChange cambio,
            string? capaActual)
        {
            if (capas == null) throw new ArgumentNullException(nameof(capas));
            if (patrones == null) throw new ArgumentNullException(nameof(patrones));
            if (cambio == null) throw new ArgumentNullException(nameof(cambio));

            var pats = new List<string>();
            foreach (string raw in patrones)
            {
                string t = (raw ?? "").Trim();
                if (t.Length > 0 && !pats.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                {
                    pats.Add(t);
                }
            }

            var sinCoincidencia = pats
                .Where(p => !capas.Any(c => WildcardMatch(c.Nombre, p)))
                .ToList();

            var advertencias = new List<string>();
            var items = new List<LayerStateItem>();
            foreach (LayerStateSnapshot antes in capas)
            {
                if (!pats.Any(p => WildcardMatch(antes.Nombre, p)))
                {
                    continue;
                }

                items.Add(Decide(antes, cambio, capaActual, advertencias));
            }

            // Auditoría (fallo 34, respuestas largas): una sola advertencia para todas las capas de xref omitidas.
            int xrefs = items.Count(i => i.Estado == LayerStateOutcome.Omitida && i.Motivo == MotivoXref);
            if (xrefs > 0)
            {
                advertencias.Insert(0, xrefs + " capa(s) de xref (dependientes) omitidas: este comando no cambia capas de xref.");
            }

            return new LayerStatePlan(items, sinCoincidencia, advertencias);
        }

        private static LayerStateItem Decide(
            LayerStateSnapshot antes, LayerStateChange cambio, string? capaActual, List<string> advertencias)
        {
            if (antes.Dependiente)
            {
                return new LayerStateItem(antes.Nombre, antes, antes, LayerStateOutcome.Omitida, MotivoXref);
            }

            bool esActual = capaActual != null && string.Equals(antes.Nombre, capaActual, StringComparison.OrdinalIgnoreCase);
            bool congelada = cambio.Congelada ?? antes.Congelada;
            bool omitioCongelado = false;
            if (esActual && congelada && !antes.Congelada)
            {
                congelada = antes.Congelada;
                omitioCongelado = true;
                advertencias.Add("capa '" + antes.Nombre + "': " + CapaActualNoCongelable + ".");
            }

            var despues = new LayerStateSnapshot(
                antes.Nombre,
                cambio.Apagada ?? antes.Apagada,
                congelada,
                cambio.Bloqueada ?? antes.Bloqueada,
                cambio.Imprimir ?? antes.Imprimir,
                antes.Dependiente);

            if (esActual && despues.Apagada && !antes.Apagada)
            {
                advertencias.Add("capa '" + antes.Nombre + "' es la capa actual: queda apagada (los objetos nuevos no se verán).");
            }

            if (antes.Igual(despues))
            {
                if (omitioCongelado)
                {
                    return new LayerStateItem(antes.Nombre, antes, despues, LayerStateOutcome.Omitida, CapaActualNoCongelable);
                }

                return new LayerStateItem(antes.Nombre, antes, despues, LayerStateOutcome.SinCambio, null);
            }

            return new LayerStateItem(antes.Nombre, antes, despues, LayerStateOutcome.Cambia, null);
        }
    }

    /// <summary>Estado de una capa: lo que tiene AutoCAD (<c>IsOff</c>, <c>IsFrozen</c>, <c>IsLocked</c>, <c>IsPlottable</c>).</summary>
    public sealed class LayerStateSnapshot
    {
        public LayerStateSnapshot(string nombre, bool apagada, bool congelada, bool bloqueada, bool imprimir, bool dependiente)
        {
            Nombre = nombre ?? throw new ArgumentNullException(nameof(nombre));
            Apagada = apagada;
            Congelada = congelada;
            Bloqueada = bloqueada;
            Imprimir = imprimir;
            Dependiente = dependiente;
        }

        public string Nombre { get; }

        public bool Apagada { get; }

        public bool Congelada { get; }

        public bool Bloqueada { get; }

        /// <summary>Plot on (true) / plot off (false).</summary>
        public bool Imprimir { get; }

        /// <summary>true si es de xref (dependiente o con '|' en el nombre): no se modifica.</summary>
        public bool Dependiente { get; }

        /// <summary>Compara las cuatro banderas (el nombre y la dependencia no cuentan).</summary>
        public bool Igual(LayerStateSnapshot otra) =>
            Apagada == otra.Apagada && Congelada == otra.Congelada && Bloqueada == otra.Bloqueada && Imprimir == otra.Imprimir;
    }

    /// <summary>Lo pedido: cada bandera es true/false (fijar) o null (no tocar).</summary>
    public sealed class LayerStateChange
    {
        public LayerStateChange(bool? apagada, bool? congelada, bool? bloqueada, bool? imprimir)
        {
            Apagada = apagada;
            Congelada = congelada;
            Bloqueada = bloqueada;
            Imprimir = imprimir;
        }

        public bool? Apagada { get; }

        public bool? Congelada { get; }

        public bool? Bloqueada { get; }

        public bool? Imprimir { get; }

        /// <summary>true si se pide al menos una bandera.</summary>
        public bool HasAny => Apagada.HasValue || Congelada.HasValue || Bloqueada.HasValue || Imprimir.HasValue;
    }

    /// <summary>Resultado por capa de un plan de <c>estado_capas</c>.</summary>
    public enum LayerStateOutcome
    {
        /// <summary>Cambia de estado: se escribe.</summary>
        Cambia,

        /// <summary>Ya estaba en el estado pedido: no se escribe.</summary>
        SinCambio,

        /// <summary>No se toca (xref, o la única bandera pedida era congelar la capa actual).</summary>
        Omitida,
    }

    /// <summary>Capa coincidente: estado antes y después, resultado y motivo de omisión (si lo hay).</summary>
    public sealed class LayerStateItem
    {
        public LayerStateItem(string nombre, LayerStateSnapshot antes, LayerStateSnapshot despues, LayerStateOutcome estado, string? motivo)
        {
            Nombre = nombre;
            Antes = antes;
            Despues = despues;
            Estado = estado;
            Motivo = motivo;
        }

        public string Nombre { get; }

        public LayerStateSnapshot Antes { get; }

        public LayerStateSnapshot Despues { get; }

        public LayerStateOutcome Estado { get; }

        /// <summary>Solo con Omitida: por qué no se toca.</summary>
        public string? Motivo { get; }
    }

    /// <summary>Plan completo: capas coincidentes en orden de tabla, patrones sin coincidencia y advertencias.</summary>
    public sealed class LayerStatePlan
    {
        public LayerStatePlan(IReadOnlyList<LayerStateItem> capas, IReadOnlyList<string> patronesSinCoincidencia, IReadOnlyList<string> advertencias)
        {
            Capas = capas;
            PatronesSinCoincidencia = patronesSinCoincidencia;
            Advertencias = advertencias;
        }

        public IReadOnlyList<LayerStateItem> Capas { get; }

        public IReadOnlyList<string> PatronesSinCoincidencia { get; }

        public IReadOnlyList<string> Advertencias { get; }

        /// <summary>Capas que coinciden (incluye las omitidas y las sin cambio).</summary>
        public int Coincidencias => Capas.Count;

        public int Cambian => Capas.Count(c => c.Estado == LayerStateOutcome.Cambia);

        public int SinCambio => Capas.Count(c => c.Estado == LayerStateOutcome.SinCambio);

        public int Omitidas => Capas.Count(c => c.Estado == LayerStateOutcome.Omitida);
    }
}
