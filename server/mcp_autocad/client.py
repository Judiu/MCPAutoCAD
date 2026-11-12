"""Cliente JSON-RPC 2.0 sobre el transporte del pipe (protocol.md §2 y §6.1)."""

from __future__ import annotations

import logging
import os
import threading
import time
from collections.abc import Callable, Mapping
from typing import Any

from .errors import NoInstanceError, PipeBrokenError, PipeTimeoutError, PluginError, ProtocolError
from .framing import FrameReader, FramingError, encode_frame
from .transport import READ_CHUNK, Transport

log = logging.getLogger(__name__)

DEFAULT_TIMEOUT_S = 15.0
PING_TIMEOUT_S = 3.0
ENV_TIMEOUT = "MCP_AUTOCAD_TIMEOUT"

# Métodos que se responden desde el hilo del pipe (§3.1): plazo corto.
FAST_METHODS = {"ping": PING_TIMEOUT_S}
# protocol.md §3, regla 7: el plugin espera 60 s a estas herramientas; el servidor, algo más para recibir su respuesta o su error.
# cargar_lisp: el plugin espera hasta 120 s la aprobación del usuario en un diálogo de AutoCAD (plugin 0.8.5).
# importar_geometria (0.10.1): leer un DXF de ~25.000 entidades y clonarlo tarda más que el resto; el plugin espera 120 s.
SLOW_METHODS = {"vista_png": 75.0, "insertar_export": 75.0, "auditar_archivo": 75.0, "cargar_lisp": 150.0, "crear_layout": 75.0, "configurar_layout": 75.0, "guardar": 75.0, "abrir_documento": 75.0, "muros_desde_lineas": 75.0, "reemplazar_texto": 75.0, "importar_geometria": 150.0, "nuevo_documento": 75.0, "mover_por_filtro": 75.0}


def timeout_from_env(environ: Mapping[str, str] | None = None) -> float:
    """Timeout por defecto: MCP_AUTOCAD_TIMEOUT (segundos, > 0) o 15 s."""
    env = environ if environ is not None else os.environ
    raw = env.get(ENV_TIMEOUT)
    if raw is None or raw.strip() == "":
        return DEFAULT_TIMEOUT_S
    try:
        value = float(raw.strip().replace(",", "."))
    except ValueError:
        log.warning("%s=%r no es un número; se usa %s s.", ENV_TIMEOUT, raw, DEFAULT_TIMEOUT_S)
        return DEFAULT_TIMEOUT_S
    if not (value > 0) or value == float("inf"):
        log.warning("%s=%r debe ser > 0; se usa %s s.", ENV_TIMEOUT, raw, DEFAULT_TIMEOUT_S)
        return DEFAULT_TIMEOUT_S
    return value


class _SendFailed(Exception):
    """Interno: la petición no llegó a enviarse completa (se puede reintentar)."""

    def __init__(self, cause: PipeBrokenError) -> None:
        super().__init__(str(cause))
        self.cause = cause


