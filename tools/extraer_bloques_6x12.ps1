#Requires -Version 7.0
<#
.SYNOPSIS
    Extrae y sanea los cuatro bloques del 6X12 para la biblioteca del plugin (protocol.md §5G.0 y §5G.6).

.DESCRIPTION
    Reproducible y sin interfaz. Pasos (todo sobre copias en -WorkDir):
      1. Copia tests\fixtures\casa6x12_limpio.dwg (el fixture no se modifica) y exporta con -WBLOCK desde LISP
         'Puerta Dinamica LM', 'PuertaBaño', 'VENTANA01' y 'TANQUE 1000 LTS'. Conserva el bloque dinámico.
      2. Pasa los exportados a DXF con ODA y calcula con ezdxf la esquina inferior izquierda de PuertaBaño.
      3. Sanea cada .dwg en un segundo accoreconsole: INSUNITS 6, capa actual 0, estilo de texto Standard, estilo de
         cota actual guardado como Standard (el export solo trae COTA75) con flechas y texto sin referencias, purga
         (quita MUROS, el bloque C, cota75 y COTA75; conserva HIDDEN, en uso), PuertaBaño movida a la base en (0,0),
         AUDIT y QSAVE.
      4. Pasa los saneados a DXF y los comprueba con tools\verificar_bloques_biblioteca.py.
      5. AUDIT de solo informe sobre cada resultado (sin guardar).
      6. Copia los cuatro .dwg a -OutputDir (standards\) solo si los pasos 4 y 5 son correctos, y borra -WorkDir.
    La lógica de AutoCAD está en tools\extraer_bloques_6x12.lsp. El .scr (Windows-1252, por la ñ) lleva ese LISP en
    línea en vez de hacer (load): NO se toca SECURELOAD, que AutoCAD guarda en el registro del perfil del usuario y que
    docs\INSTALACION.md pide no poner a 0. El script compara el valor antes y después y falla si cambió.
    accoreconsole no tiene ActiveX, así que los nombres de las propiedades dinámicas solo se leen en vivo (ver abajo).

.NOTES
    Camino para el gate de Fase 4B (con AutoCAD abierto y el plugin 0.7.0 instalado por el usuario):
      insertar_bloque { "nombre": "Puerta Dinamica LM", "posicion": [0, 0], "desde_biblioteca": true } devuelve
      'propiedades_dinamicas' con los nombres reales; lo mismo con VENTANA01. El usuario apunta la propiedad de ancho
      en standards\lps.json, biblioteca_vanos.<bloque>.propiedad_ancho (hoy null).

    Resultados de la ejecución de 2026-10-31 (accoreconsole W.179.0.0, /l es-ES; ODA 27.1.0 + ezdxf):
      Esquina de PuertaBaño antes de mover: 15.24111381467319,6.995516973200058.
      AUDIT sobre los resultados:
        Puerta Dinamica LM : Total de errores encontrados 0, corregidos 0
        PuertaBaño         : Total de errores encontrados 0, corregidos 0
        VENTANA01          : Total de errores encontrados 0, corregidos 0
        TANQUE 1000 LTS    : Total de errores encontrados 0, corregidos 0
      verificar_bloques_biblioteca.py sobre los DXF de los .dwg de standards\: CORRECTO (código 0).
        Puerta Dinamica LM : INSUNITS 6; capas 0, PUERTAS; 3 ARC + 7 LWPOLYLINE; caja (0,0)-(0.9,0.961); HIDDEN;
                             ACAD_ENHANCEDBLOCK presente (44 ACAD_PROXY_OBJECT, igual que el export)
        PuertaBaño         : INSUNITS 6; capa 0; 1 ARC + 19 LINE; caja (0,0)-(0.65,0.61)
        VENTANA01          : INSUNITS 6; capas 0, PUERTAS, ventanas; 8 LWPOLYLINE; caja (-0.12,0)-(0,0.3);
                             ACAD_ENHANCEDBLOCK presente (11 ACAD_PROXY_OBJECT, igual que el export)
        TANQUE 1000 LTS    : INSUNITS 6; capas 0, AC; 15 LINE + 4 LWPOLYLINE; caja (-0.7037,0)-(0.6861,1.48)
        En los cuatro: sin bloques (ni C ni anónimos), sin MUROS ni Defpoints, solo estilos Standard.
      Sin comprobar: que la referencia creada por el API siga siendo dinámica; lo fija el gate (arriba).

.PARAMETER WorkDir
    Carpeta de trabajo. Se vacía al empezar. Por defecto tests\tmp_tareaB.

.PARAMETER OutputDir
    Destino de los .dwg finales. Por defecto standards\.

.PARAMETER KeepWork
    No borra -WorkDir al terminar (logs y DXF para diagnóstico).

