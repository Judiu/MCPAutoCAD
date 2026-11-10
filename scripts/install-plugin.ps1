#Requires -Version 7.0
<#
###########################################################################################
##  ADVERTENCIA: ESTE SCRIPT LO EJECUTA EL USUARIO, NUNCA UN AGENTE.                     ##
##  Instala (o desinstala) el plugin en %APPDATA%\Autodesk\ApplicationPlugins, que      ##
##  AutoCAD carga automáticamente al iniciar. Un agente solo puede ejecutarlo con       ##
##  -DryRun (no modifica nada) o con -Destination apuntando a una carpeta de pruebas.   ##
###########################################################################################

.SYNOPSIS
    Instala el bundle MCPAutoCAD.bundle para AutoCAD 2026 (carga automática con LoadOnAutoCADStartup).

.DESCRIPTION
    Pasos:
      1. Verifica que AutoCAD (acad.exe) no esté en ejecución: la DLL estaría bloqueada.
      2. Verifica la salida de build (plugin\MCPAutoCAD.Plugin\bin\<Configuration>\net10.0-windows\) y el
         PackageContents.xml fuente (plugin\bundle\MCPAutoCAD.bundle\PackageContents.xml).
      3. Borra el bundle instalado anterior (si existe) y crea <destino>\Contents\.
      4. Copia PackageContents.xml a <destino>\ y todo el contenido de la carpeta de salida a <destino>\Contents\.
      5. Copia standards\lps.json a <destino>\Contents\lps.json (sobrescribe la copia que venga del build), para
         que el plugin compare INSUNITS con el estándar LPS (protocol.md §2.2). Si standards\lps.json no existe,
         avisa en amarillo y continúa: el plugin funciona igual, sin la advertencia LPS.
      6. Biblioteca de bloques (protocol.md §5D.5, §5G.6 y §9): copia los cinco .dwg de standards\ (LPS_ROTULO,
         Puerta Dinamica LM, PuertaBaño, VENTANA01 y TANQUE 1000 LTS) a <destino>\Contents\biblioteca\ creando la
         carpeta (sobrescribe la copia que venga del build). Por cada uno que falte avisa en amarillo y continúa: el
         plugin funciona igual, pero insertar_bloque con desde_biblioteca: true responde NOT_FOUND para ese bloque.
      7. Plantilla LPS en metros (protocol.md §5H.4 y §9): copia standards\LPS_METROS.dwt a
         <destino>\Contents\plantillas\LPS_METROS.dwt creando la carpeta (sobrescribe la copia que venga del build).
         Si falta avisa en amarillo y continúa: el plugin funciona igual. No cambia la plantilla por defecto del usuario.
      8. Desbloquea los archivos copiados (Unblock-File).
      9. Verifica que existan Contents\MCPAutoCAD.Plugin.dll (si no, falla), Contents\lps.json, los cinco .dwg de
         Contents\biblioteca\ y Contents\plantillas\LPS_METROS.dwt (si no, avisa). El resumen muestra la ruta del
         estándar, o "NO INSTALADO", cuántos bloques de la biblioteca se instalaron y la ruta de la plantilla.
    Con -Uninstall borra el bundle. Con -DryRun solo imprime lo que haría.
    No cambia TRUSTEDPATHS ni SECURELOAD: ApplicationPlugins es una ubicación de confianza de AutoCAD.

.PARAMETER Configuration
    Configuración compilada a instalar (por defecto Release).

.PARAMETER DryRun
    Imprime cada acción sin ejecutarla.

.PARAMETER Uninstall
    Elimina el bundle instalado.

.PARAMETER Destination
    Carpeta del bundle. Por defecto $env:APPDATA\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle.
    Cambiarla solo tiene sentido para pruebas del propio script.

.PARAMETER SourceRoot
    Raíz del repositorio de la que se toma el build (por defecto la carpeta padre de scripts\). Solo para pruebas.

.EXAMPLE
    pwsh -File scripts\install-plugin.ps1 -DryRun
