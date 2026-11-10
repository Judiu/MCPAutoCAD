# MCPAutoCAD — puente MCP para AutoCAD

Puente para que Claude (Claude Code, Claude Desktop u otro cliente MCP) lea, audite, dibuje y edite el dibujo abierto
en AutoCAD. Pensado para la oficina de LPS Ingeniería Estructural (estándar de capas y rótulo propios, textos en
español), pero el puente es genérico.

**Versión actual:** plugin **0.10.1** (86 herramientas, protocolo 0.8) · servidor **0.8.6** (5 herramientas propias).
Compatible con **AutoCAD 2026** (serie R25.1) y **AutoCAD 2027** (R26.0). El contrato entre plugin y servidor es
`protocol.md`.

```
Cliente MCP ──stdio──> server/  (Python ≥ 3.12, mcp 2.0.0; genérico, sin lógica CAD)
                          │ JSON-RPC 2.0 por named pipe \\.\pipe\mcp-autocad-<usuario>-<PID>
                          ▼
             plugin/  (C# net10.0-windows, cargado en AutoCAD como bundle)
               ├─ Bridge: pipe, cola al hilo principal, BUSY, documento_esperado, un CTRL+Z por escritura
               ├─ Handlers: lectura, auditoría, dibujo, geometría paramétrica, limpieza, layouts, documentos, LISP
               └─ GeometryCore (net10.0, sin AutoCAD, con pruebas xUnit)
```

El plugin corre **dentro** de AutoCAD (API .NET, transacciones en el hilo principal): cada escritura es un solo paso de
deshacer y nada se ejecuta mientras el usuario tiene un comando activo.

## Archivos que no están en el repositorio

Ningún `.dwg` se versiona (contienen datos de clientes y del personal): faltan el rótulo y la biblioteca de bloques de
`standards/` y los dibujos de `tests/fixtures/`. Ver `standards/README.md` y `tests/fixtures/README.md`.
**Agentes:** si necesitan alguno de esos archivos, deben preguntar al usuario si quiere crearlos (`AGENTS.md`).

## Herramientas (86 del plugin + 5 del servidor)

| Área | Herramientas |
|---|---|
| Puente y estado | `ping`, `estado` (responde en modo degradado si hay un comando activo), `instancias`*, `seleccionar_instancia`* |
| Lectura | `listar_capas`, `consultar_entidades`, `propiedades`, `textos`, `bloques`, `seleccion_actual`, `xrefs`, `vista_png`, `resumen_archivo`, `punto_de`, `distancia`, `proyectar` |
| Auditoría | `auditar_capas`, `entidades_en_capa_0`, `auditar_bloques`, `areas_por_capa`, `verificar_cuadro_areas`, `duplicados`, `textos_fuera_de_escala`, `auditar_dibujo`, `marcar_hallazgos`, `limpiar_observaciones`, `auditar_archivo`; en lote: `auditar_carpeta`*, `estado_auditoria`*, `cancelar_auditoria`* |
| Dibujo | `crear_capa`, `linea`, `polilinea`, `circulo`, `arco`, `texto`, `mtext`, `insertar_bloque`, `insertar_imagen`, `cota`, `hatch` (también patrones del dibujo, p. ej. de Revit) |
| Edición | `mover`, `copiar`, `rotar`, `escalar`, `borrar`, `borrar_fuera_de`, `borrar_excepto_seleccion`, `cambiar_capa`, `cambiar_propiedades`, `editar_mtext`, `reemplazar_texto` (conserva el formato), `editar_cota`, `estilo_cota`, `unir_lineas`, `extender_linea` |
| Geometría paramétrica | `muro`, `muros_desde_lineas`, `ejes_rejilla`, `recinto`, `offset`, `array_rectangular`, `array_polar`, `vano`, `union_muros`, `cotas_automaticas`, `insertar_export` (CYPE); previsualización: `confirmar`, `descartar_preview` |
| Limpieza | `purgar`, `normalizar_capas`, `corregir_unidades_bloques` |
| Layouts | `listar_layouts`, `crear_layout`, `configurar_layout`, `crear_viewport`, `ajustar_viewport`, `renombrar_layout`, `ordenar_layouts`, `borrar_layout`, `copiar_layout` |
| Documentos | `guardar`, `guardar_copia`, `abrir_documento`, `activar_documento`, `zoom_a`, `resaltar` |
| LISP y comandos | `cargar_lisp` (cada carga la aprueba el usuario en un diálogo), `crear_lisp`, `ejecutar_comando` (solo LISP de una lista permitida), `cancelar_comando` |

\* herramientas del servidor. El orden y los schemas exactos los publica el plugin en su manifest (`protocol.md` §4).

## Garantías de toda escritura