.PARAMETER TimeoutSeconds
    Tiempo máximo de cada ejecución de accoreconsole u ODA; si expira se mata el proceso.

.EXAMPLE
    pwsh -File tools\extraer_bloques_6x12.ps1
#>
[CmdletBinding()]
param(
    [string]$WorkDir = '',
    [string]$OutputDir = '',
    [switch]$KeepWork,
    [ValidateRange(10, 600)][int]$TimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $RepoRoot 'tests\fixtures\casa6x12_limpio.dwg'
$Lisp = Join-Path $PSScriptRoot 'extraer_bloques_6x12.lsp'
$Verifier = Join-Path $PSScriptRoot 'verificar_bloques_biblioteca.py'
$AcCore = 'D:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe'
$Oda = 'C:\Program Files\ODA\ODAFileConverter 27.1.0\ODAFileConverter.exe'
$Blocks = @('Puerta Dinamica LM', 'PuertaBaño', 'VENTANA01', 'TANQUE 1000 LTS')
$BlockToRebase = 'PuertaBaño'
$Win1252 = [System.Text.Encoding]::GetEncoding(1252)

function ConvertTo-LispPath {
    # AutoLISP escapa con \: las rutas van con barras normales.
    param([Parameter(Mandatory)][string]$Path)
    $Path.Replace('\', '/')
}

function Get-InlineLisp {
    # El .lsp sin comentarios ni líneas en blanco: una línea vacía en un .scr equivale a pulsar Intro.
    (Get-Content -LiteralPath $Lisp -Encoding ascii | Where-Object { $_ -notmatch '^\s*(;|$)' }) -join "`r`n"
}

function Get-SecureLoad {
    # Lectura informativa del perfil del usuario; este script nunca escribe en el registro.
    $keys = Get-ChildItem 'HKCU:\Software\Autodesk\AutoCAD' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*\Profiles\*\Variables' }
    @($keys | ForEach-Object { '{0}={1}' -f $_.Name, $_.GetValue('SECURELOAD') }) -join ';'
}

function Invoke-Bounded {
    # Lanza un proceso sin ventana, espera con límite y lo mata si expira.
    param(
        [Parameter(Mandatory)][string]$File,
        [Parameter(Mandatory)][string]$Arguments,
        [Parameter(Mandatory)][string]$StdOut
    )
    $proc = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -NoNewWindow -RedirectStandardOutput $StdOut
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
        $proc.Kill($true)
        throw "Tiempo agotado ($TimeoutSeconds s): $File $Arguments"
    }
}

function Invoke-AcCore {
    # Ejecuta un .scr sobre un .dwg y devuelve el texto de la consola (UTF-16 con NUL) ya limpio.
    param(
        [Parameter(Mandatory)][string]$Dwg,
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string]$Tag
    )
    $scr = Join-Path $WorkDir "$Tag.scr"
    [System.IO.File]::WriteAllText($scr, $Script, $Win1252)
    $raw = Join-Path $WorkDir "$Tag.raw"
    Invoke-Bounded -File $AcCore -Arguments "/i `"$Dwg`" /s `"$scr`" /l es-ES" -StdOut $raw
    $text = [System.Text.Encoding]::Unicode.GetString([System.IO.File]::ReadAllBytes($raw)).Replace("`0", '')
    Set-Content -LiteralPath (Join-Path $WorkDir "$Tag.log") -Value $text -Encoding utf8
    $text
}

function Invoke-Oda {
    # ODA convierte por carpeta.
    param([Parameter(Mandatory)][string]$InDir, [Parameter(Mandatory)][string]$OutDir)
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $log = Join-Path $WorkDir "oda_$(Split-Path -Leaf $OutDir).log"
    Invoke-Bounded -File $Oda -Arguments "`"$InDir`" `"$OutDir`" ACAD2018 DXF 0 1 `"*.DWG`"" -StdOut $log
}

function Get-AuditLine {
    # Línea de resumen de AUDIT (consola en es-ES). Lanza si no aparece o si no es 0 errores.
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Block)
    $m = [regex]::Matches($Text, 'Total de errores encontrados (\d+), corregidos (\d+)')
    if ($m.Count -eq 0) { throw "AUDIT de '$Block' no devolvió línea de resumen." }
    if ($m[$m.Count - 1].Groups[1].Value -ne '0') { throw "AUDIT de '$Block': $($m[$m.Count - 1].Value)." }
    $m[$m.Count - 1].Value
}

