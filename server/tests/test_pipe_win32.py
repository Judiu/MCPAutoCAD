"""Prueba end-to-end de Win32PipeTransport contra un pipe real creado con pywin32."""

from __future__ import annotations

import os
import sys
import threading
import time
import uuid
from collections.abc import Callable
from typing import Any

import pytest

pytestmark = pytest.mark.skipif(sys.platform != "win32", reason="solo Windows")

if sys.platform == "win32":
    import pywintypes
    import win32file
    import win32pipe

from mcp_autocad.client import PipeClient
from mcp_autocad.errors import NoInstanceError, PipeBrokenError, PipeTimeoutError
from mcp_autocad.framing import FrameReader, encode_frame
from mcp_autocad.transport import Win32PipeTransport

Behavior = Callable[[dict[str, Any]], dict[str, Any] | str | None]


def pipe_name() -> str:
    return f"\\\\.\\pipe\\mcp-autocad-pytest{uuid.uuid4().hex[:8]}-{os.getpid()}"


class PipeServer:
    """Servidor de pipe mínimo (una conexión) que atiende frames con `behavior`.

    behavior(request) -> dict (respuesta), None (no responder) o "close" (cerrar).
    """

    def __init__(self, name: str, behavior: Behavior, max_instances: int = 4) -> None:
        self.name = name
        self.behavior = behavior
        self.handle = win32pipe.CreateNamedPipe(
            name,
            win32pipe.PIPE_ACCESS_DUPLEX,
            win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
            max_instances,
            65536,
            65536,
            0,
            None,
        )
        self.requests: list[dict[str, Any]] = []
        self.connected = threading.Event()
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    def _serve(self) -> None:
        try:
            try:
                win32pipe.ConnectNamedPipe(self.handle, None)
            except pywintypes.error as exc:
                if exc.winerror != 535:  # ERROR_PIPE_CONNECTED
                    return
            self.connected.set()
            reader = FrameReader()
            while True:
                try:
                    _, data = win32file.ReadFile(self.handle, 65536)
                except pywintypes.error:
                    return
                for req in reader.feed(bytes(data)):
                    self.requests.append(req)
                    reply = self.behavior(req)
                    if reply == "close":
                        return
                    if isinstance(reply, dict):
                        win32file.WriteFile(self.handle, encode_frame(reply))
        finally:
            try:
                win32pipe.DisconnectNamedPipe(self.handle)
            except pywintypes.error:
                pass
            self.handle.Close()

    def join(self) -> None:
        self.thread.join(timeout=5)
        assert not self.thread.is_alive(), "el hilo del servidor de pipe no terminó"


def echo_ping(req: dict[str, Any]) -> dict[str, Any]:
    return {"jsonrpc": "2.0", "id": req["id"], "result": {"pid": os.getpid(), "pong": True, "eco": "Ñandú áéíóú"}}


def test_roundtrip_real() -> None:
    name = pipe_name()
    server = PipeServer(name, echo_ping)
    client = PipeClient(lambda: Win32PipeTransport(name), expected_pid=os.getpid())
    try:
        r1 = client.call("ping")
        r2 = client.call("ping")
        assert r1["pong"] is True and r1["eco"] == "Ñandú áéíóú"
        assert r2["pid"] == os.getpid()
        assert [r["id"] for r in server.requests] == [1, 2]
    finally:
        client.close()
        server.join()


def test_mensaje_grande_varios_read() -> None:
    name = pipe_name()
    big = "ñ" * 300_000  # 600 KB en UTF-8, más que el buffer de 64 KiB

    def behavior(req: dict[str, Any]) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": req["id"], "result": {"pid": os.getpid(), "dato": big}}

    server = PipeServer(name, behavior)
    client = PipeClient(lambda: Win32PipeTransport(name))
    try:
        assert client.call("estado")["dato"] == big
    finally:
        client.close()
        server.join()


def test_timeout_real() -> None:
    name = pipe_name()
    server = PipeServer(name, lambda req: None)
    client = PipeClient(lambda: Win32PipeTransport(name), timeout=0.5)
    try:
        start = time.monotonic()
        with pytest.raises(PipeTimeoutError) as err:
            client.call("estado")
        elapsed = time.monotonic() - start
        assert str(err.value) == "El plugin no respondió en 0.5 s."
        assert 0.4 <= elapsed < 3
    finally:
        client.close()
        server.join()  # el cliente cerró el handle al expirar: el servidor ve la desconexión


def test_pipe_inexistente() -> None:
    transport = Win32PipeTransport(pipe_name())
    with pytest.raises(NoInstanceError) as err:
        transport.connect()
    assert str(err.value) == "AutoCAD no está abierto o el plugin no cargó."


def test_servidor_cierra_conexion() -> None:
    name = pipe_name()
    server = PipeServer(name, lambda req: "close")
    client = PipeClient(lambda: Win32PipeTransport(name))
    try:
        with pytest.raises(PipeBrokenError):
            # Escritura: no se reintenta; el pipe se cierra sin responder.
            client.call("linea", {"inicio": [0, 0], "fin": [1, 0]})
    finally:
        client.close()
        server.join()


def test_pipe_ocupado() -> None:
    name = pipe_name()
    server = PipeServer(name, echo_ping, max_instances=1)
    first = Win32PipeTransport(name)
    first.connect()
    assert server.connected.wait(2)
    try:
        start = time.monotonic()
        with pytest.raises(PipeBrokenError):
            Win32PipeTransport(name).connect()
        assert time.monotonic() - start < 2.5
    finally:
        first.close()
        server.join()
