# plugin/ — MCPAutoCAD para AutoCAD 2026 (versión 0.7.1, protocolo 0.7)

Cambios 0.7.0 (Fase 4B, protocol.md §5G): `vano`, `union_muros` y `cotas_automaticas` (generadoras con `preview_id`) e `insertar_export` (escritura sin previsualización); `insertar_bloque` admite `propiedades_dinamicas` y devuelve `dinamico`; `normalizar_capas` admite `conservar_aspecto`; la respuesta G6 añade `handles_borrados`; biblioteca con 5 `.dwg`. Manifest: 55 herramientas. Gate en vivo pendiente (`../docs/GATE_FASE4B.md`).

Cambios 0.6.1 (correcciones del gate en vivo de la Fase L, 2026-10-27; protocolo sigue "0.6"): `normalizar_capas` nunca reasigna el viewport n.º 1 de un layout, excluye por defecto los demás VIEWPORT de papel (`incluir_viewports`, con aviso) y admite `espacios` ("modelo" y/o layouts) para limitar el mapa; la previsualización informa lo excluido (protocol.md §5F.3). Lógica pura en `GeometryCore/Clean/LayerMapScope`.

Cambios 0.6.0 (Fase L, limpieza de planos): cuatro herramientas de escritura sobre entidades y símbolos del usuario
(protocol.md §5F: `borrar_fuera_de`, `purgar`, `normalizar_capas`, `corregir_unidades_bloques`) con previsualización
sin estado (sin `confirmar: true` nunca escriben) y reglas comunes L1–L8; `auditar_capas` añade `patron_ajeno` y
`sugerencia_patron`; la advertencia de huérfanas de `MCP-PREVIEW` usa una selección nativa filtrada por capa. Ver
protocol.md ("Cambios 0.5 → 0.6").

