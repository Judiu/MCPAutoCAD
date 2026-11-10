# Gate de la Fase 3 (dibujo primitivo): checklist en vivo

Este documento comprueba en AutoCAD real las catorce herramientas de escritura de la Fase 3 (`PLAN.md` §5): `crear_capa`, `polilinea`, `circulo`, `arco`, `texto`, `mtext`, `insertar_bloque`, `cota`, `hatch`, `mover`, `copiar`, `rotar`, `escalar` y `borrar`, más los cambios de `linea`. El contrato está en `protocol.md` v0.4 (cabecera "Cambios 0.3 → 0.4", §3.11, §4, §5.4, **§5D** completa, §6 y §9). Las reglas comunes R1–R7 (§5D.0) se comprueban en cada herramienta: **un CTRL+Z por llamada** (R1), rechazo de dibujos sin unidades (`UNITS_UNKNOWN`, R2), capa por concepto o del estándar (R3), rechazo de geometría fuera de la extensión del espacio salvo `forzar` (R4), `espacio` sin cambiar la pestaña (R5), respuesta común (R6) y textos literales (R7). **Si una prueba falla y no se arregla con lo que indica "Si falla", el gate no se aprueba y no se empieza la Fase 4.**

- Quién ejecuta las pruebas: el usuario, en su equipo, con AutoCAD 2026 abierto con interfaz y Claude Code en otra ventana. Ningún agente ejecuta este checklist ni instala el plugin.
- Dibujos: **solo** los de `C:\LPSAPP\MCPAutoCAD\tests\fixtures\`. Copia de trabajo: **`tests\fixtures\fase3_trabajo.dwg`** (ya existe: es una copia de `copia_real.dwg`, plano real arquitectónico 6X12 en metros, ~36 955 entidades en modelo, 161 capas, `Layout1` y `Layout2`, estilo de cota actual `COTA75`, estilo de texto actual `Standard`; datos comprobados con ODA File Converter + ezdxf el 2026-10-26). **`copia_real.dwg` y `copia_abrir.dwg` no se abren en AutoCAD ni se modifican en ningún momento.** Las pruebas de unidades (prueba 11) usan un **dibujo nuevo** sin guardar. La limpieza final borra `fase3_trabajo.dwg`. Este checklist nunca abre ni apunta a nada de la unidad de red de expedientes.
- Consola: PowerShell 7 (`pwsh`), con el directorio actual en `C:\LPSAPP\MCPAutoCAD`, y **la misma consola** durante todo el gate (las pruebas reutilizan `Invoke-Rpc`, `Test-Borrado`, `$base`, `$layout`).
- **Todas las herramientas de esta fase escriben en el dibujo.** Úselas únicamente con `fase3_trabajo.dwg` o con el dibujo nuevo de la prueba 11 activo. Después de cada escritura de prueba se pulsa **CTRL+Z una sola vez** en AutoCAD (salvo que la prueba diga otra cosa): así se comprueba R1 y el plano vuelve a su estado.
- Zona libre del modelo para dibujar: **x 5200–5260, y 5000–5040** (por encima de la extensión del plano, que llega a y ≈ 4208, pero dentro de la extensión ampliada un 10 %, que llega a y ≈ 5119). Los valores del rótulo (prueba 8) son **ficticios**: ningún dato de clientes.

---

## 0. Preparación (una sola vez)

1. **Compilar y ejecutar todas las pruebas** (AutoCAD puede estar abierto):
   ```powershell
   cd C:\LPSAPP\MCPAutoCAD
   pwsh -File scripts\build.ps1 -ServerTests
   ```
   Debe terminar con `=== BUILD CORRECTO ===`. La línea de `dotnet test` debe indicar **al menos 708** pruebas superadas y 0 con error (442 exigidas en 0.3.1; 501 al cerrar la Fase 2), y la de `pytest` **al menos 227 passed** (146 al cerrar la Fase 2; `server/tests/test_fase3.py` añade 81). Anote ambos números en la tabla final. Si cambia código del plugin durante el gate, vuelva a ejecutar `build.ps1` (no solo `dotnet test`) antes de reinstalar.
2. **Cerrar todas las instancias de AutoCAD** e **instalar el bundle**:
   ```powershell
   Get-Process acad -ErrorAction SilentlyContinue   # no debe listar nada
   pwsh -File scripts\install-plugin.ps1 -DryRun
   pwsh -File scripts\install-plugin.ps1
   Test-Path "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\lps.json"                   # True
   Test-Path "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\biblioteca\LPS_ROTULO.dwg"   # True
   (Get-Item "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\MCPAutoCAD.Plugin.dll").VersionInfo.FileVersion   # 0.4.0.0
   ```
   El `-DryRun` debe imprimir `[DryRun] Copiar ...\standards\LPS_ROTULO.dwg -> ...\Contents\biblioteca\LPS_ROTULO.dwg`. El resumen final debe decir `Estándar LPS: ...\Contents\lps.json` y **`Biblioteca de bloques: ...\Contents\biblioteca\LPS_ROTULO.dwg`** (no `NO INSTALADO`/`NO INSTALADA`). Sin `lps.json` no valen las pruebas 1, 2 y 5–8 (R3 resuelve conceptos con `capas_preferidas`); sin la biblioteca no vale la prueba 8.
3. **Abrir AutoCAD 2026** (sin abrir ningún plano todavía) y escribir **`MCPESTADO`**: debe mostrar `MCPAutoCAD 0.4.0 (protocolo 0.4)` y el pipe `\\.\pipe\mcp-autocad-<usuario>-<PID>`.
4. **Versión** desde PowerShell:
   ```powershell
   pwsh -File scripts\check-pipe.ps1
   pwsh -File scripts\rpc-call.ps1 -Method ping
   ```
   `ping` debe traer `"protocol_version": "0.4"` y `"plugin_version": "0.4.0"`.
5. **Funciones auxiliares y manifest.** Defina en la consola (las usan casi todas las pruebas):
   ```powershell
   function Invoke-Rpc([string]$Method, [string]$Params = '{}') {
       (& .\scripts\rpc-call.ps1 -Method $Method -ParamsJson $Params -AsObject -TimeoutSec 60).Respuesta | ConvertFrom-Json
   }
   function Test-Borrado([string[]]$Handles) {
       # Tras un CTRL+Z: cada handle creado por la llamada debe dar NOT_FOUND.
       foreach ($h in $Handles) { "$h -> " + (Invoke-Rpc 'propiedades' ('{"handle":"' + $h + '"}')).error.data.code }
   }
   (Invoke-Rpc 'tools/manifest').result.protocol_version          # 0.4
   (Invoke-Rpc 'tools/manifest').result.tools.name -join ', '
   (Invoke-Rpc 'tools/manifest').result.tools | Where-Object escritura | ForEach-Object name
   ```
   La segunda línea debe imprimir **exactamente** (36 herramientas, orden de protocol.md §4):
   `ping, estado, listar_capas, linea, consultar_entidades, propiedades, textos, bloques, seleccion_actual, xrefs, vista_png, resumen_archivo, auditar_capas, entidades_en_capa_0, auditar_bloques, areas_por_capa, verificar_cuadro_areas, duplicados, textos_fuera_de_escala, auditar_dibujo, marcar_hallazgos, limpiar_observaciones, crear_capa, polilinea, circulo, arco, texto, mtext, insertar_bloque, cota, hatch, mover, copiar, rotar, escalar, borrar`.
   La tercera debe listar 17: `linea`, `marcar_hallazgos`, `limpiar_observaciones` y las 14 de la Fase 3.
6. **Claude Code**: reinicie Claude Code en `C:\LPSAPP\MCPAutoCAD` (usa `.mcp.json`). Con `/mcp`, el servidor `autocad` debe estar conectado con **38 herramientas**: las 36 del manifest + `instancias` y `seleccionar_instancia`.
7. **Abrir la copia de trabajo** (ya existe; no se crea):
   ```powershell
   $trab = 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\fase3_trabajo.dwg'
   Test-Path -LiteralPath $trab    # True
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'fase3_trabajo.dwl*'   # nada (si hay .dwl, otra sesión la tiene abierta)
   New-Item -ItemType Directory -Force tests\tmp | Out-Null
   ```
   En AutoCAD: `OPEN` → `C:\LPSAPP\MCPAutoCAD\tests\fixtures\fase3_trabajo.dwg`. Quédese en la pestaña **Modelo**, `ZOOM` → `E`, sin comandos en curso.
8. **Línea base:**
   ```powershell
   (Invoke-Rpc 'estado').result | Select-Object insunits, unidades, measurement, advertencias   # 6, m, 1, []
   $base = (Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result
   $base.total                                      # ≈ 36955
   $base | ConvertTo-Json -Depth 5 | Set-Content tests\tmp\fase3_base_resumen.json
   $layout = 'Layout1'
   (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.total                      # 161
   (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capas | Where-Object nombre -like 'MCP-*'   # ninguna
   ```
   Anote `$base.total`. Al final del gate el modelo debe volver exactamente a este resumen (todo lo de prueba se deshace).

### Recordatorio: `scripts\rpc-call.ps1`

```powershell
pwsh -File scripts\rpc-call.ps1 -Method <método> [-ParamsJson '{...}'] [-Pid <n>] [-TimeoutSec 15] [-Repeat N] [-AsObject] [-Raw]
```

- `-ParamsJson` va **entre comillas simples** y contiene JSON válido. En textos, `\\` del JSON es **una** barra invertida y `\n` es un salto de línea.
- Código de salida: `0` = `result`; `2` = `error` JSON-RPC (`INVALID_PARAMS`, `NOT_FOUND`, `UNITS_UNKNOWN`, `BUSY`…); `1` = fallo local o de transporte.
- Los errores `INVALID_PARAMS` llegan con `message` = `"Parámetros inválidos: <detalle>"` y `data.detalle` = `<detalle>`; el `detalle` se compara **literalmente** con protocol.md §5D y §6.
- Respuesta común de toda escritura de §5D (R6): `pid`, `handles`, `capa`, `capa_creada`, `espacio`, `insunits`, `unidades`, `advertencias` (+ campos propios). `mover`/`copiar`/`rotar`/`escalar`/`borrar` devuelven `handles`, `n`, `n_usuario`, `espacio`, `capas` (sin `capa`/`capa_creada`).
- **Comprobación de CTRL+Z (R1) en todas las pruebas:** tras la escritura, en AutoCAD pulse **CTRL+Z una sola vez** y ejecute `Test-Borrado <handles>`: cada handle creado debe dar `NOT_FOUND`, y lo que la llamada modificó debe volver a su valor anterior. Anote en la tabla final, por herramienta, "un CTRL+Z: sí/no".

---

## Prueba 1: `crear_capa` (idempotente)

- [ ] **Prueba 1 aprobada**

**Objetivo:** crea solo capas del estándar o `MCP-*`, con las propiedades de `lps.json`; es idempotente (si existe, no escribe ni crea paso de deshacer).

**Acción:**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method crear_capa -ParamsJson '{"nombre":"cotas"}'
pwsh -File scripts\rpc-call.ps1 -Method crear_capa -ParamsJson '{"nombre":"dibujo_mcp"}'
pwsh -File scripts\rpc-call.ps1 -Method crear_capa -ParamsJson '{"nombre":"MCP-DIBUJO"}'
pwsh -File scripts\rpc-call.ps1 -Method crear_capa -ParamsJson '{"nombre":"CAPA_INVENTADA"}'
pwsh -File scripts\rpc-call.ps1 -Method crear_capa -ParamsJson '{"nombre":"MAL*NOMBRE"}'
```
En AutoCAD pulse **CTRL+Z una sola vez** y compruebe:
```powershell
(Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capas | Where-Object nombre -like 'MCP-*'   # ninguna
```

**Resultado esperado:**
- `cotas` → `capa` `"Cotas"`, **`creada` false**, `origen` `"concepto"`, propiedades reales de la capa del plano (`color`, `tipo_linea`, `grosor`, `plot`, `bloqueada`, `congelada`, `apagada`). No escribe nada.
- `dibujo_mcp` → `capa` `"MCP-DIBUJO"`, **`creada` true**, `color` 3, `tipo_linea` `"Continuous"`, `grosor` −3, `plot` true (capas_mcp de `lps.json`).
- `MCP-DIBUJO` otra vez → **`creada` false** (idempotente).
- `CAPA_INVENTADA` → `INVALID_PARAMS` `data.detalle` = `"nombre: solo se crean capas de lps.json (capas_permitidas/capas_mcp) o MCP-*"` y `data.capa`.
- `MAL*NOMBRE` → `INVALID_PARAMS` `"capa: nombre de capa no válido"`.
- **Un** CTRL+Z quita `MCP-DIBUJO` (la única llamada que escribió). El CTRL+Z no debe deshacer nada anterior al gate.

**Si falla:**
- La segunda llamada a `MCP-DIBUJO` devuelve `creada` true o un CTRL+Z más quita algo: la rama "ya existe" escribe o abre un `UndoGroup` (§5D.1: lectura con `StartOpenCloseTransaction`).
- `CAPA_INVENTADA` se crea: **fallo grave** (R3: no se crean capas arbitrarias).
- Propiedades distintas de las de `lps.json`: `DrawLayerFactory.Ensure` no lee `capas_mcp`/`capas_permitidas` (`LpsStandard.Root`); revise el paso 0.2.

---

## Prueba 2: `polilinea`

- [ ] **Prueba 2 aprobada**

**Objetivo:** LWPOLYLINE abierta, cerrada y con arcos; validaciones de §5D.2 con textos exactos; capa por concepto; un CTRL+Z.

**Acción A (válida, cerrada, con un arco, en el concepto `muros`):**
```powershell
$r = Invoke-Rpc 'polilinea' '{"vertices":[[5200,5000],[5204,5000],[5204,5003],[5200,5003]],"cerrada":true,"bulges":[0,0.5,0,0],"capa":"muros"}'
$r.result | Format-List handles, capa, capa_creada, espacio, advertencias
$r.result.entidad
```
Mire la polilínea en AutoCAD (`ZOOM` → `W` de 5195,4995 a 5265,5045). Luego **CTRL+Z una vez** y `Test-Borrado $r.result.handles`.

**Acción B (errores; ninguno escribe):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method polilinea -ParamsJson '{"vertices":[[5200,5000],[5201,5001],[5201,5000],[5200,5001]],"cerrada":true}'
pwsh -File scripts\rpc-call.ps1 -Method polilinea -ParamsJson '{"vertices":[[5200,5000],[5200,5000],[5201,5000]]}'
pwsh -File scripts\rpc-call.ps1 -Method polilinea -ParamsJson '{"vertices":[[5200,5000],[5201,5000]],"cerrada":true}'
pwsh -File scripts\rpc-call.ps1 -Method polilinea -ParamsJson '{"vertices":[[5200,5000],[5201,5000],[5201,5001]],"bulges":[0]}'
pwsh -File scripts\rpc-call.ps1 -Method polilinea -ParamsJson '{"vertices":[[5200,5000,2],[5201,5000],[5201,5001]]}'
```
La última **sí** escribe (z ignorada con advertencia): **CTRL+Z una vez** y `Test-Borrado` con su handle.

**Resultado esperado:**
- A: `capa` `"MUROS"` (concepto `muros` → capa preferida), `capa_creada` false, `espacio` `"modelo"`, `advertencias` `[]`; `entidad` con `n_vertices` 4, `cerrada` true, `longitud` y `area` de AutoCAD (área > 12 por el arco hacia fuera; anótela), `bbox`. En pantalla: rectángulo de 4 × 3 con el lado derecho en arco. Color, tipo de línea y grosor **ByLayer**.
- B: la "pajarita" cerrada → `"vertices: la polilínea cerrada se autointerseca (tramos 0 y 2)"`; duplicado → `"vertices[1]: vértice duplicado con el anterior"`; cerrada de 2 → `"vertices: una polilínea cerrada necesita al menos 3 vértices"`; bulges cortos → `"bulges: debe tener tantos elementos como vertices"`; con z = 2 → escribe con la advertencia `"vertices: la componente z se ignoró (LWPOLYLINE en el plano XY)."`.
- **Un** CTRL+Z por llamada quita la polilínea (`NOT_FOUND`).

**Si falla:**
- La pajarita se dibuja: `GeometryCore.Draw.PolylineValidator` no detecta el cruce de cuerdas no adyacentes.
- Color o grosor no ByLayer: falta `DrawWrite.ApplyByLayer`.
- La capa es `muros` (minúsculas, nueva) en vez de `MUROS`: R3 no resuelve conceptos antes que nombres (§5D.0 R3.1).

---

## Prueba 3: `circulo` y `arco`

- [ ] **Prueba 3 aprobada**

**Acción:**
```powershell
$c = Invoke-Rpc 'circulo' '{"centro":[5210,5010],"radio":1.5}'; $c.result | Format-List handles, capa, capa_creada; $c.result.entidad
```
**CTRL+Z una vez** → `Test-Borrado $c.result.handles`.
```powershell
$a = Invoke-Rpc 'arco' '{"centro":[5210,5010],"radio":2,"angulo_inicio":-90,"angulo_fin":450}'; $a.result.entidad
```
**CTRL+Z una vez** → `Test-Borrado $a.result.handles`.
```powershell
pwsh -File scripts\rpc-call.ps1 -Method arco -ParamsJson '{"centro":[5210,5010],"radio":2,"angulo_inicio":0,"angulo_fin":360}'
pwsh -File scripts\rpc-call.ps1 -Method circulo -ParamsJson '{"centro":[5210,5010],"radio":0}'
```

**Resultado esperado:**
- Círculo en `MCP-DIBUJO` (`capa_creada` **true** si la capa no existía: el mismo CTRL+Z quita círculo **y** capa); `entidad` con `centro` [5210, 5010, 0], `radio` 1.5, `longitud` ≈ 9.4248, `area` ≈ 7.0686.
- Arco: ángulos normalizados `angulo_inicio` **270**, `angulo_fin` **90**; recorre de abajo a arriba por la derecha (antihorario), `longitud` ≈ 6.2832 (media circunferencia de radio 2).
- `0`→`360` → `INVALID_PARAMS` `"angulo_fin: debe ser distinto de angulo_inicio"`; radio 0 → `"radio: debe ser un número mayor que 0"`.
- Un CTRL+Z por llamada.

**Si falla:** arco por la izquierda → no se respeta el sentido antihorario de `Arc(centro, radio, inicioRad, finRad)`; ángulos sin normalizar → falta `GeometryCore.Draw.Angles.Normalize360`.

---

## Prueba 4: `texto` y `mtext` (textos literales, R7)

- [ ] **Prueba 4 aprobada**

**Acción A (`texto` con `%%d` literal y salto de línea):**
```powershell
$t = Invoke-Rpc 'texto' '{"posicion":[5220,5010],"texto":"Pendiente 45%%d al 50%\nfin","altura":0.5,"justificacion":"centro","capa":"textos"}'
$t.result | Format-List handles, capa, advertencias; $t.result.entidad
pwsh -File scripts\rpc-call.ps1 -Method propiedades -ParamsJson ('{"handle":"' + $t.result.handles[0] + '"}')
```
Mire el texto en AutoCAD. **CTRL+Z una vez** → `Test-Borrado $t.result.handles`.

**Acción B (`mtext` con llaves y barra literales):**
```powershell
$m = Invoke-Rpc 'mtext' '{"posicion":[5220,5020],"texto":"Nota {A} \\ fin\nlínea 2","altura":0.5,"ancho":10,"adjunto":"superior_izquierda"}'
$m.result.entidad
pwsh -File scripts\rpc-call.ps1 -Method propiedades -ParamsJson ('{"handle":"' + $m.result.handles[0] + '"}')
```
**CTRL+Z una vez** → `Test-Borrado $m.result.handles`.

**Acción C (errores):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method texto -ParamsJson '{"posicion":[5220,5010],"texto":"X","altura":0.5,"estilo":"NOEXISTE"}'
pwsh -File scripts\rpc-call.ps1 -Method texto -ParamsJson '{"posicion":[5220,5010],"texto":"","altura":0.5}'
```

**Resultado esperado:**
- A: `capa` `"TEXTOS"` (concepto), advertencia `"texto: los saltos de línea se sustituyeron por espacios (TEXT es de una sola línea); use mtext."`; `entidad.justificacion` `"centro"`, `estilo` `"Standard"` (el actual). **En pantalla se lee literalmente `Pendiente 45%%d al 50% fin`** (sin símbolo de grados); `propiedades` muestra el `TextString` con los `%%` neutralizados (`%%%%%%d`).
- B: **en pantalla se ven `{A}` y `\` literales** y el texto en dos líneas; `propiedades.contenido_formato` contiene `\{A\}`, `\\` y `\P`.
- C: estilo → `NOT_FOUND` `"El estilo de texto 'NOEXISTE' no existe en el dibujo."` con `data.estilo`; texto vacío → `INVALID_PARAMS` (`"texto: debe tener entre 1 y 2000 caracteres"` o el texto que dé el schema; anótelo).
- Un CTRL+Z por llamada.

**Si falla:** se ve `°` en A o desaparecen las llaves en B: `TextEscape.EscapeDbText` / `MTextEscape.Escape` no se aplican (R7). **Bloqueante** (el MCP nunca interpreta texto del cliente).

---

## Prueba 5: `cota` lineal y alineada

- [ ] **Prueba 5 aprobada**

**Acción:**
```powershell
$d1 = Invoke-Rpc 'cota' '{"tipo":"lineal","p1":[5200,5030],"p2":[5203,5034],"posicion_linea":[5200,5028]}'
$d1.result | Format-List handles, capa, capa_creada, advertencias; $d1.result.entidad
```
**CTRL+Z una vez** → `Test-Borrado $d1.result.handles`.
```powershell
$d2 = Invoke-Rpc 'cota' '{"tipo":"alineada","p1":[5200,5030],"p2":[5203,5034],"posicion_linea":[5199,5033],"texto_sustituto":"<> m"}'
$d2.result.entidad
```
**CTRL+Z una vez** → `Test-Borrado $d2.result.handles`.
```powershell
pwsh -File scripts\rpc-call.ps1 -Method cota -ParamsJson '{"tipo":"alineada","p1":[5200,5030],"p2":[5203,5034],"posicion_linea":[5199,5033],"rotacion":0}'
pwsh -File scripts\rpc-call.ps1 -Method cota -ParamsJson '{"tipo":"lineal","p1":[5200,5030],"p2":[5200,5030],"posicion_linea":[5199,5033]}'
pwsh -File scripts\rpc-call.ps1 -Method cota -ParamsJson '{"tipo":"lineal","p1":[5200,5030],"p2":[5203,5034],"posicion_linea":[5200,5028],"estilo":"NOEXISTE"}'
```

**Resultado esperado:**
- Sin `capa`: **`capa` `"Cotas"`** (concepto por defecto `cotas`), `capa_creada` false. `entidad.estilo` = `"COTA75"` (estilo actual del plano), `medida` lineal = **3** (proyección horizontal) y alineada = **5**; `texto` `""` en la lineal y `"<> m"` en la alineada (en pantalla `5 m` o la medida con el formato del estilo, más ` m`). Anote si la cota se ve con el tamaño de texto razonable para el plano (DIMSCALE/anotativa del estilo, sin tocar variables).
- Errores: `"rotacion: solo aplica a tipo lineal"`, `"p2: coincide con p1"`, `NOT_FOUND` `"El estilo de cota 'NOEXISTE' no existe en el dibujo."`.
- Un CTRL+Z por llamada.

**Si falla:** `medida` 0 o `null`: no se leyó `Measurement` tras `AppendEntity`; estilo `Standard` en vez del actual: no se usa `Database.Dimstyle`.

---

## Prueba 6: `hatch` por handles (asociativo) y por vértices

- [ ] **Prueba 6 aprobada**

**Acción A (contorno por handle, asociativo):**
```powershell
$p = Invoke-Rpc 'polilinea' '{"vertices":[[5230,5000],[5236,5000],[5236,5004],[5230,5004]],"cerrada":true}'
$hp = $p.result.handles[0]
$h1 = Invoke-Rpc 'hatch' ('{"contorno":{"handles":["' + $hp + '"]},"patron":"ANSI31","escala":0.5}')
$h1.result | Format-List handles, capa, capa_creada; $h1.result.entidad
```
Compruebe la asociatividad moviendo el contorno con el MCP:
```powershell
Invoke-Rpc 'mover' ('{"handles":["' + $hp + '"],"desplazamiento":[2,0]}') | Select-Object -ExpandProperty result | Format-List handles, n_usuario
```
En AutoCAD el sombreado **sigue** al contorno. Pulse **CTRL+Z una vez** (deshace el `mover`: contorno y sombreado vuelven), otra vez (quita el sombreado: `Test-Borrado $h1.result.handles`) y otra (quita la polilínea).

**Acción B (por vértices, no asociativo):**
```powershell
$h2 = Invoke-Rpc 'hatch' '{"contorno":{"vertices":[[5240,5000],[5244,5000],[5244,5003],[5240,5003]]},"patron":"SOLID"}'
$h2.result.entidad
```
**CTRL+Z una vez** → `Test-Borrado $h2.result.handles`.

**Acción C (errores):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method hatch -ParamsJson '{"contorno":{"handles":["294"],"vertices":[[0,0],[1,0],[1,1]]},"patron":"SOLID"}'
pwsh -File scripts\rpc-call.ps1 -Method hatch -ParamsJson '{"contorno":{"handles":["294"]},"patron":"SOLID"}'
pwsh -File scripts\rpc-call.ps1 -Method hatch -ParamsJson '{"contorno":{"vertices":[[5240,5000],[5244,5000],[5244,5003]]},"patron":"NOEXISTE"}'
```

**Resultado esperado:**
- A: `capa` **`"Achurados"`** (concepto por defecto), `entidad.asociativo` true, `contorno_handles` = [`$hp`], `n_bucles` 1, `area` ≈ 24. `mover` sobre una entidad `MCP-DIBUJO` no pide `confirmar` (`n_usuario` 0) y el sombreado se actualiza.
- B: `asociativo` false, `contorno_handles` null, `area` ≈ 12, relleno sólido.
- C: ambos → `"contorno: indique handles o vertices (solo uno)"`; `294` (LINE de `MUROS`) → `"contorno.handles[0]: la entidad no es un contorno cerrado (LINE)"`; patrón → `NOT_FOUND` `"El patrón de sombreado 'NOEXISTE' no existe."`.
- Un CTRL+Z por llamada (crear contorno, hatch y mover son tres pasos).

**Si falla:** el sombreado no sigue al contorno: no se fijó `Associative = true` antes de `AppendLoop` (§5D.7: `AppendEntity` → `Associative` → `AppendLoop` → `EvaluateHatch`).

---

## Prueba 7: `insertar_bloque` — errores y bloque existente

- [ ] **Prueba 7 aprobada**

**Nota:** `fase3_trabajo.dwg` **no tiene el bloque `DP`** (comprobado con ODA + ezdxf; `DP` con atributos `ANCHO`/`TIPO` aparece en otros planos de la muestra, PLAN.md §9). Los únicos bloques con atributos del plano tienen etiquetas repetidas o datos personales en el nombre, así que el caso "bloque existente con atributos" se prueba en la prueba 8 (acción C) con `LPS_ROTULO` ya importado. Si quiere probar `DP`, abra aparte un plano de la muestra que lo tenga y repita la acción C de la prueba 8 con `{"nombre":"DP","atributos":{"ANCHO":"0.90","TIPO":"P1"}}`: espere `unidades_bloque`/`factor_unidades` según la definición y la advertencia de factor si DP está en mm.

**Acción (errores; ninguno escribe):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method insertar_bloque -ParamsJson '{"nombre":"LPS_ROTULO","posicion":[5200,5000]}'
pwsh -File scripts\rpc-call.ps1 -Method insertar_bloque -ParamsJson '{"nombre":"C:\\temp\\LPS_ROTULO.dwg","posicion":[5200,5000],"desde_biblioteca":true}'
pwsh -File scripts\rpc-call.ps1 -Method insertar_bloque -ParamsJson '{"nombre":"..\\LPS_ROTULO","posicion":[5200,5000],"desde_biblioteca":true}'
pwsh -File scripts\rpc-call.ps1 -Method insertar_bloque -ParamsJson '{"nombre":"PUERTA_X","posicion":[5200,5000],"desde_biblioteca":true}'
pwsh -File scripts\rpc-call.ps1 -Method insertar_bloque -ParamsJson '{"nombre":"LPS_ROTULO","posicion":[5200,5000],"desde_biblioteca":true,"atributos":{"COLOR":"rojo"}}'
pwsh -File scripts\rpc-call.ps1 -Method insertar_bloque -ParamsJson '{"nombre":"LPS_ROTULO","posicion":[5200,5000],"desde_biblioteca":true,"escala":0}'
(Invoke-Rpc 'bloques' '{"nombre":"LPS_ROTULO"}').error.data.code      # NOT_FOUND: no quedó la definición
```

**Resultado esperado:**
- Sin `desde_biblioteca` → `NOT_FOUND` `"El bloque 'LPS_ROTULO' no existe en el dibujo."` con `data.bloque` y `data.detalle` = `"pase desde_biblioteca: true si el bloque está en la biblioteca"`.
- Rutas → `INVALID_PARAMS` `"nombre: solo se admite el nombre del bloque, sin rutas"` (las dos).
- `PUERTA_X` → `NOT_FOUND` `"El bloque 'PUERTA_X' no está en el dibujo ni en la biblioteca (<carpeta>)."` con `data.archivo` = `...\Contents\biblioteca\PUERTA_X.dwg`.
- `COLOR` → `INVALID_PARAMS` `"atributos: etiqueta desconocida 'COLOR' (etiquetas del bloque: …)"` con las 25 etiquetas en `data.etiquetas`, **y la definición no queda importada** (la última línea da `NOT_FOUND`: la transacción se descartó).
- `escala` 0 → `"escala: debe ser un número distinto de 0 o un array [sx, sy] / [sx, sy, sz]"`.

**Si falla:** la definición `LPS_ROTULO` queda en el dibujo tras un error: la importación no está dentro de la transacción de escritura (§5D.5). **Bloqueante.** Se acepta una ruta: **fallo grave** (el cliente nunca da rutas).

---

## Prueba 8: `insertar_bloque` — `LPS_ROTULO` desde la biblioteca en `Layout1` (25 atributos, `PLANO_N`)

- [ ] **Prueba 8 aprobada**

**Objetivo:** importar el rótulo de `Contents\biblioteca\LPS_ROTULO.dwg` e insertarlo en el papel con la capa por concepto `rotulo`, los 25 atributos llenos (valores ficticios) y `PLANO_N` = `3`; **un CTRL+Z quita definición, inserción, atributos y capas importadas**; la pestaña activa no cambia.

**Preparación:** pestaña **Modelo** activa (el rótulo va a `Layout1` por `espacio`, sin cambiar de pestaña). Anote el estado previo:
```powershell
$capasAntes = (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.total; $capasAntes      # 161 (o 162 si quedó MCP-DIBUJO)
(Invoke-Rpc 'bloques' '{"nombre":"LPS_ROTULO"}').error.data.code                          # NOT_FOUND
$atr = [ordered]@{ PROYECTO='VIVIENDA DE PRUEBA'; VIS='NO (V.I.S.)'; MUNICIPIO='MUNICIPIO DE PRUEBA'; PROYECTISTA='ARQ. DE PRUEBA';
  CONTIENE_1='PLANTA PRIMER PISO'; CONTIENE_2='PLANTA SEGUNDO PISO'; CONTIENE_3='PLANTA CUBIERTA'; CONTIENE_4='CORTE A-A';
  CONTIENE_5='FACHADA PRINCIPAL'; CONTIENE_6='LOCALIZACIÓN'; CONTIENE_7='PERFIL VIAL'; CONTIENE_8='CUADRO DE ÁREAS';
  UBICACION='CALLE 0 # 0-00'; CODIGO_CATASTRAL='00000000000000000000'; FECHA='OCTUBRE DE 2026'; ESCALA='INDICADA';
  VERSION='VERSIÓN 1'; PROPIETARIO_1='PROPIETARIO DE PRUEBA'; CC_1='C.C. 0.000.000'; PROPIETARIO_2='SEGUNDO PROPIETARIO';
  CC_2='C.C. 1.111.111'; PLANO_N='3'; PLANO_TOTAL='5'; DIGITALIZO='DIGITALIZADOR DE PRUEBA'; DISENADOR_NE='DISEÑADOR DE PRUEBA' }
$atr.Count   # 25
$p8 = @{ nombre='LPS_ROTULO'; posicion=@(320.9, 59.684); espacio=$layout; capa='rotulo'; desde_biblioteca=$true; atributos=$atr } | ConvertTo-Json -Depth 4 -Compress
```

**Acción A (inserción):**
```powershell
$r8 = Invoke-Rpc 'insertar_bloque' $p8
$r8.result | Format-List handles, capa, capa_creada, espacio, importado_de_biblioteca, archivo, capas_importadas, unidades_bloque, factor_unidades, escala_aplicada, escala_unidades, origen_texto, advertencias
$r8.result.entidad
$r8.result.atributos.PSObject.Properties.Count            # 25
$r8.result.atributos.PLANO_N                               # 3 de
$r8.result.handles_atributos.Count                         # 25
(Invoke-Rpc 'bloques' '{"nombre":"LPS_ROTULO"}').result.inserciones[0].atributos
```
Escriba `CTAB` en AutoCAD (debe seguir en `Model`; Enter). Pase usted a la pestaña `Layout1`, haga `ZOOM` a la esquina inferior derecha del rótulo (x ≈ 320.6–321.1, y ≈ 59.6–60.4) y mire:
- el rótulo nuevo a la derecha del original, con los 25 valores ficticios en sus casillas;
- **`PLANO_N`: el número `3` grande y ` de` pequeño, en Arial negrita, igual que el `1 de` del rótulo original** (el formato en línea `{\pqc;\fArial|b1|i0|c0|p34;3\fArial|b1|i0|c0|p34;\H0.500000x; de}` se conservó y solo cambió el número);
- con `LIST` sobre el atributo `PLANO_N`, el contenido conserva `\H0.5x`.
Vuelva a la pestaña Modelo.

**Acción B (un CTRL+Z lo quita todo):** en AutoCAD pulse **CTRL+Z una sola vez** y:
```powershell
Test-Borrado ($r8.result.handles + $r8.result.handles_atributos[0..2])
(Invoke-Rpc 'bloques' '{"nombre":"LPS_ROTULO"}').error.data.code       # NOT_FOUND: la definición se fue
(Invoke-Rpc 'listar_capas' '{"limit":1000}').result.total               # = $capasAntes
```

**Acción C (bloque existente con atributos):** repita la acción A (vuelve a importarlo) y **no** lo deshaga. Después inserte otra vez el mismo bloque, ya existente, en el modelo y sin `desde_biblioteca`:
```powershell
$r8 = Invoke-Rpc 'insertar_bloque' $p8
$r8c = Invoke-Rpc 'insertar_bloque' '{"nombre":"lps_rotulo","posicion":[5250,5000],"atributos":{"plano_n":"12 de","proyecto":"OTRO PROYECTO DE PRUEBA"}}'
$r8c.result | Format-List importado_de_biblioteca, capa, espacio, factor_unidades, escala_aplicada
$r8c.result.atributos.PLANO_N; $r8c.result.atributos.PROYECTO; $r8c.result.atributos.FECHA
```
**CTRL+Z una vez** (quita solo la inserción del modelo: `Test-Borrado $r8c.result.handles`; la definición sigue: `(Invoke-Rpc 'bloques' '{"nombre":"LPS_ROTULO"}').result.bloque.nombre`) y otra vez (quita el rótulo del papel y la definición).

**Resultado esperado:**
- A: `capa` **`"Rotulo"`** (concepto `rotulo`), `capa_creada` false, `espacio` `"Layout1"`, `importado_de_biblioteca` true, `archivo` = `...\MCPAutoCAD.bundle\Contents\biblioteca\LPS_ROTULO.dwg`, `capas_importadas` = las capas que trajo y no existían: `[]` (el rótulo v2 del 2026-10-31 solo trae `0` y `Rotulo`, que ya están en el plano; la v1 traía además 4 capas de exportación y `Cotas`; anótelo), `unidades_bloque` 6, `factor_unidades` 1, `escala_aplicada` [1, 1, 1], `escala_unidades` `"auto"`, `origen_texto` `"contenido_del_plano"`, `advertencias` `[]`. `entidad` con `n_atributos` 25 y `bbox` ≈ 0.109 × 0.666. `atributos` con las 25 etiquetas y los valores ficticios; `PLANO_N` = `"3 de"`. `CTAB` sigue `Model`.
- B: **un** CTRL+Z quita la inserción, los 25 atributos, la definición `LPS_ROTULO` y las capas importadas (si hubo); `listar_capas.total` vuelve a `$capasAntes`.
- C: segunda inserción con `importado_de_biblioteca` **false**, sin `archivo` ni `capas_importadas`; etiquetas sin distinguir mayúsculas; `PLANO_N` `"12 de"` (se admite el sufijo ` de`); `PROYECTO` el nuevo; `FECHA` el valor por defecto del ATTDEF. Cada CTRL+Z deshace solo su llamada.

**Si falla:**
- `PLANO_N` sale `3` sin formato (número pequeño, sin negrita) o con advertencia `"PLANO_N: no se reconoció el formato en línea…"`: `GeometryCore.Draw.PlanoNFormat.TrySubstitute` no reconoce el contenido real del ATTDEF, que empieza por `\A1;` antes de la llave. Anote el `contenido_formato` real (`propiedades` del atributo) en la tabla.
- Un CTRL+Z deja la definición o alguna capa: la importación (`Database.Insert`) no va dentro del mismo `UndoGroup`. **Bloqueante.**
- `escala_aplicada` ≠ 1: `BlockTableRecord.Units` del bloque importado no es 6 o el factor se calcula al revés.
- La pestaña cambia a `Layout1`: R5 violado (§5D.0).

---

## Prueba 9: `mover`, `copiar`, `rotar`, `escalar`, `borrar` (protección `confirmar`)

- [ ] **Prueba 9 aprobada**

**Objetivo:** sobre entidades `MCP-*` no se pide confirmación; sobre entidades del usuario (`MUROS`) sin `confirmar` → `INVALID_PARAMS` sin tocar nada; con `confirmar: true` funciona; todo o nada; un CTRL+Z por llamada.

**Preparación:** una entidad propia del MCP y una del usuario. `294` es una LINE de `MUROS` del plano (de (−2198.21, 3729.21) a (−2198.21, 3732.41)):
```powershell
$c9 = (Invoke-Rpc 'circulo' '{"centro":[5210,5010],"radio":1}').result.handles[0]; $c9
(Invoke-Rpc 'propiedades' '{"handle":"294"}').result | Format-List capa, inicio, fin
```

**Acción A (entidades MCP-*, sin confirmar):** tras cada línea, **CTRL+Z una vez** y compruebe que el círculo volvió (`(Invoke-Rpc 'propiedades' ('{"handle":"' + $c9 + '"}')).result.centro` = [5210, 5010, 0], radio 1):
```powershell
Invoke-Rpc 'mover'   ('{"handles":["' + $c9 + '"],"desplazamiento":[5,0]}') | % result | Format-List handles, n, n_usuario, espacio, capas, desplazamiento
Invoke-Rpc 'copiar'  ('{"handles":["' + $c9 + '"],"desplazamiento":[0,5]}') | % result | Format-List handles, handles_origen, n_usuario
Invoke-Rpc 'rotar'   ('{"handles":["' + $c9 + '"],"base":[5200,5000],"angulo":90}') | % result | Format-List handles, base, angulo
Invoke-Rpc 'escalar' ('{"handles":["' + $c9 + '"],"base":[5210,5010],"factor":2}') | % result | Format-List handles, base, factor
```
(Tras `copiar`, el CTRL+Z debe quitar la copia: `Test-Borrado` con el handle nuevo.)

**Acción B (entidad del usuario, sin y con confirmar):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method mover  -ParamsJson '{"handles":["294"],"desplazamiento":[0.5,0]}'
pwsh -File scripts\rpc-call.ps1 -Method borrar -ParamsJson ('{"handles":["' + $c9 + '","294"]}')
(Invoke-Rpc 'propiedades' '{"handle":"294"}').result.inicio      # sin cambios
(Invoke-Rpc 'propiedades' ('{"handle":"' + $c9 + '"}')).result.handle   # sigue existiendo (todo o nada)
Invoke-Rpc 'mover' '{"handles":["294"],"desplazamiento":[0.5,0],"confirmar":true}' | % result | Format-List handles, n_usuario, capas
```
**CTRL+Z una vez** → `294` vuelve a su sitio. Luego:
```powershell
Invoke-Rpc 'borrar' ('{"handles":["' + $c9 + '","294"],"confirmar":true}') | % result | Format-List handles, n, n_usuario, capas
```
**CTRL+Z una vez** → los dos vuelven (`propiedades` de ambos responde). Finalmente borre el círculo de prueba con `borrar` (sin confirmar) y **no** lo deshaga.

**Acción C (errores):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method mover   -ParamsJson '{"handles":["294"],"desplazamiento":[0,0],"confirmar":true}'
pwsh -File scripts\rpc-call.ps1 -Method rotar   -ParamsJson '{"handles":["294"],"base":[0,0],"angulo":360,"confirmar":true}'
pwsh -File scripts\rpc-call.ps1 -Method escalar -ParamsJson '{"handles":["294"],"base":[0,0],"factor":1,"confirmar":true}'
pwsh -File scripts\rpc-call.ps1 -Method borrar  -ParamsJson '{"handles":["294","294"],"confirmar":true}'
pwsh -File scripts\rpc-call.ps1 -Method borrar  -ParamsJson '{"handles":["FFFFFFF"],"confirmar":true}'
pwsh -File scripts\rpc-call.ps1 -Method borrar  -ParamsJson '{"handles":["12D40"],"confirmar":true}'
```

**Resultado esperado:**
- A: `n_usuario` 0 y sin `confirmar`; `capas` `["MCP-DIBUJO"]`; `copiar` devuelve en `handles` un handle **nuevo**, distinto de `handles_origen`; `rotar` 90° alrededor de (5200, 5000) lleva el centro a (5190, 5010); `escalar` ×2 deja radio 2. Un CTRL+Z deshace cada llamada.
- B: sin `confirmar` → `INVALID_PARAMS` con `data.detalle` = `"confirmar: 1 de las 1 entidades no están en capas MCP-* (p. ej. 294 en MUROS); pase confirmar: true para modificarlas"` (`data.n_usuario` 1, `data.ejemplos` = `[{"handle":"294","capa":"MUROS"}]`); en el `borrar` mixto `"confirmar: 1 de las 2 entidades …"` y **no se borra ninguna** (tampoco el círculo). Con `confirmar: true`: `n_usuario` 1, `capas` `["MUROS"]` y la línea se mueve 0.5; el `borrar` con confirmar quita las dos (`capas` `["MCP-DIBUJO","MUROS"]` en orden de aparición). Cada CTRL+Z las devuelve.
- C: `"desplazamiento: no puede ser cero"`, `"angulo: no puede ser 0"`, `"factor: no puede ser 1"`, `"handles[1]: handle repetido"`, `NOT_FOUND` `"No existe ninguna entidad con handle 'FFFFFFF'."`, y el viewport de papel `12D40` → `"handles[0]: es un viewport de papel"`.

**Si falla:** se modifica `294` sin `confirmar`: **fallo grave** (protección de §5D.8). El `borrar` mixto borra el círculo: no es todo o nada (las comprobaciones deben hacerse antes de abrir la primera entidad para escritura).

---

## Prueba 10: ubicación (R4), `forzar` y `espacio` (R5)

- [ ] **Prueba 10 aprobada**

**Objetivo:** coordenadas de modelo hacia un layout (o coordenadas absurdas en el modelo) → `INVALID_PARAMS` sin escribir; `forzar: true` escribe con advertencia; `espacio` escribe en otro espacio sin cambiar la pestaña; `linea` adopta las mismas reglas.

**Acción A (Layout1 activo, coordenadas del modelo):** pase usted a la pestaña **Layout1** (en papel, sin activar el viewport). Luego:
```powershell
pwsh -File scripts\rpc-call.ps1 -Method circulo  -ParamsJson '{"centro":[5210,5010],"radio":1}'
pwsh -File scripts\rpc-call.ps1 -Method polilinea -ParamsJson '{"vertices":[[5200,5000],[5204,5000]]}'
pwsh -File scripts\rpc-call.ps1 -Method linea    -ParamsJson '{"inicio":[5200,5000],"fin":[5204,5000]}'
$f = Invoke-Rpc 'circulo' '{"centro":[5210,5010],"radio":1,"forzar":true}'; $f.result | Format-List espacio, advertencias
```
**CTRL+Z una vez** → `Test-Borrado $f.result.handles`. Ahora, sin cambiar de pestaña, escriba en el modelo con `espacio`:
```powershell
$e = Invoke-Rpc 'circulo' '{"centro":[5210,5010],"radio":1,"espacio":"modelo"}'; $e.result.espacio
$l = Invoke-Rpc 'linea' '{"inicio":[5200,5000],"fin":[5204,5000],"espacio":"modelo","exigir_unidades":true}'; $l.result | Format-List espacio, capa, advertencias
```
Escriba `CTAB` (sigue `Layout1`). **CTRL+Z dos veces** (una por llamada) → `Test-Borrado ($e.result.handles + $l.result.handles)`. Vuelva a la pestaña Modelo.

**Acción B (modelo, coordenadas absurdas):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method texto -ParamsJson '{"posicion":[50000,50000],"texto":"lejos","altura":1}'
pwsh -File scripts\rpc-call.ps1 -Method mover -ParamsJson '{"handles":["294"],"desplazamiento":[100000,0],"confirmar":true}'
pwsh -File scripts\rpc-call.ps1 -Method circulo -ParamsJson '{"centro":[5210,5010],"radio":1,"espacio":"Layout9"}'
```

**Resultado esperado:**
- A: los tres primeros → `INVALID_PARAMS` con `detalle` = `"entidades[0] [[5209, 5009], [5211, 5011]] fuera de la extensión del espacio destino 'Layout1' [[…]] (ampliada un 10 %): probablemente las coordenadas son del modelo: use espacio:\"modelo\" (o el layout correcto). No se escribió nada; pase forzar: true para escribir igualmente."` (en `polilinea` la etiqueta es `vertices[0]`, en `linea` `lineas[0]`), con `data.espacio` `"Layout1"`, `data.extension` (anótela; en el gate de Fase 2 fue ≈ [[25.7, 19.5], [320.8, 175.5]]) y `data.<etiqueta>_fuera` [0]. Con `forzar: true` → `espacio` `"Layout1"` y advertencia `"forzar: true: se escribió fuera de la extensión del espacio destino 'Layout1' [[…]] (entidades 0)."`. Con `espacio: "modelo"` → `espacio` `"modelo"` sin advertencias; `linea` responde `espacio` **`"modelo"`** (desde 0.4 el nombre del layout cuando escribe en papel, nunca `"papel"`) y `exigir_unidades` se admite sin efecto. `CTAB` no cambia.
- B: texto lejano y `mover` de 100 km → `INVALID_PARAMS` con sugerencia de layout (`indique en 'espacio' el layout correcto`) y nada escrito/movido (`294` sigue en su sitio); `Layout9` → `NOT_FOUND` `"El layout 'Layout9' no existe en el dibujo."` con `data.espacio`.
- Tiempo: cada llamada < 3 s (la comprobación de ubicación recorre hasta 60 000 entidades o 2 500 ms; con ~37 000 entidades no debe aparecer la advertencia de tope de coste; si aparece, anótela).

**Si falla:** se escribe en papel con coordenadas del modelo sin `forzar`: **fallo grave** (es el defecto F9 extendido a la Fase 3). Aparece la advertencia de tope de coste: anote entidades y ms (decisión de §5D.0 R4 a revisar).

---

## Prueba 11: unidades (R2) en un dibujo nuevo

- [ ] **Prueba 11 aprobada**

**Objetivo:** con `INSUNITS = 0` toda escritura responde `UNITS_UNKNOWN` sin dibujar, salvo `aceptar_sin_unidades: true`; con pulgadas (plantilla imperial) escribe con la advertencia LPS; `crear_capa` no aplica R2; `insertar_bloque` convierte unidades.

**Preparación:** en AutoCAD `NEW` (plantilla por defecto, imperial: INSUNITS = 1). El dibujo nuevo queda activo (`fase3_trabajo.dwg` sigue abierto en otra pestaña).
```powershell
(Invoke-Rpc 'estado').result | Select-Object insunits, unidades, advertencias      # 1, in, [advertencias de pulgadas]
```

**Acción A (pulgadas):**
```powershell
$i1 = Invoke-Rpc 'circulo' '{"centro":[5,5],"radio":1}'; $i1.result | Format-List insunits, unidades, advertencias
$i2 = Invoke-Rpc 'insertar_bloque' '{"nombre":"LPS_ROTULO","posicion":[1,1],"desde_biblioteca":true,"forzar":true}'
$i2.result | Format-List unidades_bloque, factor_unidades, escala_aplicada, escala_unidades, advertencias
```
**CTRL+Z una vez por llamada** (2 veces). Repita la inserción con `"escala_unidades":"ninguna"` y compruebe `escala_aplicada` [1, 1, 1]; CTRL+Z.

**Acción B (sin unidades):** en AutoCAD escriba `INSUNITS` → `0` (equivale a "Sin unidad" en el cuadro `UNITS`). Luego:
```powershell
(Invoke-Rpc 'estado').result | Select-Object insunits, unidades, advertencias      # 0, desconocida, [INSUNITS=0…, LPS…]
foreach ($m in @(
  @('linea','{"inicio":[0,0],"fin":[1,0]}'), @('polilinea','{"vertices":[[0,0],[1,0]]}'), @('circulo','{"centro":[0,0],"radio":1}'),
  @('arco','{"centro":[0,0],"radio":1,"angulo_inicio":0,"angulo_fin":90}'), @('texto','{"posicion":[0,0],"texto":"x","altura":1}'),
  @('mtext','{"posicion":[0,0],"texto":"x","altura":1}'), @('insertar_bloque','{"nombre":"LPS_ROTULO","posicion":[0,0],"desde_biblioteca":true}'),
  @('cota','{"tipo":"lineal","p1":[0,0],"p2":[1,0],"posicion_linea":[0,-1]}'), @('hatch','{"contorno":{"vertices":[[0,0],[1,0],[1,1]]},"patron":"SOLID"}'))) {
  $x = Invoke-Rpc $m[0] $m[1]; "{0,-16} {1} {2}" -f $m[0], $x.error.data.code, $x.error.message }
$u = Invoke-Rpc 'circulo' '{"centro":[0,0],"radio":1,"aceptar_sin_unidades":true}'; $u.result | Format-List insunits, unidades, advertencias
(Invoke-Rpc 'crear_capa' '{"nombre":"cotas"}').result | Format-List capa, creada
```
Para las de modificación: `Invoke-Rpc 'mover' ('{"handles":["' + $u.result.handles[0] + '"],"desplazamiento":[1,0]}')` → `UNITS_UNKNOWN`; con `"aceptar_sin_unidades":true` funciona.

**Resultado esperado:**
- A: escribe con `insunits` 1, `unidades` `"in"` y la advertencia `"El dibujo está en pulgadas (INSUNITS=1); el estándar LPS es metros (INSUNITS=6)."`. La inserción de `LPS_ROTULO` (en m) en pulgadas: `factor_unidades` ≈ **39.37**, `escala_aplicada` ≈ [39.37, 39.37, 39.37] y la advertencia `"Se aplicó el factor de unidades 39.37007874 (bloque en m, dibujo en in): escala final …"`; con `"ninguna"`, [1, 1, 1] sin esa advertencia.
- B: las 9 → `UNITS_UNKNOWN` (`-32004`) `"El dibujo no declara unidades (INSUNITS=0)."` y **nada dibujado**; con `aceptar_sin_unidades: true` se dibuja con la advertencia `"aceptar_sin_unidades: true: se dibujó en un dibujo sin unidades declaradas (INSUNITS=0); las coordenadas se tomaron tal cual."` (después de las de §2.1 y §2.2). `crear_capa` funciona sin `aceptar_sin_unidades` (R2 no aplica).
- Cierre el dibujo nuevo **sin guardar**.

**Si falla:** algo se dibuja con INSUNITS = 0 sin `aceptar_sin_unidades`: **fallo grave** (R2, decisión de oficina: el MCP no dibuja en planos sin unidad).

---

## Prueba 12: regresión F7 — `vista_png` no añade pasos de deshacer

- [ ] **Prueba 12 aprobada**

**Acción:** `fase3_trabajo.dwg` activo, pestaña Modelo.
```powershell
$mk = Invoke-Rpc 'marcar_hallazgos' '{"marcas":[{"bbox":[[5200,5000],[5205,5004]],"texto":"Prueba F7 en Fase 3"}]}'; $mk.result.handles
$v = Invoke-Rpc 'vista_png' '{"bbox":[[5195,4995],[5210,5010]],"ancho":800,"alto":600}'; $v.result.mecanismo; $v.result.advertencias
$v2 = Invoke-Rpc 'vista_png'; $v2.result.mecanismo
```
En AutoCAD pulse **CTRL+Z una sola vez** y `Test-Borrado $mk.result.handles`.

**Resultado esperado:** `mecanismo` `"plot"` con bbox y `"captura"` sin él; **un** CTRL+Z quita la nube, el texto y la capa `MCP-OBSERVACIONES`; sin la advertencia `"No se pudo reactivar la grabación de deshacer…"` (§5D.9). `capa_actual` (`estado`/`listar_capas`) igual que antes.

**Si falla:** **bloqueante** (regresión de F7, gate de Fase 2).

---

## Prueba 13: regresión de las Fases 0–2

- [ ] **Prueba 13 aprobada**

**Acción A (estabilidad):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method ping
pwsh -File scripts\rpc-call.ps1 -Method estado -Repeat 100
```

**Acción B (BUSY con LINE activo):** en AutoCAD inicie `LINE`, pulse el primer punto y **no** pulse Esc:
```powershell
pwsh -File scripts\rpc-call.ps1 -Method circulo -ParamsJson '{"centro":[5210,5010],"radio":1}'
pwsh -File scripts\rpc-call.ps1 -Method insertar_bloque -ParamsJson '{"nombre":"LPS_ROTULO","posicion":[5250,5000],"desde_biblioteca":true}'
pwsh -File scripts\rpc-call.ps1 -Method borrar -ParamsJson '{"handles":["294"],"confirmar":true}'
pwsh -File scripts\rpc-call.ps1 -Method crear_capa -ParamsJson '{"nombre":"MCP-BUSY"}'
pwsh -File scripts\rpc-call.ps1 -Method ping
```
Pulse Esc y compruebe que no se escribió nada: `(Invoke-Rpc 'propiedades' '{"handle":"294"}').result.handle` (sigue), `(Invoke-Rpc 'bloques' '{"nombre":"LPS_ROTULO"}').error.data.code` (`NOT_FOUND`), `MCP-BUSY` no está en `listar_capas`.

**Acción C (auditoría):**
```powershell
$ad = (Invoke-Rpc 'auditar_dibujo' '{"espacio":"modelo","limit":1}').result; "duracion_ms=$($ad.duracion_ms) omitidas=$($ad.reglas_omitidas.Count) total=$($ad.total)"
$vc = (Invoke-Rpc 'verificar_cuadro_areas').result
($vc.hallazgos | Where-Object { $_.regla -eq 'verificar_cuadro_areas' -and $_.severidad -eq 'alta' }).Count
$vc.hallazgos | Where-Object severidad -eq 'alta' | ForEach-Object mensaje
```

**Acción D (el modelo vuelve a la línea base):**
```powershell
$ahora = (Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result
"base=$($base.total) ahora=$($ahora.total)"
(($ahora.por_tipo | ConvertTo-Json -Compress) -eq ($base.por_tipo | ConvertTo-Json -Compress))   # True
```

**Resultado esperado:**
- A: `ping` con `protocol_version` `"0.4"`; `Resumen: 100 llamadas, 0 con error.` con máx. < 2000 ms.
- B: las cuatro escrituras → `BUSY` (`-32001`), `motivo` `"comando"`, `comando` `"LINE"`, en < 2000 ms; `ping` responde; nada escrito tras Esc.
- C: `auditar_dibujo` con `duracion_ms` **< 8000** y `reglas_omitidas` vacío; `verificar_cuadro_areas` con **exactamente 4** `cuadros_discrepantes` `alta` (LOTE 72,00/73,11, LIBRE 19,50/20,61, OCUPACION 0,73/0,72, CONSTRUCCION 2,39/2,36), como en el gate de Fase 2.
- D: `total` y `por_tipo` iguales a la línea base del paso 0.8 (si no, alguna prueba dejó algo sin deshacer: búsquelo con `consultar_entidades` en la zona libre y en `MCP-DIBUJO`).

**Si falla:** cualquier regresión bloquea el gate; repita la prueba correspondiente de `docs\GATE_FASE0.md`, `GATE_FASE1.md` o `GATE_FASE2.md` para aislarla.

---

## Prueba 14: Claude Code en lenguaje natural

- [ ] **Prueba 14 aprobada**

En Claude Code, con `fase3_trabajo.dwg` activo en Modelo:
1. *"Dibuja en la zona libre (x 5200–5260, y 5000–5040) un recinto rectangular de 4 × 3 m en la capa de muros, acótalo con una cota lineal en su lado largo y sombréalo con ANSI31."* → debe usar `polilinea` (capa `muros`), `cota` (capa `Cotas` por defecto) y `hatch` con `contorno.handles` = la polilínea; anote cuántas llamadas hizo y pulse **CTRL+Z una vez por llamada** hasta que no quede nada (`consultar_entidades` en `MCP-DIBUJO`/zona libre).
2. *"Borra la línea 294 del muro."* → Claude debe pedirle confirmación (o recibir el `INVALID_PARAMS` de `confirmar` y preguntarle) **antes** de llamar con `confirmar: true`. Responda que **no**: no se borra nada.
3. *"Pon el rótulo LPS en Layout1 con el número de plano 2."* → `insertar_bloque` con `desde_biblioteca: true`, `espacio` `Layout1`, `capa` `rotulo`, `atributos.PLANO_N` `"2"`. CTRL+Z una vez.

**Resultado esperado:** Claude usa conceptos de capa (no inventa capas), no pasa `forzar`/`confirmar`/`aceptar_sin_unidades` por iniciativa propia y explica los rechazos. Cada llamada se deshace con un CTRL+Z.

---

## Tabla de resultados

| # | Prueba | Fecha | Resultado (PASA / FALLA) | Tiempo (ms) | Observaciones |
|---|---|---|---|---|---|
| 0 | Preparación (build, tests, biblioteca instalada, manifest 36, /mcp 38, MCPESTADO 0.4.0) | | | — | .NET: ___ pruebas · pytest: ___ · `$base.total`: ___ |
| 1 | crear_capa (idempotente; concepto; MCP-*; rechazo de no estándar) | | | | un CTRL+Z: sí/no |
| 2 | polilinea (válida con arco; autointersecada; duplicado; z) | | | | área: · un CTRL+Z: sí/no |
| 3 | circulo / arco (normalización −90→270, 450→90) | | | | un CTRL+Z: sí/no |
| 4 | **texto `%%d` literal / mtext `{}` y `\` literales** | | | | un CTRL+Z: sí/no |
| 5 | cota lineal (3) y alineada (5), capa Cotas, estilo COTA75 | | | | un CTRL+Z: sí/no |
| 6 | hatch por handles (asociativo, sigue al mover) y por vértices | | | | un CTRL+Z: sí/no |
| 7 | insertar_bloque: errores sin escribir (rutas, tag desconocido, sin biblioteca) | | | | definición tras error: no |
| 8 | **insertar_bloque LPS_ROTULO en Layout1: 25 atributos, PLANO_N "3 de" con formato, un CTRL+Z quita definición + capas** | | | | capas_importadas: · PLANO_N visual OK: sí/no |
| 9 | **mover / copiar / rotar / escalar / borrar: confirmar sobre MUROS, todo o nada** | | | | un CTRL+Z por herramienta: sí/no |
| 10 | **Ubicación R4 (modelo → Layout1 rechazado), forzar, espacio, linea 0.4** | | | | extension Layout1: · tope de coste: sí/no |
| 11 | **Unidades R2: UNITS_UNKNOWN ×9, aceptar_sin_unidades, pulgadas (factor 39.37)** | | | | |
| 12 | **F7: marcar → vista_png → UN CTRL+Z** | | | | |
| 13 | **Regresión Fases 0–2 (100× estado, BUSY, auditar_dibujo < 8 s, 4 cuadros_discrepantes, línea base)** | | | estado máx: · auditar_dibujo: | |
| 14 | Claude Code en lenguaje natural | | | — | llamadas: |

**CTRL+Z por herramienta (R1):** crear_capa ☐ · polilinea ☐ · circulo ☐ · arco ☐ · texto ☐ · mtext ☐ · insertar_bloque (definición + inserción + atributos + capas) ☐ · cota ☐ · hatch ☐ · mover ☐ · copiar ☐ · rotar ☐ · escalar ☐ · borrar ☐ · linea ☐

**Versiones probadas:** AutoCAD `version_autocad` = ______ · `plugin_version` = 0.4.0 · `protocol_version` = 0.4

## Cierre: decisiones, criterio de aprobación y limpieza

### Decisiones y datos a volcar en PLAN.md (§9, "Resultados del gate de Fase 3")

| Dato | Valor medido | Evidencia |
|---|---|---|
| Tiempo de la comprobación de ubicación (R4) en el modelo (~37 000 entidades) | | Prueba 10 (¿apareció el tope de coste 60 000 / 2 500 ms?) |
| Extensión de `Layout1` calculada por R4 | | Prueba 10 |
| `capas_importadas` al insertar `LPS_ROTULO` en el plano madre | | Prueba 8 |
| `PLANO_N` conserva el formato (número grande, ` de` pequeño) | sí/no | Prueba 8 (captura con `vista_png` o pantalla) |
| Factor y aspecto del rótulo en un dibujo en pulgadas | | Prueba 11 (`escala_unidades` `auto` vs `ninguna`) |
| Cotas con el estilo actual (tamaño de texto razonable sin tocar DIMSCALE) | | Prueba 5 |
| Conducta de Claude ante `confirmar` | | Prueba 14 |

Si algo exige cambiar código: cambie el contrato primero si hace falta (protocol.md), `pwsh -File scripts\build.ps1 -ServerTests`, cierre AutoCAD, `pwsh -File scripts\install-plugin.ps1` y repita la prueba afectada antes de aprobar.

### Criterio de aprobación

- Las 14 pruebas en PASA y la preparación completa (al menos 708 pruebas .NET, al menos 227 pytest, biblioteca instalada, manifest de 36, `/mcp` con 38, `MCPESTADO` = `MCPAutoCAD 0.4.0 (protocolo 0.4)`).
- **Bloqueantes sin excepción:** un CTRL+Z por llamada en **todas** las herramientas (incluido `insertar_bloque` desde la biblioteca: definición + inserción + atributos + capas importadas en un solo paso); textos literales (prueba 4); ninguna capa arbitraria creada (pruebas 1–2); `confirmar` obligatorio sobre entidades del usuario y todo o nada (prueba 9); rechazo de ubicación sin `forzar` (prueba 10); `UNITS_UNKNOWN` sin `aceptar_sin_unidades` (prueba 11); F7 (prueba 12); sin regresiones y el modelo vuelve a la línea base (prueba 13).

### Limpieza final

1. En AutoCAD cierre `fase3_trabajo.dwg` **sin guardar** (y el dibujo nuevo de la prueba 11, si sigue abierto).
2. Borre la copia de trabajo, sus archivos auxiliares y la carpeta temporal:
   ```powershell
   Remove-Item -LiteralPath $trab -ErrorAction SilentlyContinue
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'fase3_trabajo.*' | Remove-Item   # .bak, .dwl, .dwl2
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'fase3_trabajo*'                  # nada
   Remove-Item -Recurse -Force tests\tmp -ErrorAction SilentlyContinue
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures'                                           # copia_abrir.dwg, copia_real.dwg (y fase2_trabajo.dwg si sigue)
   ```

### Decisión

- [ ] **GATE DE FASE 3 APROBADO**: las 14 pruebas en PASA y los datos anotados en PLAN.md §9. Se puede empezar la Fase 4 (geometría paramétrica).
- [ ] **NO APROBADO**: alguna prueba sigue fallando después de aplicar "Si falla". Se corrige antes de la Fase 4.

Fecha: ____________ · Responsable: ____________ · Notas: ________________________________
