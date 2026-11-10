# Gate de la Fase 4A (geometría paramétrica): checklist en vivo

Este documento comprueba en AutoCAD real las once herramientas de la Fase 4A (`PLAN.md` §5, "Fase 4A — Geometría paramétrica"): tres lecturas (`punto_de`, `distancia`, `proyectar`), seis generadoras (`muro`, `ejes_rejilla`, `recinto`, `offset`, `array_rectangular`, `array_polar`) y las dos de previsualización (`confirmar`, `descartar_preview`). El contrato está en `protocol.md` v0.5 (cabecera "Cambios 0.4 → 0.5", §3.12, §4, §5.2, **§5E** completa, §6 y §9). En cada generadora se comprueban las reglas G1–G9 de §5E.0: **un CTRL+Z por llamada** (G1), `UNITS_UNKNOWN` **sin escape** con `INSUNITS = 0` (G2), capas por concepto y previsualización en `MCP-PREVIEW` (G3), rechazo fuera de la extensión salvo `forzar` (G4), `espacio` (G5), respuesta común con números (G6), textos literales (G7), validación antes de escribir con `INVALID_GEOMETRY` (G8) y sin uniones con muros existentes (G9). **Si una prueba falla y no se arregla con lo que indica "Si falla", el gate no se aprueba y no se empieza la Fase 4B.**

