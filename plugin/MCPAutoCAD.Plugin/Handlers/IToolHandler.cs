using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Una herramienta del manifest (= un método JSON-RPC). Los handlers con <see cref="RunsOnPipeThread"/> = false
    /// se ejecutan en el hilo principal de AutoCAD; <see cref="ValidateParams"/> siempre se llama en el hilo del pipe,
    /// antes de encolar, y no puede tocar la API de AutoCAD.
    /// </summary>
    internal interface IToolHandler
    {
        /// <summary>Nombre exacto del método JSON-RPC / herramienta MCP (protocol.md §5).</summary>
        string Name { get; }

        /// <summary>Descripción en español para el modelo (1–3 frases).</summary>
        string Description { get; }

        /// <summary>true → NO_DOC si no hay documento activo.</summary>
        bool RequiresDocument { get; }

        /// <summary>true si modifica el dibujo.</summary>
        bool IsWrite { get; }

        /// <summary>true para ping y tools/manifest: se responden en el hilo del pipe sin tocar AutoCAD.</summary>
        bool RunsOnPipeThread { get; }

        /// <summary>
        /// true SOLO para cancelar_comando (0.8.4): el despachador no responde BUSY aunque haya un comando activo
        /// (CMDACTIVE ≠ 0). Cualquier otro handler debe dejarlo en false.
        /// </summary>
        bool RunsDuringCommand => false;

        /// <summary>Espera máxima de la respuesta del hilo principal, en ms (§3, regla 7). vista_png e insertar_export la amplían.</summary>
        int MainThreadTimeoutMs => PluginInfo.MainThreadTimeoutMs;

        /// <summary>Qué hacer si se agota esa espera con estos parámetros; null = nada específico.</summary>
        string? TimeoutHint(JsonElement parameters) => null;

        /// <summary>JSON Schema draft 2020-12 de los parámetros (type object, additionalProperties false).</summary>
        JsonElement InputSchema { get; }

        /// <summary>JSON Schema informativo del resultado, o null.</summary>
        JsonElement? OutputSchema { get; }

        /// <summary>Validación equivalente al InputSchema. Lanza <see cref="ToolException"/> INVALID_PARAMS.</summary>
        void ValidateParams(JsonElement parameters);

        /// <summary>
        /// Ejecuta la herramienta. Devuelve el objeto <c>result</c> (se serializa como objeto JSON), o un
        /// <c>System.Threading.Tasks.Task&lt;object&gt;</c> si la escritura se completa de forma diferida
        /// (ruta UNDO _BE/_E en contexto de comando).
        /// </summary>
        object Execute(JsonElement parameters, ExecutionContext ctx);
    }

    /// <summary>Contexto de ejecución de un handler (documento activo en el momento de ejecutar).</summary>
    internal sealed class ExecutionContext
    {
        public ExecutionContext(int pid, Document? document, BusyState busy, int cmdActive)
        {
            Pid = pid;
            Document = document;
            Busy = busy;
            CmdActive = cmdActive;
        }

        public int Pid { get; }

        /// <summary>MdiActiveDocument en el momento de ejecutar (null solo en handlers que no requieren documento).</summary>
        public Document? Document { get; }

        public BusyState Busy { get; }

        /// <summary>Valor de CMDACTIVE leído justo antes de ejecutar (0 en hilo del pipe).</summary>
        public int CmdActive { get; }

        /// <summary>
        /// true si este trabajo ENTRA ANIDADO dentro de otro que aún está ejecutándose (p. ej. mientras DocumentCollection.Open
        /// bombea mensajes). Solo lo fija el despachador, y solo para handlers exentos (estado, cancelar_comando).
        /// Estado lo trata como modo degradado: el dibujo puede estar a medio abrir.
        /// </summary>
        public bool Anidado { get; init; }

        /// <summary>Documento activo, garantizado no nulo para handlers con RequiresDocument.</summary>
        public Document RequireDocument() => Document ?? throw ToolException.NoDoc();
    }

    /// <summary>Utilidades de schema compartidas por los handlers.</summary>
    internal static class Schema
    {
        public static JsonElement Parse(string json)
        {
            using JsonDocument d = JsonDocument.Parse(json);
            return d.RootElement.Clone();
        }
    }
}
