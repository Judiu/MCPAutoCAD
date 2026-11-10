# mcp-autocad (servidor Python 0.8.2, Fases 0, 1, 2, 3, 4A, L, 4B y 5)

Cambios 0.8.2 (solo servidor; protocolo `"0.8"` y plugin 0.8.0 sin cambios; protocol.md cabecera, §5H.2, §5H.3, §5H.5): (1) si ODA File Converter no genera el DXF, el respaldo ODA reintenta la misma copia con `accoreconsole.exe` de AutoCAD (`MCP_AUTOCAD_ACCORE`; guion fijo `DXFOUT` + `QUIT` sin guardar, `/isolate`, mismo plazo, cancelación y limpieza) y la respuesta y la fila del lote dicen `conversor` (`oda` | `accoreconsole`); los fallos se distinguen con el mismo código `INTERNAL` y `data.motivo` / `error.motivo`: `archivo_vacio` (0 bytes, no se convierte), `archivo_danado` (AutoCAD también lo rechaza) y `oda_no_lee_sin_respaldo` (no hay accoreconsole). (2) Unidad probable: piso de cota de 0,02 m solo para mm, y la extensión se descarta (con sufijo en `motivo_unidad`) en planos mezclados (extensión ≥ 100 000 y ≥ 20 × `coord_mediana`) o con extensión absurda en todas las unidades; `_unidad` añade `coord_mediana`. (3) `MCP_AUTOCAD_ODA_TIMEOUT` por defecto 300 s (antes 120), también para accoreconsole. Revisión antes de publicar (Fable): el guion de accoreconsole se escribe en ANSI (`mbcs`; con UTF-8 una carpeta con ñ no generaba DXF), con nombre corto 8.3 si la ruta no es representable y error claro si tampoco; `TEMP`/`TMP` de accoreconsole en su carpeta de trabajo (ya no deja `%TEMP%\accc<n>`); con código de salida ≠ 0 el DXF se descarta (ODA sale con 0 al convertir y al fallar); `archivo_danado` solo con evidencia en la salida (`ErrorStatus=`, "dibujo no válido"), y si no `accoreconsole_fallo`, que deja la fila `omitido` para que `reanudar` la repita; sin ODA instalado se usa accoreconsole directamente, con la advertencia `ODA File Converter no está instalado; se convirtió a DXF con accoreconsole de AutoCAD (sin guardar el DWG).` y, si AutoCAD rechaza el dibujo, `El archivo DWG está dañado o no es un dibujo válido: AutoCAD (accoreconsole) no pudo leerlo (ODA File Converter no está instalado).`; `ODA_NO_DISPONIBLE` solo si faltan los dos, y entonces nombra las dos rutas: `No se encontró ODA File Converter en '<ruta>' ni accoreconsole en '<ruta accoreconsole>'; instale uno de los dos o defina MCP_AUTOCAD_ODA o MCP_AUTOCAD_ACCORE.`; la regla de "georreferenciado" se retiró (la extensión p5–p95 es invariante a traslación y descartarla perdía veredictos `m` correctos). pytest nunca lanza el accoreconsole real: `tests/conftest.py` fija `MCP_AUTOCAD_ACCORE` en una ruta inexistente y `tests/test_fase082.py` usa uno simulado.

Cambios 0.8.1 (solo servidor; protocolo `"0.8"` y plugin 0.8.0 sin cambios, protocol.md §5H.2): el lote separa exports CYPE de planos de trabajo (`tipo`, `por_tipo`, filtro `planos_trabajo`), estima la unidad real por geometría con ODA (`unidad_probable`, `unidad_coherente`, `por_unidad_probable`, `matriz_declarada_probable`, filtro `unidad_discrepa`; con el plugin queda `null` y `unidad_probable` va a `no_disponible`) y cuenta los duplicados aparte (`n_hallazgos_duplicados`, `por_severidad_sin_duplicados`; `por_severidad` no cambia). `informe.md` lleva el titular de severidad sin duplicados y la sección "Unidades: declarada vs probable".

Cambios 0.8.0: protocolo `"0.8"` (Fase 5, protocol.md §5H). Por primera vez el servidor tiene código de negocio propio: `auditar_archivo` del plugin se enruta con `origen` (`auto` | `plugin` | `oda`) y hay tres herramientas del servidor para auditar carpetas en segundo plano (`auditar_carpeta`, `estado_auditoria`, `cancelar_auditoria`, en `mcp_autocad/lote.py`), con respaldo sin AutoCAD (`mcp_autocad/oda/`). Manifest: 56 herramientas; Claude Code ve 61.

