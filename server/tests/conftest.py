"""Configuración común: hace importable mcp_autocad y tests.fake_plugin."""

from __future__ import annotations

import os
import sys
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parent.parent
TESTS_DIR = Path(__file__).resolve().parent
for path in (str(SERVER_DIR), str(TESTS_DIR)):
    if path not in sys.path:
        sys.path.insert(0, path)

# Servidor 0.8.2: pytest nunca lanza el accoreconsole real (respaldo de ODA). Toda prueba que pase por
# server._auditar_oda_real ve una ruta inexistente; las de accoreconsole usan un conversor simulado propio.
ACCORE_INEXISTENTE = str(TESTS_DIR / "_no_existe_accoreconsole.exe")
os.environ["MCP_AUTOCAD_ACCORE"] = ACCORE_INEXISTENTE
