namespace MCPAutoCAD.Plugin
{
    /// <summary>Constantes de versión y límites del contrato (protocol.md).</summary>
    internal static class PluginInfo
    {
        public const string PluginVersion = "0.10.1";
        public const string ProtocolVersion = "0.8";

        /// <summary>Presupuesto de auditar_dibujo en el hilo principal (protocol.md §3.10), por debajo de MainThreadTimeoutMs.</summary>
        public const int AuditBudgetMs = 8000;

        /// <summary>Máximo de presupuesto_ms de auditar_archivo (0.8.1; antes 8000). El valor por defecto sigue siendo <see cref="AuditBudgetMs"/>.</summary>
        public const int AuditBudgetMaxMs = 30000;

        /// <summary>Capa por defecto de las marcas de auditoría (protocol.md §5C.9, §5C.10).</summary>
        public const string ObservationsLayer = "MCP-OBSERVACIONES";

        /// <summary>
        /// Capa de las previsualizaciones de la Fase 4A (protocol.md §5E.4–§5E.5; capas_mcp: color 4, no imprimible).
        /// Mismo valor que <c>Handlers.Gen.PreviewStore.PreviewLayer</c>.
        /// </summary>
        public const string PreviewLayer = "MCP-PREVIEW";

        /// <summary>Prefijo obligatorio de las capas sobre las que escriben/borran los métodos de auditoría.</summary>
        public const string McpLayerPrefix = "MCP-";

        /// <summary>Prefijo del nombre del pipe (sin "\\.\pipe\").</summary>
        public const string PipePrefix = "mcp-autocad-";

        /// <summary>Máximo de instancias simultáneas del pipe (protocol.md §1.1).</summary>
        public const int MaxPipeInstances = 4;

        /// <summary>Tamaño máximo de un mensaje: 16 MiB (protocol.md §1.2).</summary>
        public const int MaxMessageBytes = 16 * 1024 * 1024;

        /// <summary>
        /// Tiempo máximo para que el hilo principal TOME una petición (protocol.md §3.2).
        /// 1500 ms para que BUSY "cola" llegue en &lt; 2 s, como exige PLAN.md §5 Fase 0
        /// (con 2000 ms la respuesta medida era 2002 ms).
        /// </summary>
        public const int QueueTakeTimeoutMs = 1500;

        /// <summary>Tiempo máximo de ejecución en el hilo principal (protocol.md §3.7).</summary>
        public const int MainThreadTimeoutMs = 10000;

        /// <summary>
        /// Espera de la respuesta de vista_png, insertar_export y auditar_archivo (§3, regla 7): la captura de un plano con un
        /// export grande superó los 10 s en el gate, y leer ese export (o copiar y leer el archivo auditado) también puede. Solo alarga la espera del hilo del pipe: el hilo
        /// principal está ocupado igual, y al vencer no se cancela.
        /// </summary>
        public const int SlowToolTimeoutMs = 60000;

        /// <summary>Capa por defecto de las entidades creadas por el MCP (protocol.md §5.4, §5D.0).</summary>
        public const string DefaultLayer = "MCP-DIBUJO";

        /// <summary>Longitud máxima (caracteres) de un texto del cliente en texto/mtext (protocol.md §5D.0, R7).</summary>
        public const int MaxUserTextLength = 2000;

        /// <summary>Longitud máxima (caracteres) del valor de un atributo en insertar_bloque (§5D.5).</summary>
        public const int MaxAttributeLength = 500;

        /// <summary>Máximo de handles por llamada en mover/copiar/rotar/escalar/borrar (§5D.8).</summary>
        public const int MaxHandlesPerCall = 500;

        /// <summary>Subcarpeta (junto a la DLL) con los DWG de la biblioteca de bloques (§5D.5): biblioteca\.</summary>
        public const string LibraryFolder = "biblioteca";

        /// <summary>Construye el nombre del pipe: mcp-autocad-&lt;usuario&gt;-&lt;PID&gt;.</summary>
        public static string BuildPipeName(string userName, int pid) =>
            PipePrefix + userName + "-" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