Cambios 0.7.0: protocolo `"0.7"` (Fase 4B, 4 herramientas de escritura, protocol.md §5G: `vano`, `union_muros`, `cotas_automaticas`, `insertar_export`; cambios compatibles en `insertar_bloque`, `normalizar_capas` y la respuesta G6 de las generadoras). El servidor sigue siendo genérico: solo cambian `PROTOCOL_VERSION` (`"0.7"`) y `__version__` (`"0.7.0"`); ninguna herramienta nueva tiene código propio. Manifest: 55 herramientas; Claude Code ve 57.

Cambios 0.6.1 (gate en vivo de la Fase L): si el manifest de la instancia cambia respecto a lo publicado en `tools/list` (versión del plugin, nombres, descripciones o schemas), el servidor envía `notifications/tools/list_changed` una vez por cambio, por la sesión y por `subscriptions/listen`, declara `tools.listChanged` en ambas eras del protocolo MCP y añade una advertencia a `estado`/`ping` mientras no coincidan (protocol.md §7.1; pruebas en `tests/test_list_changed.py`).

Cambios 0.6.0: protocolo `"0.6"` (Fase L, limpieza de planos: 4 herramientas de escritura, protocol.md §5F: `borrar_fuera_de`, `purgar`, `normalizar_capas`, `corregir_unidades_bloques`). El servidor sigue siendo genérico: solo cambian `PROTOCOL_VERSION` (`"0.6"`) y `__version__` (`"0.6.0"`); ninguna herramienta nueva tiene código propio. Manifest: 51 herramientas; Claude Code ve 53.

Historial 0.5.0: protocolo `"0.5"` (Fase 4A, geometría paramétrica: 11 herramientas, protocol.md §5E). El servidor sigue siendo genérico: solo cambian `PROTOCOL_VERSION` (`"0.5"`) y `__version__` (`"0.5.0"`); ninguna herramienta nueva tiene código propio y el código de error nuevo `INVALID_GEOMETRY` (−32005) se reenvía como cualquier otro (`[INVALID_GEOMETRY] Geometría no construible: …`).

Historial 0.4.0: protocolo `"0.4"` (Fase 3, dibujo primitivo: 14 herramientas de escritura, protocol.md §5D). El servidor sigue siendo genérico: solo cambian `PROTOCOL_VERSION` (`"0.4"`) y `__version__` (`"0.4.0"`); ninguna herramienta nueva tiene código propio. Historial: 0.3.1 (correcciones F1–F8 del gate de Fase 2; `vista_png.espacio` viaja tal cual).

Servidor MCP (stdio) que traduce herramientas MCP a JSON-RPC 2.0 sobre el named
pipe del plugin de AutoCAD (`\\.\pipe\mcp-autocad-<usuario>-<PID>`). No contiene
lógica CAD salvo el respaldo sin AutoCAD de la Fase 5. Contrato: `../protocol.md` (protocolo `0.8`; si el plugin declara otra
`protocol_version`, el servidor lo avisa en el log y sigue funcionando).

## Requisitos

- Python 3.12, paquetes `mcp` (2.x) y `pywin32` (ya instalados en el entorno; no se instala nada).
- Solo para el respaldo sin AutoCAD (opcional): `ezdxf>=1.4` (extra `oda` de `pyproject.toml`) y ODA File Converter. El servidor arranca sin ellos; se importan al usarlos.
- `pytest` para las pruebas.

## Ejecutar

PowerShell:

```powershell
$env:PYTHONPATH = 'C:\LPSAPP\MCPAutoCAD\server'
python -m mcp_autocad
```

El proceso habla MCP por stdin/stdout; los logs van a **stderr** (nivel con
`MCP_AUTOCAD_LOG=DEBUG|INFO|WARNING`, por defecto `INFO`).

Registro en Claude Code (ejemplo, lo ejecuta el usuario):

```powershell
claude mcp add autocad --env PYTHONPATH=C:\LPSAPP\MCPAutoCAD\server -- python -m mcp_autocad
```

## Herramientas

- Las del manifest del plugin (`tools/manifest`), registradas de forma genérica con su
  `inputSchema` tal cual y en el orden del manifest: **56 herramientas** en 0.8 (ver «Fase 1»,
  «Fase 2», «Fase 3», «Fase 4A», «Fase L», «Fase 4B» y «Fase 5» abajo). Total publicado: 56 del manifest + 5 propias = 61.
  Única diferencia con el manifest: `auditar_archivo` se publica con la propiedad `origen` añadida.
