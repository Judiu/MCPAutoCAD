#Requires -Version 7.0
<#
.SYNOPSIS
    Compila la solución del plugin MCPAutoCAD y ejecuta las pruebas.

.DESCRIPTION
    1. Comprueba que haya un SDK .NET 10.x y las DLL de AutoCAD 2026 (acmgd, acdbmgd, accoremgd).
    2. dotnet restore + dotnet build de plugin\MCPAutoCAD.sln (-c <Configuration>).
    3. Salvo -SkipTests: dotnet test plugin\MCPAutoCAD.GeometryCore.Tests.
    4. Verifica que exista MCPAutoCAD.Plugin.dll en la salida y que NO se hayan copiado las DLL de AutoCAD
       (las referencias deben tener <Private>false</Private>).
    5. Con -ServerTests: python -m pytest server\tests con PYTHONPATH=server.
    No instala nada ni toca AutoCAD. Código de salida distinto de 0 ante cualquier fallo.

.EXAMPLE
    pwsh -File scripts\build.ps1
.EXAMPLE
    pwsh -File scripts\build.ps1 -Configuration Debug -SkipTests
.EXAMPLE
    pwsh -File scripts\build.ps1 -ServerTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$ServerTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$Root = $script:McpRepoRoot
$Solution = Join-Path $Root 'plugin\MCPAutoCAD.sln'
$TestsProject = Join-Path $Root 'plugin\MCPAutoCAD.GeometryCore.Tests'
$OutputDir = Join-Path $Root "plugin\MCPAutoCAD.Plugin\bin\$Configuration\net10.0-windows"
$PluginDll = Join-Path $OutputDir 'MCPAutoCAD.Plugin.dll'
$ServerDir = Join-Path $Root 'server'
$ServerTestsDir = Join-Path $ServerDir 'tests'

$summary = [System.Collections.Generic.List[string]]::new()

function Invoke-Step {
    # Ejecuta un programa externo y lanza si el código de salida no es 0.
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    Write-Host ""
    Write-Host "==> $Description" -ForegroundColor Cyan
    Write-Host "    $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Falló '$Description' (código de salida $LASTEXITCODE)."
    }
}

try {
    # --- 1. Requisitos ---
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'No se encontró "dotnet" en el PATH. Instale el SDK de .NET 10 (x64).'
    }
    $sdks = @(& dotnet --list-sdks 2>$null)
    $sdk10 = @($sdks | Where-Object { $_ -match '^10\.' })
    if ($sdk10.Count -eq 0) {
        throw "No hay ningún SDK .NET 10.x instalado (AutoCAD 2026 corre sobre .NET 10). SDK encontrados: $(if ($sdks) { $sdks -join '; ' } else { '(ninguno)' })"
    }
    $activeSdk = (& dotnet --version 2>$null | Select-Object -First 1)
    if ($activeSdk -notmatch '^1\d\.') {
        throw "El SDK activo es $activeSdk; net10.0-windows exige SDK 10 o superior. Revise global.json o el PATH."
    }
    $summary.Add("SDK .NET activo: $activeSdk (10.x disponibles: $(($sdk10 | ForEach-Object { ($_ -split ' ')[0] }) -join ', '))")

    $missingAcad = @($script:AutoCadAssemblies | Where-Object { -not (Test-Path -LiteralPath (Join-Path $script:AutoCadDir $_)) })
    if ($missingAcad.Count -gt 0) {
        throw "Faltan DLL de AutoCAD 2026 en '$($script:AutoCadDir)': $($missingAcad -join ', '). Verifique la instalación de AutoCAD 2026."
    }
    $summary.Add("DLL de AutoCAD: $($script:AutoCadDir) (acmgd, acdbmgd, accoremgd presentes)")

    if (-not (Test-Path -LiteralPath $Solution)) {
        throw "No existe la solución '$Solution' (protocol.md §9). ¿Está completo plugin\?"
    }

    # --- 2. Restore + build ---
    Invoke-Step -Description 'dotnet restore' -FilePath 'dotnet' -Arguments @('restore', $Solution, '--nologo')
    Invoke-Step -Description "dotnet build ($Configuration)" -FilePath 'dotnet' -Arguments @('build', $Solution, '-c', $Configuration, '--no-restore', '--nologo')
    $summary.Add("Solución compilada: $Solution ($Configuration)")

    # --- 3. Pruebas de GeometryCore ---
    if ($SkipTests) {
        Write-Host "Pruebas .NET omitidas (-SkipTests)." -ForegroundColor Yellow
        $summary.Add('Pruebas .NET: OMITIDAS (-SkipTests)')
    }
    else {
        if (-not (Test-Path -LiteralPath $TestsProject)) {
            throw "No existe el proyecto de pruebas '$TestsProject' (protocol.md §9)."
        }
        Invoke-Step -Description "dotnet test GeometryCore.Tests ($Configuration)" -FilePath 'dotnet' -Arguments @('test', $TestsProject, '-c', $Configuration, '--nologo')
        $summary.Add("Pruebas .NET: OK ($TestsProject)")
    }

    # --- 4. Verificación de la salida ---
    if (-not (Test-Path -LiteralPath $PluginDll)) {
        throw "No se generó '$PluginDll'. Revise AssemblyName=MCPAutoCAD.Plugin y TargetFramework=net10.0-windows (protocol.md §9)."
    }
    $leaked = @($script:AutoCadAssemblies | Where-Object { Test-Path -LiteralPath (Join-Path $OutputDir $_) })
    if ($leaked.Count -gt 0) {
        throw "La salida contiene DLL de AutoCAD ($($leaked -join ', ')) en '$OutputDir'. Las referencias a AutoCAD deben tener <Private>false</Private>; corrija el .csproj y reconstruya."
    }
    $summary.Add("Plugin: $PluginDll")
    $summary.Add("Carpeta de salida: $OutputDir")

    # --- 5. Pruebas del servidor Python (opcional) ---
    if ($ServerTests) {
        if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
            throw 'No se encontró "python" en el PATH (se requiere Python 3.12).'
        }
        if (-not (Test-Path -LiteralPath $ServerTestsDir)) {
            throw "No existe '$ServerTestsDir' (protocol.md §9)."
        }
        & python -c "import pytest" 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw 'pytest no está disponible para este Python. Instálelo usted mismo (p. ej. en un entorno virtual); este script no instala paquetes.'
        }
        $previousPythonPath = $env:PYTHONPATH
        try {
            $env:PYTHONPATH = $ServerDir
            Invoke-Step -Description 'pytest server\tests' -FilePath 'python' -Arguments @('-m', 'pytest', $ServerTestsDir, '-q')
        }
        finally {
            $env:PYTHONPATH = $previousPythonPath
        }
        $summary.Add("Pruebas Python: OK ($ServerTestsDir, PYTHONPATH=$ServerDir)")
    }

    Write-Host ""
    Write-Host "=== BUILD CORRECTO ===" -ForegroundColor Green
    $summary | ForEach-Object { Write-Host "  - $_" }
    Write-Host "  Siguiente paso (lo ejecuta el usuario, con AutoCAD cerrado):"
    Write-Host "    pwsh -File `"$(Join-Path $PSScriptRoot 'install-plugin.ps1')`" -Configuration $Configuration"
    exit 0
}
catch {
    Write-Host ""
    Write-Host "=== BUILD FALLIDO ===" -ForegroundColor Red
    $summary | ForEach-Object { Write-Host "  - $_" }
    Write-McpError $_.Exception.Message
    exit 1
}
