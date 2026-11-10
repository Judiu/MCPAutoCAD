"""Transporte de bytes hacia el plugin (protocol.md §1.1).

`Transport` es la interfaz que usa `PipeClient`; `Win32PipeTransport` es la
implementación real con pywin32 sobre `\\\\.\\pipe\\<nombre>`, con E/S
solapada (FILE_FLAG_OVERLAPPED) para poder aplicar timeouts de lectura y
escritura.
"""

from __future__ import annotations

import abc
import logging
import sys
import time

from .errors import NoInstanceError, PipeBrokenError, PipeTimeoutError

log = logging.getLogger(__name__)

CONNECT_TIMEOUT_S = 1.0  # protocol.md §6.1: conexión 1 s
READ_CHUNK = 64 * 1024

# Códigos Win32 relevantes.
ERROR_FILE_NOT_FOUND = 2
ERROR_BROKEN_PIPE = 109
ERROR_SEM_TIMEOUT = 121
ERROR_PIPE_BUSY = 231
ERROR_NO_DATA = 232
ERROR_PIPE_NOT_CONNECTED = 233
ERROR_MORE_DATA = 234
ERROR_OPERATION_ABORTED = 995
ERROR_IO_PENDING = 997

_BROKEN_CODES = {ERROR_BROKEN_PIPE, ERROR_NO_DATA, ERROR_PIPE_NOT_CONNECTED, ERROR_OPERATION_ABORTED}


class Transport(abc.ABC):
    """Canal de bytes bidireccional con el plugin.

    Contrato:
    - connect(): abre la conexión (idempotente si ya está abierta). Lanza
      NoInstanceError si el pipe no existe y PipeBrokenError si no se pudo abrir.
    - send(data): envía todos los bytes. PipeBrokenError si la conexión está rota.
    - recv(max_bytes, timeout): devuelve entre 1 y max_bytes bytes; lanza
      PipeTimeoutError si no llega nada en `timeout` s y PipeBrokenError si el
      otro extremo cerró.
    - close(): cierra la conexión; idempotente.
    """

    @abc.abstractmethod
    def connect(self) -> None: ...

    @abc.abstractmethod
    def send(self, data: bytes) -> None: ...

    @abc.abstractmethod
    def recv(self, max_bytes: int, timeout: float) -> bytes: ...

    @abc.abstractmethod
    def close(self) -> None: ...

    @property
    @abc.abstractmethod
    def is_connected(self) -> bool: ...


