"""Framing de protocol.md §1.2: [uint32 LE longitud N][N bytes JSON UTF-8 sin BOM]."""

from __future__ import annotations

import json
import struct
from typing import Any

MAX_FRAME_BYTES = 16 * 1024 * 1024  # 16 MiB (16 777 216), protocol.md §1.2
HEADER_SIZE = 4
_HEADER = struct.Struct("<I")


class FramingError(Exception):
    """Mensaje que viola el framing (tamaño excesivo, JSON inválido, BOM...)."""


def encode_frame(obj: Any) -> bytes:
    """Serializa obj como JSON UTF-8 (ensure_ascii=False) con el prefijo de longitud."""
    try:
        payload = json.dumps(obj, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    except (TypeError, ValueError) as exc:
        raise FramingError(f"No se puede serializar el mensaje a JSON: {exc}") from exc
    if len(payload) > MAX_FRAME_BYTES:
        raise FramingError(f"El mensaje ocupa {len(payload)} bytes; el máximo es {MAX_FRAME_BYTES} (16 MiB).")
    return _HEADER.pack(len(payload)) + payload


def decode_payload(payload: bytes) -> Any:
    """Decodifica el cuerpo de un frame (sin prefijo) a un objeto Python."""
    if payload.startswith(b"\xef\xbb\xbf"):
        raise FramingError("El JSON del mensaje empieza con BOM UTF-8 (prohibido por el contrato).")
    try:
        return json.loads(payload.decode("utf-8"))
    except UnicodeDecodeError as exc:
        raise FramingError(f"El mensaje no es UTF-8 válido: {exc}") from exc
    except json.JSONDecodeError as exc:
        raise FramingError(f"El mensaje no es JSON válido: {exc}") from exc


class FrameReader:
    """Acumula bytes recibidos y extrae mensajes completos ya decodificados."""

    def __init__(self, max_frame_bytes: int = MAX_FRAME_BYTES) -> None:
        self._buffer = bytearray()
        self._max = max_frame_bytes

    def feed(self, data: bytes) -> list[Any]:
        """Añade bytes y devuelve la lista (posiblemente vacía) de mensajes completos."""
        self._buffer.extend(data)
        messages: list[Any] = []
        while True:
            message = self._next()
            if message is _INCOMPLETE:
                return messages
            messages.append(message)

    def _next(self) -> Any:
        if len(self._buffer) < HEADER_SIZE:
            return _INCOMPLETE
        (length,) = _HEADER.unpack_from(self._buffer, 0)
        if length > self._max:
            raise FramingError(f"Mensaje anunciado de {length} bytes; el máximo es {self._max} (16 MiB).")
        end = HEADER_SIZE + length
        if len(self._buffer) < end:
            return _INCOMPLETE
        payload = bytes(self._buffer[HEADER_SIZE:end])
        del self._buffer[:end]
        return decode_payload(payload)

    @property
    def pending_bytes(self) -> int:
        """Bytes acumulados que aún no forman un mensaje completo."""
        return len(self._buffer)

    def clear(self) -> None:
        self._buffer.clear()


_INCOMPLETE: Any = object()
