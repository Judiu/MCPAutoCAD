# Gate de la Fase L (limpieza de planos): checklist en vivo

Este documento comprueba en AutoCAD real las cuatro herramientas de la Fase L (`PLAN.md` §5, "Fase L — Limpieza de planos"): `borrar_fuera_de`, `purgar`, `normalizar_capas` y `corregir_unidades_bloques`, más los cambios compatibles de `auditar_capas` (`patron_ajeno`, `sugerencia_patron`) y del aviso de huérfanas de `MCP-PREVIEW` en `estado`. El contrato está en `protocol.md` v0.6 (cabecera "Cambios 0.5 → 0.6", §4, §5C.1, §5E.5, **§5F** completa con la evidencia de §5F.7, §6 y §9). En cada herramienta se comprueban las reglas L1–L8 de §5F.0: **sin `confirmar: true` nunca se escribe** (previsualización sin estado: ningún paso de deshacer y el dibujo intacto) (L1), **un CTRL+Z por escritura** (L2), `UNITS_UNKNOWN` sin escape solo al escribir (L3), espacio (L4), el mismo inventario en ambos modos (L5), cabecera común `pid`/`preview`/`escrito`/`insunits`/`unidades`/`advertencias` (L6), todo o nada (L7) y coste medido (L8). **Si una prueba falla y no se arregla con lo que indica "Si falla", el gate no se aprueba y no se empieza la Fase 4B.**