class Win32PipeTransport(Transport):
    """Cliente de named pipe con pywin32 (solo Windows)."""

    def __init__(self, pipe_path: str, connect_timeout: float = CONNECT_TIMEOUT_S) -> None:
        if sys.platform != "win32":  # pragma: no cover - solo Windows
            raise RuntimeError("Win32PipeTransport solo funciona en Windows.")
        self.pipe_path = pipe_path
        self.connect_timeout = connect_timeout
        self._handle: object | None = None

    # -- conexión ---------------------------------------------------------
    @property
    def is_connected(self) -> bool:
        return self._handle is not None

    def connect(self) -> None:
        if self._handle is not None:
            return
        import pywintypes
        import win32file
        import win32pipe

        deadline = time.monotonic() + self.connect_timeout
        while True:
            try:
                self._handle = win32file.CreateFile(
                    self.pipe_path,
                    win32file.GENERIC_READ | win32file.GENERIC_WRITE,
                    0,  # sin compartir
                    None,
                    win32file.OPEN_EXISTING,
                    win32file.FILE_FLAG_OVERLAPPED,
                    None,
                )
                log.debug("Conectado a %s", self.pipe_path)
                return
            except pywintypes.error as exc:
                code = exc.winerror
                if code == ERROR_FILE_NOT_FOUND:
                    raise NoInstanceError() from exc
                if code != ERROR_PIPE_BUSY:
                    raise PipeBrokenError(f"CreateFile falló: {exc}") from exc
            # ERROR_PIPE_BUSY: todas las instancias ocupadas; esperar hasta agotar 1 s.
            remaining_ms = int((deadline - time.monotonic()) * 1000)
            if remaining_ms <= 0:
                raise PipeBrokenError("El pipe sigue ocupado (ERROR_PIPE_BUSY) tras el plazo de conexión.")
            try:
                win32pipe.WaitNamedPipe(self.pipe_path, remaining_ms)
            except pywintypes.error as exc:
                if exc.winerror == ERROR_FILE_NOT_FOUND:
                    raise NoInstanceError() from exc
                if exc.winerror == ERROR_SEM_TIMEOUT:
                    raise PipeBrokenError("El pipe sigue ocupado (ERROR_PIPE_BUSY) tras el plazo de conexión.") from exc
                # Otros errores: reintentar CreateFile mientras quede plazo.

    def close(self) -> None:
        handle, self._handle = self._handle, None
        if handle is None:
            return
        try:
            import win32file

            try:
                win32file.CancelIo(handle)
            except Exception:  # noqa: BLE001 - mejor esfuerzo
                pass
            handle.Close()  # type: ignore[attr-defined]
        except Exception as exc:  # noqa: BLE001 - cerrar nunca debe propagar
            log.debug("Error al cerrar el pipe: %s", exc)

    # -- E/S ----------------------------------------------------------------
    def _require_handle(self) -> object:
        if self._handle is None:
            raise PipeBrokenError("El transporte no está conectado.")
        return self._handle

    def _wait_overlapped(self, handle: object, overlapped: object, timeout: float) -> int:
        """Espera la operación solapada; devuelve bytes transferidos o lanza error."""
        import pywintypes
        import win32event
        import win32file

        ms = max(0, int(timeout * 1000))
        rc = win32event.WaitForSingleObject(overlapped.hEvent, ms)  # type: ignore[attr-defined]
        if rc == win32event.WAIT_TIMEOUT:
            # Cancelar y cerrar el handle: tras un timeout la conexión queda desincronizada.
            try:
                win32file.CancelIo(handle)
                win32file.GetOverlappedResult(handle, overlapped, True)
            except pywintypes.error:
                pass
            self.close()
            raise PipeTimeoutError(timeout)
        try:
            return int(win32file.GetOverlappedResult(handle, overlapped, True))
        except pywintypes.error as exc:
            self.close()
            if exc.winerror == ERROR_MORE_DATA:  # pragma: no cover - solo en modo mensaje
                raise PipeBrokenError("ERROR_MORE_DATA inesperado en pipe en modo byte.") from exc
            raise PipeBrokenError(f"Fallo de E/S en el pipe: {exc}") from exc

    def send(self, data: bytes) -> None:
        import pywintypes
        import win32event
        import win32file

        handle = self._require_handle()
        view = memoryview(data)
        while view:
            overlapped = pywintypes.OVERLAPPED()
            overlapped.hEvent = win32event.CreateEvent(None, True, False, None)
            try:
                win32file.WriteFile(handle, view.tobytes(), overlapped)
            except pywintypes.error as exc:
                self.close()
                raise PipeBrokenError(f"WriteFile falló: {exc}") from exc
            # La escritura la absorbe el buffer del pipe; si el plugin no lee en el
            # plazo de conexión consideramos la conexión rota.
            try:
                written = self._wait_overlapped(handle, overlapped, max(self.connect_timeout, 5.0))
            except PipeTimeoutError as exc:
                raise PipeBrokenError("La escritura en el pipe no se completó.") from exc
            if written <= 0:
                self.close()
                raise PipeBrokenError("WriteFile no escribió bytes.")
            view = view[written:]

    def recv(self, max_bytes: int, timeout: float) -> bytes:
        import pywintypes
        import win32event
        import win32file

        handle = self._require_handle()
        overlapped = pywintypes.OVERLAPPED()
        overlapped.hEvent = win32event.CreateEvent(None, True, False, None)
        buffer = win32file.AllocateReadBuffer(max(1, min(max_bytes, READ_CHUNK)))
        try:
            win32file.ReadFile(handle, buffer, overlapped)
        except pywintypes.error as exc:
            self.close()
            if exc.winerror in _BROKEN_CODES:
                raise PipeBrokenError(f"ReadFile: el plugin cerró la conexión ({exc.winerror}).") from exc
            raise PipeBrokenError(f"ReadFile falló: {exc}") from exc
        nbytes = self._wait_overlapped(handle, overlapped, timeout)
        if nbytes == 0:
            self.close()
            raise PipeBrokenError("ReadFile devolvió 0 bytes (fin de conexión).")
        return bytes(buffer[:nbytes])
