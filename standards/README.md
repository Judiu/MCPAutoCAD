# standards/

Estándar de la oficina que el plugin instala junto a él (`scripts/install-plugin.ps1`, `packaging/empaquetar.py`).

| Archivo | En el repo | Contenido |
|---|---|---|
| `lps.json` | sí | estándar LPS: unidades, capas permitidas y preferidas, rótulo, `biblioteca_vanos` |
| `LPS_METROS.dwt` | sí | plantilla en metros |
| `fuentes/LogoLPS.svg` | sí | logo |
| `historico/lps_v1_6x12_sucio.json` | sí | versión 1 del estándar (referencia) |
| `LPS_ROTULO.dwg` | **no** | rótulo de la oficina |
| `Puerta Dinamica LM.dwg`, `PuertaBaño.dwg`, `VENTANA01.dwg`, `TANQUE 1000 LTS.dwg` | **no** | biblioteca de bloques |
| `historico/LPS_ROTULO_v1.dwg` | **no** | respaldo del rótulo v1 |

Los `.dwg` no se versionan porque contienen datos del personal y de clientes. Sin ellos, `empaquetar.py` avisa
("AVISO: falta …") y el paquete sale sin rótulo ni biblioteca; `vano` e `insertar_bloque` con esos nombres fallan.

## AGENTE: si faltan, pregunte al usuario si quiere crearlos

No los cree sin permiso y no los copie de planos de clientes. Si el usuario acepta, esto es lo que espera el código:

**`LPS_ROTULO.dwg`** (`lps.json` → `rotulo`): bloque `LPS_ROTULO` en la capa `Rotulo`, logo en la capa `0`, con
atributos (TAG — descripción):
`PROYECTO` tipo de proyecto · `VIS` ¿es VIS? · `MUNICIPIO` municipio y departamento · `PROYECTISTA` proyectista y
matrícula · `CONTIENE_1`…`CONTIENE_8` contenido de la lámina · `UBICACION` dirección del predio · `CODIGO_CATASTRAL` ·
`FECHA` (MES DE AAAA) · `ESCALA` · `VERSION` · `PROPIETARIO_1`, `CC_1`, `PROPIETARIO_2`, `CC_2` · `PLANO_N` (MTEXT
en línea: número grande + " de" pequeño) · `PLANO_TOTAL` · `DIGITALIZO` · `DISENADOR_NE` diseñador de elementos no
estructurales. Valores por defecto genéricos (`NOMBRE PROPIETARIO 1`, `C.C. 0.000.000`, `MES DE AAAA`…), nunca
nombres de personas reales. Scripts de apoyo: `tools/agregar_logo.py`, `tools/normalizar_capas_rotulo.py`.

**Biblioteca de vanos** (`lps.json` → `biblioteca_vanos`): un `.dwg` por bloque, con el nombre del bloque. Cada
entrada de `biblioteca_vanos` fija el eje del ancho, la dirección del espesor, el ancho y espesor nominales, la
propiedad dinámica de ancho y la capa; un bloque nuevo debe cumplirlos o actualizar esa entrada. Verificación:
`tools/verificar_bloques_biblioteca.py`.