- Quién ejecuta las pruebas: el usuario, en su equipo, con AutoCAD 2026 abierto con interfaz y Claude Code en otra ventana. Ningún agente ejecuta este checklist ni instala el plugin.
- Dibujos: **solo** los de `C:\LPSAPP\MCPAutoCAD\tests\fixtures\`. Copia de trabajo: **`tests\fixtures\faseL_trabajo.dwg`**, **ya creada por el organizador** como copia de `copia_real.dwg` (SHA-256 idéntico; plano madre 6X12 en metros: 36 955 entidades en modelo, 161 capas, 101 definiciones de bloque con nombre y 7 413 anónimas, `Layout1` y `Layout2`, capa actual `MUROS`, estilo de cota actual `COTA75`). **`faseL_trabajo.dwg` nunca se guarda**: la limpieza final la cierra sin guardar y la borra junto con sus `.bak`/`.dwl`. **`copia_real.dwg`, `casa6x12_limpio.dwg` y `copia_abrir.dwg` no se abren en AutoCAD ni se modifican en ningún momento.** Este checklist nunca abre ni apunta a nada de la unidad de red de expedientes.
- Referencia de lo esperado: `casa6x12_limpio.dwg` es el mismo plano limpiado a mano por el usuario el 2026-10-27 (4 694 entidades en modelo, 33 capas, 10 definiciones con nombre, 689 anónimas). Las cifras de abajo salen de protocol.md §5F.7 y de una segunda extracción con ODA File Converter + ezdxf hecha el 2026-10-27 para este checklist (ver "Hallazgo de la preparación del gate" en la prueba 2).
- Consola: PowerShell 7 (`pwsh`), con el directorio actual en `C:\LPSAPP\MCPAutoCAD`, y **la misma consola** durante todo el gate (las pruebas reutilizan `Invoke-Rpc`, `Ver`, `Resumen`, `$c`, `$base`).
- **Las cuatro herramientas escriben con `confirmar: true` sobre entidades y símbolos del usuario.** Úselas solo con `faseL_trabajo.dwg` activo. El orden de las pruebas importa: cada una parte del estado que deja la anterior (se indica en cada una).

---

## 0. Preparación (una sola vez)

1. **Compilar y ejecutar todas las pruebas** (AutoCAD puede estar abierto):
   ```powershell
   cd C:\LPSAPP\MCPAutoCAD
   pwsh -File scripts\build.ps1 -ServerTests
   ```
   Debe terminar con `=== BUILD CORRECTO ===` y `0 Warning(s)`. La línea de `dotnet test` debe indicar **al menos 1 105** pruebas superadas y 0 con error (900 al cerrar la Fase 4A; 1 070 con el plugin 0.6.0 del gate del 2026-10-27) y la de `pytest` **al menos 540 passed** (379 al cerrar la Fase 4A; 513 con 0.6.0; 151 de `test_faseL.py` y 10 de `test_list_changed.py`). Son las cifras del build de **0.6.1** (correcciones del gate en vivo: `normalizar_capas` con `espacios`/`incluir_viewports` y aviso de cambio del manifest); si un build posterior da más, valen las mayores (anótelas). Anote ambos números en la tabla final. Si cambia código del plugin durante el gate, vuelva a ejecutar `build.ps1` (no solo `dotnet test`) antes de reinstalar.
2. **Cerrar todas las instancias de AutoCAD** e **instalar el bundle**:
   ```powershell
   Get-Process acad -ErrorAction SilentlyContinue   # no debe listar nada
   pwsh -File scripts\install-plugin.ps1 -DryRun
   pwsh -File scripts\install-plugin.ps1
   Test-Path "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\lps.json"                   # True
   (Get-Item "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\MCPAutoCAD.Plugin.dll").VersionInfo.FileVersion   # 0.6.1.0
   ```
   Sin `lps.json` no valen las pruebas 5 y 6 (conceptos `textos`, `achurados`, capa `ESCALERA` de `capas_permitidas`).
3. **Abrir AutoCAD 2026** (sin abrir ningún plano todavía) y escribir **`MCPESTADO`**: debe mostrar `MCPAutoCAD 0.6.1 (protocolo 0.6)` y el pipe `\\.\pipe\mcp-autocad-<usuario>-<PID>`.
4. **Versión** desde PowerShell:
   ```powershell
   pwsh -File scripts\check-pipe.ps1
   pwsh -File scripts\rpc-call.ps1 -Method ping
   ```
   `ping` debe traer `"protocol_version": "0.6"` y `"plugin_version": "0.6.0"`.
5. **Funciones auxiliares y manifest.** Defina en la consola:
   ```powershell
   function Invoke-Rpc([string]$Method, [string]$Params = '{}') {
       (& .\scripts\rpc-call.ps1 -Method $Method -ParamsJson $Params -AsObject -TimeoutSec 60).Respuesta | ConvertFrom-Json
   }
   function Ver($r) {
       # Cabecera L6 de una respuesta de Fase L, o el error.
       if ($r.error) { "{0} {1} | detalle: {2} | data: {3}" -f $r.error.data.code, $r.error.message, $r.error.data.detalle, ($r.error.data | ConvertTo-Json -Compress -Depth 5); return }
       $r.result | Format-List preview, escrito, insunits, unidades, advertencias, duracion_ms
   }
   function Resumen() {
       # Entidades del modelo (total y por tipo), capas y bloques con nombre / anónimos.
       $m = (Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result
       $cap = (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.total
       $bn = (Invoke-Rpc 'bloques' '{"limit":1}').result.total
       $bt = (Invoke-Rpc 'bloques' '{"limit":1,"incluir_anonimos":true}').result.total
       [pscustomobject]@{ modelo = $m.total; capas = $cap; bloques_con_nombre = $bn; anonimos = $bt - $bn; por_tipo = ($m.por_tipo | ConvertTo-Json -Compress) }
   }
   (Invoke-Rpc 'tools/manifest').result.protocol_version          # 0.6
   (Invoke-Rpc 'tools/manifest').result.tools.name -join ', '
   ((Invoke-Rpc 'tools/manifest').result.tools | Where-Object escritura).Count
   ```
   La segunda línea debe imprimir **exactamente** (51 herramientas, orden de protocol.md §4):
   `ping, estado, listar_capas, linea, consultar_entidades, propiedades, textos, bloques, seleccion_actual, xrefs, vista_png, resumen_archivo, auditar_capas, entidades_en_capa_0, auditar_bloques, areas_por_capa, verificar_cuadro_areas, duplicados, textos_fuera_de_escala, auditar_dibujo, marcar_hallazgos, limpiar_observaciones, crear_capa, polilinea, circulo, arco, texto, mtext, insertar_bloque, cota, hatch, mover, copiar, rotar, escalar, borrar, punto_de, distancia, proyectar, muro, ejes_rejilla, recinto, offset, array_rectangular, array_polar, confirmar, descartar_preview, borrar_fuera_de, purgar, normalizar_capas, corregir_unidades_bloques`.
   La tercera debe dar **29** (`linea`, `marcar_hallazgos`, `limpiar_observaciones`, las 14 de la Fase 3, las 8 de escritura de la 4A y las **4 de la Fase L**). Si `plugin.log` (`%LOCALAPPDATA%\MCPAutoCAD\plugin.log`) contiene `Herramienta '<x>' del contrato no está implementada en este ensamblado; se omite del manifest`, el bundle instalado no es el del build integrado: **bloqueante**.
6. **Claude Code**: reinicie Claude Code en `C:\LPSAPP\MCPAutoCAD` (usa `.mcp.json`). Con `/mcp`, el servidor `autocad` debe estar conectado con **53 herramientas**: las 51 del manifest + `instancias` y `seleccionar_instancia`.
7. **Comprobar y abrir la copia de trabajo** (ya creada por el organizador; nunca se trabaja sobre `copia_real.dwg`):
   ```powershell
   $trab = 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\faseL_trabajo.dwg'
   (Get-FileHash -LiteralPath 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\copia_real.dwg').Hash -eq (Get-FileHash -LiteralPath $trab).Hash   # True
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'faseL_trabajo.dwl*'   # nada
   New-Item -ItemType Directory -Force tests\tmp | Out-Null
   ```
   Si el hash no coincide, **no siga**: pida al organizador una copia nueva. En AutoCAD: `OPEN` → `C:\LPSAPP\MCPAutoCAD\tests\fixtures\faseL_trabajo.dwg`. Quédese en la pestaña **Modelo**, `ZOOM` → `E`, sin comandos en curso.
8. **Línea base:**
   ```powershell
   (Invoke-Rpc 'estado').result | Select-Object insunits, unidades, measurement, advertencias   # 6, m, 1, []
   $base = Resumen; $base | Format-List
   $base | ConvertTo-Json -Depth 5 | Set-Content tests\tmp\faseL_base.json
   (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capa_actual                 # MUROS
   (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capas | Where-Object nombre -like 'MCP-*'   # ninguna
   ```
   Esperado: `modelo` **36 955**, `capas` **161**, `bloques_con_nombre` **101**, `anonimos` **7 413**. Anote los cuatro números.

### Recordatorio: previsualización sin estado (L1) y cómo comprobar "ningún paso"

- **Sin `confirmar: true`** (o con `preview: true`) la herramienta solo **lee** y devuelve el inventario: `preview: true`, `escrito: false`, lo "hecho" (`n_borradas`, `purgados`, `n_reasignadas`, `corregidos`) en `null` y `duracion_ms.escritura` `null`. **No** es un `preview_id`: no hay nada que confirmar; para escribir se repite la **misma** llamada con `confirmar: true`.
- `preview: false` sin `confirmar: true` → `INVALID_PARAMS` con `data.detalle` exacto `"confirmar: debe ser true para escribir; omita preview (o pase preview: true) para solo previsualizar"`.
- **Comprobación de "ningún paso de deshacer"**: después de una o varias previsualizaciones, **un** CTRL+Z en AutoCAD debe deshacer la **última escritura anterior** (no algo invisible). El historial (F2) muestra `_u GRUPO` por cada escritura del MCP.
- Errores: desde Claude Code llegan como `[INVALID_PARAMS] Parámetros inválidos: <detalle>`, `[NOT_FOUND] …`, `[UNITS_UNKNOWN] …`, `[INVALID_GEOMETRY] Geometría no construible: …`; los `data.detalle` se comparan **literalmente** con protocol.md §5F y §6.

---

## Prueba 1: el contorno (hallazgo de §5F.7)

- [ ] **Prueba 1 aprobada**

**Objetivo:** confirmar el hallazgo de §5F.7 (la `39D46` del plano sucio **no** es el contorno que usó el usuario) y crear el contorno real con `polilinea`.

**Acción:**
```powershell
(Invoke-Rpc 'propiedades' '{"handle":"39D46"}').result | Format-List handle, tipo, capa, cerrada, bbox
$pc = Invoke-Rpc 'polilinea' '{"vertices":[[3255.1938,4034.1727],[3341.0294,4034.1727],[3341.0294,3950.8152],[3255.1938,3950.8152]],"cerrada":true,"capa":"MCP-DIBUJO"}'
$c = $pc.result.handles[0]; "contorno = $c"; $pc.result | Format-List capa, capa_creada, espacio, advertencias
```
**Resultado esperado:**
- `39D46`: `LWPOLYLINE` cerrada en la capa **`Cotas`** con `bbox` ≈ `[[3321.47, 3951.59, 0], [3407.31, 4034.95, 0]]` (rodea la **segunda** copia del plano; en el limpio la misma entidad está movida −66,28 en x y −0,78 en y y en la capa `m fijos`). Por eso el gate **no** la usa: sobre esta copia borraría la casa buena.
- `polilinea`: un handle nuevo (anótelo como `$c`), `capa` `MCP-DIBUJO`, `capa_creada` true, `espacio` `modelo`. Es **un** paso de deshacer (no lo deshaga).

**Si falla:** la `39D46` está en `m fijos` o en la posición del limpio: `faseL_trabajo.dwg` no es la copia del sucio (vuelva al paso 0.7).

---

## Prueba 2: `borrar_fuera_de` en previsualización (nada escrito)

- [ ] **Prueba 2 aprobada**

**Objetivo:** el inventario exacto del borrado sobre el plano madre, los viewports afectados con su advertencia literal, los errores de contorno y que nada de esto escribe.

**Hallazgo de la preparación del gate (2026-10-27, ODA + ezdxf, regla de §5F.1 por caja):** la `39D46` original **no** está `fuera` del contorno nuevo: su caja (x 3321.47–3407.31) atraviesa el borde derecho (x 3341.03), así que es **`cruza`**. Con el criterio por defecto `caja_cruza` se **conserva**; reproducir la limpieza del usuario (4 711 entidades finales) exige `criterio: "caja_dentro"`. protocol.md §5F.7 y el ejemplo de §5F.1 están corregidos con esta tabla (antes daban `n_cruzan` 0). Las cifras por criterio son:

| criterio | `n_borrar` | `n_conservar` | `n_cruzan` | `n_sin_caja` | `n_total` | modelo tras escribir | LWPOLYLINE tras escribir | `1789D` (borradas / conservadas) |
|---|---|---|---|---|---|---|---|---|
| `caja_cruza` (defecto) | **32 244** | **4 702** | **1** (`39D46`) | 9 | 36 955 | 4 712 | 867 | 4 044 / 4 702 |
| `caja_dentro` | **32 245** | **4 701** | 1 | 9 | 36 955 | **4 711** | 866 | 4 045 / 4 701 |

Salvedad: ezdxf mide las cotas `77893` y `779E6` (capa `Cotas`) desde x ≈ −0,12 (su bloque `*D` llega al origen); si `GeometricExtents` de AutoCAD hace lo mismo, con `caja_cruza` salen `n_cruzan` **3** y `n_borrar` **32 242** (las dos cotas se conservan; con `caja_dentro` se borran igual y la segunda fila no cambia). Anote lo que dé AutoCAD.

**Acción A (previsualización con el criterio por defecto):**
```powershell
$p2 = Invoke-Rpc 'borrar_fuera_de' ('{"contorno":"' + $c + '"}'); Ver $p2
$p2.result | Format-List contorno, espacio, criterio, n_total, n_borrar, n_conservar, n_cruzan, n_sin_caja, n_excluidas, n_borradas, muestra
$p2.result.por_tipo | ConvertTo-Json -Compress
$p2.result.por_capa | ConvertTo-Json -Compress
$p2.result.viewports | Format-Table layout, handle, estado, n_borradas, n_conservadas, @{n='ventana';e={$_.ventana | ConvertTo-Json -Compress}}
(Invoke-Rpc 'propiedades' '{"handle":"39D46"}').result.capa       # sigue existiendo: Cotas
```
**Acción B (con `caja_dentro`, el criterio que se escribirá en la prueba 3):**
```powershell
$p2b = Invoke-Rpc 'borrar_fuera_de' ('{"contorno":"' + $c + '","criterio":"caja_dentro"}')
$p2b.result | Format-List criterio, n_borrar, n_conservar, n_cruzan, n_sin_caja, preview, escrito
$p2b.result.viewports | Format-Table layout, handle, estado, n_borradas, n_conservadas
```
**Acción C (L1, errores y "nada escrito"):**
```powershell
Ver (Invoke-Rpc 'borrar_fuera_de' ('{"contorno":"' + $c + '","preview":false}'))
Ver (Invoke-Rpc 'borrar_fuera_de' ('{"contorno":"' + $c + '","confirmar":true,"preview":true}'))
Ver (Invoke-Rpc 'borrar_fuera_de' '{"contorno":"FFFFF"}')
Ver (Invoke-Rpc 'borrar_fuera_de' '{"contorno":"294"}')
Ver (Invoke-Rpc 'borrar_fuera_de' ('{"contorno":"' + $c + '","excluir_capas":["NO_EXISTE_1","Cotas","NO_EXISTE_2"]}'))
Ver (Invoke-Rpc 'borrar_fuera_de' ('{"contorno":"' + $c + '","criterio":"caja"}'))
Ver (Invoke-Rpc 'borrar_fuera_de' ('{"contorno":"' + $c + '","espacio":"Layout1"}'))
(Resumen).modelo                                                   # 36 956 (36 955 + el contorno)
```
Ahora, en AutoCAD, **CTRL+Z una vez**: debe deshacer la **polilínea** de la prueba 1 (no algo invisible). Compruebe `(Invoke-Rpc 'propiedades' ('{"handle":"' + $c + '"}')).error.data.code` → `NOT_FOUND`, y vuelva a crear el contorno (nuevo handle):
```powershell
$c = (Invoke-Rpc 'polilinea' '{"vertices":[[3255.1938,4034.1727],[3341.0294,4034.1727],[3341.0294,3950.8152],[3255.1938,3950.8152]],"cerrada":true,"capa":"MCP-DIBUJO"}').result.handles[0]; "contorno = $c"
```

**Resultado esperado:**
- A: `preview` true, `escrito` false, `n_borradas` null, `duracion_ms.escritura` null, `duracion_ms.lectura` anotado (§5F.1 estima 0,4–1 s). `contorno` = `{handle: $c, tipo: LWPOLYLINE, capa: MCP-DIBUJO, n_vertices: 4, area ≈ 7154.6, bbox [[3255.19, 3950.82], [3341.03, 4034.17]]}`; `espacio` `modelo`; `criterio` `caja_cruza`; conteos de la primera fila de la tabla (`n_total = n_borrar + n_conservar + n_sin_caja`). `por_tipo` (lo que se borra, n descendente): **LINE 17 597, LWPOLYLINE 3 962, DIMENSION 2 866, TEXT 1 434, ARC 1 397, CIRCLE 1 191, MTEXT 1 047, HATCH 605, ELLIPSE 475, INSERT 455, SPLINE 377, ATTDEF 312, POLYLINE 240, 3DFACE 218, SOLID 36, TRACE 32** (DIMENSION 2 864 si las dos cotas salen `cruza`). `por_capa` (primeras): `MUEBLE` 4 093, `3. MOBILIARIO` 3 545, `Cotas` ≈ 3 464, `MUROS` 2 971, `0` 1 113, `7. LINEA CORTE` 1 086, `ventanas` 999, `Q-SPCQ-____-OTLN` 996, `Filos` 770, `6. ACHURADOS` 713 (protocol.md §5F.7 da ya 3 464 / 2 971, cifras de la extracción del gate; una extracción anterior daba 4 109 / 2 973; anote el valor real). `muestra`: 10 handles. `viewports`: **`12D44`** (Layout1) `sin_cambio` 0 / 0; **`3B5B9`** (Layout1) `sin_cambio` 0 / 4 701; **`1789D`** (Layout2) **`parcial`** 4 044 / 4 702. `advertencias` contiene **exactamente una** de viewport, literal: `"El viewport 1789D del layout 'Layout2' mira la ventana de modelo [[2934.33, 3436.5], [3784.36, 4081.46]] que quedará parcialmente borrada: se borran 4044 entidades que la intersecan y se conservan 4702."` (con los números que dé AutoCAD), y `"9 entidad(es) sin caja calculable se conservaron (REGION 6, OLE2FRAME 3)."`. La `39D46` sigue existiendo.
- B: segunda fila de la tabla (`n_borrar` 32 245, `n_conservar` 4 701; `1789D` `parcial` 4 045 / 4 701).
- C, en orden: `INVALID_PARAMS` `"confirmar: debe ser true para escribir; omita preview (o pase preview: true) para solo previsualizar"`; `confirmar`+`preview: true` → previsualización (`preview` true, `escrito` false); `NOT_FOUND` `"No existe ninguna entidad con handle 'FFFFF'."` (`data.handle`); `INVALID_PARAMS` `"contorno: la entidad 294 no es una polilínea cerrada ni un círculo (LINE)"`; `NOT_FOUND` `"excluir_capas: capas inexistentes: NO_EXISTE_1, NO_EXISTE_2"` (`data.capas` con las dos); `INVALID_PARAMS` `"criterio: debe ser caja_cruza o caja_dentro"`; `INVALID_PARAMS` `"contorno: la entidad <$c> no está en el espacio 'Layout1'"`. El modelo sigue con 36 956.
- El CTRL+Z deshace la polilínea: **ninguna** de las previsualizaciones dejó un paso (L1). `DBMOD` no lo cambia ninguna previsualización (el dibujo ya estaba modificado por la polilínea).

**Si falla:** el CTRL+Z no quita la polilínea (deshace "nada"): alguna previsualización abrió un `UndoGroup` (**bloqueante**, L1). Falta la advertencia del viewport `1789D` o sus números no coinciden con `viewports`: **bloqueante** (§5F.1 la exige en ambos modos). La `39D46` aparece en `muestra` o en `por_tipo.LWPOLYLINE` con `caja_cruza`: el criterio no es el de §5F.1 (**bloqueante**).

---

## Prueba 3: `borrar_fuera_de` con `confirmar: true` (un paso, tiempo)

- [ ] **Prueba 3 aprobada**

**Objetivo:** el borrado masivo en **un** paso de deshacer, su duración (riesgo de §3.7: 10 s en el hilo principal) y la comparación con el plano limpio a mano.

**3A — Escribir** (con `caja_dentro`, para reproducir la limpieza del usuario; ver el hallazgo de la prueba 2):
```powershell
$t0 = Get-Date
$w3 = Invoke-Rpc 'borrar_fuera_de' ('{"contorno":"' + $c + '","criterio":"caja_dentro","confirmar":true}'); Ver $w3
"cliente: {0:N0} ms" -f ((Get-Date) - $t0).TotalMilliseconds
$w3.result | Format-List n_borrar, n_borradas, n_conservar, duracion_ms
Get-Content "$env:LOCALAPPDATA\MCPAutoCAD\plugin.log" -Tail 20 | Select-String 'borrar_fuera_de'
```
**3B — Verificar:**
```powershell
$r3 = Resumen; $r3 | Format-List
(Invoke-Rpc 'propiedades' ('{"handle":"' + $c + '"}')).result.capa     # MCP-DIBUJO: el contorno no se borró
(Invoke-Rpc 'propiedades' '{"handle":"39D46"}').error.data.code       # NOT_FOUND (borrada por caja_dentro)
(Invoke-Rpc 'propiedades' '{"handle":"B7647"}').result.tipo            # OLE2FRAME (sin caja: se conserva)
foreach ($h in 'B7111','B7142','B71A8') { (Invoke-Rpc 'propiedades' ('{"handle":"' + $h + '"}')).result.tipo }   # LWPOLYLINE ×3 (degeneradas dentro)
```
**3C — Un CTRL+Z** en AutoCAD y comprobar que devuelve **todo**:
```powershell
(Resumen).modelo                                                       # 36 956 (36 955 + el contorno)
(Invoke-Rpc 'propiedades' '{"handle":"39D46"}').result.capa            # Cotas
```
En este mismo momento (plano completo) ejecute la **acción A de la prueba 8** (rendimiento de `estado`) y deshaga su `crear_capa` como allí se indica.

**3D — Repetir la escritura** para seguir con la prueba 4:
```powershell
$w3b = Invoke-Rpc 'borrar_fuera_de' ('{"contorno":"' + $c + '","criterio":"caja_dentro","confirmar":true}'); $w3b.result | Format-List n_borradas, duracion_ms
(Resumen).modelo                                                       # 4 711
```

**Resultado esperado:**
- 3A: `preview` false, `escrito` true, `n_borradas` = `n_borrar` = **32 245** (mismo inventario que la acción B de la prueba 2: L5), `duracion_ms` `{lectura, escritura}` con números (anótelos; §5F.1 estima 2–5 s de escritura). `plugin.log`: `borrar_fuera_de: 32245 entidades borradas en <ms> ms`. Advertencias: la del viewport `1789D` y la de las 9 sin caja.
- **Riesgo de §3.7:** si llega `INTERNAL` con `detalle` `"timeout"` (más de 10 s en el hilo principal), **no repita la llamada**: espere a que AutoCAD responda, compruebe con `Resumen` que el modelo tiene 4 711 y que **un** CTRL+Z devuelve 36 956. Anótelo (no es un fallo del gate si el borrado terminó y es un paso; sí lo es si quedó a medias o en varios pasos).
- 3B: `modelo` **4 711** = 4 701 dentro + 9 sin caja + 1 contorno; `por_tipo`: **LINE 1 980, LWPOLYLINE 866 (865 + contorno), DIMENSION 684, CIRCLE 240, ARC 211, ELLIPSE 167, TEXT 160, ATTDEF 150, HATCH 127, MTEXT 82, INSERT 35, REGION 6, OLE2FRAME 3**. `capas` y bloques no cambian todavía (borrar no purga): 162 capas (161 + `MCP-DIBUJO`), 101 / 7 413.
- **Comparación con `casa6x12_limpio.dwg` (4 694):** 4 711 − 4 694 = **17** = 9 LWPOLYLINE degeneradas de `Cotas` dentro del contorno que el usuario borró a mano (`B7111`, `B7113`, `B7116`, `B7117`, `B7118`, `B7142`, `B7159`, `B719B`, `B71A8`; la herramienta las conserva porque su caja está dentro) + 8 entidades sin caja que el usuario borró (6 REGION de `3. MOBILIARIO` y los OLE2FRAME `163BF` y `3C372`; conservó `B7647`) + 1 contorno nuevo en `MCP-DIBUJO` − 1 (`39D46`, que en el limpio sigue existiendo movida y aquí se borra). Ninguna entidad `fuera` sobrevive y ninguna `dentro` falta salvo esas 9.
- 3C: **un** CTRL+Z devuelve las 32 245 (modelo 36 956, `39D46` en `Cotas`). 3D: otra vez 4 711.

**Si falla:** hacen falta varios CTRL+Z o el modelo queda entre 4 711 y 36 956: la escritura no es un solo `UndoGroup` con una `Transaction` (**bloqueante**, L2). El contorno desaparece: **bloqueante**.

---

## Prueba 4: `purgar` (previsualización, escritura y un CTRL+Z)

- [ ] **Prueba 4 aprobada**

**Parte de:** el plano limpio de la prueba 3D (4 711 entidades, 162 capas, 101 / 7 413 bloques).

**4A — Previsualización:**
```powershell
$p4 = Invoke-Rpc 'purgar'; Ver $p4
$p4.result | Format-List categorias, rondas, excluidos, purgados
$p4.result.purgables.bloques | Format-List n, n_con_nombre, n_anonimos, n_entidades, nombres_truncados
$p4.result.purgables.bloques.nombres | Where-Object { $_.nombre -notlike '*`**' } | Select-Object -First 15 | Format-Table nombre, n_entidades, ronda, unidades
foreach ($k in 'capas','tipos_linea','estilos_texto','estilos_cota','regapps') { "{0}: {1}" -f $k, $p4.result.purgables.$k.n }
$p4.result.protegidos | ConvertTo-Json -Depth 3
Ver (Invoke-Rpc 'purgar' '{"categorias":["capas","grupos"]}')
Ver (Invoke-Rpc 'purgar' '{"categorias":["capas"],"excluir":["CAPA_QUE_NO_EXISTE"]}')
(Resumen) | Format-List modelo, capas, bloques_con_nombre, anonimos         # sin cambios: 4 711, 162, 101, 7 413
```
**4B — Escritura:**
```powershell
$w4 = Invoke-Rpc 'purgar' '{"confirmar":true}'; Ver $w4
$w4.result | Format-List rondas, duracion_ms
$w4.result.purgados.bloques | Format-List n, n_con_nombre, n_anonimos
foreach ($k in 'capas','tipos_linea','estilos_texto','estilos_cota','regapps') { "{0}: {1}" -f $k, $w4.result.purgados.$k.n }
$r4 = Resumen; $r4 | Format-List modelo, capas, bloques_con_nombre, anonimos
((Invoke-Rpc 'bloques' '{"limit":200}').result.bloques).nombre
```
**4C — Un CTRL+Z** en AutoCAD:
```powershell
(Resumen) | Format-List modelo, capas, bloques_con_nombre, anonimos         # 4 711, 162, 101, 7 413
```
Haga ahora la **prueba 5** (necesita todas las capas) y después **4D — repita la escritura** (`Invoke-Rpc 'purgar' '{"confirmar":true}'`; también vale `REDO`/CTRL+Y inmediatamente después del CTRL+Z, pero entonces la prueba 5 se hace sobre el plano purgado y algunas capas no estarán) y compruebe otra vez 33 capas y ≈ 10 / 689 bloques.

**Resultado esperado:**
- 4A: `preview` true, `purgados` null, `categorias` = las seis, `rondas` ≥ 2. `purgables.bloques`: **≈ 93 con nombre** de 101 (los 8 que siguen referenciados: `Blocks_Lavadero…-255259-Nivel 2`, `Blocks_Lavadora…-255255-Nivel 2`, `C`, `EE`, `PuertaBaño`, `TANQUE 1000 LTS`, `af`, `sanit116a`), entre ellos **`VS 2 2025_6X12  ARQ CLIENTE_E`** con `n_entidades` **36 948** en la ronda 1, `_DotSmall`, `_OBLIQUE`, `_DOT`, `_ArchTick`, `car`, `PERSO13`; `n_anonimos` **6 724** (7 413 → 689). `capas` ≈ **128** (161 → 33 contando `MCP-DIBUJO`; `m fijos` vacía y purgable; `ESCALERA` también, sin entidades tras el borrado), `tipos_linea` **10** (19 → 9), `estilos_texto` **32** (36 → 4: `75`, `GENERATED_STYLE_2`, `Standard`, `cota75`), `estilos_cota` **12** (14 → 2: `COTA75`, `Standard`), `regapps` (anote n). Tolerancias de §5F.7: ±2 en bloques con nombre y ±1 en capas. `protegidos` (símbolos **sin uso** no purgados por las exclusiones fijas): en capas, los que estén vacíos de `0`, `DEFPOINTS`, `MUROS (actual)`; en tipos de línea `ByBlock`/`ByLayer (actual)` si no se usan; en estilos de texto `Standard (actual)` si no se usa; en estilos de cota `COTA75 (actual)`/`Standard` si no se usan; en regapps `ACAD`. Ningún nombre `MCP-*` aparece en `purgables.capas`. Errores: `INVALID_PARAMS` `"categorias[1]: debe ser uno de bloques, capas, tipos_linea, estilos_texto, estilos_cota, regapps, todo"`; con `excluir` inexistente la llamada responde y avisa `"excluir: no existe ningún símbolo llamado 'CAPA_QUE_NO_EXISTE'."`. Nada cambia.
- 4B: `escrito` true, `purgados` con la forma de `purgables`. `Resumen`: `capas` **33**, `anonimos` **689**, `bloques_con_nombre` **≈ 10**: los 8 de arriba más **`Puerta Dinamica LM`** y **`VENTANA01`**, que `Database.Purge` conserva por ser definiciones de bloques dinámicos referenciadas por sus representaciones anónimas aunque la previsualización las daba por purgables. En ese caso `advertencias` incluye `"La purga real difiere de la previsualización: 2 símbolo(s) previstos no se purgaron (referenciados por xdata, diccionarios o bloques dinámicos) y 0 no previstos sí."` (anote los números exactos y si la advertencia aparece; con otra diferencia, anótela). `modelo` sigue en 4 711.
- 4C: **un** CTRL+Z devuelve las 101 / 7 413 definiciones y las 162 capas.

**Si falla:** el CTRL+Z no devuelve todas las definiciones o hacen falta varios: **bloqueante** (L2). Se purga `0`, `DEFPOINTS`, `MUROS` (actual), una `MCP-*`, `Standard`, `COTA75` o `ByLayer`: **bloqueante** (§5F.2). La purga real difiere y no hay advertencia: **bloqueante** (L5 documentada).

---

## Prueba 5: `auditar_capas` con `patron_ajeno` (§5F.5)

- [ ] **Prueba 5 aprobada**

**Parte de:** el plano tras 4C (todas las capas). Es una lectura.

```powershell
$a5 = (Invoke-Rpc 'auditar_capas' '{"limit":1000}').result
foreach ($n in 'Q-SPCQ-____-OTLN','P-SANR-FIXT-OTLN','S-COLS','A-DOOR-____-OTLN','3D EJES ESTRUCTURALES_Pen_No__7','MUROS') {
    $a5.capas | Where-Object nombre -eq $n | Select-Object nombre, clasificacion, patron_ajeno, sugerencia_patron, sugerencia
}
$a5.resumen | ConvertTo-Json -Compress -Depth 3
```
**Resultado esperado** (la clasificación **no cambia** respecto a 0.5):
- `Q-SPCQ-____-OTLN` → `clasificacion` `estandar`, `patron_ajeno` `revit_aia`, `sugerencia_patron` **null**.
- `P-SANR-FIXT-OTLN` → `revit_aia`, `sugerencia_patron` **`Sanitarios`**.
- `S-COLS` → `revit_aia`, **`columnas`**.
- `A-DOOR-____-OTLN` → `revit_aia`, **`PUERTAS`**.
- `3D EJES ESTRUCTURALES_Pen_No__7` → `patron_ajeno` **`exportacion`**, `sugerencia_patron` **`EJES`**.
- `MUROS` → `patron_ajeno` null, `sugerencia_patron` null.
- Si alguno de esos nombres no existe en el plano, anótelo (no es fallo). `resumen` igual que en 0.5 (162 capas).
- **Nota (estándar v2, 2026-10-28; PLAN.md §9 "Decisión 2026-10-28: estándar v2"):** lo anterior vale para el `lps.json` v1 con el que se ejecutó el gate (hoy en `standards/historico/lps_v1_6x12_sucio.json`). Con el `lps.json` vigente ninguna de esas capas Revit/exportación es estándar y `mapa_normalizacion` gana sobre la heurística: `Q-SPCQ-____-OTLN` → `ajena_revit_aia`, `sugerencia` y `sugerencia_patron` **`muebles fijos`**; `P-SANR-FIXT-OTLN` → `ajena_revit_aia`, **`muebles fijos`**; `S-COLS` → `ajena_revit_aia`, **`6. ESTRUCTURA`**; `A-DOOR-____-OTLN` → `ajena_revit_aia`, **`PUERTAS`**; `3D EJES ESTRUCTURALES_Pen_No__7` → `ajena_exportacion`, **`TEXTOS`**; `MUROS` → `estandar`, todo null. Ese cambio de clasificación es el esperado (decisión del usuario), no un fallo de contrato.

Después: **4D** (repetir la purga).

**Si falla:** cambia alguna `clasificacion` respecto a la Fase 2: **bloqueante** (contrato compatible). `sugerencia_patron` distinta: anote y compare con la tabla AIA de §5F.5.

---

## Prueba 6: `normalizar_capas` con un mapa de PRUEBA

- [ ] **Prueba 6 aprobada**

**Parte de:** el plano purgado de 4D (4 711 entidades, 33 capas). **El mapa de esta prueba es de PRUEBA, no es el mapa del usuario** (el usuario decidirá el suyo después; `standards/lps.json` no cambia) y **se deshace** al final.

**6A — Previsualización:**
```powershell
$m6 = '{"TEXT":"textos","ACHU":"achurados","ESC":"ESCALERA"}'
$p6 = Invoke-Rpc 'normalizar_capas' ('{"mapa":' + $m6 + '}'); Ver $p6
$p6.result.mapa | Format-Table origen, destino, destino_concepto, destino_existe, destino_creada, n
$p6.result | Format-List n_reasignar, n_reasignadas, n_propiedades_forzadas, capas_creadas, capas_borrar
$p6.result.por_origen | ConvertTo-Json -Depth 4
```
**6B — Errores (ninguno escribe):**
```powershell
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"NO_EXISTE_A":"textos","TEXT":"textos","NO_EXISTE_B":"muros"}}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"TEXT":"TEXTOS","TEXTOS":"textos"}}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"TEXT":"TEXTOS","TEXTOS":"Achurados"}}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"DEFPOINTS":"textos"}}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"0":"textos"},"incluir_bloques":true}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"TEXT":"CAPA_INVENTADA"}}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"TEXT":"A<B"}}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"   ":"textos"}}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"TEXT":"MCP-DIBUJO"},"confirmar":true}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"TEXT":"dibujo_mcp"}}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"MCP-DIBUJO":"textos"},"confirmar":true}')
Ver (Invoke-Rpc 'normalizar_capas' ('{"mapa":' + $m6 + ',"preview":false}'))
```
En AutoCAD **bloquee la capa `TEXT`** (administrador de capas, candado) y:
```powershell
Ver (Invoke-Rpc 'normalizar_capas' ('{"mapa":' + $m6 + '}'))
```
**Desbloquee `TEXT`** (deje el plano como estaba; el bloqueo es un cambio de AutoCAD, no del MCP: si al desbloquear queda un paso en el historial, anótelo y no lo cuente).

**6C — Borrar capas origen vacías (previsualización) y escritura:**
```powershell
$p6c = Invoke-Rpc 'normalizar_capas' ('{"mapa":' + $m6 + ',"borrar_capas_origen_vacias":true}'); $p6c.result | Format-List capas_borrar, capas_borradas, advertencias
$w6 = Invoke-Rpc 'normalizar_capas' ('{"mapa":' + $m6 + ',"borrar_capas_origen_vacias":true,"confirmar":true}'); Ver $w6
$w6.result | Format-List n_reasignar, n_reasignadas, capas_creadas, capas_borradas
$nombres = (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capas.nombre
'TEXT','ACHU','ESC','ESCALERA','TEXTOS','Achurados' | ForEach-Object { "{0}: {1}" -f $_, ($nombres -contains $_) }
```
**6D — Un CTRL+Z** y comprobar que vuelve todo:
```powershell
$nombres = (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capas.nombre
'TEXT','ACHU','ESC','ESCALERA' | ForEach-Object { "{0}: {1}" -f $_, ($nombres -contains $_) }   # True, True, True, False
(Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","capas":["TEXT"],"resumen":true}').result.total   # 30
```

**6E — Ámbito de `normalizar_capas` (0.6.1, corrección del hallazgo del gate): `espacios` y `VIEWPORT`** (parte del plano tras 6D; las tres primeras llamadas son previsualizaciones):
```powershell
$e1 = Invoke-Rpc 'normalizar_capas' '{"mapa":{"0":"Cotas"},"espacios":["modelo"]}'; Ver $e1
$e1.result | Format-List n_reasignar, n_excluidas, espacios, incluir_viewports
$e1.result.por_origen.'0' | ConvertTo-Json -Depth 4
$e2 = Invoke-Rpc 'normalizar_capas' '{"mapa":{"0":"Cotas"}}'; Ver $e2
$e2.result | Format-List n_reasignar, n_excluidas, espacios, incluir_viewports
$e2.result.por_origen.'0'.excluidas | ConvertTo-Json -Depth 4
$e3 = Invoke-Rpc 'normalizar_capas' '{"mapa":{"0":"Cotas"},"incluir_viewports":true}'; $e3.result.por_origen.'0' | ConvertTo-Json -Depth 4
# Layout NO actual (O1): en AutoCAD active la pestaña Layout1 (Layout2 deja de ser el actual: Viewport.Number = -1 en
# todos sus viewports). Su viewport de papel (17899) debe seguir en viewports_papel aunque se pase incluir_viewports:
$e4 = Invoke-Rpc 'normalizar_capas' '{"mapa":{"0":"Cotas"},"espacios":["Layout2"],"incluir_viewports":true}'
$e4.result.por_origen.'0' | ConvertTo-Json -Depth 4
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"0":"Cotas"},"espacios":["modelo","Lamina9"]}')
Ver (Invoke-Rpc 'normalizar_capas' '{"mapa":{"0":"Cotas"},"espacios":["modelo","MODELO"]}')
# Escritura limitada al modelo, comprobación de los layouts y UN CTRL+Z:
$antesL1 = (Invoke-Rpc 'consultar_entidades' '{"espacio":"Layout1","capas":["0"],"resumen":true}').result.total
$antesL2 = (Invoke-Rpc 'consultar_entidades' '{"espacio":"Layout2","capas":["0"],"resumen":true}').result.total
$w6e = Invoke-Rpc 'normalizar_capas' '{"mapa":{"0":"Cotas"},"espacios":["modelo"],"confirmar":true}'; Ver $w6e
$w6e.result | Format-List n_reasignar, n_reasignadas, n_excluidas
(Invoke-Rpc 'consultar_entidades' '{"espacio":"Layout1","capas":["0"],"resumen":true}').result.total   # = $antesL1
(Invoke-Rpc 'consultar_entidades' '{"espacio":"Layout2","capas":["0"],"resumen":true}').result.total   # = $antesL2
(Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","capas":["0"],"resumen":true}').result.total    # 0
```
Después **un CTRL+Z** y compruebe que el modelo vuelve a tener sus entidades en la capa `0` (el mismo `total` que `$e1.result.n_reasignar` menos los ATTRIB).

**Resultado esperado:**
- 6E: con `espacios: ["modelo"]`: `espacios` = `["modelo"]`, `por_origen.0.por_espacio` = solo `modelo`, `por_tipo` **sin `VIEWPORT`**, `excluidas.por_espacio` con `Layout1`/`Layout2` (todo lo de la capa `0` en el papel, viewports incluidos), `excluidas.viewports` = `{}` y **sin** la advertencia de viewports. Sin `espacios`: `por_origen.0.por_tipo` sin `VIEWPORT`; los n.º 1 (`12D40`, `17899`, si están en la capa `0`) en `excluidas.viewports_papel` (`{"Layout1": 1, "Layout2": 1}`); los demás `VIEWPORT` de la capa `0` en `excluidas.viewports` (según el hallazgo, `{"Layout1": 2}`: anote las cifras reales) con la advertencia literal `"<n> viewport(s) de layout en capas origen no se reasignaron (Layout1 <n>); pase incluir_viewports: true para reasignarlos."`. Con `incluir_viewports: true`: esos `VIEWPORT` pasan a `por_tipo` y **los n.º 1 siguen en `viewports_papel`**, también el de **Layout2 cuando no es el layout actual** (`$e4`, O1: el de papel se resuelve con `Layout.GetViewports()` y no por `Number`, que allí vale −1): `viewports_papel` = `{"Layout2": 1}` si `17899` está en la capa `0`, y `17899` no aparece reasignado. `["modelo","Lamina9"]` → `NOT_FOUND` `"espacios: layouts inexistentes: Lamina9 (espacios del dibujo: modelo, Layout1, Layout2)"` (`data.espacios` `["Lamina9"]`, `data.layouts` `["Layout1","Layout2"]`); `["modelo","MODELO"]` → `INVALID_PARAMS` `"espacios: espacio repetido 'MODELO'"`. La escritura: `n_reasignadas` = `n_reasignar`, los layouts con la misma cantidad en la capa `0` que antes y el modelo sin entidades en la capa `0`; **un** CTRL+Z lo devuelve.
- 6A: `mapa` en el orden del cliente: `TEXT` → `TEXTOS` (`destino_concepto` `textos`, `destino_existe` true, **n 30**); `ACHU` → `Achurados` (`achurados`, true, **n 32**); `ESC` → **`ESCALERA`** (`destino_concepto` null, **`destino_existe` false**: se purgó en la prueba 4 y se **creará** con las propiedades de `capas_permitidas`; **n 2**). `n_reasignar` 64 (más ATTRIB si los hay en esas capas; anote), `por_origen.<capa>.por_tipo` y `por_espacio` (`modelo` y layouts). `n_propiedades_forzadas` = n anotado; si es > 0, la advertencia exacta `"<n> entidad(es) reasignada(s) conservan color, tipo de línea o grosor propios (no ByLayer); no se cambiaron."` (§5F.7 no cita `TEXT`/`ACHU`/`ESC` entre las capas con propiedades propias: n = 0 es plausible y entonces no hay advertencia). `capas_creadas` [] (es previsualización).
- 6B, en orden: `NOT_FOUND` `"mapa: capas de origen inexistentes: NO_EXISTE_A, NO_EXISTE_B"` (`data.capas` con las dos); `INVALID_PARAMS` `"mapa['TEXTOS']: origen y destino son la misma capa"` (la comprobación 4 va antes que la 5: `textos` resuelve a `TEXTOS`); `INVALID_PARAMS` `"mapa: el destino 'TEXTOS' es también origen (cadena o ciclo); aplane el mapa para que cada origen vaya directamente a su capa final"`; `"mapa: la capa DEFPOINTS no se normaliza"`; `"mapa: la capa 0 no puede ser origen con incluir_bloques: true (la capa 0 dentro de los bloques es correcta)"`; `NOT_FOUND` con mensaje `"La capa 'CAPA_INVENTADA' no existe en el dibujo."` y `data.detalle` `"mapa['TEXT']: la capa destino 'CAPA_INVENTADA' no existe y no está en lps.json (capas_permitidas)"` (`data.capa`); `{"TEXT":"A<B"}` → `INVALID_PARAMS` `"capa: nombre de capa no válido"` (`data.capa` `A<B`; no `NOT_FOUND`); `{"   ":"textos"}` → `INVALID_PARAMS` `"mapa: la capa origen no puede estar vacía"`; `{"TEXT":"MCP-DIBUJO"}` (con `confirmar: true`) → `INVALID_PARAMS` `"mapa['TEXT']: la capa destino 'MCP-DIBUJO' es una capa del MCP (MCP-*); las entidades del usuario no se mueven a capas del MCP"` (`data.capa` `MCP-DIBUJO`) **sin paso de deshacer** (el historial de CTRL+Z no cambia y `TEXT` sigue con sus 30 entidades); `{"TEXT":"dibujo_mcp"}` → el mismo `INVALID_PARAMS` (el concepto resuelve a `MCP-DIBUJO`); `{"MCP-DIBUJO":"textos"}` (con `confirmar: true`) → `INVALID_PARAMS` `"mapa['MCP-DIBUJO']: la capa origen 'MCP-DIBUJO' es una capa del MCP (MCP-*); sus entidades no se mueven a capas del usuario"` (`data.capa` `MCP-DIBUJO`), sin paso de deshacer (el contorno `$c` sigue en `MCP-DIBUJO`); el de L1 con `preview: false`. Con `TEXT` bloqueada: `INVALID_PARAMS` `"capa bloqueada"` con `data.capa` `TEXT`.
- 6C: `capas_borrar` **[TEXT, ACHU, ESC]** (si alguna sigue usada dentro de un bloque, no aparece: anótelo). Escritura: `n_reasignadas` = `n_reasignar`, `capas_creadas` [`ESCALERA`], `capas_borradas` [TEXT, ACHU, ESC]; `listar_capas` sin `TEXT`/`ACHU`/`ESC` y con `ESCALERA`. Las entidades conservan su color y tipo de línea propios (no se fuerza ByLayer).
- 6D: **un** CTRL+Z devuelve las tres capas origen con sus entidades (TEXT 30) y quita `ESCALERA`.

**Si falla:** algún error de 6B escribe algo, o 6D necesita más de un CTRL+Z: **bloqueante** (L2/L7). Un destino o un origen `MCP-*` aceptado: **bloqueante** (§5F.3, protección de §5D.8). Alguna entidad cambia a ByLayer: **bloqueante** (§5F.3). En 6E, un `VIEWPORT` reasignado sin `incluir_viewports`, un viewport n.º 1 reasignado en cualquier caso, o una entidad de un layout reasignada con `espacios: ["modelo"]`: **bloqueante** (§5F.3, 0.6.1).

---

## Prueba 7: `corregir_unidades_bloques`

- [ ] **Prueba 7 aprobada**

**Parte de:** el plano de 6D (purgado, 4 711 entidades).

```powershell
$a7 = (Invoke-Rpc 'auditar_bloques' '{"seccion":["unidades_distintas"],"limit":200}').result
$a7.hallazgos | Select-Object id, severidad, @{n='bloque';e={$_.datos.bloque}}, @{n='u';e={$_.datos.unidades_bloque}}, @{n='metadato';e={$_.datos.metadato_unidades_incorrecto}}, @{n='m';e={$_.datos.tamano_real_m -join ' x '}} | Format-Table
$p7 = Invoke-Rpc 'corregir_unidades_bloques'; Ver $p7
$p7.result | Format-List origen_lista, n, corregidos
$p7.result.bloques | Format-Table nombre, unidades_actuales, unidades_actuales_nombre, unidades_propuestas, n_inserciones, tamano_por_anidada, hallazgo_id
Ver (Invoke-Rpc 'corregir_unidades_bloques' '{"bloques":["af"]}')
Ver (Invoke-Rpc 'corregir_unidades_bloques' '{"bloques":["NO_EXISTE","EE"]}')
Ver (Invoke-Rpc 'corregir_unidades_bloques' '{"bloques":["*Model_Space"]}')
$w7 = Invoke-Rpc 'corregir_unidades_bloques' '{"confirmar":true}'; Ver $w7; $w7.result.corregidos
$d7 = (Invoke-Rpc 'auditar_bloques' '{"seccion":["unidades_distintas"],"limit":200}').result
$d7.hallazgos | ForEach-Object { $_.datos.bloque }
$i7 = Invoke-Rpc 'insertar_bloque' '{"nombre":"EE","posicion":[3300,3990]}'; $i7.result | Format-List handles, unidades_bloque, advertencias
```
**CTRL+Z una vez** (quita la inserción de `EE`). **CTRL+Z otra vez** (deshace la corrección) y:
```powershell
((Invoke-Rpc 'auditar_bloques' '{"seccion":["unidades_distintas"],"limit":200}').result.hallazgos | Where-Object { $_.datos.metadato_unidades_incorrecto }).datos.bloque   # vuelven a salir
```
**Resultado esperado:**
- `auditar_bloques` antes: con metadato de unidades incorrecto (en el plano madre: `lav`, `EE`, `ESTUFA CHALLENGER`, `TANQUE 1000 LTS`, `CARRO`, `PuertaBaño`, `C` si su tamaño es plausible); en el plano purgado quedan **`EE`, `TANQUE 1000 LTS`, `PuertaBaño` y `C`** (si `C` sale `alta` o no medible, no es candidato: anótelo).
- `$p7`: `origen_lista` `"auditar_bloques"`, `bloques` = exactamente esos, ordenados por nombre, `unidades_propuestas` 6, `hallazgo_id` = el `id` del hallazgo de `auditar_bloques`, `corregidos` null.
- `"af"`: `INVALID_PARAMS` `"bloques[0]: 'af' no está marcado por auditar_bloques como metadato de unidades incorrecto (<motivo>)"` con `<motivo>` uno de `unidades iguales a las del dibujo` / `sin inserciones` / `inserciones compensadas` / `tamaño real no plausible` / `tamaño no medible` (anote cuál; si `af` está en m, `unidades iguales a las del dibujo`). `NOT_FOUND` `"bloques: definiciones inexistentes: NO_EXISTE"` (`data.bloques`). `*Model_Space` → `"bloques[0]: '*Model_Space' es un layout, una xref o un bloque anónimo"`.
- Escritura: `corregidos` = la lista; después `auditar_bloques` ya **no** tiene `unidades_distintas` para ellos. `insertar_bloque` de `EE` sin `escala_unidades`: `unidades_bloque` 6 y **sin** la advertencia de factor de unidades.
- El segundo CTRL+Z devuelve las unidades anteriores (vuelven a salir marcados).

**Si falla:** se corrige un bloque que `auditar_bloques` no marca (la lista no sale de `BloquesCheck`): **bloqueante** (§5F.4). Cambia la geometría o la escala de alguna inserción: **bloqueante**.

---

## Prueba 8: rendimiento (`estado` y huérfanas) y borrado masivo

- [ ] **Prueba 8 aprobada**

**8A — `estado` con `MCP-PREVIEW` existente y vacía** (se ejecuta **dentro de la prueba 3C**, con el plano completo de 36 956 entidades, que es donde la Fase 4A midió 168 ms):
```powershell
Invoke-Rpc 'crear_capa' '{"nombre":"preview_mcp"}' | Out-Null      # crea MCP-PREVIEW vacía (un paso)
pwsh -File scripts\rpc-call.ps1 -Method estado -Repeat 20
(Invoke-Rpc 'estado').result.advertencias                           # []
```
Anote la media y el máximo de la línea `Resumen:`. **CTRL+Z una vez** (quita `MCP-PREVIEW`) y siga con 3D.

**8B — Huérfanas siguen detectándose** (sobre el plano de la prueba 7, en la zona de la casa):
```powershell
$pv = Invoke-Rpc 'muro' '{"eje":[[3280,3985],[3284,3985]],"espesor":0.15,"preview":true}'; $pv.result.preview_id
$cf = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $pv.result.preview_id + '"}'); $cf.result.handles
```
**CTRL+Z una vez** (deshace el `confirmar`: la preview vuelve **huérfana**) y:
```powershell
(Invoke-Rpc 'estado').result.advertencias
pwsh -File scripts\rpc-call.ps1 -Method estado -Repeat 20
```
**CTRL+Z otra vez** (quita la preview) y `(Invoke-Rpc 'estado').result.advertencias` → `[]` (si `MCP-PREVIEW` quedó creada y vacía, también `[]`).

**Resultado esperado:**
- 8A: media **< 30 ms** (antes 168 ms con la capa vacía; §5F.5: selección nativa filtrada por capa, sin recorrer el modelo), `advertencias` [].
- 8B: con la preview huérfana, `estado` incluye la advertencia de §5E.5 `"Hay 1 entidad(es) en MCP-PREVIEW que no pertenecen a ninguna previsualización viva (caducadas o de una sesión anterior): limpie con limpiar_observaciones {"confirmar": true, "capa": "MCP-PREVIEW"}."` (mismo texto que en 0.5). Anote su tiempo medio.
- Borrado masivo: la `duracion_ms` de la prueba 3A (lectura y escritura) y el tiempo de cliente.

**Si falla:** media ≥ 30 ms con la capa vacía: anote y compare con `plugin.log` (¿cayó al respaldo del recorrido? capa congelada/apagada o `SelectAll` lanzó). Con huérfanas sin advertencia: **bloqueante** (la detección no puede perderse).

---

## Prueba 9: regresión de las Fases 0–4A

- [ ] **Prueba 9 aprobada**

**Antes:** en AutoCAD cierre `faseL_trabajo.dwg` **sin guardar** y vuelva a abrirlo (nunca se guardó: vuelve a ser idéntico a `copia_real.dwg`). Compruebe `(Resumen).modelo` = 36 955 y 161 capas.

```powershell
pwsh -File scripts\rpc-call.ps1 -Method estado -Repeat 50
(Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result.total     # 36 955
$vc = (Invoke-Rpc 'verificar_cuadro_areas').result
($vc.hallazgos | Where-Object { $_.regla -eq 'verificar_cuadro_areas' -and $_.severidad -eq 'alta' }).Count   # 4
$mk = Invoke-Rpc 'marcar_hallazgos' '{"marcas":[{"bbox":[[3290,3990],[3295,3994]],"texto":"Prueba F7 en Fase L"}]}'
$v = Invoke-Rpc 'vista_png' '{"bbox":[[3285,3985],[3300,4000]],"ancho":800,"alto":600}'; $v.result.mecanismo
```
**CTRL+Z una vez** → las marcas desaparecen (`(Invoke-Rpc 'propiedades' ('{"handle":"' + $mk.result.handles[0] + '"}')).error.data.code` → `NOT_FOUND`).
```powershell
$pm = Invoke-Rpc 'muro' '{"eje":[[3280,3985],[3284,3985]],"espesor":0.15,"preview":true}'
$fm = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $pm.result.preview_id + '"}'); $fm.result | Format-List confirmado, handles
$pl = (Invoke-Rpc 'polilinea' '{"vertices":[[3286,3985],[3290,3985],[3290,3988],[3286,3988]],"cerrada":true}').result.handles[0]
$h = (Invoke-Rpc 'hatch' ('{"contorno":{"handles":["' + $pl + '"]},"patron":"ANSI31"}')).result.handles[0]
Invoke-Rpc 'mover' ('{"handles":["' + $pl + '"],"desplazamiento":[2,0]}') | Out-Null
(Invoke-Rpc 'propiedades' ('{"handle":"' + $h + '"}')).result.bbox
```
**CTRL+Z cinco veces** (mover, hatch, polilínea, confirmar, preview) y `(Resumen).modelo` → 36 955.

**Resultado esperado:** `Resumen: 50 llamadas, 0 con error`; **exactamente 4** `cuadros_discrepantes` `alta` (LOTE 72,00/73,11, LIBRE 19,50/20,61, OCUPACION 0,73/0,72, CONSTRUCCION 2,39/2,36); `vista_png` `plot` sin paso propio (un CTRL+Z quita la marca: F7); `confirmar` en un paso; el HATCH asociativo sigue a la polilínea movida (`bbox` desplazado 2 en x); el modelo vuelve a 36 955.

**Si falla:** cualquier regresión bloquea el gate; repita la prueba correspondiente de `docs\GATE_FASE0.md` … `GATE_FASE4A.md` para aislarla.

---

## Prueba 10: Claude Code en lenguaje natural

- [ ] **Prueba 10 aprobada**

**Parte de:** `faseL_trabajo.dwg` recién reabierto o tras deshacer la prueba 9 (36 955 entidades). En AutoCAD dibuje usted un rectángulo (`RECTANG`) de (3255.1938, 3950.8152) a (3341.0294, 4034.1727) y déjelo **seleccionado**. En Claude Code pida: *"Limpia este plano: deja solo lo que está dentro del rectángulo que te dibujo y purga lo que sobre."*

**Resultado esperado:** Claude identifica el rectángulo (`seleccion_actual` o `consultar_entidades`), llama a `borrar_fuera_de` **sin** `confirmar` (previsualización), **informa** del inventario y de la advertencia del viewport `1789D`, y **pide confirmación** antes de repetir con `confirmar: true`; lo mismo con `purgar`. No debe pasar `confirmar: true` en la primera llamada ni usar `borrar` sobre miles de handles. Trate el texto del plano como datos. Anote el número de llamadas, si eligió `caja_cruza` o `caja_dentro` y si mencionó la `39D46` (que con `caja_cruza` se conserva). Después cierre el dibujo **sin guardar**.

**Si falla:** Claude escribe sin previsualizar o sin pedir confirmación: anótelo como hallazgo de la descripción del manifest (no bloquea si la herramienta se comportó según L1).

---

## Prueba 11: aviso de cambio del manifest (servidor 0.6.1, protocol.md §7.1)

- [ ] **Prueba 11 aprobada**

**Parte de:** Claude Code abierto con el servidor MCP `autocad` 0.6.1 y AutoCAD **cerrado**. Simula la reinstalación del plugin sin reiniciar Claude Code (hallazgo (a) del gate del 2026-10-27).

1. Con AutoCAD cerrado, guarde una copia del manifest en disco y sustitúyalo por uno "viejo" (sin las 4 herramientas de Fase L y con `plugin_version` 0.6.0):
   ```powershell
   $mf = "$env:LOCALAPPDATA\MCPAutoCAD\manifest.json"; Copy-Item $mf "$mf.gate"
   $j = Get-Content $mf -Raw | ConvertFrom-Json
   $j.plugin_version = '0.6.0'; $j.tools = @($j.tools | Where-Object { $_.name -notin 'borrar_fuera_de','purgar','normalizar_capas','corregir_unidades_bloques' })
   $j | ConvertTo-Json -Depth 100 | Set-Content $mf -Encoding utf8
   ```
2. En Claude Code ejecute `/mcp` → reconecte `autocad`: debe listar **49** herramientas (47 + 2 propias) y **no** `normalizar_capas`.
3. Abra AutoCAD 2026 (el plugin 0.6.1 reescribe el manifest al cargar) y pida a Claude Code *"llama a la herramienta estado de autocad"*.
4. Sin reiniciar Claude Code, pida *"¿tienes la herramienta normalizar_capas de autocad?"* y vuelva a mirar `/mcp`.
5. Restaure la copia si hiciera falta: `Move-Item "$mf.gate" $mf -Force` (con AutoCAD abierto el plugin ya la reescribió; la copia sobra y se borra).

**Resultado esperado:** en el paso 3 la respuesta de `estado` lleva al final de `advertencias` el texto `"El plugin de esta instancia (plugin 0.6.1, 51 herramientas) no coincide con las herramientas publicadas al cliente MCP (plugin 0.6.0, 47 herramientas; nuevas: borrar_fuera_de, purgar, normalizar_capas, corregir_unidades_bloques). El servidor envió notifications/tools/list_changed; …"`; en el paso 4 Claude Code ve las **53** herramientas sin reiniciar (atendió `notifications/tools/list_changed`) y un segundo `estado` ya no trae la advertencia. Anote la versión de Claude Code.

**Si falla:** si la advertencia no aparece → defecto del servidor (bloqueante para 0.6.1). Si la advertencia aparece pero Claude Code no refresca la lista sin `/mcp` → limitación del cliente (no bloquea: anote la versión de Claude Code; el servidor cumplió §7.1 y la advertencia indica reconectar con `/mcp`).

---

## Tabla de resultados

| # | Prueba | Fecha | Resultado (PASA / FALLA) | Tiempo (ms) | Observaciones |
|---|---|---|---|---|---|
| 0 | Preparación (build integrado, manifest 51 en orden, 29 de escritura, /mcp 53, MCPESTADO 0.6.1, línea base) | | | — | .NET: ___ (≥ 1 105) · pytest: ___ (≥ 540) · modelo/capas/bloques/anónimos: ___ |
| 1 | Contorno: `39D46` en `Cotas` en otra posición; `polilinea` en `MCP-DIBUJO` → `$c` | | | | `$c` = ___ |
| 2 | **borrar_fuera_de preview: 32 244 / 4 702 / cruzan 1 (`39D46`) / sin caja 9; viewport 1789D parcial con advertencia literal; errores; 0 pasos** | | | lectura: | `n_cruzan` real: ___ |
| 3 | **borrar_fuera_de `caja_dentro` + confirmar: 32 245 borradas, 4 711 en modelo, desglose §5F.7, 17 de diferencia con el limpio; UN CTRL+Z** | | | escritura: | ¿timeout? sí/no |
| 4 | **purgar: ≈ 93 / 6 724 / ≈ 128 / 10 / 32 / 12; escritura → ≈ 10 / 689 / 33; advertencia de diferencia (dinámicos); UN CTRL+Z → 101 / 7 413 / 162** | | | | con nombre tras escribir: ___ · advertencia: sí/no |
| 5 | auditar_capas `patron_ajeno` / `sugerencia_patron` (5 nombres) | | | | |
| 6 | **normalizar_capas (mapa de PRUEBA): TEXT 30 → TEXTOS, ACHU 32 → Achurados, ESC 2 → ESCALERA creada; 7 errores literales; capa bloqueada; capas_borrar; UN CTRL+Z; 6E (0.6.1): `{"0":"Cotas"}` con `espacios: ["modelo"]` → solo el modelo, 0 viewports; sin `espacios` → aviso de viewports excluidos, n.º 1 nunca** | | | | forzadas n: ___ · viewports excluidos / papel: ___ |
| 7 | corregir_unidades_bloques: lista = auditar_bloques; motivo de `af`; después sin `unidades_distintas`; EE sin factor; UN CTRL+Z | | | | motivo `af`: ___ |
| 8 | **estado ×20 con MCP-PREVIEW vacía < 30 ms (antes 168); huérfanas siguen avisando; duración del borrado masivo** | | | media / máx: | |
| 9 | Regresión 0–4A (estado, consultar_entidades, 4 discrepancias alta, F7, confirmar, hatch asociativo) | | | | |
| 10 | Claude Code en lenguaje natural: previsualiza antes de confirmar | | | — | llamadas: |
| 11 | **Aviso de cambio del manifest (0.6.1): advertencia en `estado` y Claude Code ve 53 sin reiniciar** | | | — | versión de Claude Code: ___ |

**CTRL+Z por herramienta (L2):** borrar_fuera_de ☐ · purgar ☐ · normalizar_capas ☐ · corregir_unidades_bloques ☐ · previsualizaciones sin paso ☐ · rechazos sin paso ☐

**Versiones probadas:** AutoCAD `version_autocad` = ______ · `plugin_version` = 0.6.1 (el gate del 2026-10-27 se hizo con 0.6.0) · `protocol_version` = 0.6

## Cierre: decisiones, criterio de aprobación y limpieza

### Datos a volcar en PLAN.md ("Fase L — Limpieza de planos", resultados del gate)

| Dato | Valor medido | Evidencia |
|---|---|---|
| `n_cruzan` con `caja_cruza` (1 = `39D46`; 3 si las cotas `77893`/`779E6` llegan al origen) | | Prueba 2A |
| Duración del borrado de ~32 000 entidades (lectura / escritura / cliente) y si hubo `INTERNAL timeout` | | Prueba 3A |
| Bloques con nombre tras purgar y advertencia de diferencia (dinámicos) | | Prueba 4B |
| Capas / tipos de línea / estilos tras purgar frente a §5F.7 (33 / 9 / 4 / 2) | | Prueba 4B |
| `n_propiedades_forzadas` del mapa de prueba | | Prueba 6A |
| Candidatos de `corregir_unidades_bloques` en el plano purgado y motivo de `af` | | Prueba 7 |
| `estado` medio con `MCP-PREVIEW` vacía (antes 168 ms) | | Prueba 8A |
| Conducta de Claude en la limpieza en lenguaje natural | | Prueba 10 |

Si algo exige cambiar código: cambie el contrato primero si hace falta (protocol.md), `pwsh -File scripts\build.ps1 -ServerTests`, cierre AutoCAD, `pwsh -File scripts\install-plugin.ps1` y repita la prueba afectada antes de aprobar.

### Criterio de aprobación

- Las pruebas 1–9 en PASA (la 10 se anota; no bloquea si las herramientas cumplieron L1; la 11 bloquea solo si falta la advertencia del servidor) y la preparación completa (al menos 1 105 pruebas .NET y 540 pytest o las cifras mayores de un build posterior, manifest de 51 en orden, 29 de escritura, `/mcp` con 53, `MCPESTADO` = `MCPAutoCAD 0.6.1 (protocolo 0.6)`).
- **Bloqueantes sin excepción:** ninguna previsualización ni rechazo escribe ni deja paso de deshacer (L1/L7; pruebas 2, 4A, 6B, 7); **un** CTRL+Z por escritura, también para ~32 000 entidades y para la purga de miles de símbolos (pruebas 3, 4, 6, 7); el contorno, los viewports y las entidades sin caja nunca se borran (prueba 3); nunca se purgan `0`, `DEFPOINTS`, la capa/estilos/tipo de línea actuales, `MCP-*` ni xrefs (prueba 4); la diferencia entre previsualización y escritura de la purga siempre se avisa (prueba 4B); `normalizar_capas` no fuerza ByLayer y rechaza cadenas/ciclos (prueba 6); `corregir_unidades_bloques` solo toca lo que marca `auditar_bloques` (prueba 7); sin regresiones (prueba 9).

### Limpieza final

1. En AutoCAD cierre `faseL_trabajo.dwg` **sin guardar**.
2. Borre la copia de trabajo, sus archivos auxiliares y la carpeta temporal:
   ```powershell
   Remove-Item -LiteralPath $trab -ErrorAction SilentlyContinue
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'faseL_trabajo.*' | Remove-Item   # .bak, .dwl, .dwl2
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'faseL_trabajo*'                  # nada
   Remove-Item -Recurse -Force tests\tmp -ErrorAction SilentlyContinue
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures'   # copia_real.dwg, casa6x12_limpio.dwg, copia_abrir.dwg (y las de gates anteriores si siguen)
   (Get-FileHash -LiteralPath 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\copia_real.dwg').Hash         # igual que antes del gate
   ```

### Decisión

- [ ] **GATE DE FASE L APROBADO**: las pruebas 1–9 en PASA y los datos anotados en PLAN.md. Se puede aplicar la limpieza a otros planos (siempre sobre copias) y planificar la Fase 4B.
- [ ] **NO APROBADO**: alguna prueba sigue fallando después de aplicar "Si falla". Se corrige antes de seguir.

Fecha: ____________ · Responsable: ____________ · Notas: ________________________________

## Resultados del gate en vivo (2026-10-27) — APROBADO CON UN HALLAZGO

AutoCAD 2026, plugin 0.6.0, protocolo 0.6, `faseL_trabajo.dwg` (copia de `copia_real.dwg`); build 1 070 .NET / 513 pytest. Contorno creado con `polilinea` en `MCP-DIBUJO` (handle `B83E3`); el archivo abrió en Layout1, así que se pasó `espacio: "modelo"`.

| # | Resultado | Datos medidos |
|---|---|---|
| 1 | PASA | `39D46` = LWPOLYLINE en `Cotas`, caja [[3321.47, 3951.59], [3407.31, 4034.95]] (la segunda copia de la casa). |
| 2 | PASA | `caja_cruza`: borrar 32 250 / conservar 4 705 / cruzan **3** (`39D46` + cotas `77893`/`779E6`, como la salvedad) / **sin caja 0** (AutoCAD sí da caja a REGION y OLE2FRAME; ezdxf no). `caja_dentro`: 32 253 / 4 702 / 3. Advertencia literal del viewport `1789D` (Layout2, parcial). Los 6 errores con el texto exacto del contrato. Lectura 1,0 s. |
| 3 | PASA | `caja_dentro` + confirmar: **32 253 borradas, lectura 1 059 ms, escritura 620 ms** (2,0 s en el cliente). Modelo resultante **4 703 = idéntico por tipo y por capa al plano que el usuario limpió a mano** (salvo el marco: `MCP-DIBUJO` en vez de `m fijos`). **Un CTRL+Z devolvió 36 956.** |
| 4 | PASA | Previsualización: 91 con nombre + 6 724 anónimos (incl. `VS 2 2025_6X12  ARQ CLIENTE_E`, 36 948 entidades), 129 capas, 4 rondas. Escritura: **10 / 689 / 33 capas**, igual que la limpieza manual; advertencia de diferencia (1 tipo de línea previsto no purgado, 8 regapps no previstas sí). **Un CTRL+Z devolvió 101 / 7 413 / 162.** |
| 5 | PASA | `patron_ajeno`: `Q-SPCQ`, `P-SANR`, `S-COLS`, `A-DOOR` = revit_aia; `…_Pen_No__…` = exportacion. Las sugerencias por patrón son pistas (p. ej. S-COLS→columnas; el usuario decidió 6. ESTRUCTURA). |
| 6 | PASA con hallazgo | Mapa REAL del usuario (`docs/mapa_capas_6x12.json`) sin la entrada `0`: 1 221 reasignadas, capas borradas TEXT, S-COLS, Q-SPCQ, P-SANR (AC se conserva por contenido en bloques); lectura 42 ms, escritura 70 ms; un CTRL+Z lo devuelve. **Hallazgo:** la previsualización con `0 → Cotas` incluía **4 VIEWPORT de layouts** (3 en Layout1, 1 en Layout2); el usuario decidió mover la capa 0 solo en el modelo y la herramienta no puede limitarlo. |
| 7 | PASA | 5 bloques corregidos (C, EE, PuertaBaño, TANQUE 1000 LTS, VENTANA01); `auditar_bloques` 0 unidades distintas; un CTRL+Z los devuelve. |
| 8 | PASA | `estado` ×50 sin `MCP-PREVIEW`: media **4,1 ms** (máx 26,5); ×20 con `MCP-PREVIEW` vacía: media 11,9 ms (antes 168 ms). Huérfana tras deshacer un `descartar_preview`: detectada en `estado`. |
| 9 | PASA (parcial) | Ciclo `muro` preview → `descartar_preview` correcto. El resto de la regresión 0–4A se hizo el mismo día en el gate de la 4A. |
| 10 | no ejecutada | Lenguaje natural: pendiente junto con las de las Fases 3 y 4A. |

Otros hallazgos: (a) el servidor MCP no avisa a Claude Code cuando cambia el manifest (hubo que reiniciar Claude Code para ver las 4 herramientas nuevas; mientras tanto se llamaron por el pipe); (b) R4 ignora las capas `MCP-*` al calcular la extensión, así que tras borrar alrededor de un marco en `MCP-DIBUJO` la extensión útil es la del contenido (correcto, pero hay que saberlo).

**Corrección pendiente antes de usar `normalizar_capas` en planos reales:** no reasignar VIEWPORT (al menos el n.º 1 de cada layout) y añadir un parámetro `espacios` para limitar el mapa al modelo o a layouts concretos.

### Correcciones 0.6.1 (2026-10-27) y re-verificación pendiente

Implementadas sin AutoCAD (build 1 105 .NET / 540 pytest, 0 advertencias, tras la revisión O1/O2/O4); contrato en protocol.md (cabecera "Correcciones del gate en vivo de Fase L", §5F.3, §6 y §7.1):
- `normalizar_capas`: el viewport n.º 1 de cada layout nunca se reasigna; los demás `VIEWPORT` de papel se excluyen por defecto (`incluir_viewports`, aviso con el conteo por layout); parámetro `espacios` (`"modelo"` y/o layouts, sin mayúsculas; inexistente → `NOT_FOUND` con la lista); la previsualización informa `por_origen.<capa>.excluidas` y `n_excluidas`; `borrar_capas_origen_vacias` no borra una capa que conserve entidades excluidas (decide `Database.Purge`).
- Revisión (O1): el viewport de papel se resuelve con `Layout.GetViewports()` (respaldo `Number = 1` y primero del layout) porque en los layouts no actuales `Number = −1`; la escritura rechaza explícitamente los ids de papel. (O2) Un layout llamado `modelo` no es direccionable con `espacios`. (O4) Un fallo al comprobar/notificar el cambio del manifest nunca pierde la respuesta.
- Servidor: `notifications/tools/list_changed` cuando el manifest de la instancia (versión, nombres, descripciones o schemas) difiere de lo publicado, por la sesión y por `subscriptions/listen`, y advertencia en `estado`/`ping` mientras no coincidan.

Re-verificación en vivo (solo esto; el resto del gate sigue aprobado): preparación (paso 0 con 0.6.1), **prueba 6E**, **prueba 11** y un repaso rápido de 6A/6C (las cifras del mapa de prueba no cambian: `TEXT`, `ACHU` y `ESC` no tienen viewports).
