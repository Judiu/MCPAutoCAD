using System;
using System.Collections.Generic;

namespace MCPAutoCAD.Plugin.Bridge
{
    /// <summary>Códigos simbólicos de error (protocol.md §6) y su código numérico JSON-RPC.</summary>
    internal static class ErrorCodes
    {
        public const string ParseError = "PARSE_ERROR";
        public const string MethodNotFound = "METHOD_NOT_FOUND";
        public const string InvalidParams = "INVALID_PARAMS";
        public const string Internal = "INTERNAL";
        public const string Busy = "BUSY";
        public const string NoDoc = "NO_DOC";
        public const string NotFound = "NOT_FOUND";
        public const string UnitsUnknown = "UNITS_UNKNOWN";

        /// <summary>Fase 4A (protocol.md §6): la geometría pedida no es construible (inglete imposible, autointersección, área 0…).</summary>
        public const string InvalidGeometry = "INVALID_GEOMETRY";

        /// <summary>Fallo 25 (0.10.1): documento_esperado no es el documento activo; no se ejecutó nada.</summary>
        public const string WrongDoc = "WRONG_DOC";

        public static int Numeric(string code) => code switch
        {
            ParseError => -32700,
            MethodNotFound => -32601,
            InvalidParams => -32602,
            Internal => -32603,
            Busy => -32001,
            NoDoc => -32002,
            NotFound => -32003,
            UnitsUnknown => -32004,
            InvalidGeometry => -32005,
            WrongDoc => -32006,
            _ => -32603,
        };
    }

    /// <summary>
    /// Error de herramienta que se convierte en una respuesta JSON-RPC de error.
    /// <see cref="ExtraData"/> son los campos adicionales de error.data (además de "code").
    /// </summary>
    internal sealed class ToolException : Exception
    {
        public ToolException(string code, string message, IReadOnlyDictionary<string, object?>? extraData = null)
            : base(message)
        {
            Code = code;
            ExtraData = extraData ?? new Dictionary<string, object?>();
        }

        public string Code { get; }

        public IReadOnlyDictionary<string, object?> ExtraData { get; }

        // ---- Fábricas con los mensajes de referencia de protocol.md §6 ----

        public static ToolException InvalidParams(string detail, IDictionary<string, object?>? extra = null)
        {
            var data = new Dictionary<string, object?> { ["detalle"] = detail };
            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    data[kv.Key] = kv.Value;
                }
            }