try {
    if (-not $WorkDir) { $WorkDir = Join-Path $RepoRoot 'tests\tmp_tareaB' }
    if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'standards' }
    $WorkDir = [System.IO.Path]::GetFullPath($WorkDir)
    $OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
    foreach ($required in @($Source, $Lisp, $Verifier, $AcCore, $Oda)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "No existe '$required'." }
    }

    $secureLoadBefore = Get-SecureLoad
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    $exportDir = Join-Path $WorkDir 'export'
    $cleanDir = Join-Path $WorkDir 'saneado'
    New-Item -ItemType Directory -Path $exportDir, $cleanDir -Force | Out-Null
    $inline = Get-InlineLisp

    # --- 1. Exportar ---
    $sourceCopy = Join-Path $WorkDir 'casa6x12_copia.dwg'
    Copy-Item -LiteralPath $Source -Destination $sourceCopy
    $calls = foreach ($block in $Blocks) {
        '(mcp-extraer "{0}" "{1}")' -f $block, (ConvertTo-LispPath (Join-Path $exportDir "$block.dwg"))
    }
    $script1 = @($inline) + $calls + @('_.QUIT', '_N', '') -join "`r`n"
    $log1 = Invoke-AcCore -Dwg $sourceCopy -Script $script1 -Tag 'paso1_export'
    foreach ($block in $Blocks) {
        if (-not (Test-Path -LiteralPath (Join-Path $exportDir "$block.dwg"))) {
            throw "No se exportó '$block'. Consola:`n$log1"
        }
    }
    Write-Host 'Paso 1: exportados los 4 bloques.'

    # --- 2. Base de PuertaBaño ---
    $exportDxf = Join-Path $WorkDir 'export_dxf'
    Invoke-Oda -InDir $exportDir -OutDir $exportDxf
    $corner = (& python -I $Verifier --esquina (Join-Path $exportDxf "$BlockToRebase.dxf")).Trim()
    if ($LASTEXITCODE -ne 0) { throw "No se pudo leer la esquina de $BlockToRebase." }
    $parts = $corner.Split(',') | ForEach-Object { [double]::Parse($_, [cultureinfo]::InvariantCulture) }
    $displacement = '{0},{1}' -f (-$parts[0]).ToString('R', [cultureinfo]::InvariantCulture), (-$parts[1]).ToString('R', [cultureinfo]::InvariantCulture)
    Write-Host "Paso 2: esquina de $BlockToRebase = $corner; desplazamiento $displacement."

    # --- 3. Sanear ---
    foreach ($block in $Blocks) {
        $dwg = Join-Path $cleanDir "$block.dwg"
        Copy-Item -LiteralPath (Join-Path $exportDir "$block.dwg") -Destination $dwg
        $argument = if ($block -eq $BlockToRebase) { "`"$displacement`"" } else { 'nil' }
        $tag = "paso3_" + ($block -replace '[^A-Za-z0-9]', '_')
        $script2 = @($inline, "(mcp-sanear $argument)", '_.QSAVE', '_.QUIT', '_N', '') -join "`r`n"
        $log3 = Invoke-AcCore -Dwg $dwg -Script $script2 -Tag $tag
        if ($log3 -notmatch 'MCP-SANEADO') { throw "No terminó el saneado de '$block'. Vea $tag.log en $WorkDir (use -KeepWork)." }
    }
    Write-Host 'Paso 3: saneados.'

    # --- 4. Comprobar ---
    $cleanDxf = Join-Path $WorkDir 'saneado_dxf'
    Invoke-Oda -InDir $cleanDir -OutDir $cleanDxf
    & python -I $Verifier $cleanDxf
    if ($LASTEXITCODE -ne 0) { throw 'La comprobación con ezdxf falló: no se copia nada a la biblioteca.' }

    # --- 5. AUDIT final, sin guardar ---
    foreach ($block in $Blocks) {
        $tag = "paso5_" + ($block -replace '[^A-Za-z0-9]', '_')
        $log = Invoke-AcCore -Dwg (Join-Path $cleanDir "$block.dwg") -Script (@('_.AUDIT', '_N', '_.QUIT', '_N', '') -join "`r`n") -Tag $tag
        Write-Host "AUDIT $block : $(Get-AuditLine -Text $log -Block $block)"
    }

    # --- 6. Publicar ---
    foreach ($block in $Blocks) {
        Copy-Item -LiteralPath (Join-Path $cleanDir "$block.dwg") -Destination (Join-Path $OutputDir "$block.dwg") -Force
    }
    Write-Host "Paso 6: copiados los 4 .dwg a $OutputDir."

    if ((Get-SecureLoad) -ne $secureLoadBefore) { throw 'SECURELOAD cambió durante la extracción: revise el perfil de AutoCAD.' }
    if (-not $KeepWork) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
    Write-Host '=== EXTRACCIÓN CORRECTA ===' -ForegroundColor Green
    exit 0
}
catch {
    [Console]::Error.WriteLine("ERROR: $($_.Exception.Message)")
    exit 1
}
