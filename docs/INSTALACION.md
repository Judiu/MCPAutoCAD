# Instalación del MCP para AutoCAD 2026 (plugin 0.8.0, Fases 0, 1, 2, 3, 4A, L, 4B y 5)

Cambios 0.8.0: protocolo `"0.8"`; Fase 5 (protocol.md §5H): `auditar_archivo` en el plugin, `auditar_carpeta`, `estado_auditoria` y `cancelar_auditoria` en el servidor, respaldo sin AutoCAD (ODA File Converter + ezdxf) y plantilla `Contents\plantillas\LPS_METROS.dwt`; 56 herramientas del plugin (61 en Claude Code). Validación en vivo: `docs\GATE_FASE5.md`.

Historial 0.7.0: protocolo `"0.7"`; cuatro herramientas de la Fase 4B (protocol.md §5G): `vano`, `union_muros`, `cotas_automaticas` e `insertar_export`; biblioteca con 5 `.dwg`; 55 herramientas del plugin (57 en Claude Code). Validación en vivo: `docs\GATE_FASE4B.md`.

Historial 0.5.0: protocolo `"0.5"`; once herramientas de geometría paramétrica (Fase 4A, protocol.md §5E): `punto_de`, `distancia` y `proyectar` (lectura), `muro`, `ejes_rejilla`, `recinto`, `offset`, `array_rectangular` y `array_polar` (generadoras) y `confirmar`/`descartar_preview` (previsualización con `preview_id`); código de error nuevo `INVALID_GEOMETRY` (−32005); `estado` avisa de entidades huérfanas en `MCP-PREVIEW`. Ver protocol.md ("Cambios 0.4 → 0.5"). La instalación no cambia.

