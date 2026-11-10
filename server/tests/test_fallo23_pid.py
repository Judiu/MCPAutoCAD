"""Fallo 23 (0.10.0): aviso cuando la llamada anterior fue a una PID de AutoCAD que ya no está en ejecución."""

from __future__ import annotations

from types import SimpleNamespace

from mcp_autocad.server import AutocadBridge


class _Selector:
    def __init__(self, pids: list[int]) -> None:
        self.pids = pids

    def instances(self) -> list[SimpleNamespace]:
        return [SimpleNamespace(pid=p) for p in self.pids]


def _bridge(pids: list[int]) -> tuple[AutocadBridge, _Selector]:
    sel = _Selector(pids)
    return AutocadBridge(client_factory=lambda *_a, **_k: None, selector=sel), sel  # type: ignore[arg-type]


def test_primera_llamada_y_misma_pid_no_avisan() -> None:
    b, _ = _bridge([100])
    assert b._aviso_cambio_pid(100) is None
    assert b._aviso_cambio_pid(100) is None


def test_reinicio_avisa_una_vez() -> None:
    b, sel = _bridge([86484])
    assert b._aviso_cambio_pid(86484) is None
    sel.pids = [253188]
    aviso = b._aviso_cambio_pid(253188)
    assert aviso is not None
    assert "PID 86484" in aviso and "PID 253188" in aviso and "no se guardó" in aviso
    assert b._aviso_cambio_pid(253188) is None


def test_cambio_voluntario_con_la_anterior_viva_no_avisa() -> None:
    b, _ = _bridge([1, 2])
    assert b._aviso_cambio_pid(1) is None
    assert b._aviso_cambio_pid(2) is None


def test_selector_que_falla_no_tumba_la_llamada() -> None:
    b, sel = _bridge([1])
    b._aviso_cambio_pid(1)

    def boom() -> list[SimpleNamespace]:
        raise OSError("pipes")

    sel.instances = boom  # type: ignore[method-assign]
    assert b._aviso_cambio_pid(2) is None


def test_error_del_plugin_tambien_avisa() -> None:
    import pytest

    from mcp_autocad.errors import PluginError

    class _Client:
        def call(self, *_a, **_k):
            raise PluginError("INTERNAL", "Error interno del plugin de AutoCAD.", {"detalle": "x"}, -32603)

    b, sel = _bridge([1])
    b._aviso_cambio_pid(1)
    sel.pids = [2]
    inst = SimpleNamespace(pid=2)
    b.current_instance = lambda: inst  # type: ignore[method-assign]
    b.manifest_tools = lambda _i: [{"name": "ping", "escritura": False}]  # type: ignore[method-assign]
    b.client_for = lambda _i: _Client()  # type: ignore[method-assign]
    with pytest.raises(PluginError) as info:
        b._call_plugin_tool("ping", {}, "auto")
    assert info.value.code == "INTERNAL"
    assert str(info.value).startswith("[INTERNAL] Error interno del plugin de AutoCAD. La instancia")
    assert "PID 1" in str(info.value) and info.value.data == {"detalle": "x"}
