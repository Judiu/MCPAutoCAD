"""Respaldo sin AutoCAD de auditar_archivo: ODA File Converter → DXF → ezdxf (protocol.md §5H.3).

ezdxf es una dependencia opcional: solo se importa al leer un DXF, de modo que
importar este paquete no la exige y no cambia el arranque del servidor.
"""

from __future__ import annotations

from .audit import ADVERTENCIA_ODA, NO_DISPONIBLE_ODA, auditar_archivo_oda
from .convert import (
    OdaCanceladoError,
    OdaConversionError,
    OdaNoDisponibleError,
    OdaTimeoutError,
    accore_exe_por_defecto,
    dwg_a_dxf,
    oda_exe_por_defecto,
    timeout_por_defecto,
)
from .lps_rules import Classification, LpsRules, classify

__all__ = [
    "ADVERTENCIA_ODA",
    "NO_DISPONIBLE_ODA",
    "Classification",
    "LpsRules",
    "OdaCanceladoError",
    "OdaConversionError",
    "OdaNoDisponibleError",
    "OdaTimeoutError",
    "accore_exe_por_defecto",
    "auditar_archivo_oda",
    "classify",
    "dwg_a_dxf",
    "oda_exe_por_defecto",
    "timeout_por_defecto",
]
