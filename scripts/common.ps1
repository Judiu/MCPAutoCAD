# common.ps1 — Funciones compartidas por los scripts de MCPAutoCAD.
# Se carga con dot-sourcing:  . (Join-Path $PSScriptRoot 'common.ps1')
# No tiene efectos al cargarse: solo define funciones y constantes.
# Referencias: protocol.md §1.1 (nombre del pipe), §1.2 (framing), §1.3 (descubrimiento), §9 (rutas).

Set-StrictMode -Version Latest

# Raíz del repositorio = carpeta padre de scripts\
$script:McpRepoRoot = Split-Path -Parent $PSScriptRoot

# Única ruta absoluta permitida fuera del repositorio (instalación verificada de AutoCAD 2026).
$script:AutoCadDir = 'D:\Program Files\Autodesk\AutoCAD 2026'
$script:AutoCadAssemblies = @('acmgd.dll', 'acdbmgd.dll', 'accoremgd.dll')

# Prefijo y expresión del nombre del pipe (protocol.md §1.1 y §1.3).
$script:PipePrefix = 'mcp-autocad-'
$script:PipeRegex = '^mcp-autocad-(?<usuario>.+)-(?<pid>\d+)$'

# Texto exacto de NO_INSTANCE (protocol.md §6.1).
$script:NoInstanceText = 'AutoCAD no está abierto o el plugin no cargó.'

function Write-McpError {
    # Escribe un mensaje de error en stderr (sin lanzar).
    param([Parameter(Mandatory)][string]$Message)
    [Console]::Error.WriteLine("ERROR: $Message")
}

function Write-McpInfo {
    # Escribe en stderr: útil para scripts cuyo stdout se reserva (run-server.ps1).
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Message)
    [Console]::Error.WriteLine($Message)
}

function Get-McpAutoCadPipe {
    <#
    Lista los pipes \\.\pipe\mcp-autocad-<usuario>-<PID>.
    -CurrentUserOnly filtra por el usuario actual ($env:USERNAME, sin distinguir mayúsculas), como hace el servidor.
    Devuelve objetos con: Nombre, Ruta, Usuario, Pid, EsUsuarioActual, ProcesoVivo, Proceso.
    #>
    [CmdletBinding()]
    param([switch]$CurrentUserOnly)

    $names = @()
    try {
        $names = [System.IO.Directory]::GetFiles('\\.\pipe\') | ForEach-Object { [System.IO.Path]::GetFileName($_) }
    }
    catch {
        # Algunos nombres de pipe con caracteres no válidos hacen fallar GetFiles; se intenta por otra vía.
        try {
            $names = Get-ChildItem -LiteralPath '\\.\pipe\' -ErrorAction Stop | ForEach-Object { $_.Name }
        }
        catch {
            throw "No se pudo listar \\.\pipe\: $($_.Exception.Message)"
        }
    }

    $result = foreach ($n in $names) {
        if ($n -notlike "$script:PipePrefix*") { continue }
        $m = [regex]::Match($n, $script:PipeRegex)
        if (-not $m.Success) { continue }
        $user = $m.Groups['usuario'].Value
        $procId = [int]$m.Groups['pid'].Value
        $isCurrent = [string]::Equals($user, $env:USERNAME, [System.StringComparison]::OrdinalIgnoreCase)
        if ($CurrentUserOnly -and -not $isCurrent) { continue }
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Nombre          = $n
            Ruta            = "\\.\pipe\$n"
            Usuario         = $user
            Pid             = $procId
            EsUsuarioActual = $isCurrent
            ProcesoVivo     = [bool]$proc
            Proceso         = if ($proc) { $proc.ProcessName } else { $null }
        }
    }
    return @($result | Sort-Object Pid)
}
