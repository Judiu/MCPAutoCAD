# MCPAutoCAD — MCP para AutoCAD 2026

Puente para que Claude Code (u otro cliente MCP) lea, audite, dibuje y genere geometría paramétrica sobre el dibujo
abierto en AutoCAD 2026. Versión actual: **plugin 0.8.0, protocolo 0.8** (Fases 0–3, 4A, L, 4B y 5; gate en vivo de la Fase 5 pendiente). Plan: `PLAN.md`. Contrato único plugin ⇄ servidor:
`protocol.md`.

```
Claude Code ──stdio──> server/ (Python 3.12, mcp==2.0.0, servidor genérico, sin lógica CAD)
                           │ JSON-RPC 2.0 por named pipe \\.\pipe\mcp-autocad-<usuario>-<PID>
                           ▼
              plugin/ (C# net10.0-windows, cargado en AutoCAD 2026 como bundle)
                ├─ Bridge: pipe, cola al hilo principal, BUSY, un CTRL+Z por escritura
                ├─ Handlers: lectura (Fase 1), Audit/ (Fase 2), Draw/ (Fase 3), Gen/ (Fase 4A), Clean/ (Fase L)
                └─ GeometryCore (net10.0, sin AutoCAD, con pruebas xUnit)
```

## Archivos que no están en el repositorio

Ningún `.dwg` se versiona (contienen datos de clientes y del personal): faltan el rótulo y la biblioteca de bloques
de `standards/` y los dibujos de `tests/fixtures/`. Ver `standards/README.md` y `tests/fixtures/README.md`.
**Agentes:** si necesitan alguno de esos archivos, deben preguntar al usuario si quiere crearlos (`AGENTS.md`).

## Herramientas (56 del plugin + 5 del servidor = 61)

| Fase | Estado | Herramientas |
|---|---|---|
| 0 — Puente | gate aprobado | `ping`, `estado`, `listar_capas`, `linea` |
| 1 — Lectura | gate aprobado | `consultar_entidades`, `propiedades`, `textos`, `bloques`, `seleccion_actual`, `xrefs`, `vista_png`, `resumen_archivo` |
| 2 — Auditoría | gate aprobado (0.3.2) | `auditar_capas`, `entidades_en_capa_0`, `auditar_bloques`, `areas_por_capa`, `verificar_cuadro_areas`, `duplicados`, `textos_fuera_de_escala`, `auditar_dibujo`, `marcar_hallazgos`, `limpiar_observaciones` |
| 3 — Dibujo primitivo | gate aprobado (0.4.0) | `crear_capa`, `polilinea`, `circulo`, `arco`, `texto`, `mtext`, `insertar_bloque`, `cota`, `hatch`, `mover`, `copiar`, `rotar`, `escalar`, `borrar` |
| 4A — Geometría paramétrica | gate aprobado (0.5.0, `docs/GATE_FASE4A.md`) | lectura: `punto_de`, `distancia`, `proyectar`; generadoras: `muro`, `ejes_rejilla`, `recinto`, `offset`, `array_rectangular`, `array_polar`; previsualización: `confirmar`, `descartar_preview` |
| L — Limpieza de planos | gate en vivo aprobado con un hallazgo (0.6.0, 2026-10-27); corregido en 0.6.1, re-verificación de las pruebas 6E y 11 pendiente (`docs/GATE_FASEL.md`) | `borrar_fuera_de`, `purgar`, `normalizar_capas`, `corregir_unidades_bloques` |
| 4B — Vanos, cotas automáticas, ensamblado CYPE | gate aprobado en vivo (regate 0.7.1, 2026-10-31, `docs/GATE_FASE4B.md`) | `vano`, `union_muros`, `cotas_automaticas`, `insertar_export` |
| 5 — Lote y respaldo | gate en vivo pendiente (0.8.0, `docs/GATE_FASE5.md`) | plugin: `auditar_archivo`; servidor: `auditar_carpeta`, `estado_auditoria`, `cancelar_auditoria`; respaldo sin AutoCAD (ODA File Converter + ezdxf) y plantilla `standards/LPS_METROS.dwt` |
| Servidor | — | `instancias`, `seleccionar_instancia` |