- Propias del servidor: `instancias`, `seleccionar_instancia(pid)` y las 3 del lote: `auditar_carpeta`,
  `estado_auditoria` y `cancelar_auditoria`.
- `estado` se devuelve enriquecido con `instancias`.

Si AutoCAD no está abierto al arrancar, el servidor publica las herramientas de la
copia en disco `%LOCALAPPDATA%\MCPAutoCAD\manifest.json` (protocol.md §4.1;
`MCP_AUTOCAD_MANIFEST` cambia la ruta) más las 5 propias. Si no hay manifest, o el publicado no trae
`auditar_archivo` (copia en disco de un plugin anterior a 0.8), añade una entrada propia de
`auditar_archivo` para que el respaldo ODA siga visible. Reintenta cargar el manifest en cada
llamada; cuando cambia (versión, nombres, descripciones o schemas) envía `notifications/tools/list_changed`
y avisa en `estado`/`ping` (protocol.md §7.1).

## Fase 1 (lectura, desde protocolo 0.2)

Herramientas del manifest, en este orden (protocol.md §4):

| Herramienta | Escritura | Requiere documento |
|---|---|---|
| `ping`, `estado` | no | no |
| `listar_capas` | no | sí |
| `linea` | **sí** | sí |
| `consultar_entidades`, `propiedades`, `textos`, `bloques`, `seleccion_actual`, `xrefs`, `vista_png` | no | sí |
| `resumen_archivo` | no | no |

Seguidas de las 10 de Fase 2, las 14 de Fase 3, las 11 de Fase 4A (abajo) y de las propias del servidor:
`instancias`, `seleccionar_instancia`.

**`vista_png` → imagen MCP (protocol.md §6.2).** Es la única herramienta con traducción
propia. El resultado del plugin se devuelve como:

1. `ImageContent(type="image", data=<png_base64 tal cual>, mimeType=<mime>)`;
2. `TextContent` con el JSON del resultado **sin** `png_base64` (`ancho`, `alto`, `bbox`,
   `mecanismo`, `espacio`, `layout` (desde 0.3.1), `vista_restaurada`, `advertencias`, …).

`structuredContent` es ese mismo objeto sin `png_base64`. Si el plugin responde sin
`png_base64` o sin `mime` (cadenas), el servidor devuelve `isError: true` con
`[INTERNAL] vista_png devolvió una respuesta sin imagen.` El PNG no se decodifica ni se valida.

**Texto del plano = datos no confiables (protocol.md §2.3 y §7).** Las respuestas que pueden
llevar texto escrito en el dibujo (`consultar_entidades`, `propiedades`, `textos`, `bloques`,
`seleccion_actual`, `resumen_archivo`) traen `"origen_texto": "contenido_del_plano"`. Las
instrucciones MCP del servidor (`SERVER_INSTRUCTIONS` en `server.py`) avisan al modelo de que
esos textos los escribieron terceros y deben tratarse como datos, nunca como instrucciones.
Ni el servidor ni el plugin interpretan esos textos.

## Fase 2 (auditoría, protocolo 0.3)

Diez herramientas más del manifest (protocol.md §5C), todas con `requiere_documento: true`.
El servidor **no tiene código propio** para ninguna: se publican y se llaman de forma genérica.
Las de escritura (`escritura: true`) se envían como no idempotentes (sin reintento si la
conexión se corta esperando la respuesta).

| Herramienta | Escritura | Qué hace |
|---|---|---|
| `auditar_capas` | no | Clasifica las capas frente a `lps.json` (estándar, con diferencias, cype, mcp, ajena, genérica…) con sugerencia; solo propone. |
| `entidades_en_capa_0` | no | Cuenta y lista las entidades en la capa 0 (por defecto en todos los espacios). |
| `auditar_bloques` | no | Bloques con unidades distintas a las del dibujo, nombres sospechosos y sin uso. |
| `areas_por_capa` | no | Suma por capa el área de las entidades cerradas; con `detalle` lista cada entidad. |
| `verificar_cuadro_areas` | no | Detecta cuadros de áreas en los textos y comprueba su aritmética (y contornos opcionales). |
| `duplicados` | no | Agrupa entidades idénticas por hash de geometría redondeada a `tolerancia`. |
| `textos_fuera_de_escala` | no | Textos con altura muy alejada de la mediana del espacio. |
| `auditar_dibujo` | no | Ejecuta las 8 reglas y devuelve resumen + hallazgos priorizados (alta → media → baja), paginados. |
| `marcar_hallazgos` | **sí** | Dibuja nube de revisión + MTEXT por marca en una capa `MCP-*` (un CTRL+Z). |
| `limpiar_observaciones` | **sí** | Borra las entidades de una capa `MCP-*` (exige `confirmar: true`; un CTRL+Z). |