- Quién ejecuta las pruebas: el usuario, en su equipo, con AutoCAD 2026 abierto con interfaz y Claude Code en otra ventana. Ningún agente ejecuta este checklist ni instala el plugin.
- Dibujos: **solo** los de `C:\LPSAPP\MCPAutoCAD\tests\fixtures\`. Copia de trabajo: **`tests\fixtures\fase4_trabajo.dwg`**, una copia de `copia_real.dwg` (plano madre 6X12 en metros, ~36 955 entidades en modelo, 161 capas, `Layout1` y `Layout2`, estilo de cota actual `COTA75`, capa actual `MUROS`) que **crea el usuario (o el orquestador) antes del gate** (paso 0.7) y que la limpieza final **borra**. **`copia_real.dwg` y `copia_abrir.dwg` no se abren en AutoCAD ni se modifican en ningún momento.** Este checklist nunca abre ni apunta a nada de la unidad de red de expedientes.
- Datos reales del plano usados abajo (comprobados con ODA File Converter + ezdxf sobre una copia de `copia_real.dwg` el 2026-10-26; los mismos handles existen en `fase4_trabajo.dwg`):
  - `6D136`: LINE de `MUROS` de (3381.1632, 3986.4523) a (3375.1632, 3986.4523) (horizontal, 6 m).
  - `6D350`: LINE de `MUROS` de (3381.1632, 3978.3523) a (3381.1632, 3986.4523) (vertical, 8,1 m; esquina con `6D136` en (3381.1632, 3986.4523)).
  - `6D34B`: LINE de `MUROS` de (3375.1632, 3987.6523) a (3375.1632, 3978.3523) (vertical; corta a `6D136` en (3375.1632, 3986.4523)).
  - Ejes reales: burbuja `859EE` = CIRCLE en `EJES` de centro (3375.3382, 3977.388) y radio **0,1538**; rótulo `859F0` = TEXT `A` en `EJES`, altura **0,175**, estilo `75`, justificación centro/medio (`halign 1`, `valign 2`) en el centro de la burbuja; el eje `859EF` (LINE en `EJES`, escala de tipo de línea 0,03) termina a 0,1539 del centro: **tangente a la burbuja**.
  - `294`: LINE de `MUROS` de (−2198.21, 3729.21) a (−2198.21, 3732.41) (la misma del gate de Fase 3).
- Consola: PowerShell 7 (`pwsh`), con el directorio actual en `C:\LPSAPP\MCPAutoCAD`, y **la misma consola** durante todo el gate (las pruebas reutilizan `Invoke-Rpc`, `Test-Borrado`, `Ver`, `$base`).
- **Las generadoras y `confirmar`/`descartar_preview` escriben en el dibujo.** Úselas solo con `fase4_trabajo.dwg` activo. Después de cada escritura de prueba se pulsa **CTRL+Z una sola vez** en AutoCAD salvo que la prueba diga otra cosa: así se comprueba G1 y el plano vuelve a su estado.
- Zona libre del modelo para dibujar: **x 5200–5260, y 5000–5040** (por encima de la extensión del plano, que llega a y ≈ 4208, pero dentro de la extensión ampliada un 10 %, que llega a y ≈ 5119). Los nombres de recinto son ficticios.

---

## 0. Preparación (una sola vez)

1. **Compilar y ejecutar todas las pruebas** (AutoCAD puede estar abierto):
   ```powershell
   cd C:\LPSAPP\MCPAutoCAD
   pwsh -File scripts\build.ps1 -ServerTests
   ```
   Debe terminar con `=== BUILD CORRECTO ===` y `0 Warning(s)`. La línea de `dotnet test` debe indicar **al menos 900** pruebas superadas y 0 con error (708 al cerrar la Fase 3; 743 al empezar la 4A; 868 al cerrar la implementación; 883 tras la primera ronda de correcciones de la auditoría; 886 tras la segunda; 896 tras las correcciones del gate en vivo del 2026-10-27; 900 tras las revisiones O1/O3) y la de `pytest` **al menos 379 passed** (227 al cerrar la Fase 3; 348 al cerrar la implementación; 367 tras la primera ronda de correcciones; 370 tras la segunda; 376 tras las correcciones del gate en vivo; 379 tras O1/O3). Son las cifras del build integrado del 2026-10-27. Anote ambos números en la tabla final. Si cambia código del plugin durante el gate, vuelva a ejecutar `build.ps1` (no solo `dotnet test`) antes de reinstalar.
2. **Cerrar todas las instancias de AutoCAD** e **instalar el bundle**:
   ```powershell
   Get-Process acad -ErrorAction SilentlyContinue   # no debe listar nada
   pwsh -File scripts\install-plugin.ps1 -DryRun
   pwsh -File scripts\install-plugin.ps1
   Test-Path "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\lps.json"                   # True
   Test-Path "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\biblioteca\LPS_ROTULO.dwg"   # True
   (Get-Item "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\MCPAutoCAD.Plugin.dll").VersionInfo.FileVersion   # 0.5.0.0
   ```
   Sin `lps.json` no valen las pruebas 2–7 (los conceptos `muros`, `ejes`, `textos`, `achurados` y la capa `MCP-PREVIEW` salen de `lps.json`).
3. **Abrir AutoCAD 2026** (sin abrir ningún plano todavía) y escribir **`MCPESTADO`**: debe mostrar `MCPAutoCAD 0.5.0 (protocolo 0.5)` y el pipe `\\.\pipe\mcp-autocad-<usuario>-<PID>`.
4. **Versión** desde PowerShell:
   ```powershell
   pwsh -File scripts\check-pipe.ps1
   pwsh -File scripts\rpc-call.ps1 -Method ping
   ```
   `ping` debe traer `"protocol_version": "0.5"` y `"plugin_version": "0.5.0"`.
5. **Funciones auxiliares y manifest.** Defina en la consola:
   ```powershell
   function Invoke-Rpc([string]$Method, [string]$Params = '{}') {
       (& .\scripts\rpc-call.ps1 -Method $Method -ParamsJson $Params -AsObject -TimeoutSec 60).Respuesta | ConvertFrom-Json
   }
   function Test-Borrado([string[]]$Handles) {
       # Tras un CTRL+Z: cada handle creado por la llamada debe dar NOT_FOUND.
       foreach ($h in $Handles) { "$h -> " + (Invoke-Rpc 'propiedades' ('{"handle":"' + $h + '"}')).error.data.code }
   }
   function Ver($r) {
       # Resumen de una respuesta G6 (§5E.0) o del error.
       if ($r.error) { "{0} {1} | detalle: {2}" -f $r.error.data.code, $r.error.message, $r.error.data.detalle; return }
       $r.result | Format-List preview, preview_id, preview_dibujada, caduca, handles, capa, capas, capas_creadas, espacio, advertencias
       $r.result.numeros | ConvertTo-Json -Depth 5 -Compress
   }
   (Invoke-Rpc 'tools/manifest').result.protocol_version          # 0.5
   (Invoke-Rpc 'tools/manifest').result.tools.name -join ', '
   (Invoke-Rpc 'tools/manifest').result.tools | Where-Object escritura | ForEach-Object name
   ```
   La segunda línea debe imprimir **exactamente** (47 herramientas, orden de protocol.md §4):
   `ping, estado, listar_capas, linea, consultar_entidades, propiedades, textos, bloques, seleccion_actual, xrefs, vista_png, resumen_archivo, auditar_capas, entidades_en_capa_0, auditar_bloques, areas_por_capa, verificar_cuadro_areas, duplicados, textos_fuera_de_escala, auditar_dibujo, marcar_hallazgos, limpiar_observaciones, crear_capa, polilinea, circulo, arco, texto, mtext, insertar_bloque, cota, hatch, mover, copiar, rotar, escalar, borrar, punto_de, distancia, proyectar, muro, ejes_rejilla, recinto, offset, array_rectangular, array_polar, confirmar, descartar_preview`.
   La tercera debe listar **25**: `linea`, `marcar_hallazgos`, `limpiar_observaciones`, las 14 de la Fase 3 y las 8 de escritura de la 4A (`muro` … `array_polar`, `confirmar`, `descartar_preview`). `punto_de`, `distancia` y `proyectar` **no** son de escritura. Si `plugin.log` (`%LOCALAPPDATA%\MCPAutoCAD\plugin.log`) contiene `Herramienta '<x>' del contrato no está implementada en este ensamblado; se omite del manifest`, el bundle instalado no es el del build integrado: **bloqueante**.
6. **Claude Code**: reinicie Claude Code en `C:\LPSAPP\MCPAutoCAD` (usa `.mcp.json`). Con `/mcp`, el servidor `autocad` debe estar conectado con **49 herramientas**: las 47 del manifest + `instancias` y `seleccionar_instancia`.
7. **Crear y abrir la copia de trabajo** (el usuario o el orquestador la crea aquí; nunca se trabaja sobre `copia_real.dwg`):
   ```powershell
   $trab = 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\fase4_trabajo.dwg'
   if (-not (Test-Path -LiteralPath $trab)) { Copy-Item -LiteralPath 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\copia_real.dwg' -Destination $trab }
   (Get-FileHash -LiteralPath 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\copia_real.dwg').Hash -eq (Get-FileHash -LiteralPath $trab).Hash   # True
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'fase4_trabajo.dwl*'   # nada
   New-Item -ItemType Directory -Force tests\tmp | Out-Null
   ```
   En AutoCAD: `OPEN` → `C:\LPSAPP\MCPAutoCAD\tests\fixtures\fase4_trabajo.dwg`. Quédese en la pestaña **Modelo**, `ZOOM` → `E`, sin comandos en curso.
8. **Línea base:**
   ```powershell
   (Invoke-Rpc 'estado').result | Select-Object insunits, unidades, measurement, advertencias   # 6, m, 1, []
   $base = (Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result
   $base.total                                      # ≈ 36955
   $base | ConvertTo-Json -Depth 5 | Set-Content tests\tmp\fase4_base_resumen.json
   (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.total                      # 161
   (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capas | Where-Object nombre -like 'MCP-*'   # ninguna
   foreach ($h in '6D136','6D350','6D34B','859EE','859F0','294') { (Invoke-Rpc 'propiedades' ('{"handle":"' + $h + '"}')).result | Select-Object handle, tipo, capa }
   ```
   Anote `$base.total`. Los seis handles deben existir con los tipos y capas de la introducción. Al final del gate el modelo debe volver exactamente a este resumen.

### Recordatorio: `scripts\rpc-call.ps1` y la respuesta G6

```powershell
pwsh -File scripts\rpc-call.ps1 -Method <método> [-ParamsJson '{...}'] [-Pid <n>] [-TimeoutSec 15] [-Repeat N] [-AsObject] [-Raw]
```

- `-ParamsJson` va **entre comillas simples** y contiene JSON válido.
- Errores: `INVALID_PARAMS` (`-32602`) con `message` = `"Parámetros inválidos: <detalle>"`; **`INVALID_GEOMETRY` (`-32005`, nuevo en 0.5)** con `message` = `"Geometría no construible: <detalle>"`; en ambos `data.detalle` se compara **literalmente** con protocol.md §5E y §6. Desde Claude Code llegan como `[INVALID_PARAMS] …` / `[INVALID_GEOMETRY] Geometría no construible: …`.
- **PuntoRef** (§5E.1): `[x, y]`, `{"handle":"6D136","punto":"inicio|fin|medio|centro|vertice","indice":n}` o `{"handle":"6D136","interseccion_con":"6D350","cerca_de":[x,y]}`.
- **Respuesta G6** de toda generadora (§5E.0): `pid`, `preview`, `preview_id`, `preview_dibujada`, `caduca`, `handles`, `entidades` (con `handle`, `tipo`, `capa`, `bbox` y medidas), `capa`, `capas`, `capas_creadas`, `espacio`, `puntos_resueltos` (`{ref, punto, ancla}`), `numeros` (siempre `n_entidades` y `bbox` + los de la herramienta), `insunits`, `unidades`, `advertencias`. `confirmar` añade `confirmado`, `herramienta` y `n_preview_borradas`.
- **Comprobación de CTRL+Z (G1):** tras cada escritura, en AutoCAD pulse **CTRL+Z una sola vez** y ejecute `Test-Borrado <handles>`: cada handle creado debe dar `NOT_FOUND`. Una previsualización dibujada es **su propio** paso; `confirmar` es **un** paso que borra la preview y crea la definitiva (§5E.7).

---

## Prueba 1: `punto_de`, `distancia`, `proyectar` sobre entidades reales

- [ ] **Prueba 1 aprobada**

**Objetivo:** las tres lecturas resuelven PuntoRef sobre las líneas reales de `MUROS`, devuelven los textos exactos de error de §5E.1 y **no** crean pasos de deshacer.

**Acción A (puntos de entidad, intersección y lecturas):**
```powershell
(Invoke-Rpc 'punto_de' '{"ref":{"handle":"6d136","punto":"inicio"}}').result | Format-List punto, ref, ancla, tipo_entidad, advertencias
(Invoke-Rpc 'punto_de' '{"ref":{"handle":"6D136","punto":"medio"}}').result.punto
(Invoke-Rpc 'punto_de' '{"ref":{"handle":"6D136","punto":"vertice","indice":1}}').result.punto
(Invoke-Rpc 'punto_de' '{"ref":{"handle":"6D136","interseccion_con":"6D34B"}}').result | Format-List punto, ancla, tipo_entidad
(Invoke-Rpc 'punto_de' '{"ref":{"handle":"859F0","punto":"centro"}}').result | Format-List punto, advertencias
(Invoke-Rpc 'distancia' '{"a":{"handle":"6D350","punto":"inicio"},"b":{"handle":"6D350","punto":"fin"}}').result | Format-List distancia, distancia_2d, dx, dy, dz, angulo
(Invoke-Rpc 'proyectar' '{"punto":[3378,3990],"handle":"6D136"}').result | Format-List punto_proyectado, distancia, parametro, distancia_desde_inicio, longitud_curva
```

**Acción B (errores; ninguno escribe):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method punto_de -ParamsJson '{"ref":{"handle":"6D136","interseccion_con":"6D350","punto":"fin"}}'
pwsh -File scripts\rpc-call.ps1 -Method punto_de -ParamsJson '{"ref":{"handle":"6D136","punto":"esquina"}}'
pwsh -File scripts\rpc-call.ps1 -Method punto_de -ParamsJson '{"ref":{"handle":"6D136","punto":"vertice","indice":5}}'
pwsh -File scripts\rpc-call.ps1 -Method punto_de -ParamsJson '{"ref":{"handle":"6D350","interseccion_con":"6D34B"}}'
pwsh -File scripts\rpc-call.ps1 -Method punto_de -ParamsJson '{"ref":{"handle":"859F0","punto":"inicio"}}'
pwsh -File scripts\rpc-call.ps1 -Method proyectar -ParamsJson '{"punto":[0,0],"handle":"859F0"}'
pwsh -File scripts\rpc-call.ps1 -Method punto_de -ParamsJson '{"ref":{"handle":"FFFFFFF","punto":"fin"}}'
```
Después pulse **CTRL+Z una vez**: debe deshacer **lo último que usted hizo antes del gate** (o nada), no una lectura. Si muestra `_u` sin deshacer nada, alguna lectura dejó un paso.

**Resultado esperado:**
- A: `inicio` de `6D136` = [3381.1632, 3986.4523, 0], `ref` con el handle en mayúsculas (`"6D136"`), `ancla` `"6D136"`, `tipo_entidad` `"LINE"`; `medio` = [3378.1632, 3986.4523, 0]; `vertice` 1 = [3375.1632, 3986.4523, 0]; la intersección con `6D34B` = [3375.1632, 3986.4523, 0] con `ancla` `"6D136+6D34B"`; `centro` del TEXT `859F0` → centro de su caja con la advertencia `"ref: 859F0 (TEXT) no es un círculo ni un arco; se usó el centro de su caja."`; `distancia` = 8.1 (dx 0, dy 8.1, `angulo` 90); `proyectar` → [3378, 3986.4523, 0], `distancia` ≈ 3.5477, `distancia_desde_inicio` 3.1632, `longitud_curva` 6. Todas con `insunits` 6, `unidades` `"m"`.
- B, en orden: `INVALID_PARAMS` `"ref: no puede llevar punto e interseccion_con a la vez"`; `"ref.punto: debe ser uno de inicio, fin, medio, centro, vertice"`; `"ref.indice: 5 fuera de rango; la entidad 6D136 tiene 2 vértice(s) (índices 0 a 1)"` (`data.n_vertices` 2); **`INVALID_GEOMETRY` (`-32005`)** `"Geometría no construible: ref: las entidades 6D350 y 6D34B no se intersecan"` (paralelas; `data.handle`, `data.interseccion_con`); `"ref: la entidad 859F0 (TEXT) no es una curva: no tiene inicio, fin ni medio"`; `"handle: la entidad 859F0 (TEXT) no es una curva"`; `NOT_FOUND` `"No existe ninguna entidad con handle 'FFFFFFF'."`.
- El CTRL+Z no encuentra ningún paso de las lecturas (§3.9).

**Si falla:** la intersección de dos rectas paralelas devuelve un punto: `Curve.IntersectWith` con `OnBothOperands` mal interpretado (§5E.1). Una lectura deja paso de deshacer: **bloqueante** (§3.9, regresión de F7).

---

## Prueba 2: `muro` recto, en L (inglete), cerrado (anillo con achurado) y rechazos

- [ ] **Prueba 2 aprobada**

**Objetivo:** contorno cerrado en `MUROS` con esquinas a inglete; anillo con achurado asociativo en `Achurados`; rechazos `INVALID_GEOMETRY`/`INVALID_PARAMS` sin escribir; un CTRL+Z por llamada.

**Acción A (recto, centrado, espesor 0,15):**
```powershell
$m1 = Invoke-Rpc 'muro' '{"eje":[[5200,5000],[5206,5000]],"espesor":0.15}'; Ver $m1
$m1.result.entidades | Format-Table handle, tipo, capa, n_vertices, cerrada, longitud, area
```
**CTRL+Z una vez** → `Test-Borrado $m1.result.handles`.

**Acción B (en L, inglete):**
```powershell
$m2 = Invoke-Rpc 'muro' '{"eje":[[5210,5000],[5216,5000],[5216,5004]],"espesor":0.15}'; Ver $m2
(Invoke-Rpc 'propiedades' ('{"handle":"' + $m2.result.handles[0] + '"}')).result.vertices
```
Mire la esquina en AutoCAD (`ZOOM` → `W` de 5214,4998 a 5218,5002). **CTRL+Z una vez** → `Test-Borrado`.

**Acción C (cerrado, anillo con achurado SOLID):**
```powershell
$m3 = Invoke-Rpc 'muro' '{"eje":[[5220,5000],[5226,5000],[5226,5004],[5220,5004]],"espesor":0.15,"cerrado":true,"achurado":{"patron":"SOLID"}}'; Ver $m3
$m3.result.entidades | Format-Table handle, tipo, capa, area, contorno_handles
```
**CTRL+Z una vez** → `Test-Borrado $m3.result.handles` (las tres entidades).

**Acción D (caras y eje por handle):**
```powershell
$m4 = Invoke-Rpc 'muro' '{"eje":[[5200,5010],[5206,5010]],"espesor":0.15,"representacion":"caras"}'; $m4.result.entidades | Format-Table handle, tipo, capa, longitud
```
**CTRL+Z una vez**.

**Acción E (rechazos; ninguno escribe):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method muro -ParamsJson '{"eje":[[5230,5000],[5230.1,5000]],"espesor":0.15}'
pwsh -File scripts\rpc-call.ps1 -Method muro -ParamsJson '{"eje":[[5230,5000],[5234,5000],[5234,5003],[5232,4999]],"espesor":0.15}'
pwsh -File scripts\rpc-call.ps1 -Method muro -ParamsJson '{"eje":[[5230,5000],[5234,5000],[5230,5000.5]],"espesor":0.15}'
pwsh -File scripts\rpc-call.ps1 -Method muro -ParamsJson '{"eje":[[5230,5000],[5234,5000],[5234,5000]],"espesor":0.15}'
pwsh -File scripts\rpc-call.ps1 -Method muro -ParamsJson '{"eje":[[5230,5000],[5234,5000]],"espesor":0.15,"cerrado":true}'
pwsh -File scripts\rpc-call.ps1 -Method muro -ParamsJson '{"eje":"859F0","espesor":0.15}'
pwsh -File scripts\rpc-call.ps1 -Method muro -ParamsJson '{"eje":[[5230,5000],[5234,5000]],"espesor":0.15,"representacion":"caras","achurado":{}}'
```

**Resultado esperado:**
- A: una `LWPOLYLINE` cerrada de 4 vértices en `MUROS` (concepto `muros`; `capas_creadas` `[]`), `area` 0.9, `bbox` [[5200, 4999.925], [5206, 5000.075]]; `numeros`: `longitud_eje` 6, `longitud_contorno` 12.3, `area` 0.9, `espesor` 0.15, `n_tramos` 1, `n_entidades` 1. ByLayer.
- B: una `LWPOLYLINE` cerrada de **6 vértices**, con la esquina exterior a inglete en (5216.075, 4999.925) y la interior en (5215.925, 5000.075); `area` 1.5 (= 0,15 × 10), `longitud_eje` 10. En pantalla, la esquina es un inglete limpio, sin solapes.
- C: `entidades` = 2 `LWPOLYLINE` (exterior 6,15 × 4,15, `area` 25.5225; interior 5,85 × 3,85, `area` 22.5225) + 1 `HATCH` `SOLID` **asociativo** en `Achurados` (`contorno_handles` = las dos polilíneas; isla interior sin rellenar); `capas` `["MUROS","Achurados"]`; `numeros.area` 3.0. **Un** CTRL+Z quita las tres.
- D: 2 `LWPOLYLINE` abiertas (caras) + 2 `LINE` de tapa (longitud 0.15), todas en `MUROS`.
- E, en orden: **`INVALID_GEOMETRY`** `"espesor: el tramo 0 (longitud 0.1) es más corto que el espesor 0.15"`; **`INVALID_GEOMETRY`** `"eje: el eje se autointerseca (tramos 0 y 2)"`; **`INVALID_GEOMETRY`** `"eje: el ángulo en el vértice 1 (7.1°) es demasiado agudo para el inglete (límite 4× el espesor)"` (el ángulo interior real es 7,125°; el plugin lo escribe con **una** cifra decimal, `WallOutline`: formato `0.0`); `INVALID_PARAMS` `"eje[2]: vértice duplicado con el anterior"`; `"eje: un muro cerrado necesita al menos 3 vértices"`; `"eje: la entidad 859F0 (TEXT) no es una línea ni una polilínea"`; `"achurado: solo con representacion 'contorno'"`. Ninguno deja paso de deshacer (un CTRL+Z después no debe mostrar `_u` vacío).

**Si falla:** el HATCH no es asociativo o rellena la isla: revise el orden `AppendEntity` → `Associative` → `AppendLoop` (§5D.7) en `GeometryDrawer`. Un rechazo deja un paso vacío: `UndoFailedGroup` no se aplica (§3.5). El muro se une con muros existentes: **G9** (fuera de 4A; no debe ocurrir).

---

## Prueba 3: `ejes_rejilla` frente a las burbujas reales

- [ ] **Prueba 3 aprobada**

**Objetivo:** líneas, burbujas tangentes y rótulos centrados como los del plano madre (r 0,154; texto 0,175, estilo `75`, centro/medio).

**Acción:**
```powershell
$e1 = Invoke-Rpc 'ejes_rejilla' '{"origen":[5200,5010],"separaciones_x":[3,3],"separaciones_y":[4],"extension":1,"radio_burbuja":0.154,"altura_texto":0.175,"estilo":"75"}'; Ver $e1
$e1.result.entidades | Group-Object tipo | Select-Object Name, Count
$e1.result.entidades | Where-Object tipo -eq 'TEXT' | Format-Table handle, texto, altura, justificacion, estilo, posicion
(Invoke-Rpc 'propiedades' '{"handle":"859EE"}').result | Format-List centro, radio, capa
(Invoke-Rpc 'propiedades' '{"handle":"859F0"}').result | Format-List texto, altura, estilo, capa
```
Compare en pantalla con los ejes reales (`ZOOM` → `W` de 3374,3976 a 3382,3980) y con los nuevos (5198,5008 a 5210,5017). **CTRL+Z una vez** → `Test-Borrado $e1.result.handles`.

**Acción B (rechazos):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method ejes_rejilla -ParamsJson '{"origen":[5200,5010],"separaciones_x":[3],"separaciones_y":[4],"rotulos_x":["1","1"],"extension":1,"radio_burbuja":0.154,"altura_texto":0.175}'
pwsh -File scripts\rpc-call.ps1 -Method ejes_rejilla -ParamsJson '{"origen":[5200,5010],"separaciones_x":[],"separaciones_y":[],"extension":1,"radio_burbuja":0.154,"altura_texto":0.175}'
pwsh -File scripts\rpc-call.ps1 -Method ejes_rejilla -ParamsJson '{"origen":[5200,5010],"separaciones_x":[3],"separaciones_y":[4],"extension":1,"radio_burbuja":0.154}'
```

**Resultado esperado:**
- A: 5 `LINE` + 5 `CIRCLE` + 5 `TEXT` (15 entidades), todas en `EJES` (concepto `ejes`; `capas_creadas` `[]` porque la capa existe); rótulos `1`, `2`, `3` (verticales) y `A`, `B` (horizontales); `numeros`: `n_ejes_x` 3, `n_ejes_y` 2, `ancho` 6, `alto` 4, `posiciones_x` [5200, 5203, 5206], `posiciones_y` [5010, 5014], `n_burbujas` 5. Burbuja del eje 1 en (5200, 5015.154): la línea termina en (5200, 5015) a **0,154** del centro (tangente, como `859EF`/`859EE`); TEXT con altura 0.175, estilo `75`, justificación `medio_centro` en el centro de la burbuja. En pantalla, igual que los ejes reales (salvo el radio 0,154 vs 0,1538).
- B: `INVALID_PARAMS` `"rotulos: rótulo repetido '1'"`; `"separaciones: se necesita al menos un eje en x o en y"`; `"altura_texto: obligatorio"` (la altura es obligatoria).

**Acción C (segunda pasada; corrección del gate 2026-10-27: estilo por defecto `75`):**
```powershell
$e3 = Invoke-Rpc 'ejes_rejilla' '{"origen":[5200,5010],"separaciones_x":[3],"separaciones_y":[4],"extension":1,"radio_burbuja":0.154,"altura_texto":0.175,"preview":true}'
$e3.result.entidades | Where-Object tipo -eq 'TEXT' | Format-Table texto, estilo; $e3.result.advertencias
Invoke-Rpc 'descartar_preview' ('{"preview_id":"' + $e3.result.preview_id + '"}') | Out-Null
```
Esperado: todos los TEXT con `estilo` `"75"` **sin** `estilo` en la llamada y sin la advertencia `"ejes_rejilla: el estilo de texto '75' … no existe en el dibujo; …"` (el estilo existe en `fase4_trabajo.dwg`). Burbuja y rótulo en rojo = color de la capa `EJES` (ByLayer), decisión del usuario del 2026-10-27.

**Si falla:** la burbuja está centrada en el extremo (no tangente): `GridLayout` no suma `extension + radio` (§5E.3.2). Textos con otra justificación: `PlannedText` `medio_centro` debe dar `TextMid` + `AlignmentPoint`.

---

## Prueba 4: `recinto`

- [ ] **Prueba 4 aprobada**

**Objetivo:** polilínea cerrada + MTEXT `medio_centro` con `NOMBRE` y el área con coma decimal; vértices anclados por intersección de líneas reales; polígono en U con el centroide fuera.

**Acción A (12,35 m² en la zona libre):**
```powershell
$r1 = Invoke-Rpc 'recinto' '{"contorno":[[5240,5000],[5245,5000],[5245,5002.47],[5240,5002.47]],"nombre":"COCINA","altura_texto":0.15}'; Ver $r1
$r1.result.entidades | Format-Table handle, tipo, capa, area, texto, adjunto
```
**CTRL+Z una vez** → `Test-Borrado $r1.result.handles`.

**Acción B (vértices por intersección de dos líneas existentes):**
```powershell
$r2 = Invoke-Rpc 'recinto' '{"contorno":[{"handle":"6D136","interseccion_con":"6D34B"},{"handle":"6D136","interseccion_con":"6D350"},{"handle":"6D350","punto":"inicio"},{"handle":"6D34B","punto":"fin"}],"nombre":"SALA","altura_texto":0.15}'
$r2.result.puntos_resueltos | Format-Table ref, punto, ancla
$r2.result.entidades | Format-Table handle, tipo, capa, texto
```
**CTRL+Z una vez**.

**Acción C (en U: el centroide cae fuera):**
```powershell
$r3 = Invoke-Rpc 'recinto' '{"contorno":[[5250,5000],[5256,5000],[5256,5004],[5254,5004],[5254,5001],[5252,5001],[5252,5004],[5250,5004]],"nombre":"PATIO","altura_texto":0.15}'
$r3.result.numeros | Format-List area, centroide, punto_etiqueta, etiqueta_en_centroide
```
**CTRL+Z una vez**.

**Acción D (rechazos):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method recinto -ParamsJson '{"contorno":[[5240,5000],[5241,5001],[5241,5000],[5240,5001]],"nombre":"X","altura_texto":0.15}'
pwsh -File scripts\rpc-call.ps1 -Method recinto -ParamsJson '{"contorno":[[5240,5000],[5241,5000],[5242,5000]],"nombre":"X","altura_texto":0.15}'
pwsh -File scripts\rpc-call.ps1 -Method recinto -ParamsJson '{"contorno":"6D136","nombre":"X","altura_texto":0.15}'
```

**Resultado esperado:**
- A: `LWPOLYLINE` cerrada en `MCP-DIBUJO` (se crea: `capas_creadas` `["MCP-DIBUJO"]`) + `MTEXT` en `TEXTOS` con `adjunto` `medio_centro` y texto **`COCINA` / `12,35 m²`** en dos líneas (coma decimal); `numeros.area` 12.35, `perimetro` 14.94, `etiqueta_en_centroide` true. Un CTRL+Z quita las dos y la capa.
- B: `puntos_resueltos` con `ancla` `"6D136+6D34B"`, `"6D136+6D350"`, `"6D350"`, `"6D34B"` y puntos (3375.1632, 3986.4523), (3381.1632, 3986.4523), (3381.1632, 3978.3523), (3375.1632, 3978.3523); etiqueta `SALA` / `48,60 m²`.
- C: `area` 18, `centroide` ≈ (5253, 5001.833) **fuera** del polígono (en la muesca), `etiqueta_en_centroide` false y `punto_etiqueta` dentro de una de las alas de la U (no en la muesca). Compruébelo en pantalla.
- D: `INVALID_PARAMS` `"contorno: la polilínea cerrada se autointerseca (tramos 0 y 2)"`; **`INVALID_GEOMETRY`** `"contorno: el área es 0"`; `INVALID_PARAMS` `"contorno: la entidad 6D136 no es una polilínea cerrada (LINE)"`.

**Si falla:** el área sale con punto decimal o con separador de miles: `PolygonMetrics.FormatArea` (§5E.3.3). La etiqueta de la U queda en la muesca: `PolygonMetrics.InteriorPoint`.

---

## Prueba 5: `offset`

- [ ] **Prueba 5 aprobada**

**Objetivo:** curva paralela del lado de un punto o a izquierda/derecha del sentido de la curva, en la capa de la original; un CTRL+Z.

**Acción:**
```powershell
$o1 = Invoke-Rpc 'offset' '{"handle":"6D136","distancia":0.15,"lado":[3378,3986]}'; Ver $o1
(Invoke-Rpc 'propiedades' ('{"handle":"' + $o1.result.handles[0] + '"}')).result | Format-List capa, inicio, fin
($o1.result.numeros.bbox | ConvertTo-Json -Compress) -eq ($o1.result.entidades[0].bbox | ConvertTo-Json -Compress)   # True (corrección del gate 2026-10-27)
```
**CTRL+Z una vez** → `Test-Borrado $o1.result.handles`.
```powershell
$o2 = Invoke-Rpc 'offset' '{"handle":"6D136","distancia":0.15,"lado":"izquierda"}'; (Invoke-Rpc 'propiedades' ('{"handle":"' + $o2.result.handles[0] + '"}')).result.inicio
```
**CTRL+Z una vez**.
```powershell
$o3 = Invoke-Rpc 'offset' '{"handle":"6D136","distancia":0.15,"lado":"derecha","capa":"MCP-DIBUJO"}'; (Invoke-Rpc 'propiedades' ('{"handle":"' + $o3.result.handles[0] + '"}')).result | Format-List capa, inicio
pwsh -File scripts\rpc-call.ps1 -Method offset -ParamsJson '{"handle":"859F0","distancia":0.15,"lado":"izquierda"}'
pwsh -File scripts\rpc-call.ps1 -Method offset -ParamsJson '{"handle":"859EE","distancia":0.2,"lado":[3375.3382,3977.30]}'
```
(La última es la acción **5B**, revisión O3: hacia dentro de la burbuja `859EE`, radio 0,1538, con distancia 0,2: no escribe nada.)
**CTRL+Z una vez** (por `$o3`).

**Resultado esperado:**
- `$o1`: una `LINE` en **`MUROS`** (capa de la original) de (3381.1632, 3986.3023) a (3375.1632, 3986.3023); `entidades[0].origen` `"6D136"`; `numeros`: `distancia` 0.15, `longitud_original` 6, `lado_punto` [3378, 3986, 0], **`bbox` [[3375.1632, 3986.3023], [3381.1632, 3986.3023]]** = `entidades[0].bbox` (la caja de la paralela elegida, de altura 0; el código de la primera pasada del 2026-10-27 devolvía la caja de la original ampliada 0,15, [[3375.0132, 3986.3023], [3381.3132, 3986.6023]], que abarca las dos candidatas: hallazgo 2, corregido).
- `$o2`: `6D136` va de x 3381 a 3375 (hacia −X): su izquierda es −Y → `inicio` y = 3986.3023. `$o3` (derecha): y = 3986.6023 en `MCP-DIBUJO`.
- `859F0` → `INVALID_PARAMS` `"handle: la entidad 859F0 (TEXT) no es una curva: no admite offset"`.
- 5B: **`INVALID_GEOMETRY`** `"lado: no se puede construir la paralela a 0.2 del lado pedido (la distancia supera o iguala el radio 0.1538…)"` (el radio con hasta 6 decimales, como esté en el dibujo); antes de la revisión O3 dibujaba en silencio la paralela exterior. Nada escrito.

**Si falla:** el lado sale invertido: el signo de `GetOffsetCurves` depende de la normal de la curva; la elección por cercanía al punto de lado (§5E.3.4) debe corregirlo. Si `$o1`/`$o2` responden `INVALID_PARAMS` `"capa: la curva 6D136 está en la capa bloqueada 'MUROS' y la paralela heredaría esa capa (regla R3); indique capa con otra capa de destino o desbloquee 'MUROS'"` (o `congelada`), la capa `MUROS` de la copia está bloqueada/congelada: es la conducta esperada por R3 (§5E.3.4), no un fallo; desbloquéela y repita.

---

## Prueba 6: `array_rectangular` y `array_polar`

- [ ] **Prueba 6 aprobada**

**Objetivo:** copias independientes (no matriz asociativa); `confirmar` exigido sobre entidades del usuario; tope 500; previsualización en `MCP-PREVIEW` y confirmación en la capa original.

**Preparación:** un círculo propio del MCP: `$c6 = (Invoke-Rpc 'circulo' '{"centro":[5210,5030],"radio":0.5}').result.handles[0]; $c6`

**Acción A (rectangular sobre MCP-DIBUJO, sin confirmar):**
```powershell
$a1 = Invoke-Rpc 'array_rectangular' ('{"handles":["' + $c6 + '"],"filas":2,"columnas":3,"sep_filas":2,"sep_columnas":2}'); Ver $a1
$a1.result.entidades | Format-Table handle, tipo, capa, centro, origen
```
**CTRL+Z una vez** → `Test-Borrado $a1.result.handles` (las 5 copias; el original sigue).

**Acción B (polar, vuelta completa):**
```powershell
$a2 = Invoke-Rpc 'array_polar' ('{"handles":["' + $c6 + '"],"centro":[5220,5030],"n":6,"angulo_total":360}'); $a2.result.numeros | ConvertTo-Json -Depth 4
```
**CTRL+Z una vez**.

**Acción C (entidades del usuario, tope y preview):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method array_rectangular -ParamsJson '{"handles":["6D136"],"filas":1,"columnas":2,"sep_filas":1,"sep_columnas":0.5}'
pwsh -File scripts\rpc-call.ps1 -Method array_rectangular -ParamsJson ('{"handles":["' + $c6 + '"],"filas":30,"columnas":20,"sep_filas":1,"sep_columnas":1}')
$a3 = Invoke-Rpc 'array_rectangular' ('{"handles":["' + $c6 + '"],"filas":1,"columnas":3,"sep_filas":1,"sep_columnas":2,"preview":true}'); Ver $a3
$a4 = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $a3.result.preview_id + '"}'); Ver $a4; $a4.result | Format-List confirmado, herramienta, n_preview_borradas
```
**CTRL+Z una vez** (deshace la confirmación: las copias definitivas desaparecen y **vuelven** las dos copias de `MCP-PREVIEW`) y **otra vez** (quita la preview). Por último borre `$c6` con `borrar` (sin confirmar: está en `MCP-DIBUJO`) y **no** lo deshaga.

**Resultado esperado:**
- A: 5 copias (`handles` nuevos) en `MCP-DIBUJO` con centros (5212, 5030), (5214, 5030), (5210, 5032), (5212, 5032), (5214, 5032); `origen` = `$c6`; `numeros`: `n_copias` 5, `n_posiciones` 6, `n_entidades` 5, `n_usuario` 0, `transformaciones[0]` = `{posicion: [0,1], desplazamiento: [2,0], rotacion: 0}`. Son círculos independientes (`propiedades` de cada copia: `CIRCLE`, no `ARRAY`/bloque anónimo).
- B: 5 copias a 60°, 120°, … alrededor de (5220, 5030); `transformaciones[].rotacion` 60, 120, 180, 240, 300.
- C: `6D136` sin `confirmar` → `INVALID_PARAMS` `"confirmar: 1 de las 1 entidades no están en capas MCP-* (p. ej. 6D136 en MUROS); pase confirmar: true para modificarlas"`; 30 × 20 → `"array: el número de copias (599) supera el tope de 500"`; la preview dibuja 2 copias en `MCP-PREVIEW` (`capas_creadas` `["MCP-PREVIEW"]` la primera vez; color 4, no imprimible); `confirmar` → copias en `MCP-DIBUJO`, `confirmado` true, `herramienta` `"array_rectangular"`, `n_preview_borradas` 2.

**Si falla:** las copias salen como matriz asociativa (`ARRAY`): no es lo que pide §5E.3.5 (copias con `DeepCloneObjects` + `TransformBy`). Se copia `6D136` sin `confirmar`: **fallo grave** (protección de §5D.8).

---

## Prueba 7: ciclo preview → números → `vista_png` → `confirmar` (un CTRL+Z)

- [ ] **Prueba 7 aprobada**

**Objetivo:** el bucle recomendado al modelo (§5E.4) y la tabla de deshacer de §5E.7.

**Acción A (revisión O1: con `MCP-PREVIEW` sin entidades, `vista_png` no toca la capa ni `DBMOD`):**
```powershell
$p7a = Invoke-Rpc 'muro' '{"eje":[[5200,5020],[5206,5020]],"espesor":0.15,"preview":true}'
Invoke-Rpc 'descartar_preview' ('{"preview_id":"' + $p7a.result.preview_id + '"}') | Out-Null   # la capa queda, vacía
(Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","capas":["MCP-PREVIEW"],"resumen":true}').result.total   # 0
$v7a = Invoke-Rpc 'vista_png' '{"bbox":[[5195,5015],[5210,5030]],"ancho":800,"alto":600}'; $v7a.result.mecanismo
Get-Content "$env:LOCALAPPDATA\MCPAutoCAD\plugin.log" -Tail 30 | Select-String 'MCP-PREVIEW|grupo neutro'
```
Esperado: `plot`; en `plugin.log` aparece `vista_png plot: MCP-PREVIEW sin entidades en el espacio trazado; no se toca la capa.` y **no** aparece `imprimible durante el trazado`; la línea `grupo neutro BE/E/U terminado` sin `Difiere de antes` (`DBMOD` igual). No deshaga el descarte ni la preview (no hace falta).

**Acción B (antes de seguir, cree además la preview que se usará en la prueba 10 y anote la hora):**
```powershell
$cad = Invoke-Rpc 'muro' '{"eje":[[5240,5030],[5246,5030]],"espesor":0.15,"preview":true}'; $cad.result | Format-List preview_id, caduca; Get-Date
```
(**No** deshaga esta preview: queda viva hasta la prueba 10.)
```powershell
$p7 = Invoke-Rpc 'muro' '{"eje":[[5200,5020],[5206,5020],[5206,5024]],"espesor":0.2,"preview":true}'; Ver $p7
$bb = $p7.result.numeros.bbox
$png = Invoke-Rpc 'vista_png' ('{"bbox":[[' + ($bb[0][0]-1) + ',' + ($bb[0][1]-1) + '],[' + ($bb[1][0]+1) + ',' + ($bb[1][1]+1) + ']],"ancho":800,"alto":600}'); $png.result.mecanismo; $png.result.advertencias
[IO.File]::WriteAllBytes("$PWD\tests\tmp\prueba7.png", [Convert]::FromBase64String($png.result.png_base64)); Invoke-Item tests\tmp\prueba7.png
(Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capas | Where-Object nombre -eq 'MCP-PREVIEW' | Format-List nombre, plot   # plot: False
Get-Content "$env:LOCALAPPDATA\MCPAutoCAD\plugin.log" -Tail 40 | Select-String 'MCP-PREVIEW|grupo neutro'
$f7 = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p7.result.preview_id + '"}'); Ver $f7; $f7.result | Format-List confirmado, herramienta, n_preview_borradas
pwsh -File scripts\rpc-call.ps1 -Method confirmar -ParamsJson ('{"preview_id":"' + $p7.result.preview_id + '"}')
```
Ahora en AutoCAD: **CTRL+Z una vez** y compruebe: `Test-Borrado $f7.result.handles` (`NOT_FOUND`) y `(Invoke-Rpc 'propiedades' ('{"handle":"' + $p7.result.handles[0] + '"}')).result.capa` (`MCP-PREVIEW`: la preview **vuelve**). **CTRL+Z otra vez** → `Test-Borrado $p7.result.handles`.

**Resultado esperado:**
- `$p7`: `preview` true, `preview_id` `prev-` + 12 hexadecimales, `preview_dibujada` true, `caduca` = ahora + 15 min (ISO con offset `-05:00`), `capa`/`capas` `MCP-PREVIEW`, `numeros` completos (`area` 2.0 = 0,2 × 10, `bbox`); la entidad se ve en color 4.
- Acción A: ver arriba (sin entidades, la capa no se toca).
- `vista_png` con `mecanismo` `"plot"`: **la preview SE VE en la imagen** (`prueba7.png`: el muro en L en color 4 / cian; este era el fallo de la primera pasada del 2026-10-27, imagen vacía porque `MCP-PREVIEW` es no imprimible). `advertencias` sin las de `MCP-PREVIEW` ni las del grupo neutro. Después de la llamada, `MCP-PREVIEW` sigue **no imprimible** (`listar_capas` → `plot` `False`; en el administrador de capas, icono de trazar desactivado). `plugin.log` muestra, entre `grupo neutro BE/E/U (UNDOCTL=…)` y `… terminado (operacion=ok, E=ok, U=ok)`, las líneas `vista_png plot: capa MCP-PREVIEW imprimible durante el trazado …` y `… devuelta a no imprimible.`; aquí, con la preview presente, `DBMOD` puede quedar marcado y la línea de cierre mostrar `Difiere de antes` solo en `DBMOD` (excepción acotada de protocol.md §5B.7: el dibujo ya estaba modificado por la preview); cualquier otra variable distinta es fallo. **No** añade pasos de deshacer (F7).
- `confirmar`: respuesta G6 con `preview` false, `preview_id` = el de la preview, `capa` `MUROS`, `confirmado` true, `herramienta` `"muro"`, `n_preview_borradas` 1, mismos `numeros` que la preview (recalculados, no copiados). El segundo `confirmar` → `NOT_FOUND` `"No existe ninguna previsualización con id 'prev-…' (caducada, descartada, confirmada o de otra sesión)."` con `data.preview_id`.
- Deshacer: el **primer** CTRL+Z deshace **juntos** el borrado de la preview y el muro definitivo (un paso); el **segundo** quita la preview (su propio paso). El historial (F2) muestra un `_u GRUPO` por cada uno.
- Entre los dos CTRL+Z, la preview que vuelve es **huérfana**: su registro se olvidó al confirmar y el almacén en memoria no se entera del CTRL+Z (§5E.7). `(Invoke-Rpc 'estado').result.advertencias` incluye `"Hay <n> entidad(es) en MCP-PREVIEW que no pertenecen a ninguna previsualización viva …"` (cuenta esta preview, 1 entidad, más otras huérfanas que haya) y la misma advertencia sale en cualquier generadora. Es lo esperado, no un fallo; el segundo CTRL+Z la quita.

**Si falla:** hacen falta dos CTRL+Z para deshacer la confirmación: `confirmar` no es un solo `UndoGroup` (G1, **bloqueante**). `vista_png` deja un paso: regresión de F7 (**bloqueante**). La imagen no muestra la preview: revise `plugin.log` (¿aparece `capa MCP-PREVIEW imprimible durante el trazado`?); si aparece y aun así no se ve, el trazado no lee la tabla de capas viva: **bloqueante**, aplique la alternativa de protocol.md §5B.7 punto 5. `MCP-PREVIEW` queda imprimible tras la llamada: **bloqueante** (debe volver a no imprimible por la restauración explícita y por el `U` del grupo).

---

## Prueba 8: `confirmar` tras mover y tras borrar la preview

- [ ] **Prueba 8 aprobada**

**Objetivo:** la preview es una guía visual; `confirmar` recalcula y nunca copia la preview (§5E.5 paso 4).

**Acción A (el usuario mueve la preview):**
```powershell
$p8 = Invoke-Rpc 'muro' '{"eje":[[5220,5020],[5226,5020]],"espesor":0.15,"preview":true}'; $p8.result.handles
```
En AutoCAD mueva la preview con `MOVE` (2 unidades hacia arriba). Luego:
```powershell
$c8 = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p8.result.preview_id + '"}'); $c8.result | Format-List n_preview_borradas, handles; $c8.result.entidades[0].bbox
```
**CTRL+Z una vez** (vuelve la preview movida) y **CTRL+Z otra vez** (deshace su `MOVE`) y **otra** (quita la preview).

**Acción B (el usuario borra la preview con CTRL+Z antes de confirmar):**
```powershell
$p8b = Invoke-Rpc 'muro' '{"eje":[[5220,5025],[5226,5025]],"espesor":0.15,"preview":true}'
```
**CTRL+Z una vez** (quita la preview). Luego:
```powershell
$c8b = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p8b.result.preview_id + '"}'); $c8b.result | Format-List confirmado, n_preview_borradas, handles
```
**CTRL+Z una vez** → `Test-Borrado $c8b.result.handles`.

**Resultado esperado:**
- A: `n_preview_borradas` 1 (la movida sigue en `MCP-PREVIEW` y se borra) y el muro definitivo con `bbox` [[5220, 5019.925], [5226, 5020.075]]: en la posición **calculada**, no en la de la preview movida (y ≈ 5022).
- B: `confirmado` true, `n_preview_borradas` **0** y el muro se crea igual.

**Si falla:** el definitivo aparece donde quedó la preview movida: se está copiando la preview (prohibido, §5E.5).

---

## Prueba 9: `confirmar` tras modificar el ancla (nada escrito)

- [ ] **Prueba 9 aprobada**

**Objetivo:** si una entidad ancla de un PuntoRef cambió desde la previsualización, `confirmar` no escribe nada.

**Acción:**
```powershell
$p9 = Invoke-Rpc 'muro' '{"eje":[{"handle":"6D136","punto":"inicio"},{"handle":"6D136","punto":"fin"}],"espesor":0.15,"justificacion":"izquierda","preview":true,"dibujar_preview":false}'
$p9.result.puntos_resueltos | Format-Table ref, punto, ancla
Invoke-Rpc 'mover' '{"handles":["6D136"],"desplazamiento":[0,0.01],"confirmar":true}' | % result | Format-List handles
$antes = (Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","capas":["MUROS"],"resumen":true}').result.total
pwsh -File scripts\rpc-call.ps1 -Method confirmar -ParamsJson ('{"preview_id":"' + $p9.result.preview_id + '"}')
(Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","capas":["MUROS"],"resumen":true}').result.total -eq $antes   # True
```
**CTRL+Z una vez** (deshace el `mover`: `6D136` vuelve a y = 3986.4523). Ahora, con el ancla otra vez igual:
```powershell
$c9 = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p9.result.preview_id + '"}'); $c9.result | Format-List confirmado, handles
```
**CTRL+Z una vez** → `Test-Borrado $c9.result.handles`.

**Resultado esperado:** con el ancla movida → `INVALID_PARAMS` `data.detalle` = `"preview_id: la entidad ancla 6D136 cambió o ya no existe desde la previsualización; no se escribió nada; vuelva a previsualizar"` (`data.preview_id`, `data.handle` `"6D136"`), `consultar_entidades` igual (nada escrito) y **ningún** paso de deshacer nuevo. El registro no se olvida: tras restaurar el ancla (huella igual) la confirmación funciona (`confirmado` true; muro a la izquierda del eje, que **en este caso queda DEBAJO de la línea real**: `6D136` va de x 3381 a 3375, hacia −X, así que su izquierda es −Y; contorno en y 3986.3023–3986.4523, `bbox` [[3375.1632, 3986.3023], [3381.1632, 3986.4523]]; corregido el 2026-10-27, el texto anterior decía "encima"). Anote si la huella vuelve a coincidir tras el CTRL+Z.

**Acción B (entidad usada directamente por handle; corrección de la auditoría del 2026-10-26):**
```powershell
$p9b = Invoke-Rpc 'offset' '{"handle":"6D136","distancia":0.15,"lado":[3378,3986],"preview":true,"dibujar_preview":false}'
Invoke-Rpc 'mover' '{"handles":["6D136"],"desplazamiento":[0,0.01],"confirmar":true}' | Out-Null
pwsh -File scripts\rpc-call.ps1 -Method confirmar -ParamsJson ('{"preview_id":"' + $p9b.result.preview_id + '"}')
```
**CTRL+Z una vez** (deshace el `mover`) y descarte: `Invoke-Rpc 'descartar_preview' ('{"preview_id":"' + $p9b.result.preview_id + '"}') | Out-Null`.

**Resultado esperado (B):** `INVALID_PARAMS` con el mismo `data.detalle` que en A (`"preview_id: la entidad ancla 6D136 cambió o ya no existe desde la previsualización; no se escribió nada; vuelva a previsualizar"`), aunque `offset` no use ningún PuntoRef anclado a `6D136`: la huella de la curva original se registra en la preview (§5E.5). Nada escrito.

**Si falla:** `confirmar` escribe con el ancla movida: **fallo grave** (§5E.5 paso 3).

---

## Prueba 10: caducidad (15 min) y huérfanas de `MCP-PREVIEW`

- [ ] **Prueba 10 aprobada**

**Objetivo:** al caducar, el plugin **no** escribe nada: solo olvida el registro; las entidades quedan huérfanas, se informan en `estado` y en las generadoras, y se limpian a petición con `limpiar_observaciones`.

**Método:** la caducidad usa el reloj real del plugin (`DateTimeOffset.Now`, 15 min desde la creación); no cambie la hora de Windows. Se usa la preview `$cad` creada al empezar la prueba 7: continúe con las pruebas 8, 9, 11 y 12 y vuelva aquí cuando hayan pasado **al menos 15 minutos** desde la hora anotada (si no, espere).

**Acción:**
```powershell
Get-Date; $cad.result.caduca
(Invoke-Rpc 'estado').result.advertencias
pwsh -File scripts\rpc-call.ps1 -Method confirmar -ParamsJson ('{"preview_id":"' + $cad.result.preview_id + '"}')
$g10 = Invoke-Rpc 'muro' '{"eje":[[5200,5035],[5206,5035]],"espesor":0.15,"preview":true,"dibujar_preview":false}'; $g10.result.advertencias
(Invoke-Rpc 'propiedades' ('{"handle":"' + $cad.result.handles[0] + '"}')).result.capa       # MCP-PREVIEW (sigue en el dibujo)
$l10 = Invoke-Rpc 'limpiar_observaciones' '{"confirmar":true,"capa":"MCP-PREVIEW"}'; $l10.result | Format-List n_borradas, por_espacio
(Invoke-Rpc 'estado').result.advertencias
```
**CTRL+Z una vez** (devuelve la huérfana, deshace la limpieza) y vuelva a ejecutar el `limpiar_observaciones` (sin deshacer).

**Resultado esperado:**
- Antes de 15 min, `estado.advertencias` es `[]` (una preview viva no es huérfana). Después: `confirmar` → `NOT_FOUND` con el mensaje exacto de la prueba 7; `estado.advertencias` termina con `"Hay 1 entidad(es) en MCP-PREVIEW que no pertenecen a ninguna previsualización viva (caducadas o de una sesión anterior): limpie con limpiar_observaciones {"confirmar": true, "capa": "MCP-PREVIEW"}."` (la misma advertencia sale en `$g10`); la entidad sigue en el dibujo (nadie la borró al caducar); si el recuento se corta por el tope de coste de R4 (2 500 ms; improbable con ~37 000 entidades) el texto empieza por `"Hay al menos 1 entidad(es)…"` y menciona el tope (anótelo, no es fallo); `limpiar_observaciones` borra 1 (más las de otras previews de prueba que queden: anótelo) y `estado` vuelve a `[]`.
- **Recuento real de huérfanas:** además de la caducada, cuentan las previews que **volvieron** al deshacer un `confirmar` o un `descartar_preview` (su registro ya se había olvidado; §5E.7): la de la prueba 7 si no se dio el segundo CTRL+Z, las 2 copias de la prueba 6C si no se dio el segundo CTRL+Z, las 12 de la prueba 11A tras su primer CTRL+Z (si no se dio el segundo), etc. Por eso el `n` de `estado`/`$g10` y el `n_borradas` de `limpiar_observaciones` pueden ser mayores que 1 (primera pasada del 2026-10-27: **14** = 1 caducada + 1 de deshacer un `confirmar` + 12 de deshacer un `descartar_preview`). No es fallo; anote el desglose.
- Durante la espera, AutoCAD no mostró ningún paso de deshacer ni cambio que usted no pidiera (no hay temporizadores que escriban).

**Si falla:** la preview desaparece sola al caducar o al desconectar Claude Code: **fallo grave** (restricción de seguridad de §5E.5).

---

## Prueba 11: `descartar_preview` (con y sin entidades)

- [ ] **Prueba 11 aprobada**

**Acción A (con entidades):**
```powershell
$p11 = Invoke-Rpc 'ejes_rejilla' '{"origen":[5230,5010],"separaciones_x":[3],"separaciones_y":[3],"extension":1,"radio_burbuja":0.154,"altura_texto":0.175,"preview":true}'
$d11 = Invoke-Rpc 'descartar_preview' ('{"preview_id":"' + $p11.result.preview_id + '"}'); $d11.result | Format-List preview_id, descartada, n_borradas, n_no_encontradas, herramienta
```
**CTRL+Z una vez** → vuelven las 12 entidades de la preview (en `MCP-PREVIEW`); **CTRL+Z otra vez** → desaparecen (`Test-Borrado $p11.result.handles`).

**Acción B (sin entidades: 0 pasos):**
```powershell
$marca = (Invoke-Rpc 'circulo' '{"centro":[5250,5030],"radio":0.3}').result.handles[0]
$p11b = Invoke-Rpc 'muro' '{"eje":[[5240,5035],[5246,5035]],"espesor":0.15,"preview":true,"dibujar_preview":false}'
(Invoke-Rpc 'descartar_preview' ('{"preview_id":"' + $p11b.result.preview_id + '"}')).result | Format-List n_borradas, n_no_encontradas
pwsh -File scripts\rpc-call.ps1 -Method confirmar -ParamsJson ('{"preview_id":"' + $p11b.result.preview_id + '"}')
```
**CTRL+Z una vez** → `Test-Borrado $marca` (`NOT_FOUND`: el CTRL+Z deshizo el círculo, no un paso vacío).

**Resultado esperado:** A: `descartada` true, `n_borradas` 12, `n_no_encontradas` 0, `herramienta` `"ejes_rejilla"`, un paso de deshacer. B: `n_borradas` 0 sin abrir paso de deshacer (preview y descarte sin `dibujar_preview`: 0 pasos, §5E.7); `confirmar` después → `NOT_FOUND`; el CTRL+Z quita el círculo marcador.

---

## Prueba 12: unidades (G2 estricta)

- [ ] **Prueba 12 aprobada**

**Objetivo:** con `INSUNITS = 0` las generadoras responden `UNITS_UNKNOWN` **sin escape** (no existe `aceptar_sin_unidades`); las lecturas responden.

**Acción:** en AutoCAD escriba `INSUNITS` → `0`. Luego:
```powershell
foreach ($m in @(
  @('muro','{"eje":[[5200,5000],[5206,5000]],"espesor":0.15}'),
  @('ejes_rejilla','{"origen":[5200,5010],"separaciones_x":[3],"separaciones_y":[3],"extension":1,"radio_burbuja":0.154,"altura_texto":0.175}'),
  @('recinto','{"contorno":[[5240,5000],[5245,5000],[5245,5002.47]],"nombre":"X","altura_texto":0.15}'),
  @('offset','{"handle":"6D136","distancia":0.15,"lado":"izquierda"}'),
  @('array_rectangular','{"handles":["6D136"],"filas":1,"columnas":2,"sep_filas":1,"sep_columnas":1,"confirmar":true}'),
  @('array_polar','{"handles":["6D136"],"centro":[3378,3986],"n":2,"angulo_total":90,"confirmar":true}'))) {
  $x = Invoke-Rpc $m[0] $m[1]; "{0,-18} {1} {2}" -f $m[0], $x.error.data.code, $x.error.message }
pwsh -File scripts\rpc-call.ps1 -Method muro -ParamsJson '{"eje":[[5200,5000],[5206,5000]],"espesor":0.15,"aceptar_sin_unidades":true}'
(Invoke-Rpc 'punto_de' '{"ref":{"handle":"6D136","punto":"fin"}}').result | Format-List punto, insunits, unidades, advertencias
```
Devuelva `INSUNITS` a `6` (escriba el valor; no use CTRL+Z) y compruebe `(Invoke-Rpc 'estado').result.insunits` = 6.

**Resultado esperado:** las 6 → `UNITS_UNKNOWN` (`-32004`) `"El dibujo no declara unidades (INSUNITS=0)."` sin escribir nada; con `aceptar_sin_unidades` → `INVALID_PARAMS` `"aceptar_sin_unidades: propiedad no permitida"`; `punto_de` responde con `insunits` 0, `unidades` `"desconocida"` y las advertencias de §2.1 y §2.2.

**Si falla:** alguna generadora dibuja con `INSUNITS = 0`: **fallo grave** (G2, PLAN.md §9).

---

## Prueba 13: regresión de las Fases 0–3

- [ ] **Prueba 13 aprobada**

**Acción A (estabilidad y BUSY):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method estado -Repeat 100
```
En AutoCAD inicie `LINE`, pulse el primer punto y **no** pulse Esc:
```powershell
pwsh -File scripts\rpc-call.ps1 -Method muro -ParamsJson '{"eje":[[5200,5000],[5206,5000]],"espesor":0.15}'
pwsh -File scripts\rpc-call.ps1 -Method punto_de -ParamsJson '{"ref":[0,0]}'
pwsh -File scripts\rpc-call.ps1 -Method ping
```
Pulse Esc.

**Acción B (rótulo desde la biblioteca, hatch asociativo tras mover):**
```powershell
$rot = Invoke-Rpc 'insertar_bloque' '{"nombre":"LPS_ROTULO","posicion":[300,50],"desde_biblioteca":true,"espacio":"Layout1","capa":"rotulo","atributos":{"PLANO_N":"2"}}'; $rot.result | Format-List handles, importado_de_biblioteca, unidades_bloque, espacio
```
**CTRL+Z una vez** (quita inserción, atributos y definición). Luego:
```powershell
$pl = (Invoke-Rpc 'polilinea' '{"vertices":[[5200,5000],[5204,5000],[5204,5003],[5200,5003]],"cerrada":true}').result.handles[0]
$h = (Invoke-Rpc 'hatch' ('{"contorno":{"handles":["' + $pl + '"]},"patron":"ANSI31"}')).result.handles[0]
Invoke-Rpc 'mover' ('{"handles":["' + $pl + '"],"desplazamiento":[2,0]}') | Out-Null
(Invoke-Rpc 'propiedades' ('{"handle":"' + $h + '"}')).result.bbox
```
**CTRL+Z tres veces** (mover, hatch, polilínea).

**Acción C (auditoría y F7):**
```powershell
$vc = (Invoke-Rpc 'verificar_cuadro_areas').result
($vc.hallazgos | Where-Object { $_.regla -eq 'verificar_cuadro_areas' -and $_.severidad -eq 'alta' }).Count
$mk = Invoke-Rpc 'marcar_hallazgos' '{"marcas":[{"bbox":[[5200,5000],[5205,5004]],"texto":"Prueba F7 en Fase 4A"}]}'
$v = Invoke-Rpc 'vista_png' '{"bbox":[[5195,4995],[5210,5010]],"ancho":800,"alto":600}'; $v.result.mecanismo
```
**CTRL+Z una vez** → `Test-Borrado $mk.result.handles`.

**Acción D (línea base):**
```powershell
$ahora = (Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result
"base=$($base.total) ahora=$($ahora.total)"
(($ahora.por_tipo | ConvertTo-Json -Compress) -eq ($base.por_tipo | ConvertTo-Json -Compress))   # True
(Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capas | Where-Object nombre -like 'MCP-*' | ForEach-Object nombre
```

**Resultado esperado:**
- A: `Resumen: 100 llamadas, 0 con error.` (máx. < 2000 ms); `muro` y `punto_de` → `BUSY` (`-32001`, `motivo` `"comando"`, `comando` `"LINE"`) en < 2 s; `ping` responde; nada escrito.
- B: `LPS_ROTULO` desde la biblioteca en `Layout1` sin cambiar la pestaña, `unidades_bloque` 6; el HATCH asociativo **sigue** a la polilínea movida (`bbox` desplazado 2 en x; `AssociativeHatchSync`).
- C: `verificar_cuadro_areas` con **exactamente 4** `cuadros_discrepantes` `alta` (LOTE 72,00/73,11, LIBRE 19,50/20,61, OCUPACION 0,73/0,72, CONSTRUCCION 2,39/2,36); `vista_png` `"plot"`; **un** CTRL+Z quita la marca (F7).
- D: `total` y `por_tipo` iguales a la línea base del paso 0.8. Capas `MCP-*` que pueden quedar (vacías): `MCP-PREVIEW`, `MCP-DIBUJO`, `MCP-OBSERVACIONES` solo si alguna creación no se deshizo; anótelas (no afectan al resumen de entidades).

**Si falla:** cualquier regresión bloquea el gate; repita la prueba correspondiente de `docs\GATE_FASE0.md` … `GATE_FASE3.md` para aislarla.

---

## Prueba 14: limpieza, Claude Code y tabla de resultados

- [ ] **Prueba 14 aprobada**

**Acción A (Claude Code, opcional pero recomendado):** con `fase4_trabajo.dwg` activo en Modelo, pida a Claude Code: *"En la zona libre (x 5200–5260, y 5000–5040) previsualiza un muro de 0,15 en L de 6 × 4 m, dime su área y su caja, enséñamelo con vista_png y, si está bien, confírmalo."* → debe usar `muro` con `preview: true`, leer `numeros`, llamar a `vista_png` con el `bbox` ampliado y después `confirmar`. No debe pasar `forzar` ni `confirmar: true` (el de los arrays) por iniciativa propia. Anote el número de llamadas y pulse **CTRL+Z** hasta que no quede nada (el `confirmar` se deshace con uno; la preview con otro).

**Acción B (limpieza y comprobación final):**
```powershell
(Invoke-Rpc 'estado').result.advertencias                    # [] (sin huérfanas)
(Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","capas":["MCP-PREVIEW"],"resumen":true}').result.total   # 0
```
Si quedan huérfanas, `Invoke-Rpc 'limpiar_observaciones' '{"confirmar":true,"capa":"MCP-PREVIEW"}'`.

---

## Tabla de resultados

| # | Prueba | Fecha | Resultado (PASA / FALLA) | Tiempo (ms) | Observaciones |
|---|---|---|---|---|---|
| 0 | Preparación (build integrado, manifest 47 en orden, 25 de escritura, /mcp 49, MCPESTADO 0.5.0) | | | — | .NET: ___ pruebas (≥ 900) · pytest: ___ (≥ 379) · `$base.total`: ___ |
| 1 | punto_de / distancia / proyectar sobre 6D136, 6D350, 6D34B; errores exactos; sin pasos de deshacer | | | | |
| 2 | **muro recto, en L (inglete), anillo + SOLID asociativo, caras; rechazos INVALID_GEOMETRY sin paso** | | | | ángulo (formato): · un CTRL+Z: sí/no |
| 3 | ejes_rejilla frente a 859EE/859F0 (r 0,154 tangente, texto 0,175, estilo 75, centrado) | | | | un CTRL+Z: sí/no |
| 4 | recinto 12,35 m²; vértices por intersección (48,60 m²); U con centroide fuera | | | | un CTRL+Z: sí/no |
| 5 | offset (lado punto, izquierda/derecha, capa de la original) | | | | un CTRL+Z: sí/no |
| 6 | **array_rectangular / array_polar (confirmar sobre MUROS, tope 500, preview → confirmar)** | | | | copias independientes: sí/no |
| 7 | **ciclo preview → numeros → vista_png → confirmar; UN CTRL+Z deshace borrado de preview + definitivo** | | | | |
| 8 | confirmar tras mover y tras borrar la preview | | | | |
| 9 | **confirmar tras modificar el ancla: INVALID_PARAMS, nada escrito** | | | | |
| 10 | **caducidad 15 min sin escrituras; huérfanas en estado; limpiar_observaciones MCP-PREVIEW** | | | | minutos esperados: |
| 11 | descartar_preview con (1 paso) y sin entidades (0 pasos) | | | | |
| 12 | **G2: UNITS_UNKNOWN ×6 sin escape; aceptar_sin_unidades no permitido; lecturas responden** | | | | |
| 13 | **Regresión Fases 0–3 (100× estado, BUSY, LPS_ROTULO, hatch asociativo, 4 discrepancias alta, F7, línea base)** | | | estado máx: | |
| 14 | Limpieza y Claude Code | | | — | llamadas: |

**CTRL+Z por herramienta (G1):** muro ☐ · ejes_rejilla ☐ · recinto ☐ · offset ☐ · array_rectangular ☐ · array_polar ☐ · preview dibujada ☐ · confirmar (borrado + definitivo en uno) ☐ · descartar_preview ☐ · lecturas sin paso ☐

**Versiones probadas:** AutoCAD `version_autocad` = ______ · `plugin_version` = 0.5.0 · `protocol_version` = 0.5

## Cierre: decisiones, criterio de aprobación y limpieza

### Datos a volcar en PLAN.md (§9, "Resultados del gate de Fase 4A")

| Dato | Valor medido | Evidencia |
|---|---|---|
| Formato del ángulo en el rechazo de inglete (esperado `7.1°`) | | Prueba 2E |
| HATCH del anillo: asociativo e isla correcta | sí/no | Prueba 2C |
| Diferencia visual con los ejes reales (radio 0,154 vs 0,1538) | | Prueba 3 |
| Posición de la etiqueta en la U | | Prueba 4C |
| La huella del ancla vuelve a coincidir tras deshacer el `mover` | sí/no | Prueba 9 |
| Huérfanas tras caducar (n) y conducta durante la espera | | Prueba 10 |
| Conducta de Claude en el bucle preview → confirmar | | Prueba 14 |

Si algo exige cambiar código: cambie el contrato primero si hace falta (protocol.md), `pwsh -File scripts\build.ps1 -ServerTests`, cierre AutoCAD, `pwsh -File scripts\install-plugin.ps1` y repita la prueba afectada antes de aprobar.

### Criterio de aprobación

- Las 14 pruebas en PASA y la preparación completa (al menos 900 pruebas .NET y 379 pytest, manifest de 47 en orden, 25 de escritura, `/mcp` con 49, `MCPESTADO` = `MCPAutoCAD 0.5.0 (protocolo 0.5)`).
- **Bloqueantes sin excepción:** un CTRL+Z por llamada en todas las escrituras y **un solo** paso en `confirmar` (prueba 7); lecturas sin pasos de deshacer (prueba 1); `confirmar` nunca copia la preview ni escribe con un ancla cambiada (pruebas 8–9); el plugin no escribe nada al caducar ni al desconectar (prueba 10); `UNITS_UNKNOWN` sin escape (prueba 12); `confirmar: true` obligatorio sobre entidades del usuario en los arrays (prueba 6); sin regresiones y el modelo vuelve a la línea base (prueba 13).

### Limpieza final

1. En AutoCAD cierre `fase4_trabajo.dwg` **sin guardar**.
2. Borre la copia de trabajo, sus archivos auxiliares y la carpeta temporal:
   ```powershell
   Remove-Item -LiteralPath $trab -ErrorAction SilentlyContinue
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'fase4_trabajo.*' | Remove-Item   # .bak, .dwl, .dwl2
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'fase4_trabajo*'                  # nada
   Remove-Item -Recurse -Force tests\tmp -ErrorAction SilentlyContinue
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures'                                           # copia_abrir.dwg, copia_real.dwg (y las de gates anteriores si siguen)
   ```

### Decisión

- [ ] **GATE DE FASE 4A APROBADO**: las 14 pruebas en PASA y los datos anotados en PLAN.md §9. Se puede planificar la Fase 4B (vanos, cotas automáticas, ensamblado CYPE).
- [ ] **NO APROBADO**: alguna prueba sigue fallando después de aplicar "Si falla". Se corrige antes de la Fase 4B.

Fecha: ____________ · Responsable: ____________ · Notas: ________________________________

---

## Resultados del gate en vivo (2026-10-27, primera pasada)

AutoCAD 2026, plugin 0.5.0, protocolo 0.5, `fase4_trabajo.dwg`.

| # | Resultado | Notas |
|---|---|---|
| 1–4 | **PASA** | Prueba 2E: formato del ángulo **`7.1°`** (una cifra decimal, como el contrato). Prueba 4C: la etiqueta de la U queda en el **ala izquierda**, (5251, 5001), fuera de la muesca. |
| 5 | **PASA** | Hallazgo 2 (`numeros.bbox` de `offset`, abajo): los números se vuelven a comprobar en la segunda pasada. |
| 6 | **PASA** | |
| 7 | **FALLA** | `vista_png` (plot con `bbox`) devolvió la imagen **vacía**: la preview no aparecía porque `MCP-PREVIEW` es no imprimible → corrección 1. |
| 8 | **PASA** | |
| 9 | **PASA** | La huella del ancla **vuelve a coincidir** tras deshacer el `mover` (la confirmación funciona). Texto esperado corregido: el muro justificado `izquierda` sobre `6D136` queda **debajo** de la línea (y 3986.30–3986.45). |
| 10 | **PASA** | **14 huérfanas** tras la caducidad = 1 caducada + 1 de deshacer un `confirmar` + 12 de deshacer un `descartar_preview`; `limpiar_observaciones` (`capa: MCP-PREVIEW`) las limpió todas. Recuento documentado en las pruebas 7 y 10. |
| 11–12 | **PASA** | |
| 13–14 | pendientes | |

**Correcciones aplicadas tras la primera pasada** (build integrado: **900** pruebas .NET, **379** pytest, 0 advertencias, incluidas las revisiones O1/O3):
1. **(bloqueante, prueba 7)** `vista_png` con trazado: dentro del grupo neutro (`_.UNDO _BE` → … → `_E` → `_U`) y solo durante el trazado, `MCP-PREVIEW` se hace imprimible y después se devuelve explícitamente a no imprimible; el `U` del grupo lo deshace además. `lps.json` no cambia (protocol.md §5B.7 "Previsualización visible en el trazado", §5E.4; `Handlers/PreviewPlotVisibility.cs`).
2. **Hallazgo 2, `offset`:** `numeros.bbox` abarcaba las dos paralelas candidatas; ahora es la caja de la curva elegida (= `entidades[0].bbox`), calculada en la lectura con el mismo código que el dibujo (`GeometryDrawer.BuildOffset` + `GeometryCore.Gen.OffsetChoice`); también es la caja de R4 (§5E.3.4).
3. **Hallazgo 3, `ejes_rejilla` sin `estilo`:** usaba `Standard`; ahora usa `75` (estilo de los rótulos de eje de la oficina, `859F0`) si existe en el dibujo, y si no el actual con advertencia (`GeometryCore.Gen.AxisLabelStyle`; constante con TODO de moverla a `lps.json` `estilos_texto.ejes`, clave que el plugin ya lee si existe) (§5E.3.2).

4. **Revisión O1 (`DBMOD`):** `MCP-PREVIEW` solo se hace imprimible si tiene entidades en el espacio trazado (sin ellas, `DBMOD` intacto; prueba 7A). Excepción acotada en protocol.md §5B.7.
5. **Revisión O3 (`offset`):** lado no construible (p. ej. hacia dentro de un arco con distancia ≥ radio) → `INVALID_GEOMETRY` en vez de elegir el otro lado en silencio (prueba 5B).

**Errores del checklist corregidos:** prueba 9 ("encima" → **debajo** de la línea); prueba 7 (al deshacer un `confirmar` la preview vuelve **huérfana** y sale en el aviso de `estado` y de las generadoras); prueba 10 (recuento de huérfanas con las que vuelven al deshacer `confirmar`/`descartar_preview`); prueba 7 (el resultado esperado exige que la preview **se vea** en la imagen); prueba 5 (`numeros.bbox` = `entidades[0].bbox`); prueba 3C nueva (estilo por defecto `75`); mínimos de pruebas 896 / 376.

**Decisión del usuario (2026-10-27):** burbuja y rótulo de ejes en **rojo** = color de la capa `EJES` (ByLayer). Sin cambio de código.

## Resultados del gate en vivo (2026-10-27, segunda pasada) — GATE DE FASE 4A APROBADO

Bundle del build de 900 .NET / 379 pytest reinstalado; `fase4_trabajo.dwg` recién copiado (36 955 entidades).

| # | Resultado | Notas |
|---|---|---|
| 3C | **PASA** | Sin `estilo`, los TEXT de la rejilla salen con estilo `75` y sin advertencia. |
| 5 | **PASA** | `numeros.bbox` de `offset` sobre `6D136` = [[3375.1632, 3986.3023], [3381.1632, 3986.3023]] (= caja de la paralela); arco `8294C` izquierda → caja de la paralela interior. 5B: burbuja `859EE`, distancia 0,2 hacia dentro → `INVALID_GEOMETRY` "lado: no se puede construir la paralela a 0.2 del lado pedido (la distancia supera o iguala el radio 0.153824)". |
| 7 | **PASA** | La preview **se ve** en el PNG (color 4); `listar_capas` → `MCP-PREVIEW` `plot: false` después; log: "imprimible durante el trazado" → "devuelta a no imprimible", grupo neutro `U=ok`, `DBMOD` 29 → 29. 7A: con `MCP-PREVIEW` vacía, log "sin entidades en el espacio trazado; no se toca la capa", `DBMOD` 29 → 29. |
| 13 | **PASA** | 100× `estado`: 0 errores, máx 346 ms (media 168 ms: la búsqueda de huérfanas recorre el modelo cuando `MCP-PREVIEW` existe vacía; dentro del criterio < 2 s). `LPS_ROTULO` desde la biblioteca en Layout1 (25 atributos, `unidades_bloque` 6); HATCH asociativo sigue al `mover` (caja 5202–5206); `verificar_cuadro_areas` con exactamente 4 `alta`; marca F7 + `vista_png` sin paso. **8 CTRL+Z = 8 escrituras** (preview, confirmar muro, confirmar ejes, rótulo, polilínea, hatch, mover, marca) y el modelo vuelve exactamente a la línea base (36 955, `por_tipo` igual, sin INSERT en Layout1). La prueba BUSY (comando LINE abierto) no se repitió: el puente no cambió en la 4A. |
| 14 | parcial | Limpieza: `estado.advertencias` = [] al final. La acción A (Claude Code en lenguaje natural) no se ejecutó; queda junto a la prueba 14 de la Fase 3 como prueba de lenguaje natural pendiente. |

**Decisión:** GATE DE FASE 4A **APROBADO** (2026-10-27). Pendiente no bloqueante: prueba de lenguaje natural (Fases 3 y 4A).

**Pendiente de la segunda pasada (histórico)** (reinstalar el bundle del build de 900/379 antes): prueba **5** (números, en particular `numeros.bbox`, y el rechazo 5B del lado no construible), prueba **7** completa (la preview se ve en `vista_png`; `MCP-PREVIEW` vuelve a no imprimible; sin entidades en `MCP-PREVIEW` la capa no se toca y `DBMOD` no cambia; un CTRL+Z por paso), prueba **13**, prueba **14**; y, para cerrar el hallazgo 3, la acción **3C** (estilo `75` sin pasarlo).
