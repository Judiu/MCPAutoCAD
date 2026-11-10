using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Una previsualización registrada (protocol.md §5E.5). <see cref="ParamsJson"/> = los parámetros ORIGINALES de la
    /// llamada (sin 'preview'/'dibujar_preview'), con los que <c>confirmar</c> RECALCULA la geometría definitiva (nunca copia
    /// la preview, que el usuario pudo mover o borrar). <see cref="Anclas"/> = huellas de las entidades ancla de los PuntoRef
    /// al previsualizar. <see cref="HandlesPreview"/> = entidades dibujadas en MCP-PREVIEW (pueden haber desaparecido).
    /// </summary>
    public sealed class PreviewRecord
    {
        public PreviewRecord(
            string id,
            string docKey,
            string herramienta,
            string paramsJson,
            IReadOnlyList<AnchorFingerprint> anclas,
            IReadOnlyList<string> handlesPreview,
            string espacio,
            DateTimeOffset creado,
            DateTimeOffset caduca)
        {
            Id = id;
            DocKey = docKey;
            Herramienta = herramienta;
            ParamsJson = paramsJson;
            Anclas = anclas;
            HandlesPreview = handlesPreview;
            Espacio = espacio;
            Creado = creado;
            Caduca = caduca;
        }

        public string Id { get; }

        /// <summary>Identidad del documento (plugin: huella de la Database + ruta).</summary>
        public string DocKey { get; }

        /// <summary>Nombre de la herramienta generadora (muro, ejes_rejilla, …).</summary>
        public string Herramienta { get; }

        public string ParamsJson { get; }

        public IReadOnlyList<AnchorFingerprint> Anclas { get; }

        public IReadOnlyList<string> HandlesPreview { get; }

        /// <summary>"modelo" o nombre del layout donde se dibujó / se dibujará.</summary>
        public string Espacio { get; }

        public DateTimeOffset Creado { get; }

        public DateTimeOffset Caduca { get; }

        /// <summary>ISO 8601 con offset (como 'hora' de ping) para el campo 'caduca'.</summary>
        public string CaducaIso => Caduca.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Almacén en memoria de previsualizaciones (protocol.md §5E.5): por documento, con caducidad (reloj inyectable) y
    /// tope de cantidad por documento. PURO y sin hilos: el plugin lo usa solo desde el hilo principal. NUNCA escribe en
    /// el dibujo: caducar o desalojar una previsualización solo la olvida; sus entidades de MCP-PREVIEW quedan como
    /// huérfanas y se informan/limpian en una petición del cliente (§5E.5, restricción de seguridad).
    /// </summary>
    public sealed class PreviewRegistry
    {
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);
        public const int DefaultMaxPerDoc = 20;
        public const string IdPrefix = "prev-";

        private readonly Func<DateTimeOffset> _clock;
        private readonly Func<string> _idFactory;
        private readonly Dictionary<string, PreviewRecord> _records = new Dictionary<string, PreviewRecord>(StringComparer.Ordinal);

        public PreviewRegistry(Func<DateTimeOffset> clock, TimeSpan? ttl = null, int maxPerDoc = DefaultMaxPerDoc, Func<string>? idFactory = null)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Ttl = ttl ?? DefaultTtl;
            if (Ttl <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(ttl), "La caducidad debe ser positiva.");
            }

            if (maxPerDoc < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPerDoc), "El tope por documento debe ser ≥ 1.");
            }

            MaxPerDoc = maxPerDoc;
            _idFactory = idFactory ?? (() => IdPrefix + Guid.NewGuid().ToString("N").Substring(0, 12));
        }

        public TimeSpan Ttl { get; }

        public int MaxPerDoc { get; }

        /// <summary>Previsualizaciones vivas (no caducadas) de un documento.</summary>
        public int Count(string docKey)
        {
            Purge();
            return _records.Values.Count(r => string.Equals(r.DocKey, docKey, StringComparison.Ordinal));
        }

        /// <summary>Todas las vivas de un documento, de más antigua a más reciente.</summary>
        public IReadOnlyList<PreviewRecord> List(string docKey)
        {
            Purge();
            return _records.Values.Where(r => string.Equals(r.DocKey, docKey, StringComparison.Ordinal)).OrderBy(r => r.Creado).ToList();
        }

        /// <summary>
        /// Registra una previsualización. Antes purga las caducadas; si el documento ya tiene <see cref="MaxPerDoc"/>, desaloja
        /// la más antigua (se devuelve en <paramref name="desalojada"/> para que el plugin la informe como advertencia; sus
        /// entidades NO se borran aquí).
        /// </summary>
        public PreviewRecord Add(
            string docKey,
            string herramienta,
            string paramsJson,
            IReadOnlyList<AnchorFingerprint> anclas,
            IReadOnlyList<string> handlesPreview,
            string espacio,
            out PreviewRecord? desalojada)
        {
            Purge();
            desalojada = null;
            List<PreviewRecord> vivas = _records.Values.Where(r => r.DocKey == docKey).OrderBy(r => r.Creado).ToList();
            if (vivas.Count >= MaxPerDoc)
            {
                desalojada = vivas[0];
                _records.Remove(desalojada.Id);
            }

            DateTimeOffset now = _clock();
            string id = _idFactory();
            while (_records.ContainsKey(id))
            {
                id = _idFactory();
            }

            var rec = new PreviewRecord(id, docKey, herramienta, paramsJson, anclas, handlesPreview, espacio, now, now + Ttl);
            _records[id] = rec;
            return rec;
        }

        /// <summary>Registro vivo con ese id, o null si no existe o caducó (entonces se olvida).</summary>
        public PreviewRecord? Get(string id)
        {
            Purge();
            return _records.TryGetValue(id, out PreviewRecord? r) ? r : null;
        }

        /// <summary>Olvida la previsualización (confirmada o descartada). true si existía y estaba viva.</summary>
        public bool Remove(string id)
        {
            Purge();
            return _records.Remove(id);
        }

        /// <summary>Handles de preview de TODAS las previsualizaciones vivas del documento (para detectar huérfanas).</summary>
        public HashSet<string> TrackedHandles(string docKey)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PreviewRecord r in List(docKey))
            {
                foreach (string h in r.HandlesPreview)
                {
                    set.Add(h);
                }
            }

            return set;
        }

        /// <summary>Elimina las caducadas (Caduca ≤ ahora) y las devuelve.</summary>
        public List<PreviewRecord> Purge()
        {
            DateTimeOffset now = _clock();
            List<PreviewRecord> expired = _records.Values.Where(r => r.Caduca <= now).ToList();
            foreach (PreviewRecord r in expired)
            {
                _records.Remove(r.Id);
            }

            return expired;
        }

        // ---- Mensajes del contrato (protocol.md §5E.5 / §6) ----

        public static string NotFoundMessage(string id) =>
            "No existe ninguna previsualización con id '" + id + "' (caducada, descartada, confirmada o de otra sesión).";

        public static string OtherDocumentDetail(string id) =>
            "preview_id: la previsualización '" + id + "' pertenece a otro documento; active ese documento o vuelva a previsualizar";

        public static string AnchorChangedDetail(string handle) =>
            "preview_id: la entidad ancla " + handle + " cambió o ya no existe desde la previsualización; no se escribió nada; vuelva a previsualizar";

        public static string EvictedWarning(string id) =>
            "Se desalojó la previsualización más antigua '" + id + "' (tope por documento alcanzado); sus entidades de MCP-PREVIEW quedan huérfanas: use descartar_preview o limpiar_observaciones con capa MCP-PREVIEW.";

        public static string OrphansWarning(int n) =>
            "Hay " + n.ToString(CultureInfo.InvariantCulture) + " entidad(es) en MCP-PREVIEW que no pertenecen a ninguna previsualización viva (caducadas o de una sesión anterior): limpie con limpiar_observaciones {\"confirmar\": true, \"capa\": \"MCP-PREVIEW\"}.";

        /// <summary>Variante de <see cref="OrphansWarning"/> cuando la pasada se cortó por el tope de coste: n es una cota inferior.</summary>
        public static string OrphansWarningPartial(int n) =>
            "Hay al menos " + n.ToString(CultureInfo.InvariantCulture) + " entidad(es) en MCP-PREVIEW que no pertenecen a ninguna previsualización viva (caducadas o de una sesión anterior; recuento interrumpido por el tope de coste): limpie con limpiar_observaciones {\"confirmar\": true, \"capa\": \"MCP-PREVIEW\"}.";
    }
}
