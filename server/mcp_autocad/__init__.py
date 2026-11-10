"""Servidor MCP para AutoCAD 2026 (Fases 0, 1, 2, 3, 4A, L, 4B y 5, protocolo 0.8).

Traduce herramientas MCP a JSON-RPC 2.0 sobre el named pipe del plugin
(ver protocol.md). No contiene lógica CAD.
"""

__version__ = "0.8.6"
PROTOCOL_VERSION = "0.8"

__all__ = ["__version__", "PROTOCOL_VERSION"]
