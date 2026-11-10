from __future__ import annotations

import logging

import pytest
from fake_plugin import FakePluginState, FakeTransport

from mcp_autocad.client import DEFAULT_TIMEOUT_S, PipeClient, timeout_from_env
from mcp_autocad.errors import NoInstanceError, PipeBrokenError, PipeTimeoutError, PluginError
from mcp_autocad.framing import FrameReader


def make(state: FakePluginState | None = None, **kw: object) -> tuple[PipeClient, FakePluginState, list[FakeTransport]]:
    st = state or FakePluginState()
    created: list[FakeTransport] = []

    def factory() -> FakeTransport:
        t = FakeTransport(st)
        created.append(t)
        return t

    client = PipeClient(factory, expected_pid=st.pid, **kw)  # type: ignore[arg-type]
    return client, st, created


def test_call_correcto_y_framing_real() -> None:
    client, st, transports = make()
    result = client.call("ping")
    assert result["pong"] is True and result["pid"] == 18244
    req = st.requests[0]
    assert req == {"jsonrpc": "2.0", "id": 1, "method": "ping", "params": {}}
    # El frame enviado se decodifica con el FrameReader real.
    assert FrameReader().feed(transports[0].sent_frames[0]) == [req]


def test_ids_incrementales_y_misma_conexion() -> None:
    client, st, transports = make()
    client.call("ping")
    client.call("estado")
    client.call("linea", {"inicio": [0, 0], "fin": [5, 0]})
    assert [r["id"] for r in st.requests] == [1, 2, 3]
    assert len(transports) == 1


def test_correlacion_descarta_id_ajeno() -> None:
    st = FakePluginState(stale_reply_before={"estado"})
    client, _, _ = make(st)
    result = client.call("estado")
    assert "viejo" not in result
    assert result["unidades"] == "m"


def test_error_plugin_busy() -> None:
    st = FakePluginState()
    st.program_error("estado", "BUSY")
    client, _, _ = make(st)
    with pytest.raises(PluginError) as err:
        client.call("estado")
    e = err.value
    assert e.code == "BUSY"
    assert e.rpc_code == -32001
    assert e.data["comando"] == "LINE"
    assert str(e) == "[BUSY] Hay un comando activo en AutoCAD: LINE. Reintente cuando termine."
    # Tras un error del plugin la conexión sigue sirviendo.
    assert client.call("estado")["pid"] == 18244


@pytest.mark.parametrize(
    ("code", "symbolic"),
    [("NO_DOC", "NO_DOC"), ("NOT_FOUND", "NOT_FOUND"), ("INVALID_PARAMS", "INVALID_PARAMS"), ("BUSY_COLA", "BUSY")],
)
def test_errores_simbolicos(code: str, symbolic: str) -> None:
    st = FakePluginState()
    st.program_error("listar_capas", code)
    client, _, _ = make(st)
    with pytest.raises(PluginError) as err:
        client.call("listar_capas")
    assert err.value.code == symbolic
    assert str(err.value).startswith(f"[{symbolic}] ")


def test_error_sin_data_code_usa_tabla() -> None:
    e = PluginError.from_rpc_error({"code": -32002, "message": "No hay ningún documento abierto en AutoCAD."})
    assert str(e) == "[NO_DOC] No hay ningún documento abierto en AutoCAD."


def test_timeout_texto_exacto_y_cierra_conexion() -> None:
    st = FakePluginState(slow={"estado"})
    client, _, transports = make(st, timeout=2)
    with pytest.raises(PipeTimeoutError) as err:
        client.call("estado")
    assert str(err.value) == "El plugin no respondió en 2 s."
    # La siguiente llamada abre una conexión nueva.
    st.slow.clear()
    client.call("estado")
    assert len(transports) == 2


def test_ping_usa_3_s() -> None:
    st = FakePluginState(slow={"ping"})
    client, _, _ = make(st)
    assert client.timeout == DEFAULT_TIMEOUT_S
    with pytest.raises(PipeTimeoutError) as err:
        client.call("ping")
    assert str(err.value) == "El plugin no respondió en 3 s."


def test_timeout_por_defecto_15_s() -> None:
    st = FakePluginState(slow={"estado"})
    client, _, _ = make(st)
    with pytest.raises(PipeTimeoutError) as err:
        client.call("estado")
    assert str(err.value) == "El plugin no respondió en 15 s."


def test_timeout_env() -> None:
    assert timeout_from_env({"MCP_AUTOCAD_TIMEOUT": "30"}) == 30.0
    assert timeout_from_env({"MCP_AUTOCAD_TIMEOUT": "2,5"}) == 2.5
    assert timeout_from_env({"MCP_AUTOCAD_TIMEOUT": "abc"}) == DEFAULT_TIMEOUT_S
    assert timeout_from_env({"MCP_AUTOCAD_TIMEOUT": "-1"}) == DEFAULT_TIMEOUT_S
    assert timeout_from_env({}) == DEFAULT_TIMEOUT_S


def test_timeout_env_aplicado(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("MCP_AUTOCAD_TIMEOUT", "7")
    st = FakePluginState(slow={"estado"})
    client, _, _ = make(st)
    with pytest.raises(PipeTimeoutError) as err:
        client.call("estado")
    assert str(err.value) == "El plugin no respondió en 7 s."


def test_desconexion_en_escritura_no_reintenta() -> None:
    st = FakePluginState(disconnect_on={"linea"})
    client, _, transports = make(st)
    with pytest.raises(PipeBrokenError) as err:
        client.call("linea", {"inicio": [0, 0], "fin": [1, 0]})
    assert str(err.value) == "Se perdió la conexión con AutoCAD (¿se cerró?)."
    assert len(transports) == 1  # no se duplicó la escritura
    assert sum(1 for r in st.requests if r["method"] == "linea") == 1


def test_desconexion_idempotente_reconecta_una_vez() -> None:
    st = FakePluginState(disconnect_on={"estado"})
    client, _, transports = make(st)
    assert client.call("estado", idempotent=True)["pid"] == 18244
    assert len(transports) == 2


def test_fallo_al_enviar_reconecta_transparente() -> None:
    client, st, transports = make()
    client.call("ping")
    st.fail_send = 1
    assert client.call("linea", {"inicio": [0, 0], "fin": [1, 0]})["handles"]
    assert len(transports) == 2
    assert sum(1 for r in st.requests if r["method"] == "linea") == 1


def test_fallo_al_enviar_dos_veces_falla() -> None:
    client, st, _ = make()
    st.fail_send = 2
    with pytest.raises(PipeBrokenError):
        client.call("ping")


def test_sin_instancia_al_conectar() -> None:
    st = FakePluginState(alive=False)
    client, _, _ = make(st)
    with pytest.raises(NoInstanceError) as err:
        client.call("ping")
    assert str(err.value) == "AutoCAD no está abierto o el plugin no cargó."


def test_aviso_pid_distinto(caplog: pytest.LogCaptureFixture) -> None:
    st = FakePluginState(pid=111)
    client = PipeClient(lambda: FakeTransport(st), expected_pid=222)
    with caplog.at_level(logging.WARNING, logger="mcp_autocad.client"):
        client.call("ping")
    assert any("111" in r.getMessage() and "222" in r.getMessage() for r in caplog.records)