.EXAMPLE
    pwsh -File scripts\install-plugin.ps1
.EXAMPLE
    pwsh -File scripts\install-plugin.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$DryRun,
    [switch]$Uninstall,
    [string]$Destination = '',
    [string]$SourceRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$ExpectedModuleName = './Contents/MCPAutoCAD.Plugin.dll'

function Invoke-Action {
    # Ejecuta $Action salvo en -DryRun, donde solo imprime la descripción.
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    if ($DryRun) {
        Write-Host "[DryRun] $Description"
    }
    else {
        Write-Host "  -> $Description"
        & $Action
    }
}

try {
    if (-not $SourceRoot) { $SourceRoot = $script:McpRepoRoot }
    $SourceRoot = [System.IO.Path]::GetFullPath($SourceRoot)

    if (-not $Destination) {
        if (-not $env:APPDATA) { throw 'La variable APPDATA no está definida; no se puede calcular el destino.' }
        $Destination = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\MCPAutoCAD.bundle'
    }
    $Destination = [System.IO.Path]::GetFullPath($Destination)
    if ((Split-Path -Leaf $Destination) -ne 'MCPAutoCAD.bundle') {
        throw "El destino debe terminar en 'MCPAutoCAD.bundle' (recibido: '$Destination'). Protección contra borrados accidentales."
    }
    $ContentsDir = Join-Path $Destination 'Contents'
    $OutputDir = Join-Path $SourceRoot "plugin\MCPAutoCAD.Plugin\bin\$Configuration\net10.0-windows"
    $PluginDll = Join-Path $OutputDir 'MCPAutoCAD.Plugin.dll'
    $PackageContents = Join-Path $SourceRoot 'plugin\bundle\MCPAutoCAD.bundle\PackageContents.xml'
    $LpsSource = Join-Path $SourceRoot 'standards\lps.json'
    $LpsTarget = Join-Path $ContentsDir 'lps.json'
    $LpsMissingText = 'No existe standards\lps.json: el plugin no comparará unidades con el estándar LPS'
    $LibraryDir = Join-Path $ContentsDir 'biblioteca'
    $LibraryBlocks = @('LPS_ROTULO', 'Puerta Dinamica LM', 'PuertaBaño', 'VENTANA01', 'TANQUE 1000 LTS')
    $TemplateSource = Join-Path $SourceRoot 'standards\LPS_METROS.dwt'
    $TemplateDir = Join-Path $ContentsDir 'plantillas'
    $TemplateTarget = Join-Path $TemplateDir 'LPS_METROS.dwt'
    $TemplateMissingText = 'No existe standards\LPS_METROS.dwt: los dibujos nuevos no tendrán la plantilla LPS en metros instalada'

    if ($DryRun) { Write-Host "Modo -DryRun: no se modificará nada." -ForegroundColor Yellow }
    Write-Host "Destino del bundle: $Destination"

    # --- 1. AutoCAD no debe estar en ejecución ---
    $acad = @(Get-Process -Name acad -ErrorAction SilentlyContinue)
    if ($acad.Count -gt 0) {
        $msg = "AutoCAD está en ejecución (PID: $(($acad | ForEach-Object Id) -join ', ')). Ciérrelo antes de instalar o desinstalar: la DLL del plugin queda bloqueada mientras AutoCAD está abierto."
        if ($DryRun) { Write-Host "[DryRun] $msg (la ejecución real se detendría aquí)" -ForegroundColor Yellow }
        else { throw $msg }
    }

    # --- Desinstalación ---
    if ($Uninstall) {
        if (Test-Path -LiteralPath $Destination) {
            Invoke-Action "Borrar $Destination" { Remove-Item -LiteralPath $Destination -Recurse -Force }
            Write-Host ""
            Write-Host "Plugin desinstalado$(if ($DryRun) { ' (simulado)' }). AutoCAD ya no lo cargará al iniciar." -ForegroundColor Green
        }
        else {
            Write-Host "No hay bundle instalado en $Destination. Nada que hacer."
        }
        exit 0
    }

    # --- 2. Verificar la salida de build ---
    if (-not (Test-Path -LiteralPath $PluginDll)) {
        throw "No existe la salida de build '$PluginDll'. Ejecute primero: pwsh -File `"$(Join-Path $PSScriptRoot 'build.ps1')`" -Configuration $Configuration"
    }
    if (-not (Test-Path -LiteralPath $PackageContents)) {
        throw "No existe '$PackageContents' (protocol.md §9). El plugin no está completo."
    }
    $leaked = @($script:AutoCadAssemblies | Where-Object { Test-Path -LiteralPath (Join-Path $OutputDir $_) })
    if ($leaked.Count -gt 0) {
        throw "La salida de build contiene DLL de AutoCAD ($($leaked -join ', ')). Corrija <Private>false</Private> y reconstruya; instalar copias de esas DLL rompe AutoCAD."
    }
    try {
        [xml]$xml = Get-Content -LiteralPath $PackageContents -Raw
    }
    catch {
        throw "PackageContents.xml no es XML válido: $($_.Exception.Message)"
    }
    $modules = @($xml.SelectNodes('//*[@ModuleName]') | ForEach-Object { $_.GetAttribute('ModuleName') })
    if ($modules -notcontains $ExpectedModuleName) {
        Write-Host "Advertencia: PackageContents.xml no declara ModuleName=`"$ExpectedModuleName`" (encontrado: $($modules -join ', ')). AutoCAD podría no cargar el plugin." -ForegroundColor Yellow
    }
    $files = @(Get-ChildItem -LiteralPath $OutputDir -Recurse -File)

    # --- 3-4. Copia ---
    if (Test-Path -LiteralPath $Destination) {
        Invoke-Action "Borrar la instalación anterior $Destination" { Remove-Item -LiteralPath $Destination -Recurse -Force }
    }
    Invoke-Action "Crear $ContentsDir" { New-Item -ItemType Directory -Path $ContentsDir -Force | Out-Null }
    Invoke-Action "Copiar $PackageContents -> $Destination\" { Copy-Item -LiteralPath $PackageContents -Destination $Destination -Force }
    Invoke-Action "Copiar $($files.Count) archivo(s) de $OutputDir\ -> $ContentsDir\" {
        Copy-Item -Path (Join-Path $OutputDir '*') -Destination $ContentsDir -Recurse -Force
    }
    if ($DryRun) {
        $files | ForEach-Object { Write-Host "[DryRun]     $($_.FullName.Substring($OutputDir.Length + 1))" }
    }

    # --- 5. Estándar LPS (protocol.md §2.2 y §9): copia explícita, sobrescribe la del build ---
    $lpsInstalled = $false
    if (Test-Path -LiteralPath $LpsSource -PathType Leaf) {
        Invoke-Action "Copiar $LpsSource -> $LpsTarget" {
            Copy-Item -LiteralPath $LpsSource -Destination $LpsTarget -Force
        }
        $lpsInstalled = $true
    }
    else {
        Write-Host "Advertencia: $LpsMissingText ($LpsSource)." -ForegroundColor Yellow
    }

    # --- 6. Biblioteca de bloques (protocol.md §5D.5, §5G.6 y §9): Contents\biblioteca\<bloque>.dwg ---
    $libraryInstalled = @()
    foreach ($block in $LibraryBlocks) {
        $blockSource = Join-Path $SourceRoot "standards\$block.dwg"
        $blockTarget = Join-Path $LibraryDir "$block.dwg"
        if (Test-Path -LiteralPath $blockSource -PathType Leaf) {
            Invoke-Action "Copiar $blockSource -> $blockTarget" {
                New-Item -ItemType Directory -Path $LibraryDir -Force | Out-Null
                Copy-Item -LiteralPath $blockSource -Destination $blockTarget -Force
            }
            $libraryInstalled += $block
        }
        else {
            Write-Host "Advertencia: no existe standards\$block.dwg: insertar_bloque no podrá importar '$block' desde la biblioteca ($blockSource)." -ForegroundColor Yellow
        }
    }

    # --- 7. Plantilla LPS en metros (protocol.md §5H.4 y §9): Contents\plantillas\LPS_METROS.dwt ---
    $templateInstalled = $false
    if (Test-Path -LiteralPath $TemplateSource -PathType Leaf) {
        Invoke-Action "Copiar $TemplateSource -> $TemplateTarget" {
            New-Item -ItemType Directory -Path $TemplateDir -Force | Out-Null
            Copy-Item -LiteralPath $TemplateSource -Destination $TemplateTarget -Force
        }
        $templateInstalled = $true
    }
    else {
        Write-Host "Advertencia: $TemplateMissingText ($TemplateSource)." -ForegroundColor Yellow
    }

    # --- 8. Desbloquear ---
    Invoke-Action "Desbloquear archivos (Unblock-File) en $Destination" {
        Get-ChildItem -LiteralPath $Destination -Recurse -File | Unblock-File
    }

    # --- 9. Verificación final ---
    if (-not $DryRun) {
        $installedDll = Join-Path $ContentsDir 'MCPAutoCAD.Plugin.dll'
        if (-not (Test-Path -LiteralPath $installedDll)) { throw "La copia terminó pero no existe '$installedDll'." }
        if (Test-Path -LiteralPath $LpsTarget -PathType Leaf) {
            $lpsInstalled = $true
        }
        else {
            $lpsInstalled = $false
            Write-Host "Advertencia: no existe '$LpsTarget'. El plugin cargará igual, con estandar_lps.disponible=false y sin la advertencia de unidades LPS." -ForegroundColor Yellow
        }
        $libraryInstalled = @($LibraryBlocks | Where-Object { Test-Path -LiteralPath (Join-Path $LibraryDir "$_.dwg") -PathType Leaf })
        foreach ($block in @($LibraryBlocks | Where-Object { $libraryInstalled -notcontains $_ })) {
            Write-Host "Advertencia: no existe '$(Join-Path $LibraryDir "$block.dwg")'. El plugin cargará igual, pero insertar_bloque con desde_biblioteca: true no encontrará '$block'." -ForegroundColor Yellow
        }
        $templateInstalled = Test-Path -LiteralPath $TemplateTarget -PathType Leaf
        if (-not $templateInstalled) {
            Write-Host "Advertencia: no existe '$TemplateTarget'. El plugin cargará igual; la plantilla LPS en metros no está instalada." -ForegroundColor Yellow
        }
    }
    $lpsSummary = if ($lpsInstalled) { $LpsTarget } else { "NO INSTALADO ($LpsMissingText)" }
    $librarySummary = "$($libraryInstalled.Count) de $($LibraryBlocks.Count) en $LibraryDir ($($libraryInstalled -join ', '))"
    $templateSummary = if ($templateInstalled) { $TemplateTarget } else { "NO INSTALADA ($TemplateMissingText)" }

    Write-Host ""
    Write-Host "=== INSTALACIÓN $(if ($DryRun) { 'SIMULADA (-DryRun)' } else { 'COMPLETA' }) ===" -ForegroundColor Green
    Write-Host "  Bundle:  $Destination"
    Write-Host "  Plugin:  $ContentsDir\MCPAutoCAD.Plugin.dll"
    Write-Host "  Estándar LPS: $lpsSummary"
    Write-Host "  Biblioteca de bloques: $librarySummary"
    Write-Host "  Plantilla en metros: $templateSummary"
    Write-Host "  El bundle se carga al iniciar AutoCAD 2026 (LoadOnAutoCADStartup=True)."
    Write-Host "  Abra AutoCAD y ejecute el comando MCPESTADO para confirmar la carga."
    Write-Host "  El pipe se llama \\.\pipe\mcp-autocad-$env:USERNAME-<PID de acad.exe>; compruébelo con:"
    Write-Host "    pwsh -File `"$(Join-Path $PSScriptRoot 'check-pipe.ps1')`""
    exit 0
}
catch {
    Write-McpError $_.Exception.Message
    exit 1
}
