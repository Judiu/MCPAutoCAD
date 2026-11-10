# Gate de la Fase 5 (auditoría de archivos cerrados, lote, respaldo sin AutoCAD y plantilla): checklist en vivo

Este documento comprueba en AutoCAD real la Fase 5 (`PLAN.md` §5, "Fase 5"; contrato en `protocol.md` §5H): `auditar_archivo` (plugin), `auditar_carpeta`, `estado_auditoria` y `cancelar_auditoria` (servidor), el respaldo ODA + ezdxf y la plantilla `standards/LPS_METROS.dwt`. Plugin y servidor **0.8.0**, protocolo **0.8**.

- Quién ejecuta las pruebas: **el usuario**, en su equipo, con AutoCAD 2026 con interfaz y Claude Code en otra ventana. Ningún agente ejecuta este checklist ni instala el plugin.
- Dibujos: `tests\fixtures\casa6x12_limpio.dwg` y `copia_real.dwg` son de solo lectura y no se abren. Todo se hace sobre copias en **`tests\tmp_fase5\`**, que la limpieza final borra. **`O:\` es solo lectura:** se lee y se copia desde allí, nunca se abre un plano allí ni se escribe nada.
- Consola: PowerShell 7 (`pwsh`), con el directorio actual en `C:\LPSAPP\MCPAutoCAD`, y **la misma consola** durante todo el gate.
- `auditar_carpeta`, `estado_auditoria` y `cancelar_auditoria` son herramientas **del servidor**: no pasan por `scripts\rpc-call.ps1`. Esas pruebas se piden en **Claude Code**, con los parámetros indicados.
- **INSUNITS es lo que el archivo declara, no su unidad real.** Un valor en metros o no en metros en este gate se lee siempre como "declarado".
- Los `data.detalle` se comparan **literalmente** con protocol.md §5H.5 y §6.
- Donde dice **"se fija en el gate"** no hay cifra medida antes: anote la que salga. Los esperados numéricos salen de protocol.md §5H.0.5 y §5H.7, medidos con ODA + ezdxf sobre copias el 2026-10-31. Lo medido con el plugin solo existe después de este gate.
- **Lo que no se pudo verificar sin AutoCAD con interfaz** y este gate fija: `_NEW` con la plantilla (las comprobaciones del agente abren una copia con accoreconsole), la lectura en segundo plano con el usuario trabajando, el comportamiento de `ReadDwgFile` con un DWG truncado (no debe abrir ningún diálogo) y las cifras reales de `auditar_archivo` con el plugin.
- **`es_export_cype` y `es_lamina` se miden con los dos orígenes:** con `origen: oda` los mide `lote.medir_auxiliares` sobre el documento ezdxf; con `origen: plugin`, el propio `auditar_archivo` (campos `_cype`, `_lamina`, `_bloques` y `_mediana_texto`, clase `FileAuxiliaries` de `AuditarArchivoHandler.cs`), y `lote.rellenar_fila` los usa sea cual sea el origen. Solo salen `null` (sin medir, no `false`) si la medición auxiliar no cabe en su plazo de 4 s o falla, y entonces `estado_auditoria` lo advierte. Por eso la **prueba 3** (plugin) y la **prueba 6** (ODA) esperan lo mismo (`FILE24.DWG`: `true` y `true`, A3 a 1:50), y la 6 es la comparación de paridad. Con el plugin esas cifras salen de leer el código, no de una medición: las confirma este gate.
- **`unidades_inconsistentes` compara INSUNITS con MEASUREMENT (§5H.2):** no significa "hay hallazgo `unidades`". De la muestra solo `ARQ CLIENTE_B (1).dwg` sale `true`.
- **`n_entidades_capa_0` de una fila cuenta modelo + layouts:** suma los `datos.n` de los hallazgos `entidades_en_capa_0`, y esa regla cubre todos los espacios (§5C.8). Por eso el 6X12 da **42** (modelo 38, `Layout1` 3, `Layout2` 1).
- **El orden importa:** cada prueba indica de qué estado parte.

---

## 0. Preparación (una sola vez)

1. **Compilar y ejecutar todas las pruebas** (AutoCAD puede estar abierto):
   ```powershell
   cd C:\LPSAPP\MCPAutoCAD
   pwsh -File scripts\build.ps1 -ServerTests
   ```
   Debe terminar con `=== BUILD CORRECTO ===` y `0 Warning(s)`, con **al menos 1 770** pruebas .NET y **al menos 1 633** pytest (.NET medido con `build.ps1 -ServerTests` el 2026-11-01; pytest medido con `python -m pytest tests` el 2026-11-02), y 0 con error. Una cifra menor es un retroceso: pare y averigüe qué pruebas se perdieron. Anote las cifras en la tabla.
2. **Cerrar todas las instancias de AutoCAD** e **instalar el bundle**:
   ```powershell
   Get-Process acad -ErrorAction SilentlyContinue   # no debe listar nada
   pwsh -File scripts\install-plugin.ps1 -DryRun
   pwsh -File scripts\install-plugin.ps1
   $bundle = "$env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents"
   (Get-Item "$bundle\MCPAutoCAD.Plugin.dll").VersionInfo.FileVersion   # 0.8.0.0
   Get-ChildItem "$bundle\plantillas" | Select-Object Name, Length       # LPS_METROS.dwt
   (Get-FileHash "$bundle\plantillas\LPS_METROS.dwt").Hash -eq (Get-FileHash standards\LPS_METROS.dwt).Hash   # True
   ```
3. **ezdxf en el Python del servidor** (dependencia opcional del respaldo): `python -c "import ezdxf; print(ezdxf.__version__)"` debe imprimir 1.4 o superior. Compruebe también que existe ODA: `Test-Path 'C:\Program Files\ODA\ODAFileConverter 27.1.0\ODAFileConverter.exe'`.
4. **Abrir AutoCAD 2026** (sin abrir ningún plano) y escribir **`MCPESTADO`**: debe mostrar `MCPAutoCAD 0.8.0 (protocolo 0.8)` y el pipe `\\.\pipe\mcp-autocad-<usuario>-<PID>`.
5. **Funciones auxiliares y manifest.** Defina en la consola:
   ```powershell
   function Invoke-Rpc([string]$Method, [string]$Params = '{}') {
       # 75 s: auditar_archivo espera hasta 60 s en el hilo principal (protocol.md §3, regla 7).
       (& .\scripts\rpc-call.ps1 -Method $Method -ParamsJson $Params -AsObject -TimeoutSec 75).Respuesta | ConvertFrom-Json
   }
   function Err($r) { "{0} | {1} | {2}" -f $r.error.data.code, $r.error.message, $r.error.data.detalle }
   function Reglas($r) {
       # por_regla como tabla comparable.
       $r.result.resumen.por_regla.PSObject.Properties | ForEach-Object {
           [pscustomobject]@{ regla = $_.Name; n = $_.Value.n; alta = $_.Value.alta; media = $_.Value.media; baja = $_.Value.baja; completo = $_.Value.completo }
       }
   }
   function Huella([string]$Carpeta) {
       # Nombre, fecha y tamaño de todo lo que hay en la carpeta, incluidos ocultos.
       Get-ChildItem -LiteralPath $Carpeta -Recurse -File -Force |
           Select-Object FullName, @{n='Utc';e={$_.LastWriteTimeUtc.ToString('o')}}, Length | Sort-Object FullName
   }
   (Invoke-Rpc 'tools/manifest').result.protocol_version          # 0.8
   (Invoke-Rpc 'tools/manifest').result.tools.name -join ', '
   ((Invoke-Rpc 'tools/manifest').result.tools | Where-Object escritura).Count
   ```
   La segunda línea debe imprimir **exactamente** (56 herramientas, orden de protocol.md §4):
   `ping, estado, listar_capas, linea, consultar_entidades, propiedades, textos, bloques, seleccion_actual, xrefs, vista_png, resumen_archivo, auditar_capas, entidades_en_capa_0, auditar_bloques, areas_por_capa, verificar_cuadro_areas, duplicados, textos_fuera_de_escala, auditar_dibujo, marcar_hallazgos, limpiar_observaciones, crear_capa, polilinea, circulo, arco, texto, mtext, insertar_bloque, cota, hatch, mover, copiar, rotar, escalar, borrar, punto_de, distancia, proyectar, muro, ejes_rejilla, recinto, offset, array_rectangular, array_polar, confirmar, descartar_preview, borrar_fuera_de, purgar, normalizar_capas, corregir_unidades_bloques, vano, union_muros, cotas_automaticas, insertar_export, auditar_archivo`
   La tercera debe dar **33**: `auditar_archivo` es de lectura. Si `plugin.log` contiene `Herramienta '<x>' del contrato no tiene handler`, pare.
6. **Claude Code**: reinicie Claude Code en `C:\LPSAPP\MCPAutoCAD`. Con `/mcp`, el servidor `autocad` debe estar conectado con **61 herramientas**: las 56 del manifest + `instancias`, `seleccionar_instancia`, `auditar_carpeta`, `estado_auditoria` y `cancelar_auditoria`.
7. **Copias de trabajo y muestra real.** Las copias de `O:\` las hace usted; `O:\` solo se lee:
   ```powershell
   New-Item -ItemType Directory -Force tests\tmp_fase5\origen, tests\tmp_fase5\muestra, tests\tmp_fase5\muestra5 | Out-Null
   Copy-Item tests\fixtures\casa6x12_limpio.dwg tests\tmp_fase5\origen\casa6x12_copia.dwg
   Copy-Item tests\fixtures\casa6x12_limpio.dwg tests\tmp_fase5\fase5_trabajo.dwg

   $p = 'O:\Empresa\Proyectos'
   $muestra = @(
     "$p\2023\ARQUITECTO_D\CLIENTE_D\CASA\AUTOCAD\FILE24.DWG"
     "$p\2025\MUNICIPIO\CLIENTE_A\PLANTILLA.dwg"
     "$p\2026\ARQUITECTO_B\cliente_b\ARQUITECTURA\ARQ CLIENTE_B (1).dwg"
     "$p\2026\ARQUITECTO_C\CLIENTE_C\ARQUITECTURA\ARQ CLIENTE_C.dwg"
   )
   $o0 = $muestra | ForEach-Object { Get-Item -LiteralPath $_ } | Select-Object FullName, LastWriteTimeUtc, Length
   $muestra | ForEach-Object { Copy-Item -LiteralPath $_ -Destination tests\tmp_fase5\muestra\ }
   Copy-Item tests\tmp_fase5\muestra\* tests\tmp_fase5\muestra5\
   Get-ChildItem tests\tmp_fase5\muestra | Select-Object Name, Length      # 4 archivos
   ```
   **Esperado:** cuatro archivos: `FILE24.DWG` (≈ 18 KB), `PLANTILLA.dwg` (≈ 33 KB), `ARQ CLIENTE_B (1).dwg` (≈ 223 KB), `ARQ CLIENTE_C.dwg` (≈ 463 KB). Si alguna ruta ya no existe, sustitúyala por otro plano equivalente y anótelo.

8. **Plantilla comprobada antes del gate** (resultado real del 2026-10-31, sobre `standards\LPS_METROS.dwt`, con `tools\crear_plantilla_metros.py` y `tools\verificar_plantilla_metros.py --accoreconsole`; no hay que repetirlo):

   Plantilla verificada: 13 701 bytes, sha256 `3f3144e7880a746fb9bb49165996f816c0928605628738f58eafbaa1a3afedb1`. Reproducible con:
   ```powershell
   python -I tools\crear_plantilla_metros.py standards\lps.json tests\fixtures\casa6x12_limpio.dwg standards\LPS_METROS.dwt
   python -I tools\verificar_plantilla_metros.py standards\LPS_METROS.dwt --accoreconsole
   ```
   Cada generación cambia los bytes y el sha256 (ezdxf y ODA estampan fechas y GUID), no el contenido.

   Además, en una comprobación puntual con accoreconsole sobre una copia de la plantilla abierta como `.dwt`: `AUDIT` con 0 errores y una cota `_DIMLINEAR` de 3,25 con estilo `COTA75`, texto `3.25` y `INSERT C` a escala 0,045. La misma comprobación demostró que el verificador falla: una plantilla anterior con anulaciones dio `FALLO | accoreconsole: anulaciones de COTA75 | leído ['DIMADEC', 'DIMAZIN', 'DIMFXL', 'DIMJOGANG']` y código 1.

   El verificador lanza accoreconsole con `/isolate` y con `TEMP`/`TMP` en su carpeta de trabajo: no toca el perfil del usuario ni deja archivos `accc*` en `%TEMP%` (17 antes y 17 después de esta ejecución).

   `install-plugin.ps1` solo se probó con `-DryRun` (destino real y `-Destination` temporal; ambos muestran la copia de `LPS_METROS.dwt`). La ejecución real con `-Destination` terminó con `ERROR: AutoCAD está en ejecución (PID: 78576)` y código 1, sin crear nada, porque el AutoCAD del usuario estaba abierto: no se cerró ni se esquivó esa comprobación. La copia real se comprueba por primera vez en la instalación de este gate.

   Salida del verificador (fecha del equipo: 2026-10-31):
   ```text
   OK    | cabecera $INSUNITS                    | esperado 6 | leído 6
   OK    | cabecera $MEASUREMENT                 | esperado 1 | leído 1
   OK    | cabecera $LUNITS                      | esperado 2 | leído 2
   OK    | cabecera $LUPREC                      | esperado 2 | leído 2
   OK    | cabecera $AUNITS                      | esperado 0 | leído 0
   OK    | cabecera $AUPREC                      | esperado 0 | leído 0
   OK    | cabecera $LTSCALE                     | esperado 0.0003 | leído 0.0003
   OK    | cabecera $PSLTSCALE                   | esperado 1 | leído 1
   OK    | cabecera $CELTSCALE                   | esperado 1.0 | leído 1.0
   OK    | cabecera $CLAYER                      | esperado '0' | leído '0'
   OK    | cabecera $DIMSTYLE                    | esperado 'COTA75' | leído 'COTA75'
   OK    | cabecera $TEXTSTYLE                   | esperado '75' | leído '75'
   OK    | cabecera $TEXTSIZE                    | esperado 0.09 | leído 0.09
   OK    | cabecera $LWDISPLAY                   | esperado 0 | leído 0
   OK    | capas: las de lps.json y ninguna más  | esperado ['0', '6. ESTRUCTURA', 'AC', 'Achurados', 'Antepecho', 'Cotas', 'DEFPOINTS', 'EJES', 'ESCALERA', 'Filos', 'MAMPOSTERIA', 'MUR BAAAJO', 'MUROS', 'PUERTAS', 'Proyeccion', 'Rotulo', 'Sanitarios', 'TEXTOS', 'VIGA', 'columnas', 'm fijos', 'muebles fijos', 'niveles', 'ventanas'] | leído ['0', '6. ESTRUCTURA', 'AC', 'Achurados', 'Antepecho', 'Cotas', 'DEFPOINTS', 'EJES', 'ESCALERA', 'Filos', 'MAMPOSTERIA', 'MUR BAAAJO', 'MUROS', 'PUERTAS', 'Proyeccion', 'Rotulo', 'Sanitarios', 'TEXTOS', 'VIGA', 'columnas', 'm fijos', 'muebles fijos', 'niveles', 'ventanas']
   OK    | capa 0                                | esperado (7, 'Continuous', -3, 1) | leído (7, 'Continuous', -3, 1)
   OK    | capa 6. ESTRUCTURA                    | esperado (5, 'Continuous', 70, 1) | leído (5, 'Continuous', 70, 1)
   OK    | capa AC                               | esperado (7, 'Continuous', -3, 1) | leído (7, 'Continuous', -3, 1)
   OK    | capa Achurados                        | esperado (151, 'Continuous', 9, 1) | leído (151, 'Continuous', 9, 1)
   OK    | capa Antepecho                        | esperado (201, 'Continuous', 20, 1) | leído (201, 'Continuous', 20, 1)
   OK    | capa columnas                         | esperado (1, 'Continuous', -3, 1) | leído (1, 'Continuous', -3, 1)
   OK    | capa Cotas                            | esperado (8, 'Continuous', 13, 1) | leído (8, 'Continuous', 13, 1)
   OK    | capa DEFPOINTS                        | esperado (7, 'Continuous', -3, 0) | leído (7, 'Continuous', -3, 0)
   OK    | capa EJES                             | esperado (1, 'CENTER', 5, 1) | leído (1, 'CENTER', 5, 1)
   OK    | capa Filos                            | esperado (3, 'Continuous', 15, 1) | leído (3, 'Continuous', 15, 1)
   OK    | capa m fijos                          | esperado (2, 'Continuous', 0, 1) | leído (2, 'Continuous', 0, 1)
   OK    | capa muebles fijos                    | esperado (2, 'Continuous', -3, 1) | leído (2, 'Continuous', -3, 1)
   OK    | capa MUR BAAAJO                       | esperado (241, 'Continuous', 20, 1) | leído (241, 'Continuous', 20, 1)
   OK    | capa MUROS                            | esperado (155, 'Continuous', 50, 1) | leído (155, 'Continuous', 50, 1)
   OK    | capa Proyeccion                       | esperado (220, 'CONTINUA', 5, 1) | leído (220, 'CONTINUA', 5, 1)
   OK    | capa PUERTAS                          | esperado (9, 'Continuous', -3, 1) | leído (9, 'Continuous', -3, 1)
   OK    | capa Rotulo                           | esperado (150, 'Continuous', 80, 1) | leído (150, 'Continuous', 80, 1)
   OK    | capa Sanitarios                       | esperado (6, 'Continuous', 5, 1) | leído (6, 'Continuous', 5, 1)
   OK    | capa TEXTOS                           | esperado (152, 'Continuous', 15, 1) | leído (152, 'Continuous', 15, 1)
   OK    | capa ventanas                         | esperado (6, 'Continuous', 9, 1) | leído (6, 'Continuous', 9, 1)
   OK    | capa VIGA                             | esperado (2, 'Continuous', -3, 1) | leído (2, 'Continuous', -3, 1)
   OK    | capa MAMPOSTERIA                      | esperado (30, 'Continuous', -3, 1) | leído (30, 'Continuous', -3, 1)
   OK    | capa niveles                          | esperado (8, 'Continuous', -3, 1) | leído (8, 'Continuous', -3, 1)
   OK    | capa ESCALERA                         | esperado (6, 'Continuous', 5, 1) | leído (6, 'Continuous', 5, 1)
   OK    | tipos de línea: solo los de §5H.4     | esperado ['CENTER', 'CONTINUA', 'Continuous'] | leído ['CENTER', 'CONTINUA', 'Continuous']
   OK    | tipo de línea Continuous              | esperado ('Solid line', 0.0, ()) | leído ('Solid line', 0.0, ())
   OK    | tipo de línea CENTER                  | esperado ('Center ____ _ ____ _ ____ _ ____ _ ____ _ ____', 50.8, (31.75, -6.35, 6.35, -6.35)) | leído ('Center ____ _ ____ _ ____ _ ____ _ ____ _ ____', 50.8, (31.75, -6.35, 6.35, -6.35))
   OK    | tipo de línea CONTINUA                | esperado ('Linea ininterrumpida', 0.0, ()) | leído ('Linea ininterrumpida', 0.0, ())
   OK    | estilos de texto: solo los de §5H.4   | esperado ['75', 'Standard', 'cota75'] | leído ['75', 'Standard', 'cota75']
   OK    | estilo de texto Standard              | esperado {'font': 'arial.ttf', 'height': 0.0} | leído {'font': 'arial.ttf', 'height': 0.0}
   OK    | estilo de texto 75                    | esperado {'font': 'micross.ttf', 'height': 0.09, 'width': 1.0, 'oblique': 0.0} | leído {'font': 'micross.ttf', 'height': 0.09, 'width': 1.0, 'oblique': 0.0}
   OK    | estilo de texto cota75                | esperado {'font': 'simplex.shx', 'height': 0.09, 'width': 1.0} | leído {'font': 'simplex.shx', 'height': 0.09, 'width': 1.0}
   OK    | estilos de cota: Standard y COTA75    | esperado ['COTA75', 'Standard'] | leído ['COTA75', 'Standard']
   OK    | COTA75 DIMTXT                         | esperado 0.09 | leído 0.09
   OK    | cabecera $DIMTXT                      | esperado 0.09 | leído 0.09
   OK    | COTA75 DIMASZ                         | esperado 0.045 | leído 0.045
   OK    | cabecera $DIMASZ                      | esperado 0.045 | leído 0.045
   OK    | COTA75 DIMEXO                         | esperado 0.1 | leído 0.1
   OK    | cabecera $DIMEXO                      | esperado 0.1 | leído 0.1
   OK    | COTA75 DIMEXE                         | esperado 0.1 | leído 0.1
   OK    | cabecera $DIMEXE                      | esperado 0.1 | leído 0.1
   OK    | COTA75 DIMDLI                         | esperado 0.0 | leído 0.0
   OK    | cabecera $DIMDLI                      | esperado 0.0 | leído 0.0
   OK    | COTA75 DIMGAP                         | esperado 0.015 | leído 0.015
   OK    | cabecera $DIMGAP                      | esperado 0.015 | leído 0.015
   OK    | COTA75 DIMDEC                         | esperado 2 | leído 2
   OK    | cabecera $DIMDEC                      | esperado 2 | leído 2
   OK    | COTA75 DIMTDEC                        | esperado 2 | leído 2
   OK    | cabecera $DIMTDEC                     | esperado 2 | leído 2
   OK    | COTA75 DIMZIN                         | esperado 4 | leído 4
   OK    | cabecera $DIMZIN                      | esperado 4 | leído 4
   OK    | COTA75 DIMTZIN                        | esperado 8 | leído 8
   OK    | cabecera $DIMTZIN                     | esperado 8 | leído 8
   OK    | COTA75 DIMTAD                         | esperado 1 | leído 1
   OK    | cabecera $DIMTAD                      | esperado 1 | leído 1
   OK    | COTA75 DIMTIH                         | esperado 0 | leído 0
   OK    | cabecera $DIMTIH                      | esperado 0 | leído 0
   OK    | COTA75 DIMTOH                         | esperado 0 | leído 0
   OK    | cabecera $DIMTOH                      | esperado 0 | leído 0
   OK    | COTA75 DIMSE1                         | esperado 1 | leído 1
   OK    | cabecera $DIMSE1                      | esperado 1 | leído 1
   OK    | COTA75 DIMSE2                         | esperado 1 | leído 1
   OK    | cabecera $DIMSE2                      | esperado 1 | leído 1
   OK    | COTA75 DIMTIX                         | esperado 1 | leído 1
   OK    | cabecera $DIMTIX                      | esperado 1 | leído 1
   OK    | COTA75 DIMTOFL                        | esperado 1 | leído 1
   OK    | cabecera $DIMTOFL                     | esperado 1 | leído 1
   OK    | COTA75 DIMTMOVE                       | esperado 2 | leído 2
   OK    | cabecera $DIMTMOVE                    | esperado 2 | leído 2
   OK    | COTA75 DIMATFIT                       | esperado 0 | leído 0
   OK    | cabecera $DIMATFIT                    | esperado 0 | leído 0
   OK    | COTA75 DIMTOLJ                        | esperado 0 | leído 0
   OK    | cabecera $DIMTOLJ                     | esperado 0 | leído 0
   OK    | COTA75 DIMCEN                         | esperado 0.2 | leído 0.2
   OK    | cabecera $DIMCEN                      | esperado 0.2 | leído 0.2
   OK    | COTA75 DIMCLRD                        | esperado 256 | leído 256
   OK    | cabecera $DIMCLRD                     | esperado 256 | leído 256
   OK    | COTA75 DIMCLRE                        | esperado 8 | leído 8
   OK    | cabecera $DIMCLRE                     | esperado 8 | leído 8
   OK    | COTA75 DIMCLRT                        | esperado 256 | leído 256
   OK    | cabecera $DIMCLRT                     | esperado 256 | leído 256
   OK    | COTA75 DIMLWD                         | esperado -1 | leído -1
   OK    | cabecera $DIMLWD                      | esperado -1 | leído -1
   OK    | COTA75 DIMALTF                        | esperado 0.03937007874016 | leído 0.03937007874016
   OK    | cabecera $DIMALTF                     | esperado 0.03937007874016 | leído 0.03937007874016
   OK    | COTA75 DIMALTD                        | esperado 3 | leído 3
   OK    | cabecera $DIMALTD                     | esperado 3 | leído 3
   OK    | COTA75 DIMALTTD                       | esperado 3 | leído 3
   OK    | cabecera $DIMALTTD                    | esperado 3 | leído 3
   OK    | COTA75 DIMBLK                         | esperado 'C' | leído 'C'
   OK    | cabecera $DIMBLK                      | esperado 'C' | leído 'C'
   OK    | COTA75 DIMBLK1                        | esperado '' | leído ''
   OK    | cabecera $DIMBLK1                     | esperado '' | leído ''
   OK    | COTA75 DIMBLK2                        | esperado '' | leído ''
   OK    | cabecera $DIMBLK2                     | esperado '' | leído ''
   OK    | COTA75 DIMLDRBLK                      | esperado '' | leído ''
   OK    | cabecera $DIMLDRBLK                   | esperado '' | leído ''
   OK    | COTA75 DIMTXSTY                       | esperado 'cota75' | leído 'cota75'
   OK    | cabecera $DIMTXSTY                    | esperado 'cota75' | leído 'cota75'
   OK    | cabecera $DIMDSEP (fuera de la tabla) | esperado 46 | leído 46
   OK    | cabecera $DIMADEC (fuera de la tabla) | esperado 0 | leído 0
   OK    | cabecera $DIMAZIN (fuera de la tabla) | esperado 0 | leído 0
   OK    | cabecera $DIMFXL (fuera de la tabla)  | esperado 1.0 | leído 1.0
   OK    | bloques: solo C y los espacios        | esperado ['*Model_Space', '*Paper_Space', 'C'] | leído ['*Model_Space', '*Paper_Space', 'C']
   OK    | bloque C: entidades                   | esperado {'CIRCLE': 1, 'LINE': 3, 'LWPOLYLINE': 1} | leído {'CIRCLE': 1, 'LINE': 3, 'LWPOLYLINE': 1}
   OK    | bloque C: base                        | esperado (0.0, 0.0, 0.0) | leído (0.0, 0.0, 0.0)
   OK    | bloque C: capa de sus entidades       | esperado ['0'] | leído ['0']
   OK    | bloque C: anchura de la polilínea     | esperado [0.4] | leído [0.4]
   OK    | bloque C: metadato de unidades        | esperado 0 | leído 0
   OK    | layouts                               | esperado ['Layout1', 'Model'] | leído ['Layout1', 'Model']
   OK    | entidades en Layout1                  | esperado 0 | leído 0
   OK    | entidades en Model                    | esperado 0 | leído 0
   OK    | accoreconsole: código de salida       | esperado 0 | leído 0
   OK    | accoreconsole: llegó a QUIT           | esperado '_.QUIT en el log' | leído 'sí'
   OK    | accoreconsole _.INSUNITS              | esperado '6' | leído '6'
   OK    | accoreconsole _.MEASUREMENT           | esperado '1' | leído '1'
   OK    | accoreconsole _.LTSCALE               | esperado '0.0003' | leído '0.0003'
   OK    | accoreconsole _.TEXTSTYLE             | esperado '75' | leído '75'
   OK    | accoreconsole _.CLAYER                | esperado '0' | leído '0'
   OK    | accoreconsole DIMSTYLE                | esperado 'COTA75' | leído 'COTA75'
   OK    | accoreconsole: anulaciones de COTA75  | esperado [] | leído []
   OK    | accoreconsole DIMTXT                  | esperado '0.09' | leído '0.09'
   OK    | accoreconsole DIMASZ                  | esperado '0.05' | leído '0.05'
   OK    | accoreconsole DIMEXO                  | esperado '0.10' | leído '0.10'
   OK    | accoreconsole DIMEXE                  | esperado '0.10' | leído '0.10'
   OK    | accoreconsole DIMDLI                  | esperado '0.00' | leído '0.00'
   OK    | accoreconsole DIMGAP                  | esperado '0.02' | leído '0.02'
   OK    | accoreconsole DIMDEC                  | esperado '2' | leído '2'
   OK    | accoreconsole DIMTDEC                 | esperado '2' | leído '2'
   OK    | accoreconsole DIMZIN                  | esperado '4' | leído '4'
   OK    | accoreconsole DIMTZIN                 | esperado '8' | leído '8'
   OK    | accoreconsole DIMTAD                  | esperado '1' | leído '1'
   OK    | accoreconsole DIMTMOVE                | esperado '2' | leído '2'
   OK    | accoreconsole DIMATFIT                | esperado '0' | leído '0'
   OK    | accoreconsole DIMCEN                  | esperado '0.20' | leído '0.20'
   OK    | accoreconsole DIMCLRE                 | esperado '8' | leído '8'
   OK    | accoreconsole DIMLWD                  | esperado '-1' | leído '-1'
   OK    | accoreconsole DIMBLK                  | esperado 'C' | leído 'C'
   OK    | accoreconsole DIMTXSTY                | esperado 'cota75' | leído 'cota75'
   OK    | accoreconsole DIMSCALE                | esperado '1.00' | leído '1.00'
   OK    | accoreconsole DIMDSEP                 | esperado '.' | leído '.'
   OK    | accoreconsole DIMADEC                 | esperado '0' | leído '0'
   OK    | accoreconsole DIMAZIN                 | esperado '0' | leído '0'
   OK    | accoreconsole: capas                  | esperado ['0', '6. ESTRUCTURA', 'AC', 'Achurados', 'Antepecho', 'columnas', 'Cotas', 'DEFPOINTS', 'EJES', 'ESCALERA', 'Filos', 'm fijos', 'MAMPOSTERIA', 'muebles fijos', 'MUR BAAAJO', 'MUROS', 'niveles', 'Proyeccion', 'PUERTAS', 'Rotulo', 'Sanitarios', 'TEXTOS', 'ventanas', 'VIGA'] | leído ['0', '6. ESTRUCTURA', 'AC', 'Achurados', 'Antepecho', 'columnas', 'Cotas', 'DEFPOINTS', 'EJES', 'ESCALERA', 'Filos', 'm fijos', 'MAMPOSTERIA', 'muebles fijos', 'MUR BAAAJO', 'MUROS', 'niveles', 'Proyeccion', 'PUERTAS', 'Rotulo', 'Sanitarios', 'TEXTOS', 'ventanas', 'VIGA']
   OK    | accoreconsole capa 0                  | esperado (7, 'Continuous', -3) | leído (7, 'Continuous', -3)
   OK    | accoreconsole capa 6. ESTRUCTURA      | esperado (5, 'Continuous', 70) | leído (5, 'Continuous', 70)
   OK    | accoreconsole capa AC                 | esperado (7, 'Continuous', -3) | leído (7, 'Continuous', -3)
   OK    | accoreconsole capa Achurados          | esperado (151, 'Continuous', 9) | leído (151, 'Continuous', 9)
   OK    | accoreconsole capa Antepecho          | esperado (201, 'Continuous', 20) | leído (201, 'Continuous', 20)
   OK    | accoreconsole capa columnas           | esperado (1, 'Continuous', -3) | leído (1, 'Continuous', -3)
   OK    | accoreconsole capa Cotas              | esperado (8, 'Continuous', 13) | leído (8, 'Continuous', 13)
   OK    | accoreconsole capa DEFPOINTS          | esperado (7, 'Continuous', -3) | leído (7, 'Continuous', -3)
   OK    | accoreconsole capa EJES               | esperado (1, 'CENTER', 5) | leído (1, 'CENTER', 5)
   OK    | accoreconsole capa Filos              | esperado (3, 'Continuous', 15) | leído (3, 'Continuous', 15)
   OK    | accoreconsole capa m fijos            | esperado (2, 'Continuous', 0) | leído (2, 'Continuous', 0)
   OK    | accoreconsole capa muebles fijos      | esperado (2, 'Continuous', -3) | leído (2, 'Continuous', -3)
   OK    | accoreconsole capa MUR BAAAJO         | esperado (241, 'Continuous', 20) | leído (241, 'Continuous', 20)
   OK    | accoreconsole capa MUROS              | esperado (155, 'Continuous', 50) | leído (155, 'Continuous', 50)
   OK    | accoreconsole capa Proyeccion         | esperado (220, 'CONTINUA', 5) | leído (220, 'CONTINUA', 5)
   OK    | accoreconsole capa PUERTAS            | esperado (9, 'Continuous', -3) | leído (9, 'Continuous', -3)
   OK    | accoreconsole capa Rotulo             | esperado (150, 'Continuous', 80) | leído (150, 'Continuous', 80)
   OK    | accoreconsole capa Sanitarios         | esperado (6, 'Continuous', 5) | leído (6, 'Continuous', 5)
   OK    | accoreconsole capa TEXTOS             | esperado (152, 'Continuous', 15) | leído (152, 'Continuous', 15)
   OK    | accoreconsole capa ventanas           | esperado (6, 'Continuous', 9) | leído (6, 'Continuous', 9)
   OK    | accoreconsole capa VIGA               | esperado (2, 'Continuous', -3) | leído (2, 'Continuous', -3)
   OK    | accoreconsole capa MAMPOSTERIA        | esperado (30, 'Continuous', -3) | leído (30, 'Continuous', -3)
   OK    | accoreconsole capa niveles            | esperado (8, 'Continuous', -3) | leído (8, 'Continuous', -3)
   OK    | accoreconsole capa ESCALERA           | esperado (6, 'Continuous', 5) | leído (6, 'Continuous', 5)
   183 comprobaciones, 0 fallo(s)
   ```

---

## Prueba 1: `auditar_archivo` frente a `auditar_dibujo`

- [x] **Prueba 1 aprobada**

**Parte de:** ningún plano abierto. `$rutaJ` lleva la copia que se audita cerrada.

```powershell
$copia = (Resolve-Path tests\tmp_fase5\origen\casa6x12_copia.dwg).Path
$h0 = Huella tests\tmp_fase5\origen                               # línea base de la prueba 2
$j = @{ ruta = $copia } | ConvertTo-Json -Compress
$sw = [Diagnostics.Stopwatch]::StartNew()
$a = Invoke-Rpc 'auditar_archivo' $j; $sw.Elapsed.TotalSeconds
$a.result | Format-List origen, espacio, total, duracion_ms, insunits, unidades, en_metros_declarado, advertencias
$a.result.archivo | Format-List
$a.result.no_disponible | Format-List
$a.result.resumen_archivo | Format-List measurement, n_capas, n_bloques, layouts, n_xrefs
$a.result.resumen_archivo.entidades_modelo.total
Reglas $a | Format-Table
```
**Esperado:** `origen: plugin`, `espacio: modelo`, `insunits` 6, `unidades` m, `en_metros_declarado: True`, `archivo.copia_temporal: True`, `resumen_archivo.n_capas` **33**, `entidades_modelo.total` **4 694**, `no_disponible` con **`xrefs_no_resueltas`** y el motivo de §5H.1, `reglas_omitidas` y `reglas_con_error` vacíos. **Anote:** segundos de la llamada y `duracion_ms` (se fija en el gate; el presupuesto de reglas es 8 s).

Ahora **el mismo plano abierto**. Compruebe primero que es idéntico; debe hacerse **antes de abrirlo**, porque AutoCAD bloquea el archivo abierto y `Get-FileHash` no puede leerlo (visto en el gate del 2026-11-01):
```powershell
(Get-FileHash tests\tmp_fase5\fase5_trabajo.dwg).Hash -eq (Get-FileHash $copia).Hash   # True
```
En AutoCAD abra `C:\LPSAPP\MCPAutoCAD\tests\tmp_fase5\fase5_trabajo.dwg` y deje activa la pestaña **Modelo**, sin tocar nada más (si abre en un layout, pulse **Modelo**; escriba `TILEMODE` en la línea de comandos: debe dar 1):
```powershell
$d = Invoke-Rpc 'auditar_dibujo' '{"espacio":"modelo"}'
$cmp = 'auditar_capas','entidades_en_capa_0','auditar_bloques','verificar_cuadro_areas','duplicados','textos_fuera_de_escala','unidades'
foreach ($n in $cmp) {
  $x = $a.result.resumen.por_regla.$n; $y = $d.result.resumen.por_regla.$n
  "{0,-24} archivo n={1} | dibujo n={2} | {3}" -f $n, $x.n, $y.n, $(if ($x.n -eq $y.n) { 'IGUAL' } else { 'DISTINTO' })
}
$a.result.resumen_archivo.n_bloques -eq (Invoke-Rpc 'resumen_archivo' ('{"ruta":' + ($copia | ConvertTo-Json) + '}')).result.n_bloques   # True
(Invoke-Rpc 'textos_fuera_de_escala' '{"espacio":"modelo"}').result.mediana                                                              # 0.15
$a.result._mediana_texto                                                                                                                  # 0.15, haya o no hallazgos de esa regla
($null -ne $a.result._cype), ($null -ne $a.result._lamina), ($null -ne $a.result._bloques)                                                # True, True, True: auxiliares medidos
$sf = @{ ruta = $copia; copiar = $false } | ConvertTo-Json -Compress
$a2 = Invoke-Rpc 'auditar_archivo' $sf
$a2.result.archivo.copia_temporal; $a2.result.no_disponible.Count; $a2.result.resumen.por_regla.xrefs_no_resueltas | Format-List
```
**Esperado:** las **siete** reglas `IGUAL`, comparadas campo a campo: mismas `por_regla.n`. Compare también `alta`/`media`/`baja` con `Reglas $d`. Con `copiar: false`: `copia_temporal: False`, `no_disponible` vacío (0) y `xrefs_no_resueltas` presente en `por_regla`.

`verificar_cuadro_areas` entra en la igualdad **solo porque esta comparación se hace en la pestaña Modelo**. `Viewport.On` se respeta únicamente en el layout actual: en Modelo no hay layout actual y el dibujo abierto se evalúa igual que el archivo cerrado (§5H.0.3), que nunca lo tiene. Por eso aquí los dos `n` deben ser **iguales**; distintos en Modelo es un hallazgo.

**Opcional, desde un layout.** Solo aquí se admite una diferencia. Pulse la pestaña `Layout1`, repita la consulta y vuelva a **Modelo**:
```powershell
(Invoke-Rpc 'auditar_dibujo' '{"espacio":"modelo"}').result.resumen.por_regla.verificar_cuadro_areas.n
```
Anote el `n`. Puede diferir del de `auditar_archivo` si ese layout tiene viewports apagados, porque el dibujo abierto usa `CTAB` y `TILEMODE` y el archivo cerrado no. Esa diferencia no es un fallo; se fija en el gate.

**Errores** (no escriben nada):
```powershell
Err (Invoke-Rpc 'auditar_archivo' '{"ruta":"tests\\fixtures\\casa6x12_limpio.dwg"}')
Err (Invoke-Rpc 'auditar_archivo' '{"ruta":"C:\\LPSAPP\\MCPAutoCAD\\tests\\fixtures\\sintetico_cype.dxf"}')
Err (Invoke-Rpc 'auditar_archivo' '{"ruta":"C:\\LPSAPP\\..\\x.dwg"}')
Err (Invoke-Rpc 'auditar_archivo' '{"ruta":"C:\\LPSAPP\\MCPAutoCAD\\tests\\fixtures\\no_existe.dwg"}')
Err (Invoke-Rpc 'auditar_archivo' ('{"ruta":' + ($copia | ConvertTo-Json) + ',"presupuesto_ms":500}'))
Err (Invoke-Rpc 'auditar_archivo' ('{"ruta":' + ($copia | ConvertTo-Json) + ',"comprobaciones":["unidades","unidades"]}'))
```
**Esperado**, en orden: `ruta: debe ser una ruta absoluta`, `ruta: solo se admiten archivos .dwg`, `ruta: no se admiten '..' ni comodines`, `NOT_FOUND` con `El archivo '<ruta>' no existe.`, `presupuesto_ms: debe ser un entero entre 1000 y 8000`, `comprobaciones: comprobación repetida 'unidades'`.

**Límite de 50 MB.** Son los guardarraíles contra congelar AutoCAD más de 60 s con un plano de red. El tamaño se rechaza antes de leer, así que basta un archivo vacío de 51 MB; no hace falta un plano real tan grande:
```powershell
$g = "$PWD\tests\tmp_fase5\grande.dwg"
$fs = [IO.File]::Create($g); $fs.SetLength(51MB); $fs.Close()
Err (Invoke-Rpc 'auditar_archivo' (@{ ruta = $g } | ConvertTo-Json -Compress))
Err (Invoke-Rpc 'auditar_archivo' (@{ ruta = $g; copiar = $false } | ConvertTo-Json -Compress))
Remove-Item $g
```
**Esperado**, las dos con `INVALID_PARAMS` y estos `data.detalle` literales:
- Con `copiar` por defecto: `ruta: el archivo pesa 51 MB y con copiar: true el máximo es 50 MB, porque la copia bloquearía AutoCAD; use auditar_carpeta con patron igual al nombre del archivo, que copia fuera de AutoCAD`
- Con `copiar: false`: `ruta: el archivo pesa 51 MB y con copiar: false, fuera de la carpeta temporal del plugin, el máximo es 50 MB, porque la lectura bloquearía AutoCAD sin poder cancelarse; use auditar_carpeta con patron igual al nombre del archivo, que copia fuera de AutoCAD`

**Si falla:** una regla `DISTINTO` es un hallazgo: anote las dos cifras. Con `INTERNAL` y `superó el tiempo máximo (60 s)` no repita de inmediato: la lectura sigue en el hilo principal. Si `grande.dwg` responde otra cosa que `INVALID_PARAMS`, el plugin intentó copiarlo o leerlo: es **bloqueante**.

---

## Prueba 2: la carpeta de origen queda sin rastro

- [x] **Prueba 2 aprobada**

**Parte de:** la prueba 1, con `$h0` tomada antes de la primera llamada.

```powershell
$h1 = Huella tests\tmp_fase5\origen
Compare-Object $h0 $h1 -Property FullName, Utc, Length                       # nada
Get-ChildItem tests\tmp_fase5\origen -Force | Where-Object Extension -in '.dwl','.dwl2','.bak'   # nada
(Get-FileHash $copia).Hash -eq (Get-FileHash tests\fixtures\casa6x12_limpio.dwg).Hash            # True
Get-ChildItem "$env:LOCALAPPDATA\MCPAutoCAD\tmp" -Filter 'auditar_*' -ErrorAction SilentlyContinue   # nada
```
**Esperado:** listado **idéntico** antes y después (nombre, `LastWriteTimeUtc`, tamaño), sin `.dwl`/`.dwl2`/`.bak`, y sin copias `auditar_<pid>_<n>.dwg` en la carpeta temporal.

**Si falla:** cualquier archivo nuevo o modificado en el origen es **bloqueante**.

---

## Prueba 3: `auditar_carpeta` con AutoCAD en uso

- [ ] **Prueba 3 aprobada**

**Parte de:** `fase5_trabajo.dwg` abierto. Toma la línea base de la muestra y pide el lote en Claude Code.

```powershell
$m0 = Huella tests\tmp_fase5\muestra
```
En **Claude Code**:

> Llama a `auditar_carpeta` con `ruta` = `C:\LPSAPP\MCPAutoCAD\tests\tmp_fase5\muestra`, `origen` = `plugin` y `pausa_ms` = 8000. Después llama a `estado_auditoria` cada pocos segundos hasta que termine y enséñame los agregados, las advertencias y las filas. Al terminar llama también a `estado_auditoria` con `filtro` = `exports_cype` y con `filtro` = `laminas`.

**Mientras corre** (las pausas de 8 s son la ventana): en AutoCAD escriba `_ZOOM` → `_E` y mueva el dibujo; en la consola repita:
```powershell
$sw = [Diagnostics.Stopwatch]::StartNew(); (Invoke-Rpc 'estado').result | Select-Object insunits, unidades; $sw.Elapsed.TotalSeconds
```
**Esperado:**
- `auditar_carpeta` responde **de inmediato** con `id` `aud-<yyyyMMdd-HHmmss>-<4 hex>`, `estado: en_curso`, `total_archivos: null`, `carpeta_informe` bajo `%LOCALAPPDATA%\MCPAutoCAD\auditorias\`, y la advertencia de `carpetas_auditoria_permitidas`.
- AutoCAD responde a `_ZOOM` y `estado` responde entre archivos. Un `BUSY` aislado del `estado` durante una lectura no es un fallo; anote el tiempo máximo (se fija en el gate; cada archivo de esta muestra es pequeño).
- Termina con `estado: terminada`, `total_archivos` 4, `por_estado.ok` 4, `origen: plugin` en cada fila, con `sha256` y `origen_texto: "contenido_del_plano"`.
- Filas, con cifras medidas con el lote real y `origen: oda` sobre copias de estos cuatro archivos (2026-10-31). Las del plugin no están medidas: `n_capas` y los recuentos pueden diferir, y cualquier diferencia se anota. `unidades_inconsistentes` y `en_metros_declarado` no dependen del origen:

| archivo | `insunits` | `measurement` | `en_metros_declarado` | `unidades_inconsistentes` | `n_entidades_capa_0` | `n_capas` | `n_bloques` | `rotulo_presente` |
|---|---|---|---|---|---|---|---|---|
| `FILE24.DWG` | 4 | 1 | false | false | 0 | 8 | 0 | false |
| `PLANTILLA.dwg` | 4 | 1 | false | false | 5 (solo modelo) | 2 | 0 | false |
| `ARQ CLIENTE_B (1).dwg` | 6 | 0 | true | true | 125 (modelo 58, `ROTULO` 67) | 19 | 7 | true |
| `ARQ CLIENTE_C.dwg` | 4 | 1 | false | false | 93 (modelo 57, su layout 36) | 36 | 33 | false |

  Solo `ARQ CLIENTE_B (1).dwg` es incoherente (INSUNITS 6 con MEASUREMENT 0). Los otros tres traen hallazgo `unidades` porque declaran mm, pero son coherentes: `false`. `FILE24.DWG` da `n_capas` 8: ezdxf lista 9 porque añade `Defpoints` al cargar y el respaldo la descarta.
- **`es_export_cype`, `es_lamina` y `datos_lamina` con `origen: plugin`.** Las cifras se midieron con `origen: oda`; con el plugin se esperan las mismas porque mide la misma entrada (capas con entidades de modelo y layouts sin `VIEWPORT`, estilos de texto, recuadros y textos de escala del modelo), y este gate lo confirma:

| archivo | `es_export_cype` | `es_lamina` | `datos_lamina` |
|---|---|---|---|
| `FILE24.DWG` | **true** | **true** | `formatos` A3, `escalas` 50 |
| `PLANTILLA.dwg` | false | false | vacíos |
| `ARQ CLIENTE_B (1).dwg` | false | false | vacíos |
| `ARQ CLIENTE_C.dwg` | false | false | vacíos |

  Los filtros `exports_cype` y `laminas` dan solo `FILE24.DWG`. **Sin** la advertencia `es_export_cype o es_lamina sin medir…`: solo aparece si la medición auxiliar no cabe en 4 s o falla, y estos archivos pesan menos de 500 KB. Un `null` solo es aceptable si esa fila se debe a `La medición de capas y láminas no terminó en 4 s…` o `No se pudieron medir los datos auxiliares…` (anote cuál); `false` en `FILE24.DWG` es un **fallo** de paridad con ODA.
- `estado_auditoria` trae la advertencia `INSUNITS es el valor declarado por cada archivo, no la unidad real: <k> archivo(s) declara(n) mm con textos de altura mediana menor que 1.` con `<k>` = **2** (`FILE24.DWG` y `ARQ CLIENTE_C.dwg`): el plugin entrega `_mediana_texto` siempre que `textos_fuera_de_escala` termina completa, tenga o no hallazgos. Si esa regla no terminara dentro del presupuesto, `<k>` podría bajar; anote el real.
- Agregados: `por_insunits_declarado` con 3 en `"4"` y 1 en `"6"`; `n_no_metros_declarado` 3; `n_unidades_inconsistentes` 1; `n_con_rotulo` 1 (`ARQ CLIENTE_B (1).dwg`, por su layout `ROTULO`); `n_exports_cype` **1** y `n_laminas` **1** (`FILE24.DWG`).

Compruebe que la muestra sigue intacta y el informe:
```powershell
Compare-Object $m0 (Huella tests\tmp_fase5\muestra) -Property FullName, Utc, Length   # nada
$idLote = '<id de la respuesta>'
$inf = "$env:LOCALAPPDATA\MCPAutoCAD\auditorias\$idLote"
Get-ChildItem $inf | Select-Object Name, Length      # estado.json, archivos.jsonl, informe.json, informe.md
Get-Content "$inf\informe.md" -TotalCount 30
Get-ChildItem "$env:LOCALAPPDATA\MCPAutoCAD\tmp\$idLote" -ErrorAction SilentlyContinue   # nada
```

**Si falla:** AutoCAD bloqueado más que la lectura de un archivo o que el plugin procese más de un archivo a la vez es **bloqueante**. Cualquier cambio en `$m0` también.

---

## Prueba 4: cancelar y reanudar sin repetir los `ok`

- [x] **Prueba 4 aprobada**

**Parte de:** el lote anterior terminado. Una pausa larga da tiempo a cancelar. En **Claude Code**:

> Llama a `auditar_carpeta` con `ruta` = `C:\LPSAPP\MCPAutoCAD\tests\tmp_fase5\muestra`, `origen` = `plugin` y `pausa_ms` = 30000. Cuando `estado_auditoria` muestre `procesados` = 1, llama a `cancelar_auditoria` y enséñame el resultado.

**Esperado:** `{"estado": "cancelada", "procesados": <1 o 2>, "pendientes": <n>, ...}`, con el archivo en curso terminado. `estado_auditoria` lista el resto como `cancelado` o `pendiente`. Anote qué rutas quedaron `ok`.

Después:

> Llama a `auditar_carpeta` con `reanudar` = `<id>` y `pausa_ms` = 500, y enséñame las filas al terminar.

**Esperado:** mismo `id`, `estado: terminada`, 4 filas, una por ruta; las `ok` de antes **conservan** su `duracion_ms` y `sha256` (no se repiten) y solo se audita el resto.

```powershell
$l = Get-Content "$env:LOCALAPPDATA\MCPAutoCAD\auditorias\<id>\archivos.jsonl" | ConvertFrom-Json
$l | Group-Object ruta | Select-Object Count, Name
```
Una ruta `ok` debe aparecer con **una sola** fila `ok`.

Compruebe además los rechazos, con otra petición en Claude Code: un segundo `auditar_carpeta` con un lote en curso devuelve `[BUSY] Ya hay una auditoría en curso (<id>); use estado_auditoria o cancelar_auditoria.`; `reanudar` con otros parámetros, `reanudar: no se admiten otros parámetros salvo origen y pausa_ms`; `cancelar_auditoria` sin lote activo, `[NOT_FOUND] No hay ninguna auditoría en curso.`.

---

## Prueba 5: archivo dañado

- [x] **Prueba 5 aprobada**

**Parte de:** ningún lote en curso. Se crea una copia truncada a la mitad de bytes en `muestra5`:
```powershell
$bytes = [IO.File]::ReadAllBytes((Resolve-Path 'tests\tmp_fase5\muestra5\ARQ CLIENTE_C.dwg').Path)
[IO.File]::WriteAllBytes("$PWD\tests\tmp_fase5\muestra5\DANADO.dwg", $bytes[0..([int]($bytes.Length / 2) - 1)])
(Get-Item tests\tmp_fase5\muestra5\DANADO.dwg).Length                 # la mitad
```
En **Claude Code**:

> Llama a `auditar_carpeta` con `ruta` = `C:\LPSAPP\MCPAutoCAD\tests\tmp_fase5\muestra5`, `origen` = `plugin` y `pausa_ms` = 500. Al terminar llama a `estado_auditoria` con `filtro` = `errores`.

**Esperado:** `estado: terminada`, 5 archivos, `por_estado` con 4 `ok` y **1 `error`**. `DANADO.dwg` con `estado: error` y `error` = `{code: INTERNAL, message: "No se pudo leer el archivo DWG."}`. Los archivos posteriores en orden de ruta (`FILE24.DWG`, `PLANTILLA.dwg`) se auditan: **el lote sigue**. El filtro `errores` da solo esa fila.

**Si falla:** un diálogo de AutoCAD que espere respuesta, o un cierre de AutoCAD, es **bloqueante**: anótelo con `plugin.log`.

---

## Prueba 6: respaldo con AutoCAD cerrado

- [x] **Prueba 6 aprobada**

**Parte de:** los resultados de `$a` de la prueba 1. Cierre `fase5_trabajo.dwg` **sin guardar** y **cierre AutoCAD**: `Get-Process acad` no debe listar nada. Claude Code sigue abierto. En **Claude Code**:

> Con AutoCAD cerrado, llama a `auditar_archivo` con `ruta` = `C:\LPSAPP\MCPAutoCAD\tests\tmp_fase5\origen\casa6x12_copia.dwg` y `origen` = `auto`. Enséñame origen, pid, resumen_archivo, resumen.por_regla, resumen_reglas.auditar_capas, resumen_reglas.textos_fuera_de_escala, _cype, _lamina, _bloques, no_disponible y advertencias. Después llama a `auditar_carpeta` con `ruta` = `C:\LPSAPP\MCPAutoCAD\tests\tmp_fase5\muestra` y `origen` = `oda`, y al terminar enséñame las filas, los agregados y las advertencias, y llama a `estado_auditoria` con `filtro` = `exports_cype` y con `filtro` = `laminas`.

**Esperado de `auditar_archivo`:** `origen: oda`, `pid: 0`, `insunits` 6, `resumen_archivo.n_capas` **33**, `entidades_modelo.total` **4 694**, tres hallazgos `entidades_en_capa_0` con `datos.n` **38** en modelo, **3** en `Layout1` y **1** en `Layout2`, `n_bloques` **10**, `resumen_reglas.auditar_capas` con `por_clasificacion` 20 `estandar`, 5 `ajena_revit_aia`, 4 `ajena_exportacion`, 4 `fuera_de_estandar` y `n_vacias` **2**, `no_disponible` con `verificar_cuadro_areas` (motivo `el reconocimiento del cuadro de áreas (AreaTable, viewports) solo está en el plugin`) y la lista fija (`propiedades de bloques dinámicos`, `tamaño real de inserciones anidadas (tamano_por_anidada)`, `en_layout por viewports`, `entidades proxy y objetos de terceros`), y la advertencia `Resultado del respaldo ODA + ezdxf, sin AutoCAD: no se comprobaron cuadro de áreas, bloques dinámicos ni tamaños reales de inserciones anidadas.`.

**Comparación con la prueba 1** (`Reglas $a` sigue en la consola). Los auxiliares del plugin salen de `$a`, sin AutoCAD:
```powershell
$a.result._cype.capas_con_entidades.Count; ($a.result._cype.estilos_texto | Sort-Object) -join ', '
$a.result._lamina | ConvertTo-Json -Compress; $a.result._bloques.Count; $a.result._mediana_texto
```

| campo | plugin (prueba 1) | oda | ¿debe coincidir? |
|---|---|---|---|
| `insunits` | | | sí |
| `resumen_archivo.n_capas` | | | sí (33) |
| `entidades_modelo.total` | | | sí (4 694) |
| `n_bloques` | | | sí |
| `layouts` | | | sí |
| `por_regla.entidades_en_capa_0.n` | | | sí (3: un hallazgo por espacio, medido con ODA) |
| `por_regla.auditar_capas.n` | | | sí (13, medido con ODA) |
| `por_regla.unidades.n` | | | sí (0) |
| `por_regla.textos_fuera_de_escala.n` | | | sí (0) |
| `_cype.capas_con_entidades` (número de capas) | | | sí (30, medido con ODA) |
| `_cype.estilos_texto` | | | sí (`75`, `cota75`, `GENERATED_STYLE_2`, `Standard`, medido con ODA) |
| `_lamina` | | | sí (sin recuadros ni escalas, medido con ODA) |
| `_bloques` (número de definiciones) | | | sí (10, medido con ODA) |
| `_mediana_texto` del plugin frente a `resumen_reglas.textos_fuera_de_escala.mediana` de ODA | | | sí (0.15): ODA no emite `_mediana_texto` |
| `por_regla.auditar_bloques.n` | | | no: `parcial`, sin tamaño real ni dinámicos (§5H.3); ODA da 5 |
| `por_regla.duplicados.n` | | | no: `parcial`, solo LINE, LWPOLYLINE, CIRCLE, ARC, TEXT y MTEXT; ODA da 57 |

**Esperado de `auditar_carpeta`:** las mismas filas de la tabla de la prueba 3, con `origen: oda`. Es la comparación de paridad con esa prueba y la comprobación de §5H.7:

| archivo | `es_export_cype` | `es_lamina` | `datos_lamina` |
|---|---|---|---|
| `FILE24.DWG` | **true** | **true** | `formatos` A3, `escalas` 50 |
| `PLANTILLA.dwg` | false | false | vacíos |
| `ARQ CLIENTE_B (1).dwg` | false | false | vacíos |
| `ARQ CLIENTE_C.dwg` | false | false | vacíos |

**Severidad frente a la prueba 3:** mismos **80** hallazgos, plugin **alta 4** / media 39 / baja 37 frente a ODA **alta 0** / media 43 / baja 37 (medido). No es un fallo: sin AutoCAD no se mide el tamaño real, y `alta 0` **no** significa "sin hallazgos graves". `estado_auditoria`, `informe.json` e `informe.md` deben traer `<n> bloque(s) con unidades distintas en <k> archivo(s) auditados con ODA quedan en media porque sin AutoCAD no se mide su tamaño real; alguno podría ser alta, así que por_severidad puede subestimar alta: reaudite esos archivos con el plugin para separarlos.`, con `<n>` = `agregados.n_bloques_sin_medir` y `<k>` = `agregados.n_archivos_bloques_sin_medir`; `<n>` cuenta solo bloques con inserciones sin compensar, e incluye bloques que el plugin dejará en `media` por tamaño plausible o no medible: `<n>` mayor que 4 **no** es un fallo, y `<n>` no mide el déficit de `alta`. **Esperado por verificar:** `<n>` ≥ 4, si los 4 `alta` del plugin son esos mismos bloques; es una inferencia de la diferencia 43 − 39, no un dato medido bloque por bloque. Compárelo con los hallazgos `alta` de la prueba 3 y anote `<n>`, `<k>` y si coinciden. `informe.md` debe llevar, justo debajo de `Por severidad`, la línea `- alta puede estar subestimada: hay bloques sin medir; vea su aviso en Advertencias.`. Con `origen: plugin` esa advertencia **no** sale.

Agregados medidos: `n_exports_cype` 1, `n_laminas` 1, `n_unidades_inconsistentes` 1, `n_no_metros_declarado` 3. Los filtros `exports_cype` y `laminas` dan solo `FILE24.DWG`. **Sin** la advertencia `es_export_cype o es_lamina sin medir…`, y la de INSUNITS con `<k>` = **2** (`FILE24.DWG` y `ARQ CLIENTE_C.dwg`). Cualquier diferencia con la prueba 3 en estas columnas, en `n_exports_cype`, `n_laminas` o en `<k>` es un hallazgo de paridad entre orígenes: anote las dos cifras. Compruebe que no quedó ningún proceso ni temporal:
```powershell
Get-Process ODAFileConverter -ErrorAction SilentlyContinue                # nada
Get-ChildItem "$env:LOCALAPPDATA\MCPAutoCAD\tmp" -ErrorAction SilentlyContinue | Select-Object Name   # sin carpetas aud-*
Compare-Object $m0 (Huella tests\tmp_fase5\muestra) -Property FullName, Utc, Length   # nada
```

**Si falla:** `[ODA_NO_DISPONIBLE]` → defina `MCP_AUTOCAD_ODA` en `.mcp.json`. `INTERNAL` por `ezdxf` → paso 0.3.

### Opcional: `carpetas_auditoria_permitidas`

No bloquea el gate, pero es la única comprobación de este rechazo: no tiene prueba automática. **Parte de:** AutoCAD cerrado. Usa una **copia** de `lps.json` en `tests\tmp_fase5\`, sin tocar la del proyecto ni la instalada, y `MCP_AUTOCAD_LPS` solo en esta consola:
```powershell
$lpsPrueba = "$PWD\tests\tmp_fase5\lps_carpetas.json"
$base = Get-Content standards\lps.json -Raw | ConvertFrom-Json
$base | Add-Member -NotePropertyName carpetas_auditoria_permitidas -NotePropertyValue @("$PWD\tests\tmp_fase5\origen") -Force
$base | ConvertTo-Json -Depth 32 | Set-Content -Encoding utf8 $lpsPrueba
$env:MCP_AUTOCAD_LPS = $lpsPrueba
& 'D:\Program Files\Autodesk\AutoCAD 2026\acad.exe'
```
Cuando AutoCAD termine de abrir:
```powershell
Err (Invoke-Rpc 'auditar_archivo' '{"ruta":"C:\\LPSAPP\\MCPAutoCAD\\tests\\fixtures\\no_existe.dwg"}')
$p = Invoke-Rpc 'auditar_archivo' (@{ ruta = $copia } | ConvertTo-Json -Compress)
$p.result.origen; $p.result.advertencias -match 'carpetas_auditoria_permitidas'
```
**Esperado:** la primera da `INVALID_PARAMS` con `ruta: fuera de las carpetas permitidas de lps.json (carpetas_auditoria_permitidas)`, **no** el `NOT_FOUND` que dio esa misma ruta en la prueba 1: el rechazo llega antes de mirar si el archivo existe. La segunda da `plugin` y ninguna línea de advertencia, porque `$copia` está dentro de la carpeta permitida.

Al terminar cierre AutoCAD y deje la consola como estaba:
```powershell
Remove-Item Env:MCP_AUTOCAD_LPS
Get-Process acad -ErrorAction SilentlyContinue   # no debe listar nada
```

---

## Prueba 7: dibujo nuevo con `LPS_METROS.dwt`

- [x] **Prueba 7 aprobada**

**Parte de:** AutoCAD abierto de nuevo, sin planos. En AutoCAD ejecute `_NEW` y, en el cuadro "Seleccionar plantilla", escriba la ruta del resultado de:
```powershell
"$bundle\plantillas\LPS_METROS.dwt"
```
No hay que cambiar ninguna opción para usarla así. Con el dibujo nuevo activo:
```powershell
(Invoke-Rpc 'estado').result | Select-Object insunits, unidades, measurement, advertencias
$lc = (Invoke-Rpc 'listar_capas' '{"limit":1000}').result; $lc.total; $lc.capa_actual
$lps = (Get-Content standards\lps.json -Raw | ConvertFrom-Json).capas_permitidas
foreach ($c in $lc.capas) {
  $e = $lps.($c.nombre)
  if (-not $e -or $e.color -ne $c.color -or $e.tipo_linea -ne $c.tipo_linea -or $e.grosor -ne $c.grosor -or [bool]$e.plot -ne $c.plot) { "DIFERENTE: $($c.nombre)" }
}
(Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result.total    # 0
$m = (Invoke-Rpc 'muro' '{"eje":[[0,0],[4,0]],"espesor":0.15}').result
$c = Invoke-Rpc 'cotas_automaticas' ('{"handles":["' + $m.handles[0] + '"],"direccion":"x","lado":"izquierda","separacion":0.19,"estilo":"COTA75"}')
$c.result | Format-List advertencias, capas; $c.result.numeros | Format-List medidas, estilo, dimscale
$s = Invoke-Rpc 'cotas_automaticas' ('{"handles":["' + $m.handles[0] + '"],"direccion":"y","lado":"derecha","separacion":0.19}')
$s.result.advertencias
Reglas (Invoke-Rpc 'auditar_dibujo' '{"espacio":"modelo"}') | Format-Table
```
**Esperado:**
- `estado`: `insunits` 6, `unidades` m, `measurement` 1, **ninguna advertencia de unidades**.
- `listar_capas` **24** con `capa_actual` `0`, sin ninguna línea `DIFERENTE` (la de `DEFPOINTS` se compara con el nombre exacto de `lps.json`). Modelo vacío: **0** entidades.
- `cotas_automaticas` con `estilo: "COTA75"`: **sin advertencia de estilo**, cotas en `Cotas`. La segunda, sin `estilo`, solo avisa `cotas_automaticas: se usó el estilo de cota actual 'COTA75'; indique estilo para fijarlo (la oficina usa COTA75).`: el estilo actual es exactamente `COTA75`.
- En pantalla: texto de cota con **punto decimal** y dos decimales (medido con accoreconsole: `3.25`), y flecha `C` (círculo con cruz) con el mismo tamaño que en el 6X12. Compruebe en AutoCAD con `_DIMSTYLE`: `COTA75` actual, **sin** `<anulaciones del estilo>` antes de dibujar.
- `auditar_dibujo`: `auditar_bloques` **sin** `unidades_distintas` por el bloque `C` (lleva `units` 0 a propósito, ver "Decisiones" en `PLAN.md`). `C` deja de contar como `sin_uso` en cuanto hay cotas. Anote el `n` de cada regla (se fija en el gate).

Cierre el dibujo **sin guardar**.

### Fijarla como plantilla de QNEW (decisión del usuario)

**Ningún script ni el plugin la fija:** usar `_NEW` y elegir el archivo basta. Si decide usarla por defecto:

1. Escriba `_OPTIONS` y vaya a la pestaña **Archivos**.
2. Expanda **Parámetros de plantilla** → **Nombre de archivo de plantilla por defecto para QNEW**.
3. Seleccione la entrada, pulse **Examinar…** y elija `%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\plantillas\LPS_METROS.dwt`.
4. **Aceptar.** `_QNEW` abrirá entonces un dibujo en metros sin preguntar.

Reinstalar el plugin sustituye el archivo en la misma ruta; desinstalarlo lo borra, y entonces `_QNEW` preguntará por una plantilla: vuelva a esta opción. Decisión anotada en la tabla: ______.

---

## Prueba 8: lenguaje natural en Claude Code

- [x] **Prueba 8 anotada** (no bloquea)

**Parte de:** AutoCAD abierto, ningún lote en curso. Escriba, sin dar el nombre de ninguna herramienta:

> audita la carpeta del expediente tests\tmp_fase5\muestra y dime qué planos no están en metros

**Esperado:** usa `auditar_carpeta` y sigue con `estado_auditoria` usando el **`filtro` `no_metros`**; lista `FILE24.DWG`, `PLANTILLA.dwg` y `ARQ CLIENTE_C.dwg`; y **dice que INSUNITS es lo declarado, no la unidad real** (en `ARQ CLIENTE_C.dwg` la geometría está casi con seguridad en metros aunque declare mm). No debe abrir ningún plano ni proponer cambiar nada sin pedirlo.

---

## Prueba 9: regresión 0–4B

- [x] **Prueba 9 aprobada**

**Parte de:** AutoCAD con `tests\tmp_fase5\fase5_trabajo.dwg` abierto, pestaña **Modelo**.

```powershell
pwsh -File scripts\rpc-call.ps1 -Method ping -Repeat 20
(Invoke-Rpc 'estado').result | Select-Object insunits, unidades, advertencias
$r0 = Invoke-Rpc 'auditar_dibujo' '{"espacio":"modelo"}'
$r0.result | Select-Object total, duracion_ms; Reglas $r0 | Format-Table
$modelo0 = (Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result.total; $modelo0   # 4694
$l = Invoke-Rpc 'linea' '{"lineas":[{"inicio":[3322,4022],"fin":[3324,4022]}]}'; $l.result.handles
$v = Invoke-Rpc 'vista_png' '{"bbox":[[3320,4020],[3330,4030]],"ancho":800,"alto":600}'; $v.result.mecanismo
```
**Esperado:** los 20 `ping` responden; `auditar_dibujo` igual que `$d` de la prueba 1. **Un CTRL+Z** → desaparece la línea, no algo invisible (`vista_png` no deja paso de deshacer).

```powershell
$ruta = 'C:\\LPSAPP\\MCPAutoCAD\\tests\\fixtures\\sintetico_cype.dwg'
$hash0 = (Get-FileHash 'tests\fixtures\sintetico_cype.dwg').Hash
$r = Invoke-Rpc 'insertar_export' ('{"ruta":"' + $ruta + '","posicion":[3325,4024]}')
$r.result | Format-List capa, unidades_export, factor_unidades, n_entidades_export, n_entidades_excluidas
$r.result.capas_importadas | Format-List
(Get-FileHash 'tests\fixtures\sintetico_cype.dwg').Hash -eq $hash0   # True
```
**Esperado** (`GATE_FASE4B.md`, prueba 7): capa `MCP-DIBUJO`, `unidades_export` 4, `factor_unidades` **0.001**, `n_entidades_export` 8, `n_entidades_excluidas` 0, `capas_importadas.cype` con 4 capas. **Un CTRL+Z** → desaparecen INSERT, definición y capas.

```powershell
$hm = (Invoke-Rpc 'muro' '{"eje":[[3322,4024],[3328,4024]],"espesor":0.15}').result.handles[0]
$v1 = Invoke-Rpc 'vano' ('{"muro":"' + $hm + '","posicion":[3325,4024],"ancho":0.8,"tipo":"puerta","confirmar":true}')
$v1.result.entidades | Format-Table handle, tipo, capa, bloque, dinamico
$v1.result.numeros.bloque | Format-List ancho_por, propiedad_ancho, escala
```
**Esperado** (`GATE_FASE4B.md`, prueba 2): dos `LWPOLYLINE` en `MUROS` y un `INSERT` `Puerta Dinamica LM` en `PUERTAS`, `ancho_por: propiedad`, `propiedad_ancho: DistanceGeneralVano`, escala ±1. **Dos CTRL+Z** → desaparecen vano y muro. `(Invoke-Rpc 'consultar_entidades' '{"espacio":"modelo","resumen":true}').result.total` = **4 694**.

**Si falla:** cualquier regresión bloquea el gate; repita la prueba correspondiente de `docs\GATE_FASE0.md` … `GATE_FASE4B.md`.

---

## Tabla de resultados

| # | Prueba | Fecha | Resultado (PASA / FALLA) | Observaciones |
|---|---|---|---|---|
| 0 | Preparación (build, MCPESTADO 0.8.0/0.8, manifest 56 en orden, 33 de escritura, /mcp 61, plantilla instalada, muestra de 4 archivos) | 2026-11-01 | PASA | .NET (mínimo 1 770): 1 770 · pytest (mínimo 1 633): 1 633 (tras la corrección de severidades; 1 610 al abrir el gate) |
| 1 | **`auditar_archivo` frente a `auditar_dibujo` en la pestaña Modelo: 33 capas, 4 694, siete reglas iguales (con `verificar_cuadro_areas`), auxiliares medidos, xrefs con `copiar: false`, errores y límite de 50 MB con `copiar` `true` y `false`** | 2026-11-01 | PASA | duración: 409 ms · cuadro de áreas: archivo 0 = dibujo en Modelo 0 · desde `Layout1` (opcional): no ejecutado |
| 2 | **Origen sin rastro: listado igual, sin `.dwl`/`.dwl2`/`.bak`, sin copias temporales** | 2026-11-01 | PASA | |
| 3 | **`auditar_carpeta` con el plugin: AutoCAD responde a `_ZOOM` y `estado`; filas y agregados; `FILE24.DWG` con `es_export_cype` y `es_lamina` en `true` (A3, 1:50), `n_exports_cype` 1, `n_laminas` 1, sin `advertencia_sin_medir`, `<k>` 2** | 2026-11-01 | PASA por datos; fluidez pendiente | peor espera de `estado`: 0,37 s · `<k>`: 2 · la fluidez subjetiva se repite presencialmente, con planos grandes y sin sonda (el usuario estaba por Escritorio Remoto) |
| 4 | **`cancelar_auditoria` y `reanudar` sin repetir los `ok`; rechazos** | 2026-11-01 | PASA | |
| 5 | **Archivo dañado: `estado: error` y el lote sigue** | 2026-11-01 | PASA | |
| 6 | **Respaldo con AutoCAD cerrado: `origen: oda`, comparación con la prueba 1 incluidos `_cype`, `_lamina` y `_bloques`; paridad con la prueba 3: `FILE24.DWG` con `es_export_cype` y `es_lamina` en `true`, `<k>` 2** | 2026-11-01 y 2026-11-02 | PASA | `carpetas_auditoria_permitidas` (opcional): no ejecutado · severidades: `<n>` 7, `<k>` 2; los 4 `alta` del plugin son 4 de esos 7, por nombre y handles · aviso nuevo probado en un servidor aparte con AutoCAD abierto, no cerrado |
| 7 | **`_NEW` con `LPS_METROS.dwt`: sin advertencia de unidades, 24 capas, `COTA75` sin advertencia** | 2026-11-02 | PASA | QNEW: fijada por el usuario en `_OPTIONS`; el registro la confirma, `_QNEW` sin ejecutar · reglas del dibujo nuevo: todas `n` 0 |
| 8 | Lenguaje natural: filtro `no_metros` y aviso de INSUNITS declarado | 2026-11-02 | PASA (no bloquea) | `auditar_carpeta` + `estado_auditoria` con `no_metros`; lista los 3 y avisa de INSUNITS. Prueba contaminada: el asistente conocía el esperado |
| 9 | Regresión 0–4B | 2026-11-01 y 2026-11-02 | PASA | los dos CTRL+Z del vano se repitieron el 2026-11-02: 4 697 → 4 694 |

**Versiones probadas:** AutoCAD `version_autocad` = 25.1.0.0 · `plugin_version` = 0.8.0 · `protocol_version` = 0.8

## Cierre: criterio de aprobación y limpieza

### Criterio de aprobación

El gate se aprueba si pasan las pruebas 0–7 y 9. La 8 no bloquea. Vuelque en `PLAN.md` ("Fase 5"): resultado, cifras fijadas en este gate (tiempos, `n` de `verificar_cuadro_areas`, reglas del dibujo nuevo) y hallazgos.

### Limpieza final

1. En AutoCAD cierre todo **sin guardar**.
2. Borre las copias y los informes **de este gate** (solo los `id` que anotó; no toque otras auditorías):
   ```powershell
   $ids = @('aud-...', 'aud-...')     # todos los id de las pruebas 3 a 6 y 8
   foreach ($i in $ids) { Remove-Item -Recurse -Force "$env:LOCALAPPDATA\MCPAutoCAD\auditorias\$i" -ErrorAction SilentlyContinue }
   Remove-Item -Recurse -Force tests\tmp_fase5
   Test-Path tests\tmp_fase5                                                   # False
   $o1 = $muestra | ForEach-Object { Get-Item -LiteralPath $_ } | Select-Object FullName, LastWriteTimeUtc, Length
   Compare-Object $o0 $o1 -Property FullName, LastWriteTimeUtc, Length         # nada: O:\ no cambió
   ```
3. Borre el `plot.log` que quedó en la carpeta de fixtures. Lo escribió AutoCAD al trazar `fase4b_trabajo.dwg` en `vista_png` durante el gate 4B (última línea: 2026-10-31 22:20): AutoCAD deja ese registro junto al dibujo cuando tiene activo el registro automático de trazado. En este gate `vista_png` corre sobre `tests\tmp_fase5\`, así que su registro se va con esa carpeta:
   ```powershell
   Remove-Item -LiteralPath tests\fixtures\plot.log -ErrorAction SilentlyContinue
   Test-Path tests\fixtures\plot.log                                         # False
   ```
4. `casa6x12_limpio.dwg`, `copia_real.dwg` y `sintetico_cype.dwg` no se han tocado: sus hashes siguen iguales.