Historial 0.4.0: protocolo `"0.4"`; catorce herramientas de dibujo primitivo (Fase 3, protocol.md §5D) y biblioteca de bloques instalada en `Contents\biblioteca\` (hoy `LPS_ROTULO.dwg`). Ver protocol.md ("Cambios 0.3 → 0.4"). Historial: 0.3.1/0.3.2 corrigieron los defectos del gate de Fase 2.

Los scripts de `scripts\` **los ejecuta el usuario**. `install-plugin.ps1` escribe en `%APPDATA%\Autodesk\ApplicationPlugins` y ningún agente debe ejecutarlo sin `-DryRun`.

## Requisitos

| Componente | Versión / ruta |
|---|---|
| Windows | 10/11 x64 |
| PowerShell | 7.x (`pwsh`). Los scripts usan `#Requires -Version 7.0` |
| AutoCAD | 2026 en `D:\Program Files\Autodesk\AutoCAD 2026\` (debe tener `acmgd.dll`, `acdbmgd.dll`, `accoremgd.dll`) |
| .NET SDK | 10.x x64 (verificado 10.0.403); AutoCAD 2026 R25.1 corre sobre .NET 10 |
| Python | 3.12 con los paquetes `mcp==2.0.0` y `pywin32`. `pytest` solo hace falta para `build.ps1 -ServerTests` |
| ezdxf (opcional) | ≥ 1.4 (`pip install "ezdxf>=1.4"`, extra `oda` de `server/pyproject.toml`). Solo para el respaldo sin AutoCAD; sin él, `auditar_archivo` y el lote funcionan igual con el plugin |
| ODA File Converter (opcional) | 27.1.0 en `C:\Program Files\ODA\ODAFileConverter 27.1.0\` (otra ruta: `MCP_AUTOCAD_ODA`). Solo para el respaldo sin AutoCAD |
| Claude Code | con soporte de `.mcp.json` en la raíz del proyecto |

## Instalación

1. **Compilar** el plugin y ejecutar las pruebas de GeometryCore:
   ```powershell
   cd C:\LPSAPP\MCPAutoCAD
   pwsh -File scripts\build.ps1                       # Release
   pwsh -File scripts\build.ps1 -Configuration Debug  # opcional
   pwsh -File scripts\build.ps1 -ServerTests          # además pytest de server\tests
   ```
   El script comprueba el SDK 10.x y las DLL de AutoCAD, compila `plugin\MCPAutoCAD.sln`, ejecuta las pruebas y verifica que en `plugin\MCPAutoCAD.Plugin\bin\Release\net10.0-windows\` exista `MCPAutoCAD.Plugin.dll` y **no** estén las DLL de AutoCAD.
2. **Cerrar AutoCAD** (todas las instancias).
3. **Instalar el bundle**:
   ```powershell
   pwsh -File scripts\install-plugin.ps1 -DryRun   # muestra cada acción sin ejecutarla
   pwsh -File scripts\install-plugin.ps1           # instala
   ```
   Queda así:
   ```
   %APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\
     PackageContents.xml
     Contents\MCPAutoCAD.Plugin.dll, MCPAutoCAD.GeometryCore.dll, *.deps.json, ...
     Contents\lps.json          copia de standards\lps.json (estándar LPS: unidad oficial m, INSUNITS=6, capas)
     Contents\biblioteca\*.dwg            copia de los 5 .dwg de standards\ (LPS_ROTULO, Puerta Dinamica LM, PuertaBaño, VENTANA01, TANQUE 1000 LTS)
     Contents\plantillas\LPS_METROS.dwt   copia de standards\LPS_METROS.dwt (plantilla LPS en metros, sin entidades)
   ```
   Si ya había una instalación, se borra antes de copiar la nueva. Los archivos se desbloquean con `Unblock-File`.
   Versión instalada: `(Get-Item "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\MCPAutoCAD.Plugin.dll").VersionInfo.FileVersion` debe devolver `0.8.0.0`.
   `lps.json` llega por dos vías: el `.csproj` lo copia a la salida de build y `install-plugin.ps1` lo copia de nuevo desde `standards\lps.json` (sobrescribe la del build). Si `standards\lps.json` no existe, el script avisa en amarillo y sigue; el resumen final muestra `Estándar LPS: <ruta>` o `NO INSTALADO`. Sin `lps.json` el plugin funciona igual, pero `estado.estandar_lps.disponible` = `false`, no advierte cuando el dibujo no está en metros (protocol.md §2.2) y `auditar_capas` no puede comparar con `capas_permitidas` (`estandar_disponible` = `false`, protocol.md §2.6); además las herramientas de dibujo no resuelven conceptos de capa (`muros`, `cotas`…) y solo pueden crear capas `MCP-*` (protocol.md §5D.0 R3).
   La **biblioteca de bloques** (5 `.dwg` desde 0.7) llega igual por dos vías: el `.csproj` copia `standards\LPS_ROTULO.dwg` a `biblioteca\` en la salida de build y `install-plugin.ps1` la copia de nuevo a `Contents\biblioteca\LPS_ROTULO.dwg` (crea la carpeta). Si falta, avisa en amarillo y sigue; el resumen final muestra `Biblioteca de bloques: <ruta>` o `NO INSTALADA`. Sin ella, `insertar_bloque` con `desde_biblioteca: true` responde `NOT_FOUND` para `LPS_ROTULO` (protocol.md §5D.5). `lps.json` puede definir `"biblioteca_bloques"` con otra carpeta.
   La **plantilla en metros** llega por dos vías, como la biblioteca: el `.csproj` copia `standards\LPS_METROS.dwt` a `plantillas\` en la salida de build (solo si existe) e `install-plugin.ps1` la copia de nuevo a `Contents\plantillas\LPS_METROS.dwt` (crea la carpeta). Si falta, avisa en amarillo y sigue. Para usarla: `_NEW` y elegir el archivo. La instalación **no** cambia la plantilla por defecto: cómo fijarla para `_QNEW` con `_OPTIONS` está en `docs\GATE_FASE5.md`, prueba 7, y es decisión del usuario.
4. **Abrir AutoCAD 2026**. El bundle se carga al iniciar (`LoadOnAutoCADStartup="True"`). Escriba `MCPESTADO`: debe mostrar `MCPAutoCAD 0.8.0 (protocolo 0.8)` y el pipe `\\.\pipe\mcp-autocad-<usuario>-<PID>`.
5. **Comprobar el pipe** desde PowerShell:
   ```powershell
   pwsh -File scripts\check-pipe.ps1
   pwsh -File scripts\rpc-call.ps1 -Method ping
   ```
   `ping` debe devolver `"protocol_version": "0.8"` y `"plugin_version": "0.8.0"`.
6. **Configurar Claude Code**: copie `.mcp.json.example` a `.mcp.json` en la raíz del proyecto donde usará Claude Code y reinicie Claude Code. `/mcp` debe mostrar el servidor `autocad` con 61 herramientas: las 56 del plugin y 5 del servidor (lista abajo).
7. Ejecute los checklists `docs\GATE_FASE0.md` (puente), `docs\GATE_FASE1.md` (lectura), `docs\GATE_FASE2.md` (auditoría; trabaja sobre una copia `tests\fixtures\fase2_trabajo.dwg` que crea y borra) `docs\GATE_FASE3.md` (dibujo; trabaja sobre `tests\fixtures\fase3_trabajo.dwg` y la borra al final) y `docs\GATE_FASE4A.md` (geometría paramétrica; trabaja sobre `tests\fixtures\fase4_trabajo.dwg`, copia de `copia_real.dwg` que se crea antes del gate y se borra al final).

### Herramientas publicadas (plugin 0.8.0)

Las 56 del plugin, en el orden del manifest (protocol.md §4), más 5 del servidor Python:

| Herramienta | Fase | Qué hace |
|---|---|---|
| `ping` | 0 | Comprueba que el plugin responde (no toca AutoCAD) |
| `estado` | 0 | Documento activo, unidades, UCS, comando en curso, `estandar_lps`, instancias |
| `listar_capas` | 0 | Capas del dibujo, paginadas |
| `linea` | 0 | **Escritura**: crea líneas (un CTRL+Z por llamada) |
| `consultar_entidades` | 1 | Entidades del espacio con filtros por tipo, capa y caja; resumen por tipo y capa |
| `propiedades` | 1 | Todas las propiedades de una entidad por handle |
| `textos` | 1 | TEXT, MTEXT y atributos, con búsqueda por subcadena |
| `bloques` | 1 | Definiciones de bloque, o inserciones y atributos de un bloque |
| `seleccion_actual` | 1 | Entidades seleccionadas por el usuario en AutoCAD |
| `xrefs` | 1 | Referencias externas y su estado (resuelta, no encontrada…) |
| `vista_png` | 1 | Imagen PNG de la vista actual o de una caja (Claude la recibe como imagen); desde 0.3.1, `espacio` plotea el modelo o un layout sin cambiar la pestaña activa. Nunca toca la vista ni crea pasos de deshacer |
| `resumen_archivo` | 1 | Resumen de un DWG cerrado leído en una base de datos lateral (no lo abre ni lo guarda) |
| `auditar_capas` | 2 | Clasifica las capas frente a `lps.json` y propone capa sugerida (no renombra) |
| `entidades_en_capa_0` | 2 | Entidades en la capa `0` por tipo y espacio |
| `auditar_bloques` | 2 | Bloques en otras unidades (factor de escala), nombres sospechosos y sin uso |
| `areas_por_capa` | 2 | Suma de áreas de entidades cerradas por capa (con `detalle`, lista paginada) |
| `verificar_cuadro_areas` | 2 | Detecta cuadros de áreas y comprueba su aritmética y las diferencias entre cuadros |
| `duplicados` | 2 | Grupos de entidades idénticas (hash de geometría con tolerancia) |
| `textos_fuera_de_escala` | 2 | Textos con altura anómala frente a la mediana del espacio |
| `auditar_dibujo` | 2 | Ejecuta las 8 reglas de auditoría (presupuesto de 8 s) y prioriza los hallazgos |
| `marcar_hallazgos` | 2 | **Escritura**: nube de revisión + texto por hallazgo, solo en capas `MCP-*` (un CTRL+Z) |
| `limpiar_observaciones` | 2 | **Escritura**: borra las entidades de una capa `MCP-*`; exige `confirmar: true` (un CTRL+Z) |
| `crear_capa` | 3 | **Escritura**: crea una capa del estándar LPS o `MCP-*` (idempotente) |
| `polilinea` | 3 | **Escritura**: LWPOLYLINE abierta o cerrada, con arcos |
| `circulo` | 3 | **Escritura**: círculo |
| `arco` | 3 | **Escritura**: arco antihorario entre dos ángulos (grados) |
| `texto` | 3 | **Escritura**: TEXT literal |
| `mtext` | 3 | **Escritura**: MTEXT literal |
| `insertar_bloque` | 3 | **Escritura**: bloque del dibujo o de la biblioteca (5 bloques) con atributos, propiedades dinámicas y conversión de unidades |
| `cota` | 3 | **Escritura**: cota lineal o alineada (capa `Cotas` por defecto) |
| `hatch` | 3 | **Escritura**: sombreado asociativo o por vértices (capa `Achurados` por defecto) |
| `mover` | 3 | **Escritura**: desplaza entidades por handle |
| `copiar` | 3 | **Escritura**: copia entidades por handle (devuelve los handles nuevos) |
| `rotar` | 3 | **Escritura**: rota entidades alrededor de un punto |
| `escalar` | 3 | **Escritura**: escala entidades desde un punto |
| `borrar` | 3 | **Escritura**: borra entidades por handle |
| `punto_de` | 4A | Resuelve un `PuntoRef` (coordenada, punto de una entidad o intersección de dos curvas) a coordenadas WCS |
| `distancia` | 4A | Distancia 3D y en planta, dx, dy, dz y ángulo entre dos `PuntoRef` |
| `proyectar` | 4A | Punto más cercano de una curva existente a un `PuntoRef` |
| `muro` | 4A | **Escritura**: muro como polilínea cerrada con ingletes en `MUROS` (achurado opcional en `Achurados`) |
| `ejes_rejilla` | 4A | **Escritura**: rejilla de ejes en `EJES` con burbujas tangentes y rótulos centrados |
| `recinto` | 4A | **Escritura**: polilínea cerrada + MTEXT con el nombre y el área (`12,35 m²`) |
| `offset` | 4A | **Escritura**: curva paralela a una entidad existente |
| `array_rectangular` | 4A | **Escritura**: copias independientes en retícula (tope 500; `confirmar` para entidades del usuario) |
| `array_polar` | 4A | **Escritura**: copias independientes alrededor de un centro (tope 500; `confirmar` para entidades del usuario) |
| `confirmar` | 4A | **Escritura**: dibuja la geometría definitiva de un `preview_id` y borra su previsualización (un CTRL+Z) |
| `descartar_preview` | 4A | **Escritura**: borra la previsualización de un `preview_id` y la olvida |
| `borrar_fuera_de` | L | **Escritura**: borra lo que queda fuera de un contorno (solo con `confirmar: true`) |
| `purgar` | L | **Escritura**: purga símbolos sin uso (solo con `confirmar: true`) |
| `normalizar_capas` | L | **Escritura**: reasigna capas según un mapa (`conservar_aspecto` desde 0.7; solo con `confirmar: true`) |
| `corregir_unidades_bloques` | L | **Escritura**: corrige el metadato de unidades de bloques (solo con `confirmar: true`) |
| `vano` | 4B | **Escritura**: parte el contorno de un muro e inserta la puerta o ventana (`confirmar` en muros del usuario; admite `preview`) |
| `union_muros` | 4B | **Escritura**: une dos muros rectos en un contorno (`confirmar` en muros del usuario; admite `preview`) |
| `cotas_automaticas` | 4B | **Escritura**: cotas lineales en cadena o total en la capa `Cotas` (admite `preview`) |
| `insertar_export` | 4B | **Escritura**: inserta un export de CYPE como bloque con factor de unidades (sin previsualización) |
| `auditar_archivo` | 5 | Audita un `.dwg` **cerrado** sobre una copia temporal, sin abrirlo ni modificarlo; sin instancia, respaldo ODA + ezdxf (`origen`) |
| `instancias` | servidor | Lista las instancias de AutoCAD visibles |
| `seleccionar_instancia` | servidor | Fija la instancia por PID |
| `auditar_carpeta` | servidor (5) | Audita en segundo plano los `.dwg` de una carpeta; responde un `id` de inmediato; nunca escribe en la carpeta auditada |
| `estado_auditoria` | servidor (5) | Progreso, agregados y resultados por archivo, con filtros (`no_metros`, `errores`, …). INSUNITS es lo declarado, no la unidad real |
| `cancelar_auditoria` | servidor (5) | Cancela el lote en curso; se reanuda con `auditar_carpeta(reanudar: id)` |

Todas las de Fase 1 son de solo lectura; en la Fase 2 solo `marcar_hallazgos` y `limpiar_observaciones` escriben, y **únicamente en capas `MCP-*`** (cualquier otra capa → `INVALID_PARAMS` `"capa: solo se admiten capas MCP-*"`). Las 14 de la Fase 3 escriben con las reglas de protocol.md §5D.0: un CTRL+Z por llamada; con `INSUNITS = 0` responden `UNITS_UNKNOWN` salvo `aceptar_sin_unidades: true`; solo crean capas del estándar o `MCP-*`; rechazan geometría fuera de la extensión del espacio salvo `forzar: true`; no cambian la pestaña ni la vista; y `mover`/`copiar`/`rotar`/`escalar`/`borrar` exigen `confirmar: true` sobre entidades que no estén en capas `MCP-*`. Las generadoras de la Fase 4A (protocol.md §5E) siguen esas mismas reglas salvo que **no** admiten `aceptar_sin_unidades` (con `INSUNITS = 0` responden siempre `UNITS_UNKNOWN`); con `preview: true` solo calculan y devuelven números y un `preview_id` (dibujo opcional en `MCP-PREVIEW`, caducidad 15 min); la geometría no construible responde `INVALID_GEOMETRY` sin escribir; el plugin nunca escribe fuera de una petición (las previsualizaciones caducadas quedan en `MCP-PREVIEW` y se limpian con `limpiar_observaciones` y `capa: "MCP-PREVIEW"`). Con AutoCAD cerrado, el servidor publica las 55 del plugin desde `%LOCALAPPDATA%\MCPAutoCAD\manifest.json` (protocol.md §4.1).

### Carpetas del plugin en `%LOCALAPPDATA%\MCPAutoCAD\`

| Ruta | Contenido |
|---|---|
| `plugin.log` | Log del plugin (rotación a 5 MB). Si falta `lps.json`, aquí queda una línea de aviso al cargar |
| `manifest.json` | Copia del manifest que el plugin escribe al cargar |
| `tmp\` | Archivos temporales de `vista_png` con el mecanismo `plot` (`vista_<guid>.png`); se borran al terminar cada llamada. Si queda alguno, se puede borrar a mano con AutoCAD cerrado |

### `.mcp.json.example`

```json
{
  "mcpServers": {
    "autocad": {
      "type": "stdio",
      "command": "python",
      "args": ["-m", "mcp_autocad"],
      "env": { "PYTHONPATH": "C:\\LPSAPP\\MCPAutoCAD\\server" }
    }
  }
}
```

Como JSON no admite comentarios, las variables opcionales se explican aquí. Se añaden dentro de `"env"`, y sus valores siempre son cadenas:

| Variable | Efecto | Ejemplo |
|---|---|---|
| `MCP_AUTOCAD_PID` | Fija la instancia de AutoCAD por PID (protocol.md §1.3). Si ese pipe no existe, las herramientas devuelven `NO_INSTANCE`. Sin ella, se usa la única instancia abierta o la que elija `seleccionar_instancia` | `"MCP_AUTOCAD_PID": "18244"` |
| `MCP_AUTOCAD_TIMEOUT` | Segundos de espera de cada respuesta del plugin (por defecto 15; `ping` usa 3) | `"MCP_AUTOCAD_TIMEOUT": "30"` |
| `MCP_AUTOCAD_ODA` | Ruta de `ODAFileConverter.exe` para el respaldo sin AutoCAD (por defecto `C:\Program Files\ODA\ODAFileConverter 27.1.0\ODAFileConverter.exe`) | `"MCP_AUTOCAD_ODA": "C:\\ODA\\ODAFileConverter.exe"` |
| `MCP_AUTOCAD_ODA_TIMEOUT` | Segundos por archivo de ODA File Converter (por defecto 120); al vencer se mata el proceso | `"MCP_AUTOCAD_ODA_TIMEOUT": "300"` |
| `MCP_AUTOCAD_AUDITORIAS` | Carpeta de informes del lote (por defecto `%LOCALAPPDATA%\MCPAutoCAD\auditorias`) | `"MCP_AUTOCAD_AUDITORIAS": "D:\\auditorias"` |

El **plugin** lee además esta variable. No va en `.mcp.json` (eso solo afecta al servidor Python): debe estar definida en el entorno del proceso `acad.exe` antes de abrir AutoCAD (variable de usuario de Windows, y reiniciar AutoCAD):

| Variable | Efecto | Ejemplo |
|---|---|---|
| `MCP_AUTOCAD_LPS` | Ruta completa a otro `lps.json`; sustituye a la copia `Contents\lps.json` junto a la DLL (protocol.md §2.2). Se lee una vez al cargar el plugin (también su clave opcional `biblioteca_bloques`) | `C:\LPSAPP\MCPAutoCAD\standards\lps.json` |

Fijar `MCP_AUTOCAD_PID` en `.mcp.json` rara vez conviene, porque el PID cambia cada vez que se abre AutoCAD. Es mejor usar la herramienta `seleccionar_instancia`.

Si `python` del PATH no es el Python 3.12 que tiene `mcp` y `pywin32`, ponga la ruta completa en `"command"` (por ejemplo, la de su entorno virtual).

### Prueba manual del servidor

```powershell
pwsh -File scripts\run-server.ps1            # python -m mcp_autocad con PYTHONPATH=server
pwsh -File scripts\run-server.ps1 -Pid 18244 -TimeoutSec 30
```
El servidor habla MCP por stdin/stdout. El script solo escribe en stderr. Salga con Ctrl+C.

## Desinstalación

```powershell
# con AutoCAD cerrado
pwsh -File scripts\install-plugin.ps1 -Uninstall -DryRun
pwsh -File scripts\install-plugin.ps1 -Uninstall
```
Borra `%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle`. Quite también la entrada `autocad` de `.mcp.json` si ya no la usa. La carpeta `%LOCALAPPDATA%\MCPAutoCAD\` (`plugin.log`, `manifest.json`, `tmp\`) no se borra: elimínela a mano si quiere. Mientras exista `manifest.json`, el servidor sigue publicando las herramientas del plugin aunque esté desinstalado.

## Solución de problemas: estándar LPS

- `estado` devuelve `"estandar_lps": {"disponible": false, ...}`: falta `Contents\lps.json` o no es JSON válido. Compruébelo con `Test-Path "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\lps.json"` y mire `plugin.log`. Reinstale con AutoCAD cerrado (`install-plugin.ps1` lo copia desde `standards\lps.json`) o defina `MCP_AUTOCAD_LPS`.
- Tras editar `standards\lps.json`, reinstale (o reinicie AutoCAD si usa `MCP_AUTOCAD_LPS`): el plugin lo lee solo al cargar.
- `insertar_bloque` con `desde_biblioteca: true` responde `NOT_FOUND` `"El bloque 'LPS_ROTULO' no está en el dibujo ni en la biblioteca (<carpeta>)"`: falta `Contents\biblioteca\LPS_ROTULO.dwg` (compruébelo con `Test-Path "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\biblioteca\LPS_ROTULO.dwg"`) o `biblioteca_bloques` de `lps.json` apunta a otra carpeta. Reinstale con AutoCAD cerrado.
- `auditar_archivo` o `auditar_carpeta` devuelven `[ODA_NO_DISPONIBLE]`: no hay AutoCAD abierto y no se encontró ODA File Converter; instálelo o defina `MCP_AUTOCAD_ODA`. `INTERNAL` con un error de `ezdxf`: instale la dependencia opcional (`pip install "ezdxf>=1.4"`).
- Las herramientas de dibujo devuelven `UNITS_UNKNOWN`: el dibujo tiene `INSUNITS = 0`. Declare la unidad (`INSUNITS` → 6 para metros) o, si es intencionado, pase `aceptar_sin_unidades: true`.

## Solución de problemas

### SECURELOAD: AutoCAD avisa o bloquea la carga de la DLL
- `%APPDATA%\Autodesk\ApplicationPlugins` es una de las ubicaciones de confianza que AutoCAD reconoce para bundles, así que normalmente no aparece ningún aviso.
- Si aparece el aviso "Archivo ejecutable de seguridad", compruebe que la ruta mostrada sea exactamente la del bundle instalado y elija **Cargar** (o **Cargar siempre**).
- Si se bloquea sin preguntar (`SECURELOAD` = 2): añada **usted** la carpeta `...\ApplicationPlugins\MCPAutoCAD.bundle\Contents` en Opciones → Archivos → Ubicaciones de confianza (`TRUSTEDPATHS`). Los scripts no cambian `TRUSTEDPATHS`.
- **No** ponga `SECURELOAD` = 0: deja AutoCAD cargando código desde cualquier carpeta.

### "AutoCAD está en ejecución" o la DLL está bloqueada al instalar
- AutoCAD mantiene abierta `MCPAutoCAD.Plugin.dll` mientras está en ejecución. Cierre todas las instancias y compruébelo con `Get-Process acad`. Si queda un proceso colgado, ciérrelo desde el Administrador de tareas.
- Cierre también cualquier `accoreconsole.exe` en ejecución por si cargó el bundle.
- Si Windows marca las DLL como descargadas de Internet (aviso al cargar), vuelva a ejecutar `install-plugin.ps1`: aplica `Unblock-File` a todo el bundle.

### El pipe no aparece (`check-pipe.ps1` dice "No hay instancias")
1. ¿AutoCAD está abierto **con interfaz** y del mismo usuario de Windows? El pipe solo es visible para el usuario que abrió AutoCAD (protocol.md §1.1).
2. Escriba `MCPESTADO` en AutoCAD. Si dice "Comando desconocido", el plugin no cargó:
   - Compruebe que exista `%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\PackageContents.xml` y `Contents\MCPAutoCAD.Plugin.dll`.
   - `PackageContents.xml` debe declarar `SeriesMin="R25.1"`, `SeriesMax="R25.1"` y `ModuleName="./Contents/MCPAutoCAD.Plugin.dll"` (protocol.md §9). Si la versión de AutoCAD instalada no es R25.1, el bundle se ignora sin avisar. Mire `ACADVER` en AutoCAD.
   - Ejecute `APPLOAD` → Contenido para ver si el bundle está registrado. Pruebe a cargar la DLL a mano con `NETLOAD` desde `Contents\` para ver el error real.
   - Revise `%LOCALAPPDATA%\MCPAutoCAD\plugin.log`.
3. Si `MCPESTADO` responde pero `check-pipe.ps1` no ve el pipe, revise si el nombre de usuario de `MCPESTADO` coincide con `$env:USERNAME`. Use `check-pipe.ps1 -AllUsers` para verlo.
4. `rpc-call.ps1` da `Acceso denegado`: la ACL del pipe solo admite al usuario que abrió AutoCAD. No ejecute PowerShell "como otro usuario" ni elevado con otra cuenta.

### Python no encuentra `mcp_autocad` (`No module named mcp_autocad`)
- `PYTHONPATH` debe apuntar a la carpeta **`server`**, no a `server\mcp_autocad`: `C:\LPSAPP\MCPAutoCAD\server`.
- Compruébelo:
  ```powershell
  $env:PYTHONPATH = 'C:\LPSAPP\MCPAutoCAD\server'; python -c "import mcp_autocad, sys; print(mcp_autocad.__file__, sys.version)"
  ```
- `No module named mcp` o `win32file`: el `python` que lanza Claude Code no es el que tiene los paquetes. Use la ruta completa del intérprete en `"command"` de `.mcp.json`.
- En Claude Code, `/mcp` muestra el estado del servidor. Los errores de arranque salen por stderr del servidor.

### `build.ps1` dice que la salida contiene `acmgd.dll` / `acdbmgd.dll` / `accoremgd.dll`
Las referencias a AutoCAD del `.csproj` deben llevar `<Private>false</Private>`. Corríjalo, borre `plugin\MCPAutoCAD.Plugin\bin\` y vuelva a compilar. **No** instale un bundle con esas DLL: AutoCAD cargaría copias duplicadas de su propia API.

### `BUSY` constante
Mire si hay un comando activo o un diálogo abierto en AutoCAD (pulse Esc). Si persiste sin motivo, ejecute `MCPESTADO` y revise `plugin.log`. Vea las pruebas 2, 3 y 8 de `docs\GATE_FASE0.md`.
