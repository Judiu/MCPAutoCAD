# Contrato de instalación del puente AutoCAD en LPS Escritorio

Esquema de manifiesto: 1. Lo genera `packaging\empaquetar.py` en `dist\puente-<versión>\`.

Las decisiones del usuario (2026-11-06) son dos. El servidor MCP va **dentro del MSI**. La plantilla QNEW la configura **la app**.

El instalador MSI corre elevado y quizá no como el usuario final. Por eso **no instala nada por usuario**: todo lo hace la app al arrancar, ya como el usuario.

## 1. Contenido del paquete

```
puente-<v>\
  manifest.json                      versiones + lista de archivos {ruta, bytes, sha256}
  MCPAutoCAD.bundle\                 plugin para AutoCAD 2026 (R25.1) y 2027 (R26.0); no carga en LT
    PackageContents.xml              un <Components> por serie: AutoCAD carga solo el suyo
    manifest.json                    copia reducida: versión del paquete + sección "plugin"
    Contents\lps.json                ruta fija para MCP_AUTOCAD_LPS
    Contents\plantillas\LPS_METROS.dwt   ruta fija para QnewTemplate
    Contents\R25.1\  Contents\R26.0\  MCPAutoCAD.Plugin.dll + GeometryCore + su copia de lps.json, biblioteca\, plantillas\
  servidor\mcp-autocad\              servidor MCP, PyInstaller onedir
    mcp-autocad.exe                  stdio; no abre puertos
    manifest_plugin.json             herramientas del plugin: se publican aunque el plugin no haya cargado nunca (desde 0.9.1)
    _internal\...
  CONTRATO_INSTALACION.md            este archivo
