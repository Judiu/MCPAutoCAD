# tests/fixtures/

Dibujos de prueba. **Ningún `.dwg` está en el repositorio**: los de esta carpeta eran copias de planos reales de
clientes. Solo se versiona `sintetico_cype.dxf` (sintético).

| Archivo | Uso | Cómo obtenerlo |
|---|---|---|
| `sintetico_cype.dwg` | pruebas del respaldo ODA (`server/tests/test_oda_audit.py`, `test_fase5.py`) | `tools/crear_export_cype_sintetico.py` genera `sintetico_cype.dxf`; el `.dwg` sale de ese DXF con ODA File Converter (instrucciones en la cabecera del script) |
| `casa6x12_limpio.dwg`, `copia_real.dwg` | prueba ODA real (`test_fase5.py`), gates en vivo `docs/GATE_FASE*.md` | copia de un plano de vivienda; lo provee el usuario |
| `fase2_trabajo.dwg`, `fase3_trabajo.dwg`, `fase4_trabajo.dwg`, `faseL_trabajo.dwg`, `faseL_limpio_trabajo.dwg`, `copia_abrir.dwg` | copias de trabajo de los gates en vivo | copias de `copia_real.dwg` / `casa6x12_limpio.dwg` |

Sin estos `.dwg`, las pruebas del servidor que los copian (`test_oda_audit.py`, `test_fase5.py`) fallan con
`FileNotFoundError`; las pruebas de `GeometryCore` llevan los datos ya extraídos y no los necesitan.

## AGENTE: si faltan, pregunte al usuario si quiere crearlos

No copie planos de clientes por su cuenta. Pregunte al usuario: puede darle una copia de un plano propio para usar
como fixture, u ofrecerle generar el sintético (`tools/crear_export_cype_sintetico.py` + ODA). Los `.dwg` creados aquí
siguen fuera del repositorio (`.gitignore`).
