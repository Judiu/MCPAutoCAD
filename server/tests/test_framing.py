from __future__ import annotations

import json
import struct

import pytest

from mcp_autocad.framing import MAX_FRAME_BYTES, FrameReader, FramingError, decode_payload, encode_frame


def test_encode_prefijo_uint32_le_y_utf8_sin_bom() -> None:
    obj = {"jsonrpc": "2.0", "id": 1, "method": "ping", "params": {}}
    frame = encode_frame(obj)
    (length,) = struct.unpack("<I", frame[:4])
    assert length == len(frame) - 4
    assert not frame[4:].startswith(b"\xef\xbb\xbf")
    assert json.loads(frame[4:].decode("utf-8")) == obj


def test_acentos_y_enie_sin_escapar() -> None:
    obj = {"capa": "MUROS-AÑO", "mensaje": "No hay ningún documento abierto", "u": "µm Å"}
    frame = encode_frame(obj)
    body = frame[4:]
    assert "AÑO".encode() in body  # ensure_ascii=False: no Ñ
    assert b"\\u" not in body
    assert struct.unpack("<I", frame[:4])[0] == len(body)  # longitud en bytes, no en caracteres
    assert FrameReader().feed(frame) == [obj]


def test_roundtrip_varios_mensajes_en_un_bloque() -> None:
    msgs = [{"id": i, "v": "ñ" * i} for i in range(5)]
    data = b"".join(encode_frame(m) for m in msgs)
    assert FrameReader().feed(data) == msgs


def test_mensaje_partido_byte_a_byte() -> None:
    obj = {"id": 7, "result": {"nombre": "Ñandú", "lista": list(range(50))}}
    data = encode_frame(obj) + encode_frame({"id": 8})
    reader = FrameReader()
    out: list[object] = []
    for i in range(len(data)):
        out.extend(reader.feed(data[i : i + 1]))
    assert out == [obj, {"id": 8}]
    assert reader.pending_bytes == 0


def test_prefijo_partido_entre_recv() -> None:
    frame = encode_frame({"a": 1})
    reader = FrameReader()
    assert reader.feed(frame[:2]) == []
    assert reader.feed(frame[2:5]) == []
    assert reader.feed(frame[5:]) == [{"a": 1}]


def test_limite_16_mib_en_lectura() -> None:
    reader = FrameReader()
    with pytest.raises(FramingError):
        reader.feed(struct.pack("<I", MAX_FRAME_BYTES + 1))


def test_limite_exacto_permitido_en_cabecera() -> None:
    reader = FrameReader()
    # Cabecera con exactamente 16 MiB: aún incompleto, no es error.
    assert reader.feed(struct.pack("<I", MAX_FRAME_BYTES)) == []


def test_limite_16_mib_en_escritura() -> None:
    with pytest.raises(FramingError):
        encode_frame({"x": "a" * (MAX_FRAME_BYTES + 1)})


def test_json_invalido_y_bom() -> None:
    with pytest.raises(FramingError):
        decode_payload(b"{no json")
    with pytest.raises(FramingError):
        decode_payload(b"\xef\xbb\xbf{}")
    with pytest.raises(FramingError):
        decode_payload(b"\xff\xfe")


def test_nan_rechazado() -> None:
    with pytest.raises(FramingError):
        encode_frame({"x": float("nan")})