```

Las rutas del manifiesto son relativas a `MCPAutoCAD.bundle\` (plugin) o a `servidor\mcp-autocad\` (servidor) y usan `/`.

## 2. Plugin: instalación por usuario al arrancar la app

Destino: `%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\`. AutoCAD trata esta ubicación como de confianza, así que **no hay que tocar TRUSTEDPATHS ni SECURELOAD** y no se debe hacer.

1. Lea `resource_dir\...\MCPAutoCAD.bundle\manifest.json` (embebido) y el del destino, si existe.
2. **No hay nada que hacer** si coinciden `version` y todos los `sha256` de `plugin.archivos` (el `manifest.json` del bundle no se lista a sí mismo). Fin.
3. **AutoCAD abierto:** si hay un `acad.exe` del usuario actual corriendo, no copie nada. La DLL está cargada y bloqueada, y reemplazar la mitad del bundle lo dejaría inconsistente.
   - Estado: "Actualización del puente pendiente: cierre AutoCAD".
   - Se reintenta en el siguiente arranque de la app o cuando `acad.exe` termine, si la app lo vigila.
4. **Copia atómica:**
   1. Copie a `...\ApplicationPlugins\MCPAutoCAD.bundle.nuevo\` y verifique cada SHA-256.
   2. Si `MCPAutoCAD.bundle\` existe, renómbrelo a `MCPAutoCAD.bundle.anterior\`.
   3. Renombre `.nuevo` a `MCPAutoCAD.bundle\` y borre `.anterior`.
   4. Si un paso falla, deshaga el renombrado y deje el estado en error.
5. **Protección contra borrados:** solo borre o renombre carpetas cuyo nombre final sea exactamente `MCPAutoCAD.bundle`, `MCPAutoCAD.bundle.nuevo` o `MCPAutoCAD.bundle.anterior`, y cuyo padre sea `%APPDATA%\Autodesk\ApplicationPlugins`.
6. **Verificación:** relea el destino y compare con el manifiesto.
7. **Cuándo actúa:** AutoCAD carga el plugin en el siguiente inicio (`LoadOnAutoCADStartup="True"`).
8. **Versiones soportadas:** son las series de `plugin.releases` del manifiesto (hoy R25.1 = 2026 y R26.0 = 2027). No las codifique en la app: una versión nueva de AutoCAD llega como paquete nuevo. Se instala el bundle si hay al menos una `ACAD-*` con `AcadLocation` en HKLM bajo alguna de esas series; el mismo bundle sirve para todas.
9. **Sin versión soportada:** no copie nada, pero **dígalo en la UI** con la versión encontrada (p. ej. "AutoCAD 2025 detectado: el puente requiere AutoCAD 2026 o 2027"). En 2025 (R25.0, .NET 8) el plugin net10.0 no carga.

**Señal "plugin cargado"** (para la UI, opcional):
- Al cargar, el plugin escribe `%LOCALAPPDATA%\MCPAutoCAD\manifest.json`. Si su fecha es posterior a la instalación, AutoCAD lo cargó al menos una vez.
- Mientras AutoCAD está abierto existe el named pipe `\\.\pipe\mcp-autocad-<usuario>-<pid>`.
- Para saber si está abierto ahora: liste `\\.\pipe\` y busque el prefijo `mcp-autocad-<usuario>-`.
- No abra el pipe desde la app: el pipe es para el servidor MCP.

**Desinstalación:**
- Con AutoCAD cerrado, borre `%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\` con la protección del punto 5.
- `%LOCALAPPDATA%\MCPAutoCAD\` contiene registros y auditorías del usuario. No se borra salvo que el usuario lo pida.

## 3. Servidor MCP: registro en Claude Desktop

- **Ubicación:** el servidor se queda en `resource_dir`, sin copiarlo.
- **Requisito previo:** solo se registra si el PC tiene Claude Desktop. Si no existe la carpeta de configuración, no haga nada.
- **Archivo**: busque en este orden y escriba solo en el primero que exista. Si hay varios `claude_*`, use el modificado más recientemente y avise en la UI.
  - `%LOCALAPPDATA%\Claude-Data\claude_<id>\claude_desktop_config.json` [Certeza: es la que usa Claude Desktop en el PC de desarrollo, 2026-11-07].
  - Las dos siguientes son [Probable: compruébelo en cada PC]:
  - Instalación normal: `%APPDATA%\Claude\claude_desktop_config.json`.
  - Versión de la tienda (MSIX): `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json`.
- **Entrada** (en `mcpServers`, clave `autocad`):

```json
"autocad": {
  "command": "<resource_dir absoluto>\\servidor\\mcp-autocad\\mcp-autocad.exe",
  "args": [],
  "env": {
    "MCP_AUTOCAD_LPS": "%APPDATA% expandido\\Autodesk\\ApplicationPlugins\\MCPAutoCAD.bundle\\Contents\\lps.json",
    "MCP_AUTOCAD_ACCORE": "<AcadLocation>\\accoreconsole.exe"
  }
}
```

### Variables de entorno

- **`MCP_AUTOCAD_LPS` es obligatoria.** Fuera del repositorio, el exe no encuentra `lps.json` y entonces el servidor corre **sin el límite de carpetas** que define ese archivo.
- **`MCP_AUTOCAD_ACCORE`:**
  - El valor por defecto es `D:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe`, que solo vale para el PC de desarrollo.
  - Lea la ruta real en `HKLM\SOFTWARE\Autodesk\AutoCAD\<serie>\ACAD-*\AcadLocation`, con la serie soportada **más alta** instalada (2027 antes que 2026). Solo lectura. accoreconsole solo se usa para auditar archivos, así que cualquiera de las dos sirve.
  - Si no la encuentra, omita la variable. Las auditorías sin AutoCAD abierto usarán ODA, si está instalado.
- **`MCP_AUTOCAD_ODA`:** solo si ODA File Converter no está en `C:\Program Files\ODA\ODAFileConverter 27.1.0\`.

### Reglas al escribir la configuración

1. **Respaldo:** antes de escribir, guarde una copia `claude_desktop_config.json.lps-respaldo-<fecha>`.
2. **Conservar el resto:** modifique solo `mcpServers.autocad`. Conserve todas las demás claves y el orden si es posible.
3. **Ruta del exe:** el updater puede cambiar `resource_dir` al reinstalar. En cada arranque de la app, recalcule la ruta del exe.
   - Si `command` ya apunta a un exe de LPS Escritorio con otra ruta, actualícelo.
   - Si `autocad` existe y apunta a otra cosa (por ejemplo `python.exe` en el PC de desarrollo), **no lo pise**: muestre un aviso.
4. **Aplicar:** Claude Desktop lee la configuración al iniciar. Indique en la UI "Reinicie Claude Desktop".
5. **Revertir:** borre `mcpServers.autocad` solo si su `command` apunta al exe de LPS Escritorio.
6. **Claude Code:** no se configura automáticamente. Si se quiere, se hace a mano con `claude mcp add`.

**Firma:** el exe no lleva Authenticode. SmartScreen o Defender pueden bloquearlo o marcarlo la primera vez. Si Defender lo pone en cuarentena, la UI debe decirlo; no reintente en bucle.

## 4. Plantilla QNEW (opt-in, reversible)

El objetivo es que los planos nuevos nazcan en metros (`LPS_METROS.dwt`, INSUNITS 6).

| Paso | Regla |
|---|---|
| (a) Opt-in | Paso separado. Antes de escribir, guarde el valor anterior de `QnewTemplate` en `%LOCALAPPDATA%\com.lpscol.escritorio\puente-qnew.json` (por usuario, igual que la clave HKCU; la base SQLite no existe antes del login). Si al activar el valor ya es nuestra plantilla, guarde "anterior desconocido" |
| (b) Clave | `HKCU\Software\Autodesk\AutoCAD\<serie>\<ACAD-xxxx:xxx>\Profiles\<perfil activo>\General\QnewTemplate`, **una vez por cada serie soportada instalada** (cada versión de AutoCAD tiene su propio perfil). El perfil activo es el valor `(Default)` de `...\Profiles`. Descubra los segmentos, no los codifique; use la `ACAD-*` con `AcadLocation` en HKLM. El valor anterior se guarda por serie. La clave HKCU de una versión recién instalada aparece la primera vez que el usuario abre esa versión: si no existe, reintente en el siguiente arranque |
| (c) AutoCAD abierto | No escriba si `acad.exe` del usuario corre: AutoCAD guarda el perfil al cerrar y pisaría el cambio. Reintente en el siguiente arranque |
| (d) Verificación | Relea el valor después de escribir |
| (e) Valor | Ruta absoluta expandida de `%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\plantillas\LPS_METROS.dwt` |
| Revertir | Restaure el valor guardado en (a). Si estaba vacío, o es "anterior desconocido", borre el valor (QNEW vuelve a mostrar el cuadro de plantillas). No es un error |

**Limitación** [Probable]:
- QNEW cubre el botón "Nuevo" de la pestaña Inicio y el comando QNEW.
- `Ctrl+N` (comando NEW) sigue mostrando el cuadro de plantillas, donde aparece `acadiso.dwt`, en mm.
- No borre ni renombre `acadiso.dwt` desde la app.

## 5. Qué no hace la app

- No instala ni modifica nada en `O:\`.
- No toca SECURELOAD, TRUSTEDPATHS ni otras claves de AutoCAD fuera de la sección 4.
- No ejecuta AutoCAD ni accoreconsole.
- No abre el named pipe del plugin.
