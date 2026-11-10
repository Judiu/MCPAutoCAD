"""Aviso de cambio del manifest (protocol.md §7.1, desde el servidor 0.6.1; cifras del manifest 0.8).

Hallazgo del gate en vivo de la Fase L (2026-10-27): al reinstalar el plugin 0.6.0 hubo que reiniciar
Claude Code para ver las 4 herramientas nuevas. Desde 0.6.1, cuando el manifest de la instancia (versión
del plugin, nombres, descripciones o schemas) deja de coincidir con lo último publicado en tools/list, el
servidor envía notifications/tools/list_changed por la sesión (era con handshake, capacidad
tools.listChanged declarada) y por subscriptions/listen (era 2026-07-28), una vez por cambio, y estado/ping
llevan una advertencia mientras no coincidan.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import anyio
import mcp_types as types
from fake_plugin import HERRAMIENTAS_FASE4B
from mcp import Client
from test_server import World, _guardar_manifest, text_of

from mcp_autocad.server import manifest_mismatch_warning


def _manifest_viejo(tmp_path: Path, *, sin_fase4b: bool = True, version: str = "0.6.1") -> Path:
    """Copia en disco de un manifest "anterior": sin las 4 de Fase 4B (como 0.6.1) y con otra versión."""
    path = tmp_path / "manifest.json"
    _guardar_manifest(World(18244), path)
    data = json.loads(path.read_text(encoding="utf-8"))
    if sin_fase4b:
        data["tools"] = [t for t in data["tools"] if t["name"] not in HERRAMIENTAS_FASE4B]
    data["plugin_version"] = version
    path.write_text(json.dumps(data), encoding="utf-8")
    return path


def _es_list_changed(message: Any) -> bool:
    root = getattr(message, "root", message)
    return getattr(root, "method", None) == "notifications/tools/list_changed" or isinstance(
        root, types.ToolListChangedNotification
    )


def _run_legacy(server: Any, body: Any) -> list[Any]:
    recibidas: list[Any] = []

    async def handler(message: Any) -> None:
        if _es_list_changed(message):
            recibidas.append(message)

    async def main() -> None:
        async with Client(server, mode="legacy", cache=None, message_handler=handler) as client:
            await body(client)
            # Deja que el bucle entregue las notificaciones pendientes.
            for _ in range(20):
                await anyio.sleep(0)

    anyio.run(main)
    return recibidas


AVISO_051 = (
    "El plugin de esta instancia (plugin 0.8.0, 56 herramientas) no coincide con las herramientas publicadas al "
    "cliente MCP (plugin 0.6.1, 52 herramientas; nuevas: vano, union_muros, cotas_automaticas, "
    "insertar_export). El servidor envió notifications/tools/list_changed; si las herramientas no se "
    "actualizan, reconecte el servidor MCP (/mcp en Claude Code) o reinicie el cliente."
)


def test_capacidad_tools_list_changed_declarada_en_la_era_con_handshake(tmp_path: Path) -> None:
    world = World(18244)

    async def body(c: Client) -> None:
        caps = c.server_capabilities
        assert caps.tools is not None and caps.tools.list_changed is True

    _run_legacy(world.server(manifest_path=None), body)


def test_reinstalar_plugin_notifica_y_estado_avisa(tmp_path: Path) -> None:
    """AutoCAD cerrado con el manifest viejo en disco → tools/list publica 52; se abre AutoCAD con 0.8.0 (56)."""
    world = World()
    manifest = _manifest_viejo(tmp_path)

    async def body(c: Client) -> None:
        assert len((await c.list_tools()).tools) == 52 + 5
        world.add(18244)
        res = await c.call_tool("estado", {})
        assert not res.is_error
        data = json.loads(text_of(res))
        assert data["advertencias"][-1] == AVISO_051
        assert data["instancias"][0]["pid"] == 18244
        # El cliente vuelve a pedir tools/list (como haría al recibir la notificación): ya coincide.
        assert len((await c.list_tools()).tools) == 56 + 5
        data = json.loads(text_of(await c.call_tool("estado", {})))
        assert AVISO_051 not in data["advertencias"]

    recibidas = _run_legacy(world.server(manifest_path=manifest), body)
    assert len(recibidas) == 1


def test_una_notificacion_por_cambio_y_ping_avisa_mientras_no_coincida(tmp_path: Path) -> None:
    world = World()
    manifest = _manifest_viejo(tmp_path)

    async def body(c: Client) -> None:
        await c.list_tools()
        world.add(18244)
        for _ in range(3):
            data = json.loads(text_of(await c.call_tool("ping", {})))
            assert data["pong"] is True
            assert data["advertencias"] == [AVISO_051]

    recibidas = _run_legacy(world.server(manifest_path=manifest), body)
    assert len(recibidas) == 1  # no una por llamada


def test_misma_lista_otra_version_tambien_notifica(tmp_path: Path) -> None:
    world = World()
    manifest = _manifest_viejo(tmp_path, sin_fase4b=False, version="0.6.1")

    async def body(c: Client) -> None:
        assert len((await c.list_tools()).tools) == 61
        world.add(18244)
        data = json.loads(text_of(await c.call_tool("ping", {})))
        assert data["advertencias"] == [
            "El plugin de esta instancia (plugin 0.8.0, 56 herramientas) no coincide con las herramientas publicadas "
            "al cliente MCP (plugin 0.6.1, 56 herramientas; mismos nombres; cambian la versión, las descripciones o "
            "los schemas). El servidor envió notifications/tools/list_changed; si las herramientas no se actualizan, "
            "reconecte el servidor MCP (/mcp en Claude Code) o reinicie el cliente."
        ]

    assert len(_run_legacy(world.server(manifest_path=manifest), body)) == 1


def test_sin_cambios_ni_aviso_ni_notificacion(tmp_path: Path) -> None:
    manifest = tmp_path / "manifest.json"
    _guardar_manifest(World(18244), manifest)
    world = World()

    async def body(c: Client) -> None:
        await c.list_tools()
        world.add(18244)
        data = json.loads(text_of(await c.call_tool("ping", {})))
        assert "advertencias" not in data
        data = json.loads(text_of(await c.call_tool("estado", {})))
        assert not any("no coincide con las herramientas publicadas" in a for a in data["advertencias"])

    assert _run_legacy(world.server(manifest_path=manifest), body) == []


def test_sin_tools_list_previo_no_avisa(tmp_path: Path) -> None:
    """Si el cliente nunca pidió tools/list no hay nada publicado con qué comparar."""
    world = World(18244)

    async def body(c: Client) -> None:
        data = json.loads(text_of(await c.call_tool("ping", {})))
        assert "advertencias" not in data

    assert _run_legacy(world.server(manifest_path=None), body) == []


def test_era_2026_07_28_por_subscriptions_listen(tmp_path: Path) -> None:
    """En la era sin handshake la notificación solo llega por subscriptions/listen (ListenHandler + bus)."""
    world = World()
    manifest = _manifest_viejo(tmp_path)
    eventos: list[Any] = []

    async def main() -> None:
        async with Client(world.server(manifest_path=manifest), cache=None) as c:
            caps = c.server_capabilities
            assert caps.tools is not None and caps.tools.list_changed is True
            assert len((await c.list_tools()).tools) == 57
            async with c.listen(tools_list_changed=True) as sub:
                world.add(18244)
                data = json.loads(text_of(await c.call_tool("estado", {})))
                assert data["advertencias"][-1] == AVISO_051
                with anyio.fail_after(5):
                    async for event in sub:
                        eventos.append(event)
                        break
            assert len((await c.list_tools()).tools) == 61

    anyio.run(main)
    assert len(eventos) == 1


def test_texto_del_aviso_trunca_nombres() -> None:
    from mcp_autocad.server import _Snapshot

    viejo = _Snapshot.of([], "0.5.0")
    nuevo = _Snapshot.of([{"name": f"h{i}", "inputSchema": {"type": "object"}} for i in range(12)], None)
    aviso = manifest_mismatch_warning(viejo, nuevo)
    assert "(plugin ?, 12 herramientas)" in aviso
    assert "nuevas: h0, h1, h2, h3, h4, h5, h6, h7, h8, h9 y 2 más)" in aviso
    assert "retiradas" not in aviso


def test_fallo_al_comprobar_el_manifest_no_pierde_la_respuesta(tmp_path: Path, caplog: Any) -> None:
    """O4: una excepción en published_changed (o en la notificación) se registra y la respuesta llega igual."""
    world = World(18244)
    server = world.server(manifest_path=None)

    def falla() -> bool:
        raise RuntimeError("fallo simulado")

    server.bridge.published_changed = falla

    async def body(c: Client) -> None:
        await c.list_tools()
        res = await c.call_tool("ping", {})
        assert not res.is_error
        assert json.loads(text_of(res))["pong"] is True

    with caplog.at_level("WARNING", logger="mcp_autocad.server"):
        _run_legacy(server, body)
    assert any("fallo simulado" in r.getMessage() for r in caplog.records)


def test_fallo_al_notificar_no_pierde_la_respuesta(tmp_path: Path, monkeypatch: Any, caplog: Any) -> None:
    import mcp_autocad.server as srv

    async def falla(_ctx: Any, _bus: Any) -> None:
        raise RuntimeError("notificación rota")

    monkeypatch.setattr(srv, "notify_tools_changed", falla)
    world = World()
    manifest = _manifest_viejo(tmp_path)

    async def body(c: Client) -> None:
        await c.list_tools()
        world.add(18244)
        data = json.loads(text_of(await c.call_tool("estado", {})))
        assert data["advertencias"][-1] == AVISO_051

    with caplog.at_level("WARNING", logger="mcp_autocad.server"):
        _run_legacy(world.server(manifest_path=manifest), body)
    assert any("notificación rota" in r.getMessage() for r in caplog.records)