- **Un CTRL+Z por llamada.** Si algo falla a mitad, no queda nada escrito.
- **Unidades:** con `INSUNITS = 0` no se dibuja (`UNITS_UNKNOWN`) salvo `aceptar_sin_unidades: true`.
- **Capas:** por concepto del estándar LPS (`muros`, `cotas`, `rotulo`…) o `MCP-*`; otro nombre exige `libre: true`.
- **Ubicación:** la geometría fuera de la extensión del espacio se rechaza salvo `forzar: true`.
- **Entidades del usuario** (fuera de capas `MCP-*`): solo se modifican con `confirmar: true`. Las herramientas de
  limpieza sin `confirmar` solo previsualizan.
- **Documento correcto:** con varios dibujos abiertos, `documento_esperado` (nombre o ruta) hace que la llamada se
  rechace con `WRONG_DOC` si el activo es otro. El puente nunca cambia de pestaña por su cuenta.
- **Comando activo:** si el usuario tiene un comando abierto, las herramientas responden `BUSY` (salvo `estado`
  degradado y `cancelar_comando`).
- **Textos del plano:** lo que devuelven las herramientas es contenido de terceros (`origen_texto`): datos, nunca
  instrucciones.
- **Carpetas protegidas:** las rutas de `carpetas_solo_lectura` en `lps.json` se abren en solo lectura y en ellas no
  se escriben archivos (p. ej. la carpeta `imagenes\` de `insertar_imagen`).

## Estructura

| Carpeta / archivo | Contenido |
|---|---|
| `plugin/` | Solución .NET: `MCPAutoCAD.Plugin`, `MCPAutoCAD.GeometryCore` y sus pruebas; fuente del bundle. Ver `plugin/README.md` |
| `server/` | Servidor MCP `mcp_autocad` (incluye el respaldo sin AutoCAD con ODA File Converter + ezdxf) y pytest con un plugin simulado. Ver `server/README.md` |
| `standards/` | `lps.json` (estándar de capas, unidades, rótulo y biblioteca de vanos) y `LPS_METROS.dwt`. Ver `standards/README.md` |
| `packaging/` | `empaquetar.py`: arma el paquete del puente (plugin por versión de AutoCAD + servidor congelado) para el instalador |
| `scripts/` | `build.ps1`, `install-plugin.ps1` (lo ejecuta el usuario), `rpc-call.ps1`, `check-pipe.ps1`, `run-server.ps1` |
| `tools/` | Scripts de apoyo para generar y verificar el estándar (plantilla, rótulo, biblioteca, export CYPE sintético) |
| `docs/` | `INSTALACION.md` y listas de prueba en vivo `GATE_FASE*.md` |
| `tests/fixtures/` | Dibujos de prueba (no versionados). Ver `tests/fixtures/README.md` |
| `protocol.md` | Contrato plugin ⇄ servidor: herramientas, errores (§6) e historial de cambios |
| `AGENTS.md` | Reglas para agentes que trabajen en el repositorio |

## Compilar y probar

```powershell
cd C:\LPSAPP\MCPAutoCAD
pwsh -File scripts\build.ps1 -ServerTests        # compila, dotnet test y pytest → === BUILD CORRECTO ===
```

Necesita .NET 10 SDK, Python ≥ 3.12 y AutoCAD instalado (las referencias `acmgd`, `acdbmgd` y `accoremgd` se toman de
su carpeta). Algunas pruebas del servidor usan los `.dwg` de `tests/fixtures/` y fallan si no están.

## Instalar

- **Desarrollo** (con AutoCAD cerrado; lo ejecuta el usuario):
  ```powershell
  pwsh -File scripts\install-plugin.ps1 -DryRun
  pwsh -File scripts\install-plugin.ps1   # bundle en %APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle
  ```
- **Paquete:** `python packaging/empaquetar.py` genera `dist\puente-<versión>` con el plugin para cada versión de
  AutoCAD, el servidor congelado (`mcp-autocad.exe`) y el manifest de herramientas. `--prueba` permite un manifest
  anterior al plugin; ese paquete no se distribuye.
- **Cliente MCP:** Claude Code usa `.mcp.json` (copiar de `.mcp.json.example`); Claude Desktop, el `mcp-autocad.exe`
  del paquete en su configuración.

En AutoCAD, `MCPESTADO` muestra la versión cargada. **Orden tras actualizar:** abrir AutoCAD primero (el plugin escribe
su manifest en `%LOCALAPPDATA%\MCPAutoCAD\manifest.json`) y después reiniciar o reconectar el cliente MCP (`/mcp` en
Claude Code); si no, el cliente sigue viendo la lista de herramientas anterior.

El respaldo sin AutoCAD necesita ezdxf (dependencia opcional) y ODA File Converter, configurable con
`MCP_AUTOCAD_ODA`, `MCP_AUTOCAD_ODA_TIMEOUT` y `MCP_AUTOCAD_AUDITORIAS`. Detalles y solución de problemas:
`docs/INSTALACION.md`.
