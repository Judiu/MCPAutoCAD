"""Punto de entrada: python -m mcp_autocad (transporte MCP por stdio).

stdout está reservado al protocolo MCP: todo log va a stderr.
"""

from __future__ import annotations

import logging
import os
import sys

import anyio
from mcp.server.stdio import stdio_server

from .client import PipeClient, timeout_from_env
from .discovery import Instance
from .server import build_server, initialization_options
from .transport import Win32PipeTransport


def _configure_logging() -> None:
    handler = logging.StreamHandler(sys.stderr)
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s"))
    root = logging.getLogger()
    root.handlers[:] = [handler]
    level = os.environ.get("MCP_AUTOCAD_LOG", "INFO").upper()
    root.setLevel(getattr(logging, level, logging.INFO))


def make_client(instance: Instance) -> PipeClient:
    return PipeClient(
        lambda: Win32PipeTransport(instance.pipe),
        timeout=timeout_from_env(),
        expected_pid=instance.pid,
    )


async def _run() -> None:
    server = build_server(make_client)
    async with stdio_server() as (read_stream, write_stream):
        await server.run(read_stream, write_stream, initialization_options(server))


def main() -> None:
    _configure_logging()
    logging.getLogger(__name__).info("Servidor MCP 'autocad' iniciado (stdio).")
    anyio.run(_run)


if __name__ == "__main__":
    main()
