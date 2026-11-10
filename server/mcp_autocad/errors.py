"""Errores del servidor Python (protocol.md §6 y §6.1).

Los textos de NO_INSTANCE, PIPE_TIMEOUT y PIPE_BROKEN son los EXACTOS del
contrato. Los errores del plugin se formatean como "[<data.code>] <message>".
"""

from __future__ import annotations

from collections.abc import Iterable
from typing import Any

# Textos exactos de protocol.md §6.1.
NO_INSTANCE_TEXT = "AutoCAD no está abierto o el plugin no cargó."
NO_INSTANCE_AMBIGUOUS_TEXT = "Hay varias instancias de AutoCAD abiertas (PID: {pids}). Use seleccionar_instancia para elegir una."
NO_INSTANCE_PID_TEXT = "No hay ninguna instancia de AutoCAD con PID {pid}."
NO_INSTANCE_LIST_SUFFIX = " Instancias disponibles (PID): {pids}. Use seleccionar_instancia."
PIPE_TIMEOUT_TEXT = "El plugin no respondió en {seconds} s."
PIPE_BROKEN_TEXT = "Se perdió la conexión con AutoCAD (¿se cerró?)."

# Tabla error.code JSON-RPC -> data.code simbólico (protocol.md §6). Solo se usa
# como respaldo si el plugin omite data.code (el contrato lo exige).
RPC_CODE_TO_SYMBOLIC: dict[int, str] = {
    -32700: "PARSE_ERROR",
    -32601: "METHOD_NOT_FOUND",
    -32602: "INVALID_PARAMS",
    -32603: "INTERNAL",
    -32001: "BUSY",
    -32002: "NO_DOC",
    -32003: "NOT_FOUND",
    -32004: "UNITS_UNKNOWN",
    -32005: "INVALID_GEOMETRY",
    -32006: "WRONG_DOC",
}


def format_seconds(seconds: float) -> str:
    """Formatea segundos sin decimales superfluos: 15.0 -> "15", 2.5 -> "2.5"."""
    return f"{float(seconds):g}"


class McpAutocadError(Exception):
    """Base de todos los errores que el servidor devuelve como isError=True."""

    code: str = "INTERNAL"

    def __init__(self, message: str) -> None:
        super().__init__(message)
        self.message = message

    def __str__(self) -> str:
        return self.message


class NoInstanceError(McpAutocadError):
    """NO_INSTANCE: no hay instancia de AutoCAD utilizable (§1.3, §6.1).

    Tres casos con texto distinto: ninguna abierta; varias sin selección (pids);
    o se pidió un PID que no existe (requested_pid, con las disponibles en pids).
    """

    code = "NO_INSTANCE"

    def __init__(self, pids: Iterable[int] | None = None, requested_pid: int | None = None) -> None:
        self.pids: list[int] = sorted(pids) if pids else []
        self.requested_pid = requested_pid
        listed = ", ".join(str(p) for p in self.pids)
        if requested_pid is not None:
            text = NO_INSTANCE_PID_TEXT.format(pid=requested_pid)
            text += NO_INSTANCE_LIST_SUFFIX.format(pids=listed) if self.pids else " " + NO_INSTANCE_TEXT
        elif len(self.pids) > 1:
            text = NO_INSTANCE_AMBIGUOUS_TEXT.format(pids=listed)
        else:
            text = NO_INSTANCE_TEXT
        super().__init__(text)


class PipeTimeoutError(McpAutocadError):
    """PIPE_TIMEOUT: el plugin no respondió dentro del plazo."""

    code = "PIPE_TIMEOUT"

    def __init__(self, seconds: float) -> None:
        self.seconds = float(seconds)
        super().__init__(PIPE_TIMEOUT_TEXT.format(seconds=format_seconds(seconds)))


class PipeBrokenError(McpAutocadError):
    """PIPE_BROKEN: la conexión con el pipe se cortó o no pudo establecerse."""

    code = "PIPE_BROKEN"

    def __init__(self, detail: str | None = None) -> None:
        # El detalle técnico no forma parte del texto del contrato: va aparte (log).
        self.detail = detail
        super().__init__(PIPE_BROKEN_TEXT)


class ProtocolError(McpAutocadError):
    """El plugin envió algo que viola protocol.md (framing, JSON o forma de respuesta).

    Se reporta con el código INTERNAL (§1.2: "con INTERNAL (servidor)"); el texto
    deja claro que lo detectó el servidor, no que lo envió el plugin.
    """

    code = "INTERNAL"

    def __init__(self, detail: str) -> None:
        self.detail = detail
        super().__init__(f"[INTERNAL] Respuesta inválida del plugin (detectado por el servidor): {detail}")


class PluginError(McpAutocadError):
    """Error JSON-RPC devuelto por el plugin. str() = "[<CODE>] <mensaje>"."""

    def __init__(
        self,
        code_symbolic: str,
        message: str,
        data: dict[str, Any] | None = None,
        rpc_code: int | None = None,
    ) -> None:
        self.code = code_symbolic
        self.plugin_message = message
        self.data: dict[str, Any] = dict(data) if data else {}
        self.rpc_code = rpc_code
        super().__init__(f"[{code_symbolic}] {message}")

    @classmethod
    def from_rpc_error(cls, error: Any) -> PluginError:
        """Construye el error a partir del objeto "error" de una respuesta JSON-RPC."""
        if not isinstance(error, dict):
            return cls("INTERNAL", f"Error del plugin con formato inválido: {error!r}")
        rpc_code = error.get("code")
        rpc_code_int = rpc_code if isinstance(rpc_code, int) and not isinstance(rpc_code, bool) else None
        data = error.get("data")
        data_dict: dict[str, Any] = data if isinstance(data, dict) else {}
        symbolic = data_dict.get("code")
        if not isinstance(symbolic, str) or not symbolic:
            symbolic = RPC_CODE_TO_SYMBOLIC.get(rpc_code_int, "INTERNAL") if rpc_code_int is not None else "INTERNAL"
        message = error.get("message")
        if not isinstance(message, str) or not message:
            message = "Error sin mensaje del plugin."
        return cls(symbolic, message, data_dict, rpc_code_int)