Garantías comunes de toda escritura (protocol.md §3.5 y §5D.0): un CTRL+Z por llamada; con `INSUNITS = 0` no se
dibuja (`UNITS_UNKNOWN`) salvo `aceptar_sin_unidades: true`; capas por concepto del estándar LPS (`muros`, `cotas`,
`rotulo`…) y nunca capas arbitrarias; geometría fuera de la extensión del espacio rechazada salvo `forzar: true`;
la pestaña activa y la vista no se tocan; los textos del cliente se escriben literales; y las entidades del usuario
(fuera de capas `MCP-*`) solo se mueven, copian, rotan, escalan o borran con `confirmar: true`.

Fase 4A (protocol.md §5E): los puntos se dan como `PuntoRef` (coordenada, punto de una entidad existente o
intersección de dos curvas) y GeometryCore calcula las coordenadas; las generadoras **no** admiten
`aceptar_sin_unidades` (`INSUNITS = 0` → `UNITS_UNKNOWN` siempre); `preview: true` devuelve números y un
`preview_id` (dibujo opcional en `MCP-PREVIEW`, caduca a los 15 min) que `confirmar` convierte en geometría
definitiva en un CTRL+Z; la geometría imposible responde `INVALID_GEOMETRY` sin escribir. El plugin nunca
escribe fuera de una petición: las previsualizaciones caducadas se avisan y se limpian con `limpiar_observaciones`.

Fase L (protocol.md §5F): las cuatro herramientas de limpieza modifican entidades y símbolos del usuario, así que
**sin `confirmar: true` nunca escriben**: devuelven el inventario exacto de lo que harían (previsualización sin
estado); con `confirmar: true` escriben en un solo CTRL+Z, y con `INSUNITS = 0` solo
`corregir_unidades_bloques` rechaza la escritura (`UNITS_UNKNOWN`); las demás escriben con advertencia (desde 0.8.3). Flujo de limpieza de un plano: contorno con `polilinea` → `borrar_fuera_de` → `purgar` →
`auditar_capas` → `normalizar_capas` (con el mapa que decida el usuario) → `corregir_unidades_bloques`.

## Estructura

| Carpeta | Contenido |
|---|---|
| `plugin/` | Solución .NET (`MCPAutoCAD.Plugin`, `MCPAutoCAD.GeometryCore`, pruebas) y fuente del bundle; ver `plugin/README.md` |
| `server/` | Servidor MCP `mcp_autocad` y pytest con plugin simulado; ver `server/README.md` |
| `standards/` | `lps.json` (estándar de capas y unidades de la oficina; v2 desde el 2026-10-28: 24 capas del 6X12 normalizado + `mapa_normalizacion`; la v1 de 161 capas queda en `historico/`) y `LPS_ROTULO.dwg` (rótulo con 25 atributos; biblioteca de bloques). Solo lectura para el MCP |
| `scripts/` | `build.ps1`, `install-plugin.ps1` (lo ejecuta el usuario), `rpc-call.ps1`, `check-pipe.ps1`, `run-server.ps1` |
| `docs/` | `INSTALACION.md` y checklists en vivo `GATE_FASE0.md` … `GATE_FASE3.md`, `GATE_FASE4A.md`, `GATE_FASEL.md`, `GATE_FASE4B.md` y `GATE_FASE5.md` |
| `tests/fixtures/` | Copias de planos reales para los gates (nunca se modifican los originales) |

## Uso rápido

```powershell
cd C:\LPSAPP\MCPAutoCAD
pwsh -File scripts\build.ps1 -ServerTests        # compila, dotnet test y pytest → === BUILD CORRECTO ===
# con AutoCAD cerrado (lo ejecuta el usuario):
pwsh -File scripts\install-plugin.ps1 -DryRun
pwsh -File scripts\install-plugin.ps1           # bundle en %APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle
```

El bundle instalado contiene `Contents\MCPAutoCAD.Plugin.dll` (`FileVersion` 0.8.0.0), `Contents\lps.json`,
`Contents\biblioteca\` con 5 `.dwg` (`LPS_ROTULO`, `Puerta Dinamica LM`, `PuertaBaño`, `VENTANA01`, `TANQUE 1000 LTS`) y `Contents\plantillas\LPS_METROS.dwt`. En AutoCAD, `MCPESTADO` muestra `MCPAutoCAD 0.8.0 (protocolo 0.8)`. Claude
Code usa `.mcp.json` (servidor `autocad`). El respaldo sin AutoCAD necesita ezdxf (dependencia opcional) y ODA File Converter, configurable con `MCP_AUTOCAD_ODA`, `MCP_AUTOCAD_ODA_TIMEOUT` y `MCP_AUTOCAD_AUDITORIAS`. Detalles y solución de problemas: `docs/INSTALACION.md`.
