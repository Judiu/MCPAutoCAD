"""Arranca `python -m mcp_autocad` por stdio real y lista herramientas.

Verifica que stdout queda limpio para el transporte MCP y que el servidor
arranca aunque AutoCAD no esté abierto. Se fuerza MCP_AUTOCAD_PID a un PID
inexistente para no depender de que haya un AutoCAD real abierto.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

import anyio
from mcp import Client
from mcp.client.stdio import StdioServerParameters, stdio_client

SERVER_DIR = Path(__file__).resolve().parent.parent


def test_stdio_arranca_sin_autocad() -> None:
    env = dict(os.environ)
    env["PYTHONPATH"] = str(SERVER_DIR)
    env["MCP_AUTOCAD_PID"] = "1"  # PID que nunca es acad.exe
    env["MCP_AUTOCAD_MANIFEST"] = str(SERVER_DIR / "tests" / "no_existe_manifest.json")
    params = StdioServerParameters(command=sys.executable, args=["-m", "mcp_autocad"], env=env)

    async def main() -> None:
        with anyio.fail_after(60):
            await scenario()

    async def scenario() -> None:
        async with Client(stdio_client(params), cache=None) as client:  # type: ignore[arg-type]
            tools = (await client.list_tools()).tools
            names = {t.name for t in tools}
            assert {"instancias", "seleccionar_instancia"} <= names
            res = await client.call_tool("estado", {})
            assert res.is_error
            assert res.content[0].text.startswith("No hay ninguna instancia de AutoCAD con PID 1.")

    anyio.run(main)