Los hallazgos usan el formato común de §5C.0 (`id`, `regla`, `severidad`, `mensaje`, `handles`,
`bbox`, `espacio`, `datos`). Todas las de lectura llevan `"origen_texto": "contenido_del_plano"`;
las de escritura no.

## Fase 3 (dibujo primitivo, protocolo 0.4)

Catorce herramientas más del manifest (protocol.md §5D), **todas de escritura** (`escritura: true`,
se envían como no idempotentes) y con `requiere_documento: true`. El servidor **no tiene código propio**
para ninguna: se publican y se llaman de forma genérica, y sus errores se reenvían como `[<CODE>] <mensaje>`.

| Herramienta | Qué hace |
|---|---|
| `crear_capa` | Crea una capa del estándar LPS o `MCP-*` (idempotente). |
| `polilinea` | LWPOLYLINE abierta o cerrada (bulges, ancho); rechaza duplicados y autointersecciones. |
| `circulo`, `arco` | Círculo; arco antihorario entre dos ángulos en grados. |
| `texto`, `mtext` | TEXT / MTEXT con el texto del cliente literal. |
| `insertar_bloque` | Bloque del dibujo o de la biblioteca del plugin (`LPS_ROTULO`, `Puerta Dinamica LM`, `PuertaBaño`, `VENTANA01`, `TANQUE 1000 LTS`), con atributos, propiedades dinámicas y conversión de unidades. |
| `cota` | Cota lineal o alineada; devuelve la medida. |
| `hatch` | Sombreado asociativo sobre entidades cerradas o sobre un polígono. |
| `mover`, `copiar`, `rotar`, `escalar`, `borrar` | Por handle; todo o nada; `confirmar: true` si alguna entidad no está en una capa `MCP-*`. |

Reglas comunes que llegan al cliente como errores del plugin: `[UNITS_UNKNOWN] El dibujo no declara
unidades (INSUNITS=0).` (salvo `aceptar_sin_unidades: true`), `[NOT_FOUND]` de capa, handle, layout,
bloque, estilo o patrón, e `[INVALID_PARAMS]` de capa bloqueada/congelada, de `confirmar` y de ubicación
fuera de la extensión del espacio (salvo `forzar: true`). Respuesta común (R6): `pid`, `handles`, `capa`,
`capa_creada`, `espacio`, `insunits`, `unidades`, `advertencias` + campos propios. `linea` adopta las
mismas reglas desde 0.4 (§5.4).

## Fase 4A (geometría paramétrica, protocolo 0.5)

Once herramientas más del manifest (protocol.md §5E), todas con `requiere_documento: true`. El servidor **no tiene
código propio** para ninguna. Tres son de lectura (`escritura: false`, se envían como idempotentes) y ocho escriben.

| Herramienta | Escritura | Qué hace |
|---|---|---|
| `punto_de`, `distancia`, `proyectar` | no | Resuelven `PuntoRef` (coordenada, `{handle, punto}`, `{handle, interseccion_con, cerca_de}`), distancias y proyecciones sobre curvas existentes |
| `muro`, `ejes_rejilla`, `recinto`, `offset`, `array_rectangular`, `array_polar` | **sí** | Generadoras: respuesta común G6 (`preview`, `preview_id`, `caduca`, `handles`, `entidades`, `capas`, `puntos_resueltos`, `numeros`…) |
| `confirmar`, `descartar_preview` | **sí** | Convierten en definitiva u olvidan una previsualización (`preview_id`) |

Errores que llegan al cliente: `[UNITS_UNKNOWN] …` con `INSUNITS = 0` en las generadoras (sin
`aceptar_sin_unidades`: pasarlo da `[INVALID_PARAMS] Parámetros inválidos: aceptar_sin_unidades: propiedad no permitida`),
`[INVALID_GEOMETRY] Geometría no construible: <detalle>` (−32005) y `[NOT_FOUND] No existe ninguna previsualización con
id '<id>' (caducada, descartada, confirmada o de otra sesión).`