class PipeClient:
    """Cliente secuencial (una petición en vuelo por conexión, §1.2).

    - id entero incremental por petición; la respuesta se correlaciona por id.
    - Si el transporte está roto, reconecta una vez de forma transparente.
      El reintento se hace siempre si la petición no llegó a enviarse; si se
      rompió esperando la respuesta, solo cuando `idempotent=True` (para no
      duplicar escrituras).
    - Tras un timeout se cierra la conexión (podría llegar una respuesta tardía).
    """

    def __init__(
        self,
        transport_factory: Callable[[], Transport],
        timeout: float | None = None,
        expected_pid: int | None = None,
        ping_timeout: float = PING_TIMEOUT_S,
    ) -> None:
        self._factory = transport_factory
        self.timeout = float(timeout) if timeout is not None else timeout_from_env()
        self.ping_timeout = float(ping_timeout)
        self.expected_pid = expected_pid
        self._transport: Transport | None = None
        self._reader = FrameReader()
        self._next_id = 1
        self._lock = threading.Lock()

    # -- utilidades ---------------------------------------------------------
    def timeout_for(self, method: str) -> float:
        if method in FAST_METHODS:
            return self.ping_timeout
        return max(self.timeout, SLOW_METHODS.get(method, 0.0))

    def _allocate_id(self) -> int:
        request_id = self._next_id
        self._next_id += 1
        return request_id

    def _ensure_transport(self) -> Transport:
        if self._transport is None or not self._transport.is_connected:
            if self._transport is not None:
                self._safe_close()
            transport = self._factory()
            transport.connect()
            self._transport = transport
            self._reader = FrameReader()
        return self._transport

    def _safe_close(self) -> None:
        transport, self._transport = self._transport, None
        self._reader = FrameReader()
        if transport is not None:
            try:
                transport.close()
            except Exception as exc:  # noqa: BLE001
                log.debug("Error al cerrar transporte: %s", exc)

    def close(self) -> None:
        with self._lock:
            self._safe_close()

    # -- API ----------------------------------------------------------------
    def call(
        self,
        method: str,
        params: Mapping[str, Any] | None = None,
        timeout: float | None = None,
        idempotent: bool = False,
    ) -> dict[str, Any]:
        """Envía la petición y devuelve `result` (dict) o lanza el error del contrato."""
        effective_timeout = float(timeout) if timeout is not None else self.timeout_for(method)
        request_params = dict(params) if params else {}
        with self._lock:
            attempts = 2
            for attempt in range(1, attempts + 1):
                try:
                    return self._call_once(method, request_params, effective_timeout)
                except _SendFailed as exc:
                    self._safe_close()
                    if attempt == attempts:
                        raise exc.cause from None
                    log.info("Conexión rota al enviar %s; reconectando (%s).", method, exc.cause.detail)
                except PipeBrokenError as exc:
                    self._safe_close()
                    if attempt == attempts or not idempotent:
                        raise
                    log.info("Conexión rota esperando %s; reintento idempotente (%s).", method, exc.detail)
                except (PipeTimeoutError, ProtocolError, NoInstanceError):
                    self._safe_close()
                    raise
            raise AssertionError("inalcanzable")  # pragma: no cover

    def _call_once(self, method: str, params: dict[str, Any], timeout: float) -> dict[str, Any]:
        deadline = time.monotonic() + timeout
        request_id = self._allocate_id()
        request = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
        try:
            frame = encode_frame(request)
        except FramingError as exc:
            raise ProtocolError(f"petición no serializable: {exc}") from exc

        # Fase de envío: un fallo aquí significa que el plugin no recibió nada útil.
        try:
            transport = self._ensure_transport()
            transport.send(frame)
        except PipeBrokenError as exc:
            raise _SendFailed(exc) from exc

        # Fase de espera de respuesta.
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise PipeTimeoutError(timeout)
            try:
                chunk = transport.recv(READ_CHUNK, remaining)
            except PipeTimeoutError:
                raise PipeTimeoutError(timeout) from None
            try:
                messages = self._reader.feed(chunk)
            except FramingError as exc:
                raise ProtocolError(str(exc)) from exc
            for message in messages:
                result = self._match_response(message, request_id)
                if result is not None:
                    return result

    def _match_response(self, message: Any, request_id: int) -> dict[str, Any] | None:
        if not isinstance(message, dict):
            raise ProtocolError("la respuesta no es un objeto JSON-RPC.")
        if message.get("id") != request_id:
            # Respuesta tardía de una petición anterior o id null (PARSE_ERROR ajeno).
            if message.get("id") is None and "error" in message:
                raise PluginError.from_rpc_error(message["error"])
            log.warning("Respuesta con id %r descartada (se esperaba %r).", message.get("id"), request_id)
            return None
        if message.get("jsonrpc") != "2.0":
            log.warning("Respuesta sin jsonrpc='2.0': %r", message.get("jsonrpc"))
        if "error" in message and message["error"] is not None:
            raise PluginError.from_rpc_error(message["error"])
        if "result" not in message:
            raise ProtocolError("respuesta sin 'result' ni 'error'.")
        result = message["result"]
        if not isinstance(result, dict):
            raise ProtocolError("'result' no es un objeto JSON.")
        self._check_pid(result)
        return result

    def _check_pid(self, result: dict[str, Any]) -> None:
        pid = result.get("pid")
        if not isinstance(pid, int) or isinstance(pid, bool):
            log.warning("La respuesta del plugin no incluye 'pid' entero (protocol.md §2.1).")
            return
        if self.expected_pid is not None and pid != self.expected_pid:
            log.warning("El plugin respondió con pid %s pero se esperaba %s.", pid, self.expected_pid)
