# Gate de la Fase 0: checklist manual

Este documento comprueba las 8 condiciones de aceptación de `PLAN.md` §5 (Fase 0). El contrato está en `protocol.md` (v0.1). **Si una sola prueba falla y no se arregla con lo que indica "Si falla", el gate no se aprueba y se replantea la arquitectura antes de empezar la Fase 1.**

- Quien ejecuta las pruebas: el usuario, en su equipo y con AutoCAD 2026 abierto con interfaz.
- Dibujos: use **solo dibujos de prueba nuevos** (`QNEW`, guardados por ejemplo en `C:\LPSAPP\MCPAutoCAD\tests\fixtures\`). No use planos de expedientes y nunca abra nada de la unidad `O:\`: la prueba 6 escribe en el dibujo.
- Consola: PowerShell 7 (`pwsh`), con el directorio actual en `C:\LPSAPP\MCPAutoCAD`.

---

## 0. Preparación (una sola vez)

1. **Compilar** (AutoCAD puede estar abierto):
   ```powershell
   cd C:\LPSAPP\MCPAutoCAD
   pwsh -File scripts\build.ps1                 # Release + pruebas de GeometryCore
   pwsh -File scripts\build.ps1 -ServerTests    # opcional: también pytest del servidor
   ```
   Debe terminar con `=== BUILD CORRECTO ===`.
2. **Instalar el bundle**. Cierre antes **todas** las instancias de AutoCAD:
   ```powershell
   pwsh -File scripts\install-plugin.ps1 -DryRun   # revisar lo que hará
   pwsh -File scripts\install-plugin.ps1
   ```
   Destino: `%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\` (`PackageContents.xml` y `Contents\`).
3. **Abrir AutoCAD 2026** y crear un dibujo nuevo. Si aparece un aviso de SECURELOAD sobre `MCPAutoCAD.Plugin.dll`, vea `docs\INSTALACION.md` (sección Solución de problemas).
4. En la línea de comandos de AutoCAD escriba **`MCPESTADO`**. Debe mostrar el nombre del pipe (`\\.\pipe\mcp-autocad-<usuario>-<PID>`), las conexiones activas y las peticiones atendidas. Si responde "Comando desconocido", el plugin no cargó (vea `plugin.log` en `%LOCALAPPDATA%\MCPAutoCAD\`).
5. Compruebe el pipe desde PowerShell:
   ```powershell
   pwsh -File scripts\check-pipe.ps1
   pwsh -File scripts\rpc-call.ps1 -Method ping
   ```
6. **Configurar Claude Code**: copie `.mcp.json.example` a `.mcp.json` en la raíz del proyecto en el que vaya a usar Claude Code (por ejemplo `C:\LPSAPP\MCPAutoCAD\.mcp.json`) y reinicie Claude Code. Con `/mcp` debe aparecer el servidor `autocad` conectado y estas herramientas: `ping`, `estado`, `listar_capas`, `linea`, `instancias`, `seleccionar_instancia`.
7. En el dibujo de prueba fije las unidades en metros (`INSUNITS` = 6), que es la unidad de la oficina, para que `advertencias` salga vacío.

### Herramienta auxiliar: `scripts\rpc-call.ps1`

Habla directamente con el pipe del plugin con el framing de `protocol.md` §1.2, **sin pasar por el servidor Python**. Así, si una prueba falla, se sabe si el fallo está en el plugin o en el servidor.

```powershell
pwsh -File scripts\rpc-call.ps1 -Method <método> [-ParamsJson '{...}'] [-Pid <n>] [-TimeoutSec 15] [-Repeat N] [-AsObject] [-Raw]
```

- Imprime la respuesta JSON y la línea `Ida y vuelta: <ms> ms`.
- `-Repeat N` hace N llamadas seguidas por la misma conexión y muestra `Resumen: N llamadas, E con error. ms mín / media / máx`.
- Si hay varias instancias y no se pasa `-Pid` (ni está definida `MCP_AUTOCAD_PID`), sale con error y lista los PID.
- Código de salida: `0` = todas las respuestas traen `result`; `2` = alguna trae `error` JSON-RPC (por ejemplo `BUSY`); `1` = fallo local o de transporte (`NO_INSTANCE`, `PIPE_TIMEOUT`, `PIPE_BROKEN`).

Para lanzar una llamada mientras AutoCAD está ocupado (pruebas 2 y 3), use una ventana de PowerShell **aparte** de AutoCAD. Si necesita las manos libres, programe la llamada con retardo:

```powershell
Start-Sleep -Seconds 5; pwsh -File scripts\rpc-call.ps1 -Method estado
```

---

## Prueba 1: 100 llamadas seguidas sin congelar AutoCAD

- [ ] **Prueba 1 aprobada**

**Preparación:** AutoCAD abierto, un dibujo de prueba activo y ningún comando en curso. Anote en `MCPESTADO` el número de peticiones atendidas.

**Acción (plugin directo):**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method estado -Repeat 100
pwsh -File scripts\rpc-call.ps1 -Method listar_capas -ParamsJson '{"limit":50}' -Repeat 100
```
Bucle equivalente que abre una conexión por llamada (el otro modo válido según §1.1) y mide el máximo:
```powershell
$r = 1..100 | ForEach-Object { & .\scripts\rpc-call.ps1 -Method estado -AsObject }
$r | Measure-Object -Property Ms -Minimum -Average -Maximum | Format-List Count, Minimum, Average, Maximum
"Errores: " + @($r | Where-Object { -not $_.Ok }).Count
```
**Acción (a través de Claude Code):** pida: *"Llama a la herramienta `estado` de autocad 100 veces seguidas y dime si alguna falló."*

**Resultado esperado:**
- `Resumen: 100 llamadas, 0 con error.` en ambos comandos, y `Errores: 0` en el bucle.
- Máximo **< 2000 ms**. Si una llamada tarda 2 s o más, el plugin la habría devuelto como `BUSY` y eso cuenta como fallo.
- Durante y después de las llamadas AutoCAD sigue respondiendo: con el ratón se puede hacer zoom y dibujar una línea a mano. AutoCAD no se cierra y no muestra un error fatal.
- `MCPESTADO` muestra que las peticiones atendidas aumentaron en ≥ 200 (300 si también hizo el bucle).

**Si falla:**
- AutoCAD se congela o se cierra: alguna llamada toca la API desde el hilo del pipe (PLAN.md §4.1). Revise `plugin.log` y el Visor de eventos de Windows (Aplicación, `acad.exe`). Esto **invalida el gate**: hay que corregir el marshaling al hilo principal.
- Hay `BUSY` intermitentes sin que el usuario haga nada: la cola al hilo principal no se vacía. Revise el mecanismo de `Post` en lugar de subir el timeout.
- `PIPE_TIMEOUT` o `PIPE_BROKEN`: revise si el plugin cierra la conexión después de cada respuesta (debe mantenerla, §1.1).

---

## Prueba 2: comando activo (LINE) → `BUSY` con motivo "comando" en menos de 2 s

- [ ] **Prueba 2 aprobada**

**Preparación:** escriba `LINE` en AutoCAD y haga clic en un primer punto. Deje el comando esperando el siguiente punto, sin pulsar Esc.

**Acción:**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method estado
pwsh -File scripts\rpc-call.ps1 -Method linea -ParamsJson '{"inicio":[0,0],"fin":[1,0]}'
pwsh -File scripts\rpc-call.ps1 -Method ping
```
En Claude Code: *"Dame el estado de AutoCAD."*

**Resultado esperado:**
- `estado` y `linea` devuelven exactamente:
  ```json
  {"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"Hay un comando activo en AutoCAD: LINE. Reintente cuando termine.","data":{"code":"BUSY","motivo":"comando","comando":"LINE"}}}
  ```
  con `Ida y vuelta` **< 2000 ms** y código de salida 2.
- `ping` responde con `"pong": true` (se atiende en el hilo del pipe, §3.1).
- Claude Code muestra `[BUSY] Hay un comando activo en AutoCAD: LINE. Reintente cuando termine.`
- El comando LINE sigue activo y sin cambios, y **no** se creó ninguna línea de (0,0) a (1,0). Al pulsar Esc y repetir `estado`, responde normalmente.

**Si falla:**
- La respuesta tarda unos 2 s y trae `motivo: "cola"`: el hilo principal no tomó la petición. Revise el timeout de cola de 2 s y que el `Post` llegue mientras AutoCAD espera un punto.
- `estado` devuelve `result` en lugar de `BUSY`: falta la comprobación de `CMDACTIVE` o de `CommandWillStart` antes de ejecutar (§3.3).
- `comando` sale vacío (`""`): está permitido por §3.3, pero anótelo; lo esperado es `"LINE"`.
- Se creó la línea: **fallo grave** (se escribió con un comando activo). El gate no se aprueba.

---

## Prueba 3: diálogo modal (OPTIONS) → `BUSY` con motivo "cola" en menos de 2 s

- [ ] **Prueba 3 aprobada**

**Preparación:** escriba `OPTIONS` en AutoCAD y deje abierto el cuadro de diálogo Opciones.

**Acción:**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method estado
pwsh -File scripts\rpc-call.ps1 -Method ping
```
Con el diálogo todavía abierto, en Claude Code: *"Lista las capas del dibujo."*

**Resultado esperado:**
- `estado` devuelve `BUSY` con `Ida y vuelta` **< 2000 ms** (valor normal ≈ 1500–1600 ms, con el timeout de cola del plugin en 1500 ms: constante `QueueTakeTimeoutMs`). La respuesta:
  ```json
  {"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"AutoCAD está ocupado (posible diálogo abierto). Reintente cuando el usuario termine.","data":{"code":"BUSY","motivo":"cola","comando":""}}}
  ```
  (`data.comando` puede valer `""` o no aparecer.)
  Se acepta también `motivo: "comando"` con `comando: "OPTIONS"`: ocurre si el bucle modal del diálogo deja pasar la petición al hilo principal y el plugin detecta `CMDACTIVE` ≠ 0. En ese caso la respuesta llega en < 2000 ms. Anote cuál de las dos salió.
- `ping` responde en < 100 ms con `"pong": true`.
- Claude Code muestra `[BUSY] AutoCAD está ocupado (posible diálogo abierto)...` (o la variante "comando").
- Al cerrar el diálogo, AutoCAD funciona normal y la siguiente llamada a `estado` devuelve `result`. Las peticiones que expiraron **no** se ejecutan después de cerrar el diálogo.

**Si falla:**
- No hay respuesta en 15 s (`PIPE_TIMEOUT`): el timeout de cola no está implementado o espera de forma bloqueante. Revise el timeout de cola (§3.2).
- Tarda 2000 ms o más: compruebe que `QueueTakeTimeoutMs` del plugin vale 1500 (si vale 2000, la prueba no puede pasar) y reconstruya; si ya vale 1500, el temporizador empieza tarde (por ejemplo al leer y no al encolar) o se encadena con la validación.
- Devuelve `result` con el diálogo abierto: el plugin tocó la base de datos durante un modal. Es un riesgo de estabilidad; repita la prueba con `listar_capas` y `linea` (en un dibujo de prueba). Si `linea` escribe con el diálogo abierto, el gate no se aprueba.
- `ping` no responde: `ping` se está encolando al hilo principal en vez de atenderse en el hilo del pipe (§3.1).

---

## Prueba 4: AutoCAD cerrado → texto exacto

- [ ] **Prueba 4 aprobada**

**Preparación:** cierre **todas** las instancias de AutoCAD. Claude Code sigue abierto con el servidor `autocad` conectado.

**Acción:**
```powershell
pwsh -File scripts\check-pipe.ps1
pwsh -File scripts\rpc-call.ps1 -Method ping
```
En Claude Code: *"Dame el estado de AutoCAD"* y luego *"Lista las instancias de AutoCAD."*

**Resultado esperado:**
- `check-pipe.ps1` imprime `No hay instancias: AutoCAD no está abierto o el plugin no cargó.`
- `rpc-call.ps1` imprime `ERROR: [NO_INSTANCE] AutoCAD no está abierto o el plugin no cargó.` y sale con código 1.
- En Claude Code, `estado` devuelve un error de herramienta (`isError: true`) cuyo texto es exactamente **`AutoCAD no está abierto o el plugin no cargó.`**, sin trazas de Python.
- `instancias` devuelve `{"instancias": [], ...}` sin error (§7).
- El servidor Python no se cae: al volver a abrir AutoCAD (y esperar a que cargue el plugin), `estado` vuelve a funcionar **sin reiniciar Claude Code**.

**Si falla:**
- Aparece una traza de Python o un texto distinto: el servidor no traduce `ERROR_FILE_NOT_FOUND` (2) a `NO_INSTANCE` (§1.1, §6.1). Se corrige en `server/`.
- Al reabrir AutoCAD hay que reiniciar Claude Code para que funcione: el servidor guarda en caché el pipe o el manifest. Debe volver a descubrir la instancia en cada llamada que falle.

---

## Prueba 5: cambio de documento activo entre llamadas

- [ ] **Prueba 5 aprobada**

**Preparación:** en una sola instancia de AutoCAD, cree y guarde dos dibujos de prueba, `GATE_A.dwg` y `GATE_B.dwg`, y déjelos abiertos. Active `GATE_A`.

**Acción:**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method estado      # con GATE_A activo
# Cambie a GATE_B con la pestaña de archivo de AutoCAD (o Ctrl+Tab)
pwsh -File scripts\rpc-call.ps1 -Method estado      # con GATE_B activo
pwsh -File scripts\rpc-call.ps1 -Method linea -ParamsJson '{"inicio":[0,0],"fin":[2,0]}'
# Vuelva a GATE_A
pwsh -File scripts\rpc-call.ps1 -Method estado
```
En Claude Code, intercale el cambio de pestaña con *"¿Qué documento está activo en AutoCAD?"*

**Resultado esperado:**
- `result.documento.nombre` es `"GATE_A.dwg"`, después `"GATE_B.dwg"` y después `"GATE_A.dwg"`. `documento.ruta` es la ruta completa de cada archivo y `documentos_abiertos` = 2.
- La línea de la llamada `linea` aparece **en GATE_B** y no en GATE_A (compruébelo visualmente en cada pestaña).
- Con todos los dibujos cerrados (pantalla de inicio), `estado` devuelve `result` con `"documento": null`, `insunits: null`, y `listar_capas` devuelve `NO_DOC`: `"No hay ningún documento abierto en AutoCAD."` (`error.code` -32002).

**Si falla:**
- Aparece el nombre del documento anterior: el plugin capturó el documento al recibir la petición y no al ejecutarla (§3.5). También puede ser que tome `Document` de un campo en caché en vez de `MdiActiveDocument`.
- La línea aparece en el dibujo equivocado: mismo problema, y es un **fallo grave**.

---

## Prueba 6: `linea` con 3 segmentos → un solo CTRL+Z las quita, capa MCP-DIBUJO

- [ ] **Prueba 6 aprobada**

**Preparación:** dibujo de prueba nuevo, **sin** capa `MCP-DIBUJO`, `INSUNITS` = 6. Dibuje primero a mano un círculo cualquiera, que sirve de "acción previa del usuario".

**Acción:**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method linea -ParamsJson '{"lineas":[{"inicio":[0,0],"fin":[5,0]},{"inicio":[5,0],"fin":[5,3]},{"inicio":[5,3],"fin":[0,0]}]}'
```
En AutoCAD: `ZOOM` → `E` (Extensión). Después pulse **CTRL+Z una sola vez**.

Repita la prueba desde Claude Code: *"Dibuja un triángulo con líneas de (0,0) a (5,0), de (5,0) a (5,3) y de (5,3) a (0,0) en una sola llamada a `linea`."*

**Resultado esperado:**
- La respuesta tiene `entidades` con 3 elementos, `handles` con 3 handles hexadecimales distintos en mayúsculas, `"capa": "MCP-DIBUJO"`, `"capa_creada": true`, `"espacio": "modelo"`, `"insunits": 6`, `"unidades": "m"`, `"advertencias": []`, y la longitud del tercer segmento es `5.830951894845301` (§10).
- Las 3 líneas se ven en AutoCAD; `LIST` sobre cualquiera de ellas muestra la capa `MCP-DIBUJO`. La capa tiene color 3 (verde), tipo de línea `Continuous` y es trazable.
- Después de **un solo** CTRL+Z: desaparecen **las 3** líneas, y también la capa `MCP-DIBUJO` porque se creó en la misma llamada. **El círculo sigue ahí.**
- Un segundo CTRL+Z quita el círculo (es decir, la llamada ocupó exactamente un paso de deshacer).
- Prueba de atomicidad (§3.6): `-ParamsJson '{"lineas":[{"inicio":[0,0],"fin":[1,0]},{"inicio":[2,2],"fin":[2,2]}]}'` devuelve `INVALID_PARAMS` (`-32602`) con `data.detalle` = `"lineas[1]: longitud cero"`, y **no** se crea ninguna línea.
- Capa inexistente: `-ParamsJson '{"inicio":[0,0],"fin":[1,0],"capa":"NO_EXISTE"}'` devuelve `NOT_FOUND` (`-32003`), mensaje `"La capa 'NO_EXISTE' no existe en el dibujo."` y `data.capa` = `"NO_EXISTE"`.

**Si falla:**
- CTRL+Z quita solo una línea, o hacen falta varios CTRL+Z: la `Transaction` en contexto de aplicación no agrupa el deshacer (riesgo previsto en PLAN.md §4.3). **Cambie la constante `UseCommandContextUndo` del plugin a `true`**, recompile e instale de nuevo, y repita la prueba 6 completa (y la 2, porque el contexto de comando cambia el comportamiento con comandos activos):
  ```powershell
  Get-ChildItem plugin -Recurse -Filter *.cs | Select-String -Pattern 'UseCommandContextUndo'   # localizar la constante
  # editar: UseCommandContextUndo = true
  pwsh -File scripts\build.ps1
  # cerrar AutoCAD
  pwsh -File scripts\install-plugin.ps1
  ```
  Si con `true` tampoco se obtiene un único paso de deshacer, **el gate no se aprueba**.
- CTRL+Z quita también el círculo: el `UNDO _BE/_E` está mal emparejado. Es un fallo grave.
- Las líneas quedan en la capa actual en vez de `MCP-DIBUJO`: el plugin no aplica la capa por defecto (§5.4).

---

## Prueba 7: dos instancias de AutoCAD → sin colisión, se distinguen por PID

- [ ] **Prueba 7 aprobada**

**Preparación:** abra **dos** instancias de AutoCAD 2026 (dos `acad.exe`, no dos dibujos en la misma instancia), cada una con un dibujo de prueba distinto: `GATE_A.dwg` en la primera y `GATE_B.dwg` en la segunda. Ejecute `MCPESTADO` en cada una y anote el PID que muestra el nombre del pipe.

**Acción:**
```powershell
pwsh -File scripts\check-pipe.ps1
pwsh -File scripts\rpc-call.ps1 -Method ping                    # debe fallar por ambigüedad
pwsh -File scripts\rpc-call.ps1 -Method estado -Pid <PID_A>
pwsh -File scripts\rpc-call.ps1 -Method estado -Pid <PID_B>
```
En Claude Code, en este orden:
1. *"Lista las instancias de AutoCAD"* (herramienta `instancias`).
2. *"Dame el estado de AutoCAD"* sin seleccionar instancia.
3. *"Selecciona la instancia `<PID_A>`"* (`seleccionar_instancia`), luego *"Dame el estado"*.
4. *"Selecciona la instancia `<PID_B>`"*, luego *"Dame el estado"* y *"Dibuja una línea de (0,0) a (3,0)"*.
5. *"Selecciona la instancia 999999"*.

**Resultado esperado:**
- `check-pipe.ps1` muestra `Instancias encontradas: 2` con **dos PID distintos**, proceso `acad` en ambos y los pipes `\\.\pipe\mcp-autocad-<usuario>-<PID_A>` y `...-<PID_B>`.
- `rpc-call.ps1` sin `-Pid` sale con `ERROR: [NO_INSTANCE] Hay varias instancias de AutoCAD abiertas (PID: <PID_A>, <PID_B>). Use -Pid <n> para elegir una.`
- Con `-Pid`, cada `estado` devuelve su propio `pid` y su propio `documento.nombre` (`GATE_A.dwg` / `GATE_B.dwg`).
- Claude Code: (1) `instancias` lista los 2 PID con `"seleccion": "auto"`. (2) `estado` devuelve exactamente `Hay varias instancias de AutoCAD abiertas (PID: <PID_A>, <PID_B>). Use seleccionar_instancia para elegir una.` (3) `estado.pid` = PID_A y documento GATE_A. (4) `estado.pid` = PID_B, y la línea aparece **solo** en GATE_B. (5) Error `NO_INSTANCE`.
- `estado` incluye `instancias` con las dos entradas y `seleccionada: true` en la elegida (§7).

**Si falla:**
- Solo aparece un pipe: el nombre no incluye el PID, o la segunda instancia no cargó el plugin (ejecute `MCPESTADO` en ella).
- Error al crear el pipe en la segunda instancia (ver `plugin.log`): colisión de nombre. Fallo de diseño; el gate no se aprueba.
- La línea aparece en la instancia equivocada: el servidor no respeta la selección.

---

## Prueba 8: AutoCAD minimizado → `ping` y `estado` responden

- [ ] **Prueba 8 aprobada**

**Preparación:** una sola instancia de AutoCAD con un dibujo de prueba, ningún comando activo. **Minimice** la ventana de AutoCAD (botón "_" de la barra de título).

**Acción:**
```powershell
pwsh -File scripts\rpc-call.ps1 -Method ping
pwsh -File scripts\rpc-call.ps1 -Method estado
pwsh -File scripts\rpc-call.ps1 -Method estado -Repeat 20
```
En Claude Code: *"Haz ping a AutoCAD y dame su estado."*

**Resultado esperado:**
- `ping` → `result.pong = true` en < 100 ms.
- `estado` → `result` con el documento correcto y **< 2000 ms** (sin `BUSY`).
- `Resumen: 20 llamadas, 0 con error.` y máximo < 2000 ms.
- Al restaurar la ventana, AutoCAD está igual que antes y responde con normalidad.

**Si falla:**
- `estado` da `BUSY` con motivo "cola" solo cuando AutoCAD está minimizado: el hilo principal no procesa mensajes estando minimizado, o el marshaling depende de `Application.Idle` (que no se dispara). Use `SynchronizationContext.Post` o un `Control` oculto como mecanismo principal (PLAN.md §4.1) y deje `Idle` solo como respaldo.

---

## Tabla de resultados

| # | Prueba | Fecha | Resultado (OK / FALLA) | ms máx. | Observaciones |
|---|---|---|---|---|---|
| 1 | 100 llamadas sin congelar | | | | |
| 2 | LINE activo → BUSY "comando" < 2 s | | | | |
| 3 | OPTIONS abierto → BUSY "cola" < 2 s | | | | motivo obtenido: |
| 4 | AutoCAD cerrado → texto exacto | | | — | |
| 5 | Cambio de documento activo | | | — | |
| 6 | 3 líneas = 1 CTRL+Z, capa MCP-DIBUJO | | | — | UseCommandContextUndo = |
| 7 | Dos instancias por PID | | | — | PID_A = , PID_B = |
| 8 | AutoCAD minimizado | | | | |

**Versiones probadas:** AutoCAD `version_autocad` = ______ · `plugin_version` = ______ · `protocol_version` = 0.1

### Decisión

- [ ] **GATE APROBADO**: las 8 pruebas están en OK. Se puede empezar la Fase 1 (PLAN.md §5).
- [ ] **REPLANTEAR ARQUITECTURA**: alguna prueba sigue fallando después de aplicar lo indicado en "Si falla". Según PLAN.md §5, no se avanza a la Fase 1 hasta revisar el diseño del puente (marshaling, deshacer o descubrimiento).

Fecha: ____________ · Responsable: ____________ · Notas: ________________________________