## Fase L (limpieza de planos, protocolo 0.6)

Cuatro herramientas más del manifest (protocol.md §5F), todas de **escritura** (`escritura: true`, se envían como no
idempotentes) y con `requiere_documento: true`. El servidor **no tiene código propio** para ninguna. Todas tienen los
parámetros comunes `confirmar` y `preview` (§5F.0, L1): **sin `confirmar: true` nunca escriben** (devuelven el
inventario exacto de lo que harían, sin paso de deshacer); `preview: false` sin `confirmar: true` →
`[INVALID_PARAMS] Parámetros inválidos: confirmar: debe ser true para escribir; omita preview (o pase preview: true)
para solo previsualizar`; `confirmar: true` + `preview: true` sigue siendo previsualización.

| Herramienta | protocol.md | Qué hace |
|---|---|---|
| `borrar_fuera_de` | §5F.1 | Borra lo que queda fuera de un contorno (polilínea cerrada o círculo) por caja; avisa de los viewports de papel afectados |
| `purgar` | §5F.2 | Purga símbolos sin uso hasta el punto fijo (bloques, capas, tipos de línea, estilos, regapps) |
| `normalizar_capas` | §5F.3 | Reasigna las entidades de cada capa origen de un mapa a su capa destino (concepto de `lps.json`, existente o creada) |
| `corregir_unidades_bloques` | §5F.4 | Fija `BlockTableRecord.Units` = `INSUNITS` en los bloques que `auditar_bloques` marca con metadato de unidades incorrecto |

Respuesta común (L6): `pid`, `preview`, `escrito`, `insunits`, `unidades`, `advertencias` + campos propios. Con
`INSUNITS = 0` solo la escritura de `corregir_unidades_bloques` da `[UNITS_UNKNOWN] …` (sin escape); las demás
escriben con la advertencia de unidades (desde 0.8.3) y la previsualización nunca rechaza.

## Fase 5 (auditoría de archivos cerrados y carpetas, protocolo 0.8)

`auditar_archivo` audita un `.dwg` cerrado sin abrirlo en pantalla (§5H.1). `origen` es del servidor, que lo retira antes
de llamar al plugin: `auto` (por defecto) usa el plugin si hay instancia y si no ODA; `plugin` da `NO_INSTANCE` sin
instancia; `oda` no toca AutoCAD. La llamada directa con ODA devuelve los mismos campos auxiliares que el plugin (`_cype`, `_lamina`,
`_bloques`; la mediana va en `resumen_reglas`) y exige ≥ 10 × tamaño + 200 MB libres, como el lote.

| Herramienta | Qué hace |
|---|---|
| `auditar_carpeta` | Lanza el lote y devuelve un `id` de inmediato; `reanudar: id` continúa uno `cancelada`, `detenida`, `error`, o `terminada` con filas `omitido`/`cancelado`, sin repetir las filas `ok` y `error` |
| `estado_auditoria` | Progreso, agregados y filas paginadas; filtros `todos`, `con_hallazgos`, `no_metros`, `errores`, `exports_cype`, `laminas`, `planos_trabajo`, `unidad_discrepa` |
| `cancelar_auditoria` | Cancelación cooperativa: el archivo en curso termina |

