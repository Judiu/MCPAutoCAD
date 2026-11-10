# Gate de la Fase 4B (vanos, encuentros, cotas automáticas y ensamblado CYPE): checklist en vivo

Este documento comprueba en AutoCAD real las cuatro herramientas de la Fase 4B (`PLAN.md` §5, "Fase 4B"; contrato en `protocol.md` §5G): `vano`, `union_muros`, `cotas_automaticas` e `insertar_export`, más los cambios compatibles de `insertar_bloque` (`propiedades_dinamicas`, biblioteca ampliada) y `normalizar_capas` (`conservar_aspecto`). Plugin y servidor **0.7.0**, protocolo **0.7**. Es un gate separado del de la Fase 4A (decisión del usuario, 2026-10-26).

- Quién ejecuta las pruebas: el usuario, en su equipo, con AutoCAD 2026 abierto con interfaz y Claude Code en otra ventana. Ningún agente ejecuta este checklist ni instala el plugin.
- Dibujos: **solo** los de `C:\LPSAPP\MCPAutoCAD\tests\fixtures\`. Copia de trabajo: **`tests\fixtures\fase4b_trabajo.dwg`**, **ya creada por el organizador** como copia de `casa6x12_limpio.dwg` (SHA-256 idéntico). **Nunca se guarda**; la limpieza final la borra. `casa6x12_limpio.dwg` y `copia_real.dwg` son de solo lectura y no se abren. La prueba 1A usa además un dibujo nuevo sin guardar.
- Consola: PowerShell 7 (`pwsh`), con el directorio actual en `C:\LPSAPP\MCPAutoCAD`, y **la misma consola** durante todo el gate (las pruebas reutilizan `Invoke-Rpc`, `VerG`, `Resumen`, `$base` y los handles que van guardando).
- **Lo que no se pudo verificar sin AutoCAD** y este gate fija: que la referencia creada por el API de una definición dinámica conserve `IsDynamicBlock` y admita escribir sus propiedades, los **nombres reales** de las propiedades de ancho, el comportamiento de `Database.Insert` con bloques dinámicos desde la biblioteca, las cotas `RotatedDimension` con `COTA75` y el rehecho de sombreados.
- Los `data.detalle` se comparan **literalmente** con protocol.md §5G y §6. Desde Claude Code los errores llegan como `[INVALID_PARAMS] Parámetros inválidos: <detalle>`, `[INVALID_GEOMETRY] Geometría no construible: <detalle>`, `[NOT_FOUND] …`.
- **El orden importa:** cada prueba indica de qué estado parte.

---

## 0. Preparación (una sola vez)

0. **Paso previo: devolver `SECURELOAD` a 1** (acción del usuario, antes de todo lo demás). En la línea de comandos de AutoCAD escriba `SECURELOAD`, pulse Intro, escriba `1` y pulse Intro. El valor se guarda en el perfil del usuario; ningún agente ni script lo cambia, y `tools\extraer_bloques_6x12.ps1` ya no lo necesita.
1. **Compilar y ejecutar todas las pruebas** (AutoCAD puede estar abierto):
   ```powershell
   cd C:\LPSAPP\MCPAutoCAD
   pwsh -File scripts\build.ps1 -ServerTests
   ```
   Debe terminar con `=== BUILD CORRECTO ===` y `0 Warning(s)`. La línea de `dotnet test` debe indicar **al menos 1 663** pruebas superadas y 0 con error, y la de `pytest` **al menos 952** (cifras del build de cierre de las correcciones de la auditoría, 2026-10-31).
2. **Cerrar todas las instancias de AutoCAD** e **instalar el bundle**:
   ```powershell
   Get-Process acad -ErrorAction SilentlyContinue   # no debe listar nada
   pwsh -File scripts\install-plugin.ps1 -DryRun
   pwsh -File scripts\install-plugin.ps1
   $bundle = "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents"
   (Get-Item "$bundle\MCPAutoCAD.Plugin.dll").VersionInfo.FileVersion   # 0.7.0.0
   Get-ChildItem "$bundle\biblioteca" -Filter *.dwg | Select-Object -ExpandProperty Name
   ```
   La biblioteca debe listar **cinco** archivos: `LPS_ROTULO.dwg`, `Puerta Dinamica LM.dwg`, `PuertaBaño.dwg`, `TANQUE 1000 LTS.dwg`, `VENTANA01.dwg`.
3. **Abrir AutoCAD 2026** (sin abrir ningún plano) y escribir **`MCPESTADO`**: debe mostrar `MCPAutoCAD 0.7.0 (protocolo 0.7)` y el pipe `\\.\pipe\mcp-autocad-<usuario>-<PID>`.
4. **Versión** desde PowerShell:
   ```powershell
   pwsh -File scripts\check-pipe.ps1
   pwsh -File scripts\rpc-call.ps1 -Method ping
   ```
   `ping` debe traer `"protocol_version": "0.7"` y `"plugin_version": "0.7.0"`.
5. **Funciones auxiliares y manifest.** Defina en la consola:
   ```powershell
   function Invoke-Rpc([string]$Method, [string]$Params = '{}') {
       (& .\scripts\rpc-call.ps1 -Method $Method -ParamsJson $Params -AsObject -TimeoutSec 60).Respuesta | ConvertFrom-Json
   }
   function VerG($r) {
       # Respuesta G6 de una generadora, o el error.
       if ($r.error) { "{0} {1} | detalle: {2}" -f $r.error.data.code, $r.error.message, $r.error.data.detalle; return }
       $r.result | Format-List preview, preview_id, handles, handles_borrados, capas, capas_creadas, advertencias
       $r.result.numeros | ConvertTo-Json -Depth 6
   }
   function Err($r) { "{0} | {1}" -f $r.error.data.code, $r.error.data.detalle }
   function Resumen() {
       $m = (Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result
       $cap = (Invoke-Rpc 'listar_capas' '{"limit":1000}').result.total
       [pscustomobject]@{ modelo = $m.total; capas = $cap }
   }
   (Invoke-Rpc 'tools/manifest').result.protocol_version          # 0.7
   (Invoke-Rpc 'tools/manifest').result.tools.name -join ', '
   ((Invoke-Rpc 'tools/manifest').result.tools | Where-Object escritura).Count
   ```
   La segunda línea debe imprimir **exactamente** (55 herramientas, orden de protocol.md §4):
   `ping, estado, listar_capas, linea, consultar_entidades, propiedades, textos, bloques, seleccion_actual, xrefs, vista_png, resumen_archivo, auditar_capas, entidades_en_capa_0, auditar_bloques, areas_por_capa, verificar_cuadro_areas, duplicados, textos_fuera_de_escala, auditar_dibujo, marcar_hallazgos, limpiar_observaciones, crear_capa, polilinea, circulo, arco, texto, mtext, insertar_bloque, cota, hatch, mover, copiar, rotar, escalar, borrar, punto_de, distancia, proyectar, muro, ejes_rejilla, recinto, offset, array_rectangular, array_polar, confirmar, descartar_preview, borrar_fuera_de, purgar, normalizar_capas, corregir_unidades_bloques, vano, union_muros, cotas_automaticas, insertar_export`
   La tercera debe dar **33** (`linea`, `marcar_hallazgos`, `limpiar_observaciones`, las 14 de la Fase 3, las 8 de escritura de la 4A, las 4 de la Fase L y las **4 de la Fase 4B**). Si `plugin.log` (`%LOCALAPPDATA%\MCPAutoCAD\plugin.log`) contiene `Herramienta '<x>' del contrato no tiene handler`, pare: el build no es el correcto.
6. **Claude Code**: reinicie Claude Code en `C:\LPSAPP\MCPAutoCAD` (usa `.mcp.json`). Con `/mcp`, el servidor `autocad` debe estar conectado con **57 herramientas**: las 55 del manifest + `instancias` y `seleccionar_instancia`.
7. **Comprobar la copia de trabajo** (ya creada; nunca se trabaja sobre `casa6x12_limpio.dwg`):
   ```powershell
   $trab = 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\fase4b_trabajo.dwg'
   (Get-FileHash -LiteralPath 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\casa6x12_limpio.dwg').Hash -eq (Get-FileHash -LiteralPath $trab).Hash   # True
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'fase4b_trabajo.dwl*'   # nada
   New-Item -ItemType Directory -Force tests\tmp | Out-Null
   ```
   Si el hash no coincide, **no siga**: pida al organizador una copia nueva. **No abra todavía el plano:** la prueba 1A va primero, en un dibujo nuevo.

---

## Prueba 1: biblioteca de bloques y propiedades dinámicas

- [ ] **Prueba 1 aprobada**

**Por qué dos partes.** `insertar_bloque` busca primero en el dibujo. El 6X12 **ya contiene** `Puerta Dinamica LM`, `PuertaBaño`, `VENTANA01` y `TANQUE 1000 LTS` (con el metadato de unidades mal: 0, 1, 4 y 4); en `fase4b_trabajo.dwg` **no se importaría nada de la biblioteca**. La importación se comprueba por eso en un dibujo nuevo (1A).

### 1A. Importación desde la biblioteca (dibujo nuevo sin guardar)

En AutoCAD: `NUEVO` con una plantilla métrica, `INSUNITS` → `6`. En la consola:
```powershell
(Invoke-Rpc 'estado').result | Select-Object insunits, unidades     # 6, m
$x = 0
foreach ($n in 'LPS_ROTULO','Puerta Dinamica LM','PuertaBaño','VENTANA01','TANQUE 1000 LTS') {
  $p = @{ nombre = $n; posicion = @($x, 0); desde_biblioteca = $true } | ConvertTo-Json -Compress
  $r = Invoke-Rpc 'insertar_bloque' $p
  if ($r.error) { "$n -> $(Err $r)"; continue }
  $r.result | Select-Object @{n='bloque';e={$n}}, importado_de_biblioteca, unidades_bloque, factor_unidades, dinamico, capa,
    @{n='capas_importadas';e={$_.capas_importadas -join ','}}
  $r.result.propiedades_dinamicas | Format-Table nombre, valor, solo_lectura, unidades, descripcion, @{n='admitidos';e={$_.valores_permitidos -join '|'}}
  $x += 5
}
```
**Esperado:** los cinco con `importado_de_biblioteca: true`, `unidades_bloque: 6`, `factor_unidades: 1`. `LPS_ROTULO` con `capas_importadas` **`Rotulo`** o vacío (**pendiente del gate anterior**: anote cuál; depende de si `Database.Insert` trae la capa). `Puerta Dinamica LM` y `VENTANA01` con `dinamico: True` y `propiedades_dinamicas` no vacío; `PuertaBaño`, `TANQUE 1000 LTS` y `LPS_ROTULO` con `dinamico: False` y `[]`. En pantalla las puertas se ven en `PUERTAS`, la ventana en `PUERTAS`/`ventanas`, con los tamaños 0,90 × 0,961 / 0,12 × 0,30.

**ANOTE AQUÍ los nombres reales** (acción del usuario):

| bloque | propiedad de ancho (distancia, escribible) | valor leído | otras propiedades |
|---|---|---|---|
| `Puerta Dinamica LM` (≈ 0,90) | `DistanceGeneralVano` | ≈ 0,90 | `DistanceHojaDoble`, también ≈ 0,90: por eso la deducción es ambigua |
| `VENTANA01` (≈ 0,30; es la longitud a lo largo del vano) | `Distance1` | 0,3045 | origen en −0,0045: con 1,2 el largo real es 1,1955; de ahí `ajuste_ancho` 0,0045 |

Comprobación de escritura (sustituya `<prop>` por el nombre anotado):
```powershell
$p = '{"nombre":"Puerta Dinamica LM","posicion":[0,5],"propiedades_dinamicas":{"<prop>":0.8}}'
$r = Invoke-Rpc 'insertar_bloque' $p; $r.result.propiedades_dinamicas | Format-Table nombre, valor
Err (Invoke-Rpc 'insertar_bloque' '{"nombre":"Puerta Dinamica LM","posicion":[0,6],"propiedades_dinamicas":{"NoExiste":1}}')
Err (Invoke-Rpc 'insertar_bloque' '{"nombre":"PuertaBaño","posicion":[0,7],"propiedades_dinamicas":{"x":1}}')
```
**Esperado:** la propiedad queda en `0.8` y la puerta se ve más estrecha; `INVALID_PARAMS | propiedades_dinamicas: propiedad desconocida 'NoExiste' (propiedades del bloque: …)`; `INVALID_PARAMS | propiedades_dinamicas: el bloque 'PuertaBaño' no es dinámico`. Si hay una propiedad de solo lectura, pásela con cualquier valor: `propiedades_dinamicas.<p>: es de solo lectura`.

### 1A bis. `ancho_por: "pendiente"`: bloque de biblioteca que aún no está en el dibujo

**Hágalo antes de escribir los nombres en `lps.json`:** con `propiedad_ancho` ya configurada este camino no existe. **Cierre el dibujo de 1A sin guardar** y abra otro `NUEVO` con plantilla métrica, `INSUNITS` → `6`.

```powershell
Err (Invoke-Rpc 'bloques' '{"nombre":"Puerta Dinamica LM"}')                       # NOT_FOUND: aún no está
$m0 = (Invoke-Rpc 'muro' '{"eje":[[0,0],[6,0]],"espesor":0.15}').result.handles[0]
$a0 = '{"muro":"' + $m0 + '","posicion":[3,0],"ancho":0.8,"tipo":"puerta","confirmar":true'
$p0 = Invoke-Rpc 'vano' ($a0 + ',"preview":true,"dibujar_preview":false}')
$p0.result.numeros.bloque | Format-List ancho_por, propiedad_ancho, factor_ancho, escala
$p0.result | Format-List entidades, handles_borrados, propiedades_dinamicas, capas_importadas, advertencias
Err (Invoke-Rpc 'bloques' '{"nombre":"Puerta Dinamica LM"}')                       # sigue sin estar
$c0 = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p0.result.preview_id + '"}'); VerG $c0
$c0.result.numeros.bloque | Format-List ancho_por, propiedad_ancho, factor_ancho, escala
$c0.result.propiedades_dinamicas | Format-Table nombre, valor
```
**Esperado:**
- `$p0`: `ancho_por: pendiente`, `propiedad_ancho` y `factor_ancho` vacíos, `escala` ±1, `entidades` y `handles_borrados` vacíos, `propiedades_dinamicas` vacío, sin campo `capas_importadas`, y la advertencia `vano: la definición 'Puerta Dinamica LM' aún no está en el dibujo; su propiedad de ancho se deducirá al crear la referencia en confirmar (ancho_por: pendiente).`. La definición sigue sin estar en el dibujo.
- `$c0`: `ancho_por: propiedad`, `propiedad_ancho` = la anotada en 1A, `factor_ancho` vacío, `escala` ±1, la propiedad vale **0.8**, `capas_importadas` presente y **sin** la advertencia de `pendiente`.

**Resultado real (2026-10-31):** con `propiedad_ancho` null la puerta fue **ambigua** (`DistanceGeneralVano` y `DistanceHojaDoble`) y cayó a escala con la advertencia; con `propiedad_ancho` fijada, por propiedad. `standards\lps.json` lleva ya los dos nombres, así que esta prueba solo se puede repetir dejando `propiedad_ancho` en null en `"$bundle\lps.json"`.

**Si no da `propiedad`:** con `ancho_por: escala` y la advertencia `vano: el bloque 'Puerta Dinamica LM' tiene varias propiedades que pueden ser el ancho (…)`, anote los nombres: no es un fallo, y por eso `propiedad_ancho` debe quedar fijada en `lps.json`. Con la advertencia de `no es dinámico (o no se encontró su propiedad de ancho)` la referencia recién creada no dio sus propiedades o ninguna vale ≈ 0,90: es un **hallazgo**; revise `plugin.log` y anótelo. **Cierre este dibujo sin guardar.**

**Escriba los nombres en el estándar:** `standards\lps.json` → `biblioteca_vanos.<bloque>.propiedad_ancho` y en `"$bundle\lps.json"` (el plugin lee el instalado, una vez, al cargar). Sin esto, `vano` deduce la propiedad por valor en la referencia recién creada; si dos propiedades encajan, no elige: cae a escala con una advertencia que las lista y pide fijar `propiedad_ancho`. Cierre AutoCAD, vuelva a abrirlo y abra `tests\fixtures\fase4b_trabajo.dwg`. Pestaña **Modelo**, `ZOOM` → `E`.

### 1B. Línea base del plano y bloques ya definidos

```powershell
(Invoke-Rpc 'estado').result | Select-Object insunits, unidades, advertencias   # 6, m
$base = Resumen; $base | Format-List
$base | ConvertTo-Json | Set-Content tests\tmp\fase4b_base.json
(Invoke-Rpc 'listar_capas' '{"limit":1000}').result.capas | Where-Object nombre -like 'MCP-*'   # ninguna
```
**Esperado:** `modelo` **4 694**, `capas` **33**, INSUNITS **6**, y estilo de cota actual **`COTA75`** (compruébelo en AutoCAD con `DIMSTYLE`). Anote los números.

**Si falla:** `importado_de_biblioteca: false` en 1A → el dibujo nuevo ya tenía el bloque. `dinamico: False` en la puerta → el hecho que §5G.6 dejaba al gate es falso: pare y avise; `vano` seguirá funcionando por escala con advertencia, pero el criterio de la prueba 2 no se cumple. `NOT_FOUND` de biblioteca → repita el paso 0.2.

---

## Prueba 2: `vano` en un muro nuevo (puerta dinámica 0,80 y 0,90)

- [ ] **Prueba 2 aprobada**

**Parte de:** `fase4b_trabajo.dwg` recién abierto. La zona x 3320–3340, y 4020–4030 está libre (solo pasa el marco `m fijos`) y dentro de la extensión.

```powershell
$m = Invoke-Rpc 'muro' '{"eje":[[3322,4024],[3328,4024]],"espesor":0.15}'; VerG $m
$hm = $m.result.handles[0]
Err (Invoke-Rpc 'vano' ('{"muro":"' + $hm + '","posicion":[3325,4024],"ancho":0.8,"tipo":"puerta"}'))
$v1 = Invoke-Rpc 'vano' ('{"muro":"' + $hm + '","posicion":[3325,4024],"ancho":0.8,"tipo":"puerta","confirmar":true}'); VerG $v1
$v1.result.entidades | Format-Table handle, tipo, capa, n_vertices, cerrada, area, bloque, dinamico
```
**Esperado:**
- El primer `muro` va a `MUROS`. Sin `confirmar`: `INVALID_PARAMS | confirmar: 1 de las 1 entidades no están en capas MCP-* (p. ej. <h> en MUROS); pase confirmar: true para modificarlas`.
- `handles_borrados` = `[<hm>]`. `entidades`: dos `LWPOLYLINE` cerradas de 4 vértices en `MUROS` (área 0,39, longitud 5,5) y un `INSERT` `Puerta Dinamica LM` en `PUERTAS`.
- `numeros.hueco`: `ancho` 0.8, `espesor` 0.15, `direccion` 0, `distancia_desde_inicio` 3, `longitud_tramo` 6, `centro` [3325, 4024].
- `numeros.bloque`: `ancho_por: "propiedad"`, `propiedad_ancho` = la anotada, `factor_ancho: null`, `escala` con ±1 en x e y, `rotacion` 0, `posicion` [3324.6, 4023.925].
- Advertencia `vano: el bloque 'Puerta Dinamica LM' está dibujado para muros de 0.13; el muro mide 0.15; las jambas no cubren todo el espesor.`
- La propiedad de ancho de `propiedades_dinamicas` (**raíz** de la respuesta; la entrada `INSERT` de `entidades` ya no lo lleva) vale **0.8**. En pantalla las jambas caben en el hueco y la hoja abre hacia +y.

Compruebe en AutoCAD con `LIST` que la escala del INSERT es ±1 y `PROPIEDADES` muestra el ancho 0,80.

Segundo vano, 0,90, en el contorno del lado fin:
```powershell
$fin = $v1.result.entidades[1].handle
$v2 = Invoke-Rpc 'vano' ('{"muro":"' + $fin + '","posicion":[3326.7,4024],"ancho":0.9,"tipo":"puerta","cara_apertura":"derecha","bisagra":"fin","confirmar":true}'); VerG $v2
```
**Esperado:** `ancho_por: "propiedad"`, propiedad = **0.9**, `handles_borrados` = `[<fin>]`, escala ±1 (con `espejo` si corresponde), la hoja abre hacia −y con la bisagra en el extremo derecho.

**CTRL+Z una vez** → desaparece el segundo vano y vuelve el contorno del lado fin. **CTRL+Z otra vez** → vuelve el muro original de 6 m (un rectángulo) y desaparece la puerta. **CTRL+Z otra vez** → desaparece el muro; `(Resumen).modelo` = 4 694.

**Si falla:** `ancho_por: "escala"` con la advertencia de bloque no dinámico → la propiedad no se encontró: revise `propiedad_ancho` en `"$bundle\lps.json"`. Más de un CTRL+Z por llamada → hallazgo bloqueante (§5G.7). La puerta queda girada o espejada respecto al hueco → anote `rotacion`, `escala` y una captura: `OpeningPlacement` o el catálogo están mal.

### 2B. Qué ancho recibe la puerta dinámica: hueco o bloque

**Por qué.** El hueco del plano mide 0,80, pero el bloque de la oficina en ese vano (`*U11`, §5G.0) mide ≈ 0,876 de ancho total: su jamba derecha llega a x 0,8757, de modo que la jamba invade la tapa. `vano` fija hoy la propiedad de ancho = ancho del hueco. **Esta prueba no cambia nada:** compara las dos opciones y deja anotada la decisión. El comportamiento de `vano` no se toca hasta que esté anotada.

**Parte de:** el estado base, tras los tres CTRL+Z anteriores (`(Resumen).modelo` = 4 694). Sustituya `<prop>` por el nombre anotado en 1A.

```powershell
# Opción HUECO: lo que hace vano hoy (propiedad = 0,80).
$ma = (Invoke-Rpc 'muro' '{"eje":[[3322,4027],[3328,4027]],"espesor":0.15}').result.handles[0]
$va = Invoke-Rpc 'vano' ('{"muro":"' + $ma + '","posicion":[3325,4027],"ancho":0.8,"tipo":"puerta","confirmar":true}'); VerG $va
# Opción BLOQUE: el mismo hueco de 0,80, y la puerta con la medida de la oficina (propiedad = 0,8757).
$mb = (Invoke-Rpc 'muro' '{"eje":[[3330,4027],[3336,4027]],"espesor":0.15}').result.handles[0]
$vb = Invoke-Rpc 'vano' ('{"muro":"' + $mb + '","posicion":[3333,4027],"ancho":0.8,"tipo":"hueco","confirmar":true}')
$b = $va.result.numeros.bloque
$p = @{ nombre = 'Puerta Dinamica LM'; posicion = @(($b.posicion[0] + 8), $b.posicion[1]); rotacion = $b.rotacion; escala = $b.escala; capa = 'PUERTAS'
        propiedades_dinamicas = @{ '<prop>' = 0.8757 } } | ConvertTo-Json -Compress -Depth 4
$ib = Invoke-Rpc 'insertar_bloque' $p; $ib.result.propiedades_dinamicas | Format-Table nombre, valor
(Invoke-Rpc 'propiedades' '{"handle":"9516C"}').result | Format-List tipo, capa, posicion, rotacion, escala, bbox
```

Compare en pantalla, con el mismo zoom, tres vanos: la opción **hueco** (x ≈ 3325, y ≈ 4027), la opción **bloque** (x ≈ 3333, y ≈ 4027) y la puerta real **`9516C`** de la oficina (x ≈ 3286, y 4004,149–4004,949; use su `bbox`). Mida con `DIST` de jamba exterior a jamba exterior y mire:

- si la jamba queda dentro del hueco, enrasada con la tapa, o la invade como en la oficina (unos 0,076 si todo el exceso va a un lado; aquí el punto base está en la esquina del hueco);
- si la hoja cierra contra la jamba opuesta;
- cuál de las dos se parece a `9516C`.

**ANOTE AQUÍ la decisión** (acción del usuario):

| dato | valor |
|---|---|
| la propiedad debe recibir | ancho de **hueco** (decisión del usuario, 2026-10-31) |
| motivo | en el plano real las jambas de `9516C` y `9518C` caen dentro del hueco de 0,80; la cifra 0,876 venía de geometría oculta |

Si la decisión es ancho de bloque, **no** es un fallo de esta prueba: se vuelca en `PLAN.md` como trabajo pendiente (hoy `vano` no separa el ancho del hueco del de la propiedad). Si `<prop>` no admite 0,8757 (`AutoCAD rechazó el valor` o aviso de `AutoCAD dejó el valor`), anote el valor más cercano que acepte.

**CTRL+Z cinco veces** (inserción, hueco, muro, puerta, muro) → `(Resumen).modelo` = 4 694.

---

## Prueba 3: `vano` en un muro real de la oficina (preview → vista → confirmar)

- [ ] **Prueba 3 aprobada**

**Parte de:** el plano en su estado base. Muro real: rectángulo **`95795`** de `MUROS`, caja [3311.853, 3983.074]–[3313.163, 3983.194], espesor 0,12, longitud 1,31.

```powershell
$a = '{"muro":"95795","posicion":{"distancia":0.655},"ancho":0.8,"tipo":"ventana"}'
Err (Invoke-Rpc 'vano' ($a.TrimEnd('}') + ',"preview":true}'))
$p = Invoke-Rpc 'vano' ($a.TrimEnd('}') + ',"preview":true,"confirmar":true}'); VerG $p
(Invoke-Rpc 'propiedades' '{"handle":"95795"}').result.capa                        # MUROS: sigue ahí
$v = Invoke-Rpc 'vista_png' '{"bbox":[[3311.4,3982.7],[3313.6,3983.6]],"ancho":800,"alto":400}'; $v.result.mecanismo
```
Mire la imagen desde Claude Code (`vista_png` con el mismo `bbox`) o en pantalla.

**Esperado en la previsualización:** sin `confirmar`, el texto de §5D.8 con `95795 en MUROS` (**ya en la previsualización**, sin dibujar nada). Con `confirmar: true`: `preview: true`, `handles_borrados: []`, capa `MCP-PREVIEW`, `numeros.contornos` con dos lados de área 0,0306 y longitud 0,75, `numeros.bloque.nombre` `VENTANA01`, sin aviso de espesor (0,12 = 0,12), `rotacion` 270 o 90. Los dos contornos y la ventana se ven en el color de preview (el contenido del bloque conserva `PUERTAS`/`ventanas`) y **`95795` sigue dibujado debajo**.

```powershell
$c = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p.result.preview_id + '"}'); VerG $c
Err (Invoke-Rpc 'propiedades' '{"handle":"95795"}')                                 # NOT_FOUND
$c.result.entidades | Format-Table handle, tipo, capa, bloque
```
**Esperado:** `confirmado: true`, `handles_borrados` = `["95795"]`, contornos en `MUROS`, `INSERT` en `ventanas`, preview desaparecida, y `ancho_por: "propiedad"` con la propiedad en 0,80 (si `VENTANA01` del dibujo no la expone, `ancho_por: "escala"` con su advertencia: anótelo).

**Muro cambiado entre preview y confirmar** (repita tras deshacer):
**CTRL+Z dos veces** (confirmar y preview): `95795` vuelve. Después:
```powershell
$p2 = Invoke-Rpc 'vano' ($a.TrimEnd('}') + ',"preview":true,"dibujar_preview":false,"confirmar":true}')
```
En AutoCAD mueva `95795` 0,01 con `DESPLAZA`. Entonces:
```powershell
Err (Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p2.result.preview_id + '"}'))
```
**Esperado:** `INVALID_PARAMS | preview_id: la entidad ancla 95795 cambió o ya no existe desde la previsualización; no se escribió nada; vuelva a previsualizar`. **CTRL+Z** para devolver el muro a su sitio.

**Si falla:** el muro desaparece en la previsualización → hallazgo bloqueante. El sombreado de un muro partido queda huérfano → repita con un muro con `HATCH` asociativo y anote `n_hatch_rehechos`.

### 3B. Sombreado con aspecto propio en un muro partido por `vano`

**Parte de:** el estado base (`(Resumen).modelo` = 4 694).

```powershell
$m3 = (Invoke-Rpc 'muro' '{"eje":[[3322,4027],[3328,4027]],"espesor":0.15}').result.handles[0]
$h3 = (Invoke-Rpc 'hatch' ('{"contorno":{"handles":["' + $m3 + '"]},"patron":"ANSI31","escala":0.02}')).result.handles[0]
function Aspecto($h) { (Invoke-Rpc 'propiedades' ('{"handle":"' + $h + '"}')).result | Select-Object handle, capa, color, transparencia, grosor, tipo_linea }
$h3
```
En AutoCAD seleccione ese sombreado y, en `PROPIEDADES`, ponga **color 3**, **transparencia 40**, **grosor 0,30** y **tipo de línea `HIDDEN`** (si no aparece en la lista, cárguelo con `TIPOLIN` o use otro y anótelo).

```powershell
Aspecto $h3                                                                         # color 3, transparencia 40, grosor 30, HIDDEN
$a3 = '{"muro":"' + $m3 + '","posicion":[3325,4027],"ancho":0.8,"tipo":"hueco","confirmar":true'
$p3 = Invoke-Rpc 'vano' ($a3 + ',"preview":true}'); VerG $p3
$p3.result.entidades | Where-Object tipo -eq 'HATCH' | ForEach-Object { Aspecto $_.handle }
$c3 = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p3.result.preview_id + '"}'); VerG $c3
$c3.result.entidades | Where-Object tipo -eq 'HATCH' | ForEach-Object { Aspecto $_.handle }
```
**Esperado:**
- Previsualización: `numeros.n_hatch_rehechos` 1, dos `HATCH` en `MCP-PREVIEW` ByLayer (`color` 256) y `$h3` intacto.
- `confirmar`: `handles_borrados` = el muro y `$h3`; dos `HATCH` nuevos, uno por contorno, en la capa de `$h3`, y **los cuatro valores conservados en los dos**: `color` 3, `transparencia` 40, `grosor` 30, `tipo_linea` `HIDDEN`. Compruébelo también con `LIST` y a la vista: mismo patrón, misma escala y ángulo.

**CTRL+Z una vez** → desaparecen los dos sombreados y los contornos definitivos, y vuelven el muro y el sombreado original (y lo dibujado en `MCP-PREVIEW`, porque la previsualización tiene su propio paso):
```powershell
Aspecto $h3                                                                         # los mismos cuatro valores
```
Siga con CTRL+Z hasta que `(Resumen).modelo` = 4 694.

**Si falla:** uno de los cuatro valores vuelve a ByLayer tras `confirmar` → hallazgo bloqueante: `GeometryDrawer.DrawHatch` no aplicó `HatchLook`. `n_hatch_rehechos` 0 con el sombreado sin rehacer → revise si es asociativo. Más de un CTRL+Z para volver al original tras `confirmar` → hallazgo bloqueante (§5G.7).

---

## Prueba 4: huecos rechazados

- [ ] **Prueba 4 aprobada**

**Parte de:** el plano en su estado base. Cree un muro recto y otro en L (dos llamadas, dos pasos):
```powershell
$h4 = (Invoke-Rpc 'muro' '{"eje":[[3322,4024],[3328,4024]],"espesor":0.15}').result.handles[0]
$hl = (Invoke-Rpc 'muro' '{"eje":[[3330,4024],[3334,4024],[3334,4027]],"espesor":0.15}').result.handles[0]
$n = Resumen
function Vano4($h, $pos, $ancho) { Invoke-Rpc 'vano' ('{"muro":"' + $h + '","posicion":' + $pos + ',"ancho":' + $ancho + ',"tipo":"hueco","confirmar":true}') }
Err (Vano4 $h4 '{"distancia":0.2}' 0.8)
Err (Vano4 $h4 '{"distancia":7}' 0.8)
Err (Vano4 $h4 '[3325,4024]' 6)
Err (Vano4 $hl '[3333.7,4023.93]' 0.8)
Err (Vano4 $hl '{"distancia":1}' 0.8)
Err (Invoke-Rpc 'vano' '{"muro":"95010","posicion":[3280,4004],"ancho":0.8,"tipo":"hueco","confirmar":true}')
(Resumen).modelo -eq $n.modelo    # True: ninguna llamada escribió
```
**Esperado, en este orden, literal:**
1. `INVALID_GEOMETRY | ancho: el hueco (0.8) no cabe en el tramo (longitud 6): se sale del tramo o cruza una esquina`
2. `INVALID_PARAMS | posicion: la distancia 7 se sale del tramo (longitud 6)`
3. `INVALID_GEOMETRY | ancho: debe ser menor que la longitud del tramo`
4. **Sobre la esquina del muro en L:** `INVALID_GEOMETRY | ancho: el hueco (0.8) no cabe en el tramo (longitud 3.925): se sale del tramo o cruza una esquina` (el tramo es el solape de las dos caras: termina en el vértice interior de la esquina; por eso es este texto y no el de "cruza un vértice", que se reserva para vértices ajenos a las dos caras, como una muesca dentro del muro).
5. `INVALID_GEOMETRY | muro: posicion por distancia solo se admite en contornos de 4 vértices`
6. `INVALID_PARAMS | muro: la entidad 95010 no es una polilínea cerrada (LINE)`, salvo que el tipo real difiera (§5G.0: las caras de ese muro son LINE).

Después, **CTRL+Z dos veces** (los dos muros) y ningún rechazo debe haber consumido un paso.

**Si falla:** un rechazo que deja algo dibujado o consume un paso es bloqueante. Un texto distinto: compare con §6 y anótelo.

---

## Prueba 5: `cotas_automaticas`

- [ ] **Prueba 5 aprobada**

**Parte de:** el plano en su estado base.
```powershell
$h5 = (Invoke-Rpc 'muro' '{"eje":[[3322,4024],[3328,4024]],"espesor":0.15}').result.handles[0]
$v5 = Invoke-Rpc 'vano' ('{"muro":"' + $h5 + '","posicion":[3325,4024],"ancho":0.8,"tipo":"hueco","confirmar":true}')
$cs = $v5.result.handles[0..1] | ForEach-Object { '"' + $_ + '"' }
$hs = $cs -join ','
$c1 = Invoke-Rpc 'cotas_automaticas' ('{"handles":[' + $hs + '],"direccion":"x","lado":"derecha","separacion":0.19,"estilo":"COTA75"}'); VerG $c1
$c1.result.entidades | Format-Table handle, tipo, capa, medida, estilo
$c2 = Invoke-Rpc 'cotas_automaticas' ('{"handles":[' + $hs + '],"direccion":"x","separacion":0.35}'); VerG $c2
$c3 = Invoke-Rpc 'cotas_automaticas' ('{"handles":[' + $hs + '],"direccion":"x","separacion":0.5,"cadena":false,"estilo":"COTA75"}'); $c3.result.numeros.medidas
Err (Invoke-Rpc 'cotas_automaticas' ('{"handles":[' + $hs + '],"separacion":0.2,"estilo":"NOEXISTE"}'))
```
**Esperado:**
- `$c1`: capa `Cotas`, 3 DIMENSION, `numeros.medidas` [2.6, 0.8, 2.6], `total` 6, `n_puntos` 8, `n_puntos_fundidos` 4, `lado` `derecha`, `estilo` `COTA75`, `dimscale` 1, `cannoscale` informado, **sin** aviso de estilo. `entidades[].medida` coincide. Línea de cota a y = 4023.925 − 0.19 = 4023.735.
- `$c2`: mismas medidas con la advertencia literal `cotas_automaticas: se usó el estilo de cota actual 'COTA75'; indique estilo para fijarlo (la oficina usa COTA75).`
- `$c3`: `[6]`.
- `NOT_FOUND`: mensaje `El estilo de cota 'NOEXISTE' no existe en el dibujo.`
- En pantalla: mismo aspecto que las cotas de la oficina (texto `simplex` 0,09, flecha `C`).

Sobre una rejilla de ejes:
```powershell
$e = Invoke-Rpc 'ejes_rejilla' '{"origen":[3322,4026],"separaciones_x":[2,2],"separaciones_y":[1.5],"extension":0.5,"radio_burbuja":0.154,"altura_texto":0.175}'
$lineas = ($e.result.entidades | Where-Object tipo -eq 'LINE').handle
$hl = ($lineas | ForEach-Object { '"' + $_ + '"' }) -join ','
$c4 = Invoke-Rpc 'cotas_automaticas' ('{"handles":[' + $hl + '],"direccion":"x","lado":[3324,4030],"separacion":0.19,"estilo":"COTA75"}'); $c4.result.numeros | Format-List medidas, total, lado, n_puntos, n_puntos_fundidos
$b = ($e.result.entidades | Where-Object tipo -eq 'CIRCLE').handle | ForEach-Object { '"' + $_ + '"' }
$c5 = Invoke-Rpc 'cotas_automaticas' ('{"handles":[' + ($b -join ',') + '],"direccion":"y","separacion":0.3,"estilo":"COTA75","preview":true}'); $c5.result.numeros.medidas
Invoke-Rpc 'descartar_preview' ('{"preview_id":"' + $c5.result.preview_id + '"}') | Out-Null
```
**Esperado:** `$c4` con las medidas 2, 2 entre ejes verticales (más lo que aporten las horizontales, que se funden o no según sus extremos: anote `medidas`), `lado` `izquierda`, cotas en `Cotas`. `$c5` con preview en `MCP-PREVIEW`, sin tocar las burbujas (no exige `confirmar`).

**Un CTRL+Z por llamada** sobre `$c4`, ejes, `$c3`, `$c2`, `$c1`, vano y muro → `(Resumen).modelo` = 4 694 (más los pasos de preview que queden).

**Si falla:** medida real distinta de la calculada → compare `entidades[].medida` con `numeros.medidas`. Cotas con otro estilo o escala → revise `DIMSCALE`; la herramienta no la toca.

---

## Prueba 6: `union_muros`

- [ ] **Prueba 6 aprobada**

**Parte de:** el plano en su estado base.

**Aviso sobre el par `95795` + `95821`.** La lectura con ODA + ezdxf del 2026-10-31 con las coordenadas completas muestra que **no** forman una T: `95795` queda a la derecha de x = 3311,8532 y por debajo de y = 3983,1940; `95821`, a la izquierda y por encima: **solo comparten la esquina** (3311,8532; 3983,1940). De los 54 rectángulos de `MUROS`, los pares que se tocan son 4 en L con arista compartida (`958E9`/`958ED`, `958EB`/`958ED`, `95C17`/`95C4E`, `95C47`/`95C4E`) y 2 solo por un punto (`95795`/`95821`, `957BF`/`95885`); **ninguno es una T**. Por eso la T se prueba con muros nuevos y el par citado comprueba el rechazo.

```powershell
Err (Invoke-Rpc 'union_muros' '{"muro_a":"95795","muro_b":"95821","confirmar":true}')
Err (Invoke-Rpc 'union_muros' '{"muro_a":"958E9","muro_b":"958ED"}')
$p = Invoke-Rpc 'union_muros' '{"muro_a":"958E9","muro_b":"958ED","confirmar":true,"preview":true}'; VerG $p
$r = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p.result.preview_id + '"}'); VerG $r
```
**Esperado:**
1. `INVALID_GEOMETRY | union: los muros solo se tocan en un punto; no hay encuentro que unir`. **Si en vivo devuelve `tipo_detectado: "T"` con 8 vértices**, anótelo y deshaga: la lectura anterior estaría equivocada y hay que revisarla.
2. `INVALID_PARAMS | confirmar: 2 de las 2 entidades no están en capas MCP-* (p. ej. 958E9 en MUROS, 958ED en MUROS); pase confirmar: true para modificarlas`.
3. `numeros.tipo_detectado` `L`, `n_vertices` 6, `area_solape` 0, `area_union` = `area_a` + `area_b`, `handles_borrados: []`, los dos muros siguen.
4. `handles_borrados` `["958E9","958ED"]`, un contorno cerrado de 6 vértices en `MUROS`.

**CTRL+Z dos veces** (confirmar y preview): vuelven los dos muros. Compruebe `propiedades` de `958E9` y `958ED`.

T y L con muros nuevos:
```powershell
$a = (Invoke-Rpc 'muro' '{"eje":[[3322,4024],[3328,4024]],"espesor":0.15}').result.handles[0]
$b = (Invoke-Rpc 'muro' '{"eje":[[3325,4024.075],[3325,4027]],"espesor":0.12}').result.handles[0]
$t = Invoke-Rpc 'union_muros' ('{"muro_a":"' + $a + '","muro_b":"' + $b + '","tipo":"L","confirmar":true}'); Err $t
$t = Invoke-Rpc 'union_muros' ('{"muro_a":"' + $a + '","muro_b":"' + $b + '","confirmar":true}'); VerG $t
$c = (Invoke-Rpc 'muro' '{"eje":[[3330,4024],[3334,4024]],"espesor":0.15}').result.handles[0]
$d = (Invoke-Rpc 'muro' '{"eje":[[3333.925,4024],[3333.925,4027]],"espesor":0.15}').result.handles[0]
$l = Invoke-Rpc 'union_muros' ('{"muro_a":"' + $c + '","muro_b":"' + $d + '","confirmar":true}'); VerG $l
```
**Esperado:** primero `INVALID_GEOMETRY | tipo: los muros forman un encuentro en T, no en L`; luego `tipo_detectado` `T`, `n_vertices` **8**, `area_solape` 0; `$l` con `tipo_detectado` `L`, `n_vertices` **6**, `area_solape` > 0. **Un CTRL+Z** tras `$t` devuelve **ambos** muros (repita la unión para seguir, o deshaga el resto).

**Si falla:** unión con vértices sobrantes o tipo inesperado: copie los vértices de `propiedades` y compárelos con `WallJoin`.

---

## Prueba 7: `insertar_export`

- [ ] **Prueba 7 aprobada**

**Parte de:** el plano en su estado base. Export sintético: `tests\fixtures\sintetico_cype.dwg` (INSUNITS 4, 8 entidades; capas `ARM_PILARES`, `ARM_VIGAS`, `ALZ_PILARES`, `CIM_ZAPATAS` y `TEXTO_CYPE`; zapata de 1500 × 1500; caja 2300 × 3000).

```powershell
$ruta = 'C:\\LPSAPP\\MCPAutoCAD\\tests\\fixtures\\sintetico_cype.dwg'
$hash0 = (Get-FileHash 'tests\fixtures\sintetico_cype.dwg').Hash
$r = Invoke-Rpc 'insertar_export' ('{"ruta":"' + $ruta + '","posicion":[3325,4024]}')
$r.result | Format-List capa, unidades_export, factor_unidades, escala_aplicada, escala_unidades, n_entidades_export, n_entidades_excluidas, advertencias
$r.result.entidad | Format-List; $r.result.capas_importadas | Format-List; $r.result.capas_export | Format-List
(Get-FileHash 'tests\fixtures\sintetico_cype.dwg').Hash -eq $hash0   # True
```
**Esperado:** capa `MCP-DIBUJO`, `unidades_export` 4, `factor_unidades` **0.001**, `escala_aplicada` [0.001, 0.001, 0.001], `entidad.bloque` `CYPE_sintetico_cype`, `n_entidades_export` 8, `n_entidades_excluidas` 0, `capas_importadas.cype` con **4** capas (`ALZ_PILARES`, `ARM_PILARES`, `ARM_VIGAS`, `CIM_ZAPATAS`), `otras` = `['TEXTO_CYPE']`, y las advertencias literales `lps.json no define carpetas_export_permitidas: se admite cualquier ruta absoluta .dwg legible; añada la clave para limitar las carpetas.` y `El export trae 1 capa(s) fuera del grupo CYPE: TEXTO_CYPE; pase filtrar_capas: "cype" para dejarlas fuera.` Con `DIST` la zapata mide **1,5 m** en el plano. `BLOQUES`: la definición tiene unidades mm.

**Un CTRL+Z** → desaparecen INSERT, definición y capas. Después, con filtro y con errores:
```powershell
$r = Invoke-Rpc 'insertar_export' ('{"ruta":"' + $ruta + '","posicion":[3325,4024],"filtrar_capas":"cype","nombre_bloque":"CYPE_PRUEBA"}')
$r.result | Format-List n_entidades_export, n_entidades_excluidas, advertencias; $r.result.capas_importadas | Format-List
Err (Invoke-Rpc 'insertar_export' ('{"ruta":"' + $ruta + '","posicion":[3325,4024],"nombre_bloque":"CYPE_PRUEBA"}'))
Err (Invoke-Rpc 'insertar_export' ('{"ruta":"' + $ruta + '","posicion":[3325,4024],"como":"xref"}'))
Err (Invoke-Rpc 'insertar_export' '{"ruta":"tests\\fixtures\\sintetico_cype.dwg","posicion":[3325,4024]}')
Err (Invoke-Rpc 'insertar_export' '{"ruta":"C:\\LPSAPP\\MCPAutoCAD\\tests\\fixtures\\sintetico_cype.dxf","posicion":[3325,4024]}')
Err (Invoke-Rpc 'insertar_export' '{"ruta":"C:\\LPSAPP\\..\\x.dwg","posicion":[3325,4024]}')
Err (Invoke-Rpc 'insertar_export' '{"ruta":"C:\\LPSAPP\\MCPAutoCAD\\tests\\fixtures\\no_existe.dwg","posicion":[3325,4024]}')
```
**Esperado:** `n_entidades_excluidas` 1, `capas_importadas.otras` vacío y sin aviso de capas ajenas; y, en orden: `nombre_bloque: el bloque 'CYPE_PRUEBA' ya existe en el dibujo`, `como: xref no está disponible en 0.7 (solo bloque)`, `ruta: debe ser una ruta absoluta`, `ruta: solo se admiten archivos .dwg`, `ruta: no se admiten '..' ni comodines`, `NOT_FOUND` con mensaje `El archivo '<ruta>' no existe.`. **Un CTRL+Z** quita la inserción con filtro.

**Export real (si lo tiene a mano):** copie un `FILE1.dwg` de CYPE a `tests\tmp\` (**nunca desde `O:\` directamente**) y repita con esa ruta. Anote `unidades_export`, `capas_export` y la medida de un elemento.

**Si falla:** `capas_importadas` vacío con las capas visibles, o el bloque en la escala incorrecta (1 500 m en vez de 1,5 m): anote `BlockTableRecord.Units`. El archivo cambia de hash o queda `.dwl`: **bloqueante** (debe ser solo lectura).

---

## Prueba 8: `normalizar_capas` con `conservar_aspecto`

- [ ] **Prueba 8 aprobada**

**Parte de:** el plano en su estado base. Mapa de **PRUEBA** (no es una decisión de estándar): la capa `TEXT` existe con 30 entidades en modelo (color 250, grosor 0,20; `TEXTOS` tiene otro color y grosor). Si no existiera, use `{"Antepecho":"MUROS"}`.

```powershell
$mapa = '{"mapa":{"TEXT":"textos"}'
$p = Invoke-Rpc 'normalizar_capas' ($mapa + '}')
$p.result | Format-List preview, n_reasignar, n_propiedades_forzadas, n_cambian_aspecto, n_aspecto_fijado, advertencias
$q = Invoke-Rpc 'normalizar_capas' ($mapa + ',"conservar_aspecto":true}')
$q.result | Format-List n_cambian_aspecto, n_aspecto_fijado, advertencias
$w = Invoke-Rpc 'normalizar_capas' ($mapa + ',"conservar_aspecto":true,"confirmar":true}')
$w.result | Format-List escrito, n_reasignadas, n_cambian_aspecto, n_aspecto_fijado, n_propiedades_forzadas, advertencias
```
**Esperado:** `$p`: `n_cambian_aspecto` > 0 (anótelo, ≤ `n_reasignar`), `n_aspecto_fijado: null` y el aviso `<n> entidad(es) cambiarían de aspecto al pasar a su capa destino (color, tipo de línea, grosor o transparencia heredados de la capa origen); pase conservar_aspecto: true para fijarlos en cada entidad antes de moverla.` `$q`: mismo `n_cambian_aspecto`, sin ese aviso. `$w`: `n_aspecto_fijado` = `n_cambian_aspecto`, aviso `conservar_aspecto: se fijaron color, tipo de línea, grosor o transparencia en <n> entidad(es) para conservar su aspecto.`, y `n_propiedades_forzadas` = el de `$p` (no suma lo fijado; nunca supera `n_reasignadas`). En pantalla los textos **no cambian de color** aunque ahora estén en `TEXTOS`.

**Un CTRL+Z** → vuelven a `TEXT` con sus propiedades ByLayer. Sin el parámetro, `normalizar_capas` escribe como en 0.6.1 (repita `$mapa` con `"confirmar":true`: `n_aspecto_fijado` 0, el color cambia al de `TEXTOS`) y **un CTRL+Z**.

**Si falla:** el color cambia con `conservar_aspecto` → revise `LayerAppearance`. El CTRL+Z deja propiedades fijadas → bloqueante.

---

## Prueba 9: un CTRL+Z por llamada (§5G.7)

- [ ] **Prueba 9 aprobada**

Reúna lo observado en las pruebas 2–8 y complete lo que falte (historial con `F2`: `_u GRUPO` por cada escritura del MCP).

| acción | pasos esperados | ☐ |
|---|---|---|
| `vano` definitivo | 1 (devuelve el muro y quita el bloque) | ☐ |
| `union_muros` definitivo | 1 (devuelve ambos) | ☐ |
| `cotas_automaticas` definitivo | 1 | ☐ |
| preview con `dibujar_preview: true` | 1 (muro intacto) | ☐ |
| `confirmar(preview_id)` | 1 (preview + muro + definitivo) | ☐ |
| `insertar_export` | 1 (definición + capas + INSERT) | ☐ |
| `normalizar_capas` + `conservar_aspecto` | 1 | ☐ |
| llamada rechazada | 0 | ☐ |

**Si falla:** cualquier llamada que necesite dos CTRL+Z bloquea el gate.

---

## Prueba 10: regresión de las Fases 0–L

- [ ] **Prueba 10 aprobada**

**Antes:** cierre `fase4b_trabajo.dwg` **sin guardar** y vuelva a abrirlo; `(Resumen).modelo` = 4 694 y 33 capas.

```powershell
pwsh -File scripts\rpc-call.ps1 -Method ping -Repeat 20
(Invoke-Rpc 'estado').result | Select-Object insunits, unidades, advertencias
$l = Invoke-Rpc 'linea' '{"lineas":[{"inicio":[3322,4022],"fin":[3324,4022]}]}'; $l.result.handles
$v = Invoke-Rpc 'vista_png' '{"bbox":[[3320,4020],[3330,4030]],"ancho":800,"alto":600}'; $v.result.mecanismo
```
**Un CTRL+Z** → desaparece la línea, no algo invisible (`vista_png` sin paso de deshacer).
```powershell
$pm = Invoke-Rpc 'muro' '{"eje":[[3322,4024],[3326,4024]],"espesor":0.15,"preview":true}'
$pm.result.handles_borrados.Count                                                      # 0
$fm = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $pm.result.preview_id + '"}'); $fm.result | Format-List confirmado, handles, handles_borrados
$e = Invoke-Rpc 'ejes_rejilla' '{"origen":[3322,4026],"separaciones_x":[2],"separaciones_y":[1.5],"extension":0.5,"radio_burbuja":0.154,"altura_texto":0.175,"preview":true}'
Invoke-Rpc 'descartar_preview' ('{"preview_id":"' + $e.result.preview_id + '"}') | Out-Null
(Invoke-Rpc 'purgar' '{"categorias":["capas"]}').result.preview                         # True, sin escribir
```
**CTRL+Z** las veces necesarias y `(Resumen).modelo` = 4 694.

**Si falla:** cualquier regresión bloquea el gate; repita la prueba correspondiente de `docs\GATE_FASE0.md` … `GATE_FASEL.md`.

---

## Prueba 11: Claude Code en lenguaje natural (pendiente de las Fases 3 y 4A)

- [ ] **Prueba 11 aprobada**

**Parte de:** el plano recién reabierto. Elija el handle de un rectángulo de `MUROS` de al menos 1 m (por ejemplo `95795`) y pida en Claude Code, **sin dar coordenadas**:

*"Abre una puerta de 0,80 en este muro (handle 95795) y acótalo."*

**Esperado:** Claude lee el muro (`propiedades`), usa `vano` con `posicion` por distancia o por `{"handle":…}`, pide confirmación antes de modificar un muro del usuario (`confirmar: true`, idealmente con `preview: true` y `vista_png`), y después `cotas_automaticas` sobre los dos contornos resultantes. Sin inventar coordenadas ni usar el `insertar_bloque` a mano. **CTRL+Z** una vez por escritura.

**Si falla:** Claude no encuentra el camino o escribe sin avisar: anótelo como hallazgo de las descripciones del manifest (no bloquea si cada herramienta cumplió su contrato). Cierre sin guardar.

---

## Regate 0.7.1

Pruebas vivas de las correcciones del gate (`protocol.md`, "Correcciones del gate en vivo de Fase 4B"). Plugin y servidor **0.7.1**, protocolo **0.7**. Trabaje en una copia recién creada, `fase4b_trabajo.dwg`, sin guardar. Repita el paso 0 entero (build, instalación con AutoCAD cerrado, `MCPESTADO` = `MCPAutoCAD 0.7.1 (protocolo 0.7)`) y las funciones `Invoke-Rpc`, `VerG`, `Err` y `Resumen`. `lps.json` cambió: la instalación debe copiarlo. Los handles son los de `casa6x12_limpio.dwg`.

### R1. `vano` en un muro de caras LINE

- [ ] **R1 aprobada**

Es el caso que bloqueó el uso real: `946C5` y `946C6`, LINE en `MUROS`, y 4009,899 de x 3274,43 a 3280,43, y y 4009,749 de x 3274,58 a 3280,28. Puerta de 1,20 entre ejes (columnas en x ≈ 3277,46 y 3280,27).

```powershell
$a = '{"muro":"946C5","posicion":[3278.87,4009.82],"ancho":1.2,"tipo":"puerta"'
Err (Invoke-Rpc 'vano' ($a + ',"preview":true}'))
$p = Invoke-Rpc 'vano' ($a + ',"confirmar":true,"preview":true}'); VerG $p
(Invoke-Rpc 'propiedades' '{"handle":"946C5"}').result.tipo                        # LINE: sigue ahí
$c = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p.result.preview_id + '"}'); VerG $c
$c.result.entidades | Format-Table handle, tipo, capa, longitud, bloque, dinamico
```
**Esperado:**
- Sin `confirmar`: `INVALID_PARAMS | confirmar: 2 de las 2 entidades no están en capas MCP-* (p. ej. 946C5 en MUROS, 946C6 en MUROS); pase confirmar: true para modificarlas`.
- Previsualización: `muro_original.cara_opuesta` = `946C6` (buscada), `hueco.espesor` 0.15, `longitud_tramo` 5.7, `contornos` vacío, cuatro `lineas`, `handles_borrados` vacío. Las dos LINE siguen.
- `confirmar`: `handles_borrados` = `946C5` y `946C6`, seis `LINE` y un `INSERT`, puerta por `DistanceGeneralVano` = 1.2. Con centro en 3278,87, **sin** aviso de columnas. Anote si avisa `sombreado(s) cruzan el hueco`: compara cajas y aquí es informativo. Compruebe en AutoCAD con `LIST` que las cuatro piezas conservan el color y el tipo de línea de su cara, y que las tapas cierran el muro a cada lado.

**Un CTRL+Z** devuelve `946C5` y `946C6` (y la previsualización) **CTRL+Z** otra vez para quitar esa. Después:
```powershell
$q = Invoke-Rpc 'vano' '{"muro":"946C6","cara_opuesta":"946C5","posicion":[3277.46,4009.82],"ancho":1.2,"tipo":"hueco","confirmar":true,"preview":true,"dibujar_preview":false}'
$q.result.advertencias
Err (Invoke-Rpc 'vano' '{"muro":"946C5","posicion":[3280.2,4009.82],"ancho":1.2,"tipo":"hueco","confirmar":true,"preview":true,"dibujar_preview":false}')
```
**Esperado:** `$q` avisa `vano: el hueco cae sobre 1 entidad(es) de la capa 'columnas' (95006): revise la estructura antes de abrirlo.`. El segundo: `INVALID_GEOMETRY | ancho: el hueco (1.2) no cabe en el tramo (longitud 5.7): se sale del tramo o cruza una esquina`.

Busque también una LINE de `MUROS` con dos candidatas o sin ninguna, y compare el rechazo con §5G.1.

### R2. Sombreado de varios bucles

- [ ] **R2 aprobada**

`95793` (capa `S-COLS`, `ANSI32`, 2 bucles, asociativo) usa el muro `95795`. En el gate quedó atravesando el hueco.

```powershell
$a = '{"muro":"95795","posicion":{"distancia":0.655},"ancho":0.8,"tipo":"hueco","confirmar":true'
$p = Invoke-Rpc 'vano' ($a + ',"preview":true}'); VerG $p
(Invoke-Rpc 'propiedades' '{"handle":"95793"}').result | Format-List tipo, capa, patron, n_bucles
$c = Invoke-Rpc 'confirmar' ('{"preview_id":"' + $p.result.preview_id + '"}'); VerG $c
$h = ($c.result.entidades | Where-Object tipo -eq 'HATCH')
$h | Format-Table handle, capa, patron, asociativo, contorno_handles
(Invoke-Rpc 'propiedades' ('{"handle":"' + $h.handle + '"}')).result | Format-List capa, patron, n_bucles, color, grosor, tipo_linea, transparencia
```
**Esperado:** `n_hatch_rehechos` 1 y `handles_borrados` = `95795` y `95793`. El sombreado nuevo es **uno**, en `S-COLS`, `ANSI32`, con la misma escala y ángulo, **3 bucles** (los dos contornos nuevos y el otro), **asociativo** y **no cruza el hueco**. Cualquier otra cosa es un hallazgo. **CTRL+Z** dos veces devuelve el original.

Repita con `union_muros` sobre un par con sombreado, si el plano lo tiene, y con un sombreado de dos caras LINE (cree el `HATCH` con `SOMBREA` eligiendo cuatro LINE cerradas): deben quedar dos bucles, uno a cada lado.

### R3. `VENTANA01`: la caja real iguala al hueco

- [ ] **R3 aprobada**

```powershell
$m = (Invoke-Rpc 'muro' '{"eje":[[3322,4027],[3328,4027]],"espesor":0.12}').result.handles[0]
$v = Invoke-Rpc 'vano' ('{"muro":"' + $m + '","posicion":[3325,4027],"ancho":1.2,"tipo":"ventana","confirmar":true}'); VerG $v
$v.result.propiedades_dinamicas | Format-Table nombre, valor
$v.result.entidades[-1] | Format-List bloque, dinamico, bbox
```
**Esperado:** `Distance1` = **1.2045**, `numeros.bloque.catalogo.ajuste_ancho` 0.0045, y la `bbox` del INSERT mide **1,20** a lo largo del muro, igual que el hueco (x 3324,4 a 3325,6, tolerancia 0,001). Si mide otra cosa, anote la diferencia: `ajuste_ancho` se midió con `Distance1` = 1,2.

Caída a escala: en una copia de `"$bundle\lps.json"` deje `propiedad_ancho` de la puerta en `null` y reinicie AutoCAD; el `vano` en un dibujo nuevo debe avisar de `DistanceGeneralVano, DistanceHojaDoble` y su INSERT traer `dinamico: True`. Devuelva el archivo a su estado.

### R4. `insertar_export` con el export real

- [ ] **R4 aprobada**

`tests\tmp\planos_cype_real.dwg`: copia del export real, 35 569 entidades, INSUNITS 4, lámina en cm a 1:50.

```powershell
$ruta = 'C:\\LPSAPP\\MCPAutoCAD\\tests\\tmp\\planos_cype_real.dwg'
$hash0 = (Get-FileHash 'tests\tmp\planos_cype_real.dwg').Hash
$a = '{"ruta":"' + $ruta + '","posicion":[3322,4020]'
Err (Invoke-Rpc 'insertar_export' ($a + '}'))
$sw = [Diagnostics.Stopwatch]::StartNew()
$r = Invoke-Rpc 'insertar_export' ($a + ',"escala":0.5,"escala_unidades":"ninguna"}'); $sw.Elapsed.TotalSeconds
if ($r.error) { Err $r } else {
  $r.result | Format-List factor_unidades, escala_aplicada, n_entidades_export, advertencias
  $r.result.entidad | Format-List bbox
  $r.result.capas_importadas.otras
}
(Get-FileHash 'tests\tmp\planos_cype_real.dwg').Hash -eq $hash0                     # True
```
**Esperado:**
1. `INVALID_PARAMS | escala_unidades: el export es una lámina de papel en centímetros (A1, A2, A3, A4) a escala 1:50, no un modelo a escala real: su INSUNITS no sirve y el factor automático la dejaría a una escala equivocada. Pase escala: 0.5 y escala_unidades: 'ninguna'`, sin escribir nada.
2. Inserción con `factor_unidades` 1, `escala_aplicada` 0.5, **`entidad.bbox` con valores** y el tamaño de la lámina en metros, y `capas_importadas.otras` **vacío**: ninguna de las 69 capas del export queda como ajena (48 lo eran en 0.7.0).
3. **Anote el tiempo.** El archivo se lee una sola vez, pero la validación mide la extensión de las 35 569 entidades. El límite es 60 s: si responde `superó el tiempo máximo (60 s)`, es un hallazgo, y **no repita**: la escritura no se revierte, puede terminar después y un reintento crearía `CYPE_planos_cype_real_2`. El mensaje dice qué comprobar con `bloques`; un CTRL+Z lo retira.
4. `filtrar_capas: "cype"` deja `n_entidades_excluidas` 0 (solo la capa 0 o `Defpoints` quedan aparte; anote si hay otras).

Con una posición lejísimos del dibujo (`[90000,90000]`) y escala 0,5, R4 rechaza con `fuera de la extensión`. **CTRL+Z** para quitar cada inserción.

### R5. `vista_png` con el export insertado dos veces

- [ ] **R5 aprobada**

Inserte el export dos veces a escala 0,5 y pida la vista que agotó el tiempo:
```powershell
$sw = [Diagnostics.Stopwatch]::StartNew()
$v = (& .\scripts\rpc-call.ps1 -Method vista_png -ParamsJson '{"ancho":1200,"alto":900}' -AsObject -TimeoutSec 80).Respuesta | ConvertFrom-Json
$sw.Elapsed.TotalSeconds; $v.result.mecanismo; $v.error.message
```
**Esperado:** responde en menos de 60 s. Si no, `INTERNAL` con `La operación superó el tiempo máximo (60 s) en el hilo principal de AutoCAD; puede completarse más tarde. Pida una vista de una zona más pequeña (bbox) o con menos píxeles (ancho y alto).`, sin colgar el servidor. Desde Claude Code la espera es de 75 s. **Anote el tiempo.** En ese caso AutoCAD sigue ocupado hasta terminar: espere antes de pedir otra cosa.

### Resultado del regate

| # | Prueba | Fecha | Resultado (PASA / FALLA) | Observaciones |
|---|---|---|---|---|
| R1 | `vano` en caras LINE `946C5`/`946C6` | | | |
| R2 | HATCH `95793`: 3 bucles, asociativo | | | |
| R3 | `VENTANA01` con `ajuste_ancho`; `dinamico` al caer a escala | | | caja: ___ |
| R4 | Export real: lámina rechazada, 0,5, `bbox`, capas | | | tiempo: ___ s |
| R5 | `vista_png` con dos inserciones | | | tiempo: ___ s |

---

## Tabla de resultados

Gate en vivo del 2026-10-31 con el plugin 0.7.0: pruebas 0 a 10 pasadas, con los hallazgos anotados en cada fila; se corrigieron en 0.7.1. **Regate 0.7.1 del 2026-10-31: APROBADO.** Fase 4B aprobada en vivo.

Resultados del regate, sobre `fase4b_trabajo.dwg` recién copiado de `casa6x12_limpio.dwg`:

- **R1, caras LINE (`946C5`/`946C6`):** puerta de 1,20 centrada entre los ejes B y C (x = 3278,8521), pedida en lenguaje natural con el muro seleccionado. Cara opuesta detectada sola, cuatro piezas, dos tapas de 0,15, hueco exacto en las dos caras, `ancho_por: propiedad` con `DistanceGeneralVano` = 1,2, sin aviso de columnas ni de sombreados. Con centro 3277,46 avisa de `95006`.
- **R2, HATCH de varios bucles (`95793`):** un HATCH asociativo con 3 bucles (`BD52B`, `BD52C`, `95794`), área 0,372 = 0,468 − 0,096 del hueco; `handles_borrados` `95795` y `95793`.
- **R3, `VENTANA01`:** `Distance1` = 0,8045, largo real 0,8000, igual al hueco.
- **R4, export real** (`CASA CLIENTE_F\PLANOS\planos cype.DWG`, copia): lámina rechazada con el texto exacto y escala sugerida 0,5; con `escala` 0,5 y `escala_unidades: ninguna`, `factor_unidades` 1, `bbox` 232,8 × 147 m, 69 capas en `cype`, `otras` vacío, 0 excluidas con `filtrar_capas: cype`. No se midió el tiempo con cronómetro; la llamada no superó el límite. Sin comprobar: que la escala 0,5 coincida con el uso de la oficina al llevar esas láminas a un plano.
- **R5, `vista_png`** con el export insertado: devolvió las 36 láminas sin superar el límite.
- **Deshacer:** tres CTRL+Z (export, ventana, puerta) devolvieron `946C5`, `946C6`, `95795` y `95793` con sus propiedades. `union_muros` y `cotas_automaticas`, un CTRL+Z cada una, con las huellas devueltas y el modelo en 4 694.
- **Sin ejercitar en vivo:** el aviso de sombreados no asociativos sobre el hueco, el reparto de bucles de HATCH entre caras LINE y `cara_opuesta` explícita.

| # | Prueba | Fecha | Resultado (PASA / FALLA) | Observaciones |
|---|---|---|---|---|
| 0 | Preparación (build, manifest 55 en orden, 33 de escritura, /mcp 57, MCPESTADO 0.7.0, 5 `.dwg` en la biblioteca) | 2026-10-31 | PASA | .NET: 1 663 · pytest: 952 (build final 0.7.1) |
| 1 | **Biblioteca (5 bloques) y propiedades dinámicas; `ancho_por: "pendiente"` hasta `confirmar` (1A bis); nombres reales anotados y en `lps.json`; línea base 4 694 / 33 / 6 / COTA75** | 2026-10-31 | PASA | puerta: `DistanceGeneralVano` · ventana: `Distance1` · 1A bis: ambigua con `DistanceHojaDoble`, cae a escala; con `propiedad_ancho`, por propiedad. Hallazgo: `dinamico: false` al caer a escala |
| 2 | **`vano` en muro nuevo: 0,80 y 0,90 por propiedad, escala ±1, dos contornos, un CTRL+Z devuelve el muro; 2B: ancho de la puerta dinámica comparado** | 2026-10-31 | PASA | decisión 2B: **hueco**, del usuario |
| 3 | **`vano` en `95795`: preview sin tocar el muro, vista, confirmar; ancla cambiada; 3B: HATCH rehecho conserva color, transparencia, grosor y tipo de línea, un CTRL+Z devuelve el original** | 2026-10-31 | PASA | hallazgos: `95793` de 2 bucles quedó cruzando el hueco (R2); `VENTANA01` con `Distance1` = 1,2 mide 1,1955 (R3) |
| 4 | Huecos rechazados: textos exactos, 0 pasos | 2026-10-31 | PASA |  |
| 5 | `cotas_automaticas`: [2,6; 0,8; 2,6] con COTA75, advertencia sin estilo, ejes y burbujas | 2026-10-31 | PASA |  |
| 6 | **`union_muros`: `95795`/`95821` solo un punto; L real `958E9`/`958ED`; T de 8 y L de 6 con muros nuevos; un CTRL+Z devuelve ambos** | 2026-10-31 | PASA |  |
| 7 | **`insertar_export`: factor 0,001, zapata 1,5 m, 4 + `TEXTO_CYPE`, filtro, errores; origen sin cambios** | 2026-10-31 | PASA | hallazgo con el export real: lámina en cm a 1:50, 500 veces pequeña sin aviso; `bbox` null; 48 de sus 69 capas como ajenas; `factor_unidades` con `ninguna` (R4) |
| 8 | `normalizar_capas` `conservar_aspecto`: cuenta, fija, avisa, CTRL+Z | 2026-10-31 | PASA |  |
| 9 | Un CTRL+Z por llamada (tabla §5G.7) | 2026-10-31 | PASA |  |
| 10 | Regresión 0–L | 2026-10-31 | PASA | hallazgo de uso real: `vano` rechazaba caras LINE (R1); `vista_png` superó 10 s con el export insertado dos veces (R5) |
| 11 | Lenguaje natural sin coordenadas | 2026-10-31 | PASA | en 0.7.0 falló sin improvisar (caras LINE); en 0.7.1, `seleccion_actual`, ejes y columnas, `vano` con `preview`, `vista_png`, `confirmar`. Sin `cotas_automaticas`: el usuario no pidió cotas |

**Versiones probadas:** AutoCAD `version_autocad` = 25.1.0.0 · `plugin_version` = 0.7.0 y 0.7.1 · `protocol_version` = 0.7

## Cierre: criterio de aprobación y limpieza

### Criterio de aprobación

El gate se aprueba si pasan las pruebas 0–10. La 11 no bloquea. Vuelque en `PLAN.md` ("Fase 4B"): resultado, nombres reales de las propiedades de ancho, la decisión de la prueba 2B (ancho de hueco o ancho de bloque) y hallazgos. Los `.dwg` de `standards/` y `lps.json` solo cambian con `propiedad_ancho`.

### Limpieza final

1. En AutoCAD cierre `fase4b_trabajo.dwg` **sin guardar** y el dibujo nuevo de la prueba 1A si sigue abierto.
2. Borre la copia de trabajo, sus auxiliares y la carpeta temporal:
   ```powershell
   Remove-Item -LiteralPath $trab -ErrorAction SilentlyContinue
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'fase4b_trabajo.*' | Remove-Item   # .bak, .dwl, .dwl2
   Get-ChildItem 'C:\LPSAPP\MCPAutoCAD\tests\fixtures' -Filter 'fase4b_trabajo*'                  # nada
   Remove-Item -Recurse -Force tests\tmp -ErrorAction SilentlyContinue
   Remove-Item -LiteralPath 'C:\LPSAPP\MCPAutoCAD\tests\fixtures\plot.log' -ErrorAction SilentlyContinue
   ```
   `vista_png` traza el dibujo: con el registro automático de trazado activo, AutoCAD deja `plot.log` junto al dibujo, aquí en la carpeta de fixtures (visto al auditar la Fase 5, 2026-10-31).
