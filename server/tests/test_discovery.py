from __future__ import annotations

import pytest

from mcp_autocad import discovery
from mcp_autocad.discovery import Instance, InstanceSelector, list_instances, parse_pipe_name
from mcp_autocad.errors import NO_INSTANCE_TEXT, NoInstanceError

PIPES = [
    "mcp-autocad-usuario-18244",
    "mcp-autocad-Usuario-20312",  # mismo usuario con otra capitalización
    "mcp-autocad-otro-11111",  # otro usuario
    "mcp-autocad-usuario-abc",  # PID no numérico
    "mcp-autocad-juan-perez-777",  # usuario con guion
    "InitShutdown",
    "mojo.1234.5678",
]


@pytest.fixture
def fake_pipes(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(discovery.os, "listdir", lambda path: list(PIPES))
    monkeypatch.setenv("USERNAME", "USUARIO")


def test_parse_pipe_name() -> None:
    assert parse_pipe_name("mcp-autocad-usuario-18244") == ("usuario", 18244)
    assert parse_pipe_name("mcp-autocad-juan-perez-777") == ("juan-perez", 777)
    assert parse_pipe_name("mcp-autocad-usuario-") is None
    assert parse_pipe_name("otro-pipe") is None


def test_list_instances_filtra_usuario_sin_mayusculas(fake_pipes: None) -> None:
    found = list_instances()
    assert [i.pid for i in found] == [18244, 20312]
    assert found[0].pipe == "\\\\.\\pipe\\mcp-autocad-usuario-18244"
    assert found[1].user == "Usuario"


def test_list_instances_usuario_con_guion(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(discovery.os, "listdir", lambda path: list(PIPES))
    assert [i.pid for i in list_instances(user="Juan-Perez")] == [777]


def test_list_instances_tolera_fallo(monkeypatch: pytest.MonkeyPatch) -> None:
    def boom(path: str) -> list[str]:
        raise OSError("sin acceso")

    monkeypatch.setattr(discovery.os, "listdir", boom)
    assert list_instances() == []


def test_list_instances_usa_ruta_del_contrato(monkeypatch: pytest.MonkeyPatch) -> None:
    seen: list[str] = []
    monkeypatch.setattr(discovery.os, "listdir", lambda path: seen.append(path) or [])
    list_instances(user="x")
    assert seen == [r"\\.\pipe\\"]


def _inst(*pids: int) -> list[Instance]:
    return [Instance(pid=p, pipe=f"\\\\.\\pipe\\mcp-autocad-usuario-{p}", user="usuario") for p in pids]


def test_seleccion_unica() -> None:
    sel = InstanceSelector(lambda: _inst(18244), environ={})
    assert sel.resolve().pid == 18244
    assert sel.selection_mode == "auto"
    assert sel.payload() == {
        "instancias": [{"pid": 18244, "pipe": "\\\\.\\pipe\\mcp-autocad-usuario-18244", "seleccionada": True}],
        "seleccion": "auto",
    }


def test_ninguna_instancia_texto_exacto() -> None:
    sel = InstanceSelector(lambda: [], environ={})
    with pytest.raises(NoInstanceError) as err:
        sel.resolve()
    assert str(err.value) == "AutoCAD no está abierto o el plugin no cargó."
    assert sel.payload() == {"instancias": [], "seleccion": "auto"}


def test_varias_sin_seleccion_lista_pids() -> None:
    sel = InstanceSelector(lambda: _inst(20312, 18244), environ={})
    with pytest.raises(NoInstanceError) as err:
        sel.resolve()
    assert str(err.value) == (
        "Hay varias instancias de AutoCAD abiertas (PID: 18244, 20312). Use seleccionar_instancia para elegir una."
    )
    assert all(not i["seleccionada"] for i in sel.payload()["instancias"])


def test_seleccion_manual() -> None:
    sel = InstanceSelector(lambda: _inst(18244, 20312), environ={})
    sel.select(20312)
    assert sel.resolve().pid == 20312
    assert sel.selection_mode == "manual"
    with pytest.raises(NoInstanceError) as err:
        sel.select(99999)
    assert str(err.value) == (
        "No hay ninguna instancia de AutoCAD con PID 99999. "
        "Instancias disponibles (PID): 18244, 20312. Use seleccionar_instancia."
    )
    assert sel.resolve().pid == 20312  # la selección previa se mantiene


def test_seleccion_env_tiene_prioridad() -> None:
    sel = InstanceSelector(lambda: _inst(18244, 20312), environ={"MCP_AUTOCAD_PID": "18244"})
    sel.select(20312)
    assert sel.resolve().pid == 18244
    assert sel.selection_mode == "env"


def test_env_pid_inexistente() -> None:
    sel = InstanceSelector(lambda: _inst(18244), environ={"MCP_AUTOCAD_PID": "5"})
    with pytest.raises(NoInstanceError) as err:
        sel.resolve()
    assert str(err.value) == (
        "No hay ninguna instancia de AutoCAD con PID 5. "
        "Instancias disponibles (PID): 18244. Use seleccionar_instancia."
    )
    sel = InstanceSelector(lambda: [], environ={"MCP_AUTOCAD_PID": "5"})
    with pytest.raises(NoInstanceError) as err:
        sel.resolve()
    assert str(err.value) == "No hay ninguna instancia de AutoCAD con PID 5. " + NO_INSTANCE_TEXT


def test_manual_desaparece() -> None:
    pids = [18244, 20312]
    sel = InstanceSelector(lambda: _inst(*pids), environ={})
    sel.select(20312)
    pids.remove(20312)
    with pytest.raises(NoInstanceError):
        sel.resolve()