Un solo lote activo. Por archivo: stat del original, espacio libre (≥ 2 × tamaño + 200 MB con el plugin, ≥ 10 × tamaño +
200 MB con ODA porque el DXF mide unas 7 veces el DWG; si no, `detenida`), copia a `%LOCALAPPDATA%\MCPAutoCAD\tmp\<id>\`,
sha256 de la copia (el original solo se lee una vez por red), una llamada a `auditar_archivo` (`copiar: false`) o al
respaldo, borrado de la copia y registro. En `<carpeta de auditorías>\<id>\`: `estado.json`, `archivos.jsonl` y, al terminar, cancelar o
detenerse, `informe.json` e `informe.md`. Nunca se escribe en la carpeta auditada (se rechaza una carpeta que contenga la
de informes o la temporal). `INSUNITS` se informa como declarado, nunca como unidad real. `archivos.jsonl` solo crece
(una línea con `fsync` por archivo; al cargar, la última fila de una ruta gana) y se reescribe una vez al arrancar o
reanudar; `estado.json` es atómico.

Con el plugin, `xrefs_no_resueltas` no se pide y queda en `no_disponible` con una advertencia: cada archivo se lee desde una
copia temporal, y `XrefsCheck` resolvería las rutas relativas contra esa carpeta dando hallazgos `alta` falsos
(§5H.0.3). Con ODA sí se comprueba, contra la carpeta del original. Si la instancia cae a mitad de archivo, ese archivo ya
se audita con ODA y las xrefs entran.

Tras un timeout del archivo (`PIPE_TIMEOUT`, o `INTERNAL` con `detalle: "timeout"`, §3 regla 7; la fila queda `omitido`)
el hilo principal sigue ocupado: el lote no pide el siguiente archivo hasta que `estado` responda (cada 5 s, tope 2 min).
Se sondea con `estado` porque `ping` se responde desde el hilo del pipe aunque el principal esté ocupado (§3 regla 1).

`es_lamina`, `es_export_cype` y `rotulo_presente` usan campos auxiliares que el lote no publica (`_cype`, `_lamina`,
`_bloques`, `_mediana_texto`, ver `mcp_autocad/lote.py`). Los dan los dos orígenes: con ODA se miden sobre el documento
ezdxf; con el plugin vienen en su respuesta. Solo llegan `null` si la medición auxiliar del plugin no cabe en su plazo o
falla: entonces esos campos salen `null`, `n_exports_cype` y `n_laminas` no los cuentan y `estado_auditoria` lo advierte,
y `rotulo_presente` ve solo `bloques_mas_usados` (top 20) y los layouts. `unidades_inconsistentes` compara `INSUNITS` con `MEASUREMENT` (§5.2):
no equivale a "hay hallazgo de la regla `unidades`", que también salta si `INSUNITS` difiere del estándar.

Con ODA, `alta 0` no significa "sin hallazgos graves": sin AutoCAD no se mide el tamaño real de los bloques, y los
`unidades_distintas` quedan en `media` con `datos.medido = false` aunque el plugin subiría a `alta` los de tamaño
implausible (plugin alta 4 / media 39 / baja 37, ODA alta 0 / media 43 / baja 37, mismos 80 hallazgos). `auditar_archivo` lo advierte con
`<n> bloque(s) con unidades distintas quedan en media porque sin AutoCAD no se mide su tamaño real; alguno podría ser alta: reaudite con el plugin para separarlos.`
(solo cuentan los bloques con `datos.n_no_compensadas` > 0, los únicos que podrían ser `alta`; solo compensados no
avisan), y el lote cuenta `n_bloques_sin_medir` por fila (0 con el plugin, y en filas antiguas sin el campo), lo agrega y
advierte en `estado_auditoria`, `informe.json` e `informe.md` si hay alguno, también en un lote mixto:
`<n> bloque(s) con unidades distintas en <k> archivo(s) auditados con ODA quedan en media porque sin AutoCAD no se mide su tamaño real; alguno podría ser alta, así que por_severidad puede subestimar alta: reaudite esos archivos con el plugin para separarlos.`
`informe.md` añade además una línea bajo "Por severidad (sin duplicados)" que remite a ese aviso (protocol.md §5H.3, §5H.5).

Unidad real (0.8.1): `INSUNITS` es lo declarado. Con ODA, `lote.medir_auxiliares` añade `_unidad`, evidencia barata de una
pasada por el modelo (`oda.audit.evidencia_unidad`: mediana de cotas de longitud, extensión p5–p95 de LINE/LWPOLYLINE,
largo mediano de segmentos, `$DIMLFAC`), y la función pura `lote.unidad_probable(evidencia, insunits)` la convierte en `m`/`cm`/`mm` (`in` solo si el
archivo declara INSUNITS imperial) o `indeterminada` (si cabe más de una unidad, o ninguna, no adivina; cm frente a
mm suele quedar indeterminada). Rangos en metros: cota 0,1–100 (0,02–100 para mm desde 0.8.2), extensión 2–20 000, largo 0,02–20. Desde 0.8.2 la extensión no cuenta en planos mezclados o con extensión absurda (protocol.md §5H.2). Con el plugin no hay evidencia: `unidad_probable`
`null` y `estado_auditoria` lo advierte. El aviso de "mm con textos de altura mediana menor que 1" queda solo para las
filas sin unidad probable conocida (sin evidencia o `indeterminada`); con ella manda `unidad_coherente: discrepa`.
Si hay filas sin desglose de duplicados (lotes 0.8.0), `informe.md` lo dice en el titular y en el total de duplicados.

## Fase 4B (vanos, encuentros, cotas y ensamblado CYPE, protocolo 0.7)

Cuatro herramientas más del manifest (protocol.md §5G), todas de **escritura** (`escritura: true`, se envían como no
idempotentes) y con `requiere_documento: true`. El servidor **no tiene código propio** para ninguna.

| Herramienta | protocol.md | Qué hace |
|---|---|---|
| `vano` | §5G.1 | Generadora: parte el contorno cerrado de un muro en dos e inserta el bloque de puerta o ventana. Exige `confirmar: true` en muros fuera de `MCP-*`, ya con `preview: true`. |
| `union_muros` | §5G.2 | Generadora: une dos cuadriláteros convexos en L, T, cruce o colineal, con la misma protección. |
| `cotas_automaticas` | §5G.3 | Generadora: cadena o cota total sobre los puntos de las entidades dadas, a `separacion` del lado pedido. |
| `insertar_export` | §5G.4 | Escritura sin previsualización: inserta un DWG de CYPE como bloque con el factor de unidades (mm → m). La ruta es de solo lectura. |

## Variables de entorno

| Variable | Efecto |
|---|---|
| `MCP_AUTOCAD_PID` | Fuerza la instancia por PID (prioridad sobre `seleccionar_instancia`). |
| `MCP_AUTOCAD_TIMEOUT` | Segundos de espera de respuesta (por defecto 15). `ping` usa siempre 3 s. |
| `MCP_AUTOCAD_LOG` | Nivel de log en stderr. |
| `MCP_AUTOCAD_AUDITORIAS` | Carpeta de informes del lote (por defecto `%LOCALAPPDATA%\MCPAutoCAD\auditorias`). Las copias temporales van siempre a `%LOCALAPPDATA%\MCPAutoCAD\tmp\<id>`. |
| `MCP_AUTOCAD_ODA` | Ruta de `ODAFileConverter.exe` (por defecto `C:\Program Files\ODA\ODAFileConverter 27.1.0\ODAFileConverter.exe`). |
| `MCP_AUTOCAD_ODA_TIMEOUT` | Segundos por archivo y por conversor del respaldo ODA (por defecto 300 desde 0.8.2; antes 120). |
| `MCP_AUTOCAD_ACCORE` | Ruta de `accoreconsole.exe`, respaldo cuando ODA no genera el DXF (por defecto `D:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe`; si no existe, el error dice `oda_no_lee_sin_respaldo`). |
| `MCP_AUTOCAD_LPS` | `lps.json` que lee el servidor (por defecto `standards/lps.json` del proyecto); si falta, no se limitan las carpetas. |

Conexión al pipe: 1 s (si el pipe está ocupado se reintenta hasta 1 s).

## Mensajes de error esperados

Se devuelven como resultado de herramienta con `isError: true`:

- AutoCAD cerrado o plugin sin cargar: `AutoCAD no está abierto o el plugin no cargó.`
- Varias instancias sin seleccionar: `Hay varias instancias de AutoCAD abiertas (PID: 18244, 20312). Use seleccionar_instancia para elegir una.`
- PID pedido inexistente: `No hay ninguna instancia de AutoCAD con PID 99999. Instancias disponibles (PID): 18244, 20312. Use seleccionar_instancia.`
- `vista_png` sin imagen: `[INTERNAL] vista_png devolvió una respuesta sin imagen.`
- Sin respuesta: `El plugin no respondió en 15 s.`
- Conexión cortada: `Se perdió la conexión con AutoCAD (¿se cerró?).`
- Errores del plugin: `[<CODE>] <mensaje>`, p. ej. `[BUSY] Hay un comando activo en AutoCAD: LINE. Reintente cuando termine.`

## Pruebas (sin AutoCAD)

```powershell
cd C:\LPSAPP\MCPAutoCAD\server
python -m pytest tests -q
```

- `tests/fake_plugin.py`: plugin simulado en memoria con framing real; publica las 55
  herramientas de 0.8 y simula un dibujo pequeño (3 LINE en MUROS, 1 LWPOLYLINE cerrada,
  2 inserciones de DP con ANCHO/TIPO, TEXT «COCINA», MTEXT, xref TOPO; para Fase 2:
  2 LINE duplicadas, un cuadro de áreas de 4 TEXT, un TEXT de altura 2.5 y los bloques
  `BASURA_jgytfyuh` y `DP` en milímetros). Para la Fase 3 modifica ese dibujo en memoria
  (handles nuevos, mover/copiar/rotar/escalar/borrar), resuelve capas con `standards/lps.json`
  (solo lectura), simula la biblioteca con `LPS_ROTULO` (25 atributos reales, `PLANO_N` con su
  formato en línea) y reproduce `UNITS_UNKNOWN` (campo `insunits=0` del estado), capas
  bloqueadas/congeladas/apagadas, `confirmar` y la comprobación de ubicación. Para la Fase 4A resuelve
  `PuntoRef` sobre el dibujo ficticio, simula las seis generadoras con la forma G6 (geometría aproximada),
  `INVALID_GEOMETRY`, el almacén de previsualizaciones con reloj simulado (`state.ahora`, caducidad 15 min, tope 20,
  anclas con huella), `confirmar`/`descartar_preview`, las huérfanas de `MCP-PREVIEW` en `estado` y un contador de
  pasos de deshacer simulados (`state.pasos_deshacer`). Para la Fase L (bandera `faseL` del estado) usa un dibujo
  propio (contorno `1C0`, entidades dentro/fuera/que cruzan/sin caja, VIEWPORT `1C8`, capas y estilos sin uso, bloques
  anidados) y simula las cuatro herramientas de §5F con la previsualización sin estado de L1. Para la Fase 4B (bandera
  `fase4b` del estado) añade muros y contornos con handles `2B0`–`2C0`, simula `vano`, `union_muros` y
  `cotas_automaticas` con entidades planificadas BORRAR, INSERT y DIMENSION, `insertar_export` con exports simulados
  (`state.exports`), `propiedades_dinamicas` sobre la biblioteca ampliada y `conservar_aspecto`. Para la Fase 5,
  `auditar_archivo` responde la forma de §5H.1 sobre los datos de `state.archivos` (dict ruta → datos simulados).
- `tests/test_server.py`: servidor genérico, instancias, manifest en disco y Fase 1.
- `tests/test_fase2.py`: las 10 herramientas de auditoría a través del cliente MCP.
- `tests/test_fase3.py`: las 14 herramientas de dibujo a través del cliente MCP y del `PipeClient`
  (81 casos: forma R6, errores reenviados, idempotencia de `crear_capa`, `LPS_ROTULO`, `confirmar`,
  manifest en disco, aviso de versión de protocolo).
- `tests/test_fase4a.py`: las 11 herramientas de la Fase 4A a través del cliente MCP y del `PipeClient`
  (121 casos: publicación y `escritura`, PuntoRef y sus errores exactos, G6 de cada generadora, G2 sin escape,
  R4/`forzar`, ciclo preview → confirmar, caducidad, huérfanas, anclas, otro documento, tope 20, arrays).
- `tests/test_faseL.py`: las 4 herramientas de la Fase L a través del cliente MCP y del `PipeClient`
  (117 casos: publicación, descripciones literales, L1–L6 para cada herramienta, criterio dentro/fuera por caja,
  viewports, punto fijo de la purga, validación del mapa 1–6, motivos de `corregir_unidades_bloques`).
- `tests/test_fase4b.py`: las 4 herramientas de la Fase 4B y los cambios compatibles, a través del cliente MCP y del
  `PipeClient` (264 casos: publicación, descripciones literales frente a protocol.md y a los handlers, G6 con
  `handles_borrados`, G2, R4, protección `confirmar` ya en preview, ciclo preview → confirmar, anclas, mensajes
  exactos de §6, `propiedades_dinamicas`, `conservar_aspecto`, rutas de `insertar_export`).
- `tests/test_fase5.py`: `auditar_archivo` y su enrutado con `origen` a través del cliente MCP y del `PipeClient`, y el
  lote con un `auditar_uno` falso sobre archivos ficticios en `tmp_path` (más de 250 casos: publicación, descripciones
  literales frente a protocol.md, progreso, agregados, filtros, paginación, cancelación, reanudación, BUSY, espacio en
  disco, caída a ODA, informe, y que nada se escribe en la carpeta auditada). No depende de ODA.
- `tests/test_oda_audit.py`: paridad del respaldo con ODA real sobre copias de los fixtures (`skip` sin ODA).
- `tests/test_pipe_win32.py`: pipe real con pywin32 (solo Windows).
- `tests/test_stdio_smoke.py`: arranca `python -m mcp_autocad` por stdio real.