            return new ToolException(ErrorCodes.InvalidParams, "Parámetros inválidos: " + detail, data);
        }

        public static ToolException ParseError(string detail) =>
            new ToolException(
                ErrorCodes.ParseError,
                "Petición inválida: no se pudo interpretar el mensaje JSON-RPC.",
                new Dictionary<string, object?> { ["detalle"] = detail });

        public static ToolException MethodNotFound(string method) =>
            new ToolException(
                ErrorCodes.MethodNotFound,
                "Método desconocido: " + method,
                new Dictionary<string, object?> { ["metodo"] = method });

        public static ToolException BusyQueue() =>
            new ToolException(
                ErrorCodes.Busy,
                "AutoCAD está ocupado (posible diálogo abierto). Reintente cuando el usuario termine.",
                new Dictionary<string, object?> { ["motivo"] = "cola", ["comando"] = "" });

        public static ToolException BusyCommand(string command)
        {
            string message = command.Length > 0
                ? "Hay un comando activo en AutoCAD: " + command + ". Reintente cuando termine."
                : "Hay un comando o diálogo activo en AutoCAD. Reintente cuando termine.";
            return new ToolException(
                ErrorCodes.Busy,
                message,
                new Dictionary<string, object?> { ["motivo"] = "comando", ["comando"] = command });
        }

        public static ToolException NoDoc() =>
            new ToolException(ErrorCodes.NoDoc, "No hay ningún documento abierto en AutoCAD.");

        public static ToolException LayerNotFound(string layer) =>
            new ToolException(
                ErrorCodes.NotFound,
                "La capa '" + layer + "' no existe en el dibujo.",
                new Dictionary<string, object?> { ["capa"] = layer });

        public static ToolException EntityNotFound(string handle) =>
            new ToolException(
                ErrorCodes.NotFound,
                "No existe ninguna entidad con handle '" + handle + "'.",
                new Dictionary<string, object?> { ["handle"] = handle });

        /// <summary>
        /// NOT_FOUND de layout. Con <paramref name="disponibles"/> (fallo 22, 0.10.0) el mensaje y data.disponibles listan los
        /// espacios que sí existen, para que el cliente no tenga que adivinar el nombre de la pestaña.
        /// </summary>
        public static ToolException LayoutNotFound(string name, IReadOnlyList<string>? disponibles = null) =>
            new ToolException(
                ErrorCodes.NotFound,
                "El layout '" + name + "' no existe en el dibujo."
                + (disponibles == null || disponibles.Count == 0
                    ? ""
                    : " Disponibles: " + string.Join(", ", disponibles) + " (nombre de la pestaña, no el título del plano; listar_layouts da sus viewports)."),
                new Dictionary<string, object?> { ["espacio"] = name, ["disponibles"] = disponibles });

        public static ToolException BlockNotFound(string name) =>
            new ToolException(
                ErrorCodes.NotFound,
                "El bloque '" + name + "' no existe en el dibujo.",
                new Dictionary<string, object?> { ["bloque"] = name });

        public static ToolException UnitsUnknown() =>
            new ToolException(ErrorCodes.UnitsUnknown, "El dibujo no declara unidades (INSUNITS=0).");

        /// <summary>INVALID_GEOMETRY (§6, Fase 4A): message = "Geometría no construible: &lt;detalle&gt;", data.detalle = detalle.</summary>
        public static ToolException InvalidGeometry(string detail, IDictionary<string, object?>? extra = null)
        {
            var data = new Dictionary<string, object?> { ["detalle"] = detail };
            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    data[kv.Key] = kv.Value;
                }
            }

            return new ToolException(ErrorCodes.InvalidGeometry, "Geometría no construible: " + detail, data);
        }

        /// <summary>NOT_FOUND de una previsualización (§5E.5): data.preview_id.</summary>
        public static ToolException PreviewNotFound(string previewId) =>
            new ToolException(
                ErrorCodes.NotFound,
                MCPAutoCAD.GeometryCore.Gen.PreviewRegistry.NotFoundMessage(previewId),
                new Dictionary<string, object?> { ["preview_id"] = previewId });

        public static ToolException Timeout(int segundos, string? consejo) =>
            new ToolException(
                ErrorCodes.Internal,
                "La operación superó el tiempo máximo (" + segundos.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " s) en el hilo principal de AutoCAD; puede completarse más tarde." + (consejo == null ? "" : " " + consejo),
                new Dictionary<string, object?> { ["detalle"] = "timeout" });

        /// <summary>INTERNAL a partir de una excepción no prevista: detalle = tipo + mensaje, sin stack.</summary>
        public static ToolException FromUnexpected(Exception ex)
        {
            string detail = ex.GetType().FullName + ": " + ex.Message;
            // Fallo 24 (verificación 2026-11-10): el servidor solo muestra el mensaje; sin la causa, el agente no puede
            // diagnosticar. Se añade el tipo y el mensaje de la excepción (recortado), sin la pila.
            string causa = ex.GetType().Name + ": " + ex.Message;
            if (causa.Length > 300)
            {
                causa = causa.Substring(0, 300) + "…";
            }

            return new ToolException(
                ErrorCodes.Internal,
                "Error interno del plugin de AutoCAD. Causa: " + causa + " (detalle en plugin.log).",
                new Dictionary<string, object?> { ["detalle"] = detail });
        }
    }
}