Historial 0.5.0 (Fase 4A, geometría paramétrica): once herramientas (protocol.md §5E), `PuntoRef`, previsualización con
`preview_id` y error `INVALID_GEOMETRY`. Historial 0.4.0 (Fase 3, dibujo primitivo): catorce herramientas de escritura (protocol.md §5D), reglas comunes R1–R7,
biblioteca de bloques en `Contents\biblioteca\` y cambios de conducta de `linea` (§5.4). Ver protocol.md
("Cambios 0.3 → 0.4"). Historial: 0.3.1/0.3.2 corrigieron los defectos del gate de Fase 2 (F1–F9).

Plugin .NET que carga AutoCAD y expone el named pipe `\\.\pipe\mcp-autocad-<usuario>-<PID>`
(contrato completo en `../protocol.md`). El manifest (`tools/manifest`) publica **55 herramientas**, en el orden
de protocol.md §4:

- Fase 0: `ping`, `estado`, `listar_capas`, `linea`.
- Fase 1 (lectura, protocol.md §5B): `consultar_entidades`, `propiedades`, `textos`, `bloques`,
  `seleccion_actual`, `xrefs`, `vista_png`, `resumen_archivo`.
- Fase 2 (auditoría, protocol.md §5C): `auditar_capas`, `entidades_en_capa_0`, `auditar_bloques`,
  `areas_por_capa`, `verificar_cuadro_areas`, `duplicados`, `textos_fuera_de_escala`, `auditar_dibujo`
  (lectura) y `marcar_hallazgos`, `limpiar_observaciones` (escritura).
- Fase 3 (dibujo primitivo, protocol.md §5D, todas de escritura): `crear_capa`, `polilinea`, `circulo`, `arco`,
  `texto`, `mtext`, `insertar_bloque`, `cota`, `hatch`, `mover`, `copiar`, `rotar`, `escalar`, `borrar`.
- Fase 4A (geometría paramétrica, protocol.md §5E): `punto_de`, `distancia`, `proyectar` (lectura); `muro`,
  `ejes_rejilla`, `recinto`, `offset`, `array_rectangular`, `array_polar`, `confirmar`, `descartar_preview` (escritura).
- Fase L (limpieza de planos, protocol.md §5F, todas de escritura): `borrar_fuera_de`, `purgar`, `normalizar_capas`,
  `corregir_unidades_bloques`.
- Fase 4B (vanos, encuentros, cotas y ensamblado CYPE, protocol.md §5G, todas de escritura): `vano`, `union_muros`,
  `cotas_automaticas`, `insertar_export`.

**Escritura (33 herramientas):** `linea`, `marcar_hallazgos`, `limpiar_observaciones`, las 14 de la Fase 3, las 8 de
escritura de la Fase 4A, las 4 de la Fase L y las 4 de la Fase 4B, siempre
con **un CTRL+Z por llamada** (`UndoGroup.Run` en contexto de comando + una `Transaction`). Reglas comunes de
protocol.md §5D.0: `INSUNITS = 0` → `UNITS_UNKNOWN` salvo `aceptar_sin_unidades: true` (R2); `capa` por concepto de
`lps.json` (`muros`, `cotas`, `rotulo`…) y solo se crean capas del estándar o `MCP-*` (R3); la geometría fuera de la
extensión del espacio destino se rechaza salvo `forzar: true`, con tope de coste 60 000 entidades / 2 500 ms (R4);
`espacio` nunca cambia la pestaña activa (R5); textos literales (R7). `mover`/`copiar`/`rotar`/`escalar`/`borrar`
sobre entidades fuera de capas `MCP-*` exigen `confirmar: true`. `marcar_hallazgos` y `limpiar_observaciones`
siguen escribiendo **únicamente en capas `MCP-*`**. Las cuatro de la Fase L solo escriben con `confirmar: true`
(sin él devuelven el inventario exacto, sin paso de deshacer) y con `INSUNITS = 0` rechazan la escritura sin escape.

| Proyecto | Target | Qué es |
|---|---|---|
| `MCPAutoCAD.Plugin` | net10.0-windows | Puente del pipe, cola al hilo principal, handlers, comando `MCPESTADO`. Auditoría en `Handlers/Audit/` (reglas `IAuditCheck` por reflexión); dibujo en `Handlers/Draw/` (`DrawWrite`, `DrawLayerFactory`, `PlacementCheck`, `BlockLibrary` y un handler por herramienta); geometría paramétrica en `Handlers/Gen/`; limpieza en `Handlers/Clean/` (`CleanWrite` y un handler por herramienta) |
| `MCPAutoCAD.GeometryCore` | net10.0 | Cálculo puro sin AutoCAD (`Point3`, `LineValidator`, `UnitsTable`); auditoría en `Audit/`; dibujo en `Draw/` (`LayerResolver`, `UnitsGate`, `PlacementBudget`, `TextEscape`, `PolylineValidator`, `Angles`, `BlockInsertScale`, `PlanoNFormat`, `HatchBoundary`, `EntityProtection`, `Transform2D`); geometría paramétrica en `Gen/`; limpieza en `Clean/` (`BoxPolygon`, `ViewportImpact`, `PurgeRules`, `LayerMapValidator`) |
| `MCPAutoCAD.GeometryCore.Tests` | net10.0 | Tests xUnit de GeometryCore (`Audit/`, `Draw/`, `Gen/` y `Clean/`) |

## Compilar

```powershell
cd C:\LPSAPP\MCPAutoCAD
pwsh -File scripts\build.ps1 -ServerTests
```

(equivale a `dotnet restore`/`build -c Release`/`test` de `plugin\MCPAutoCAD.sln` más el pytest del servidor). La DLL
queda en `MCPAutoCAD.Plugin\bin\Release\net10.0-windows\MCPAutoCAD.Plugin.dll` (versión 0.7.1, `FileVersion`
0.7.1.0), junto a `MCPAutoCAD.GeometryCore.dll`, `lps.json` (copia de `../standards/lps.json`) y
`biblioteca\` con los 5 `.dwg` de `../standards/` (`LPS_ROTULO`, `Puerta Dinamica LM`, `PuertaBaño`, `VENTANA01`, `TANQUE 1000 LTS`), copiados por el `.csproj`. El plugin lee
`lps.json` completo al cargar (o la ruta de `MCP_AUTOCAD_LPS`): unidades (§2.2), capas (§2.6) y, desde 0.4, la clave
opcional `biblioteca_bloques` (§5D.5); nunca lo modifica. Las DLL de AutoCAD (`acmgd`, `acdbmgd`, `accoremgd`) se
referencian desde `D:\Program Files\Autodesk\AutoCAD 2026\` con `Private=false` y **no** se copian.

**AutoCAD 2026 sobre .NET 10.** La instalación actual (25.1.179) corre en .NET 10 y sus DLL referencian
`System.Runtime 10.0`, por eso el plugin apunta a `net10.0-windows` y requiere el SDK 10 x64.

## Instalar

No lo hace la compilación. El usuario ejecuta `scripts/install-plugin.ps1` (con AutoCAD cerrado), que copia
`bundle/MCPAutoCAD.bundle/PackageContents.xml` y la carpeta Release a
`%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\`, vuelve a copiar `standards/lps.json` a `Contents\lps.json`
y los 5 `.dwg` de `standards/` a `Contents\biblioteca\` (ver `bundle/README.md`).

## Constantes decididas en los gates

- `Bridge/UndoGroup.UseCommandContextUndo` = `true` (gate de Fase 0): toda escritura en contexto de comando entre
  `_.UNDO _BE` y `_.UNDO _E`; con `false` AutoCAD no agrupa el deshacer.
- `SeleccionActualHandler.UseCommandContext` = `false` (gate de Fase 1, prueba 6).
- `VistaPngHandler.DefaultMechanism` = `"captura"` sin `bbox`; con `bbox` siempre `plot`, envuelto en
  `UndoRecordingSuspension` (gate de Fase 2, F7) dentro del grupo neutro `UndoGroup.RunNeutral`
  (`_.UNDO _BE` → plot → `_.UNDO _E` → `_.U`, gate de Fase 3) y solo sobre el espacio activo (F5).
- `GeometryCore.Draw.PlacementBudget`: 60 000 entidades / 2 500 ms (tope de coste de R4, Fase 3).

Cambiar cualquiera exige recompilar con `scripts/build.ps1` e instalar de nuevo con AutoCAD cerrado.

## Gates en vivo

- Fases 0, 1 y 2: aprobados (`../docs/GATE_FASE0.md`, `GATE_FASE1.md`, `GATE_FASE2.md`).
- Fase 3: aprobado (`../docs/GATE_FASE3.md`). Fase 4A: aprobado (`../docs/GATE_FASE4A.md`).
- Fase L: aprobado con un hallazgo, corregido en 0.6.1 (`../docs/GATE_FASEL.md`).
- Fase 4B: **pendiente** (`../docs/GATE_FASE4B.md`, sobre `tests/fixtures/fase4b_trabajo.dwg`).

Regla para cualquier handler de lectura (protocol.md §3.9): ningún paso de deshacer, ningún objeto residente
modificado, `CLAYER`/`CTAB`/`TILEMODE`/vista intactos. Regla para cualquier handler de escritura (§3.11, §5D.0):
flujo `Parse` → `RequireDocument` → `DrawWrite.CheckUnits` → `PlacementCheck.Run` → `UndoGroup.Run` con una
`Transaction` (`SpaceResolver.Resolve`, `DrawLayerFactory.Ensure`, `DrawWrite.ApplyByLayer` + `Append`,
`DrawWrite.Response`).

## Diagnóstico

- `MCPESTADO` (en la línea de comandos de AutoCAD): `MCPAutoCAD 0.7.1 (protocolo 0.7)`, nombre del pipe, conexiones
  activas, peticiones atendidas, última petición, valor de `UseCommandContextUndo` y mecanismo de despacho.
- Log: `%LOCALAPPDATA%\MCPAutoCAD\plugin.log` (rotación a 5 MB → `plugin.log.1`). Una línea por petición con
  método, id, duración y resultado o código de error.
