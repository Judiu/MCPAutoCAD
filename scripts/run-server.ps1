#Requires -Version 7.0
<#
.SYNOPSIS
    Arranca el servidor MCP de AutoCAD (python -m mcp_autocad) para pruebas manuales.

.DESCRIPTION
    Fija PYTHONPATH a <raíz>\server y ejecuta python -m mcp_autocad heredando stdin/stdout/stderr.
    stdout es el transporte MCP (stdio): este script solo escribe en stderr.
    Claude Code NO usa este script: usa .mcp.json (python -m mcp_autocad directamente).

.PARAMETER Pid
    Opcional: fija MCP_AUTOCAD_PID para este proceso (selecciona la instancia de AutoCAD).

.PARAMETER TimeoutSec
    Opcional: fija MCP_AUTOCAD_TIMEOUT (segundos de espera de respuesta; por defecto del servidor 15).

.EXAMPLE
    pwsh -File scripts\run-server.ps1
#>
[CmdletBinding()]
param(
    [Alias('Pid')][int]$TargetPid = 0,
    [int]$TimeoutSec = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

try {
    $serverDir = Join-Path $script:McpRepoRoot 'server'
    $mainPy = Join-Path $serverDir 'mcp_autocad\__main__.py'
    if (-not (Test-Path -LiteralPath $mainPy)) {
        throw "No existe '$mainPy' (protocol.md §9). El servidor Python no está completo."
    }
    $python = Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $python) {
        throw 'No se encontró "python" en el PATH (se requiere Python 3.12 con los paquetes mcp y pywin32).'
    }

    $env:PYTHONPATH = $serverDir
    if ($TargetPid -gt 0) { $env:MCP_AUTOCAD_PID = "$TargetPid" }
    if ($TimeoutSec -gt 0) { $env:MCP_AUTOCAD_TIMEOUT = "$TimeoutSec" }

    Write-McpInfo "PYTHONPATH=$serverDir"
    if ($env:MCP_AUTOCAD_PID) { Write-McpInfo "MCP_AUTOCAD_PID=$($env:MCP_AUTOCAD_PID)" }
    if ($env:MCP_AUTOCAD_TIMEOUT) { Write-McpInfo "MCP_AUTOCAD_TIMEOUT=$($env:MCP_AUTOCAD_TIMEOUT)" }
    Write-McpInfo "Ejecutando: $($python.Source) -m mcp_autocad (Ctrl+C para salir). stdout = transporte MCP."

    # Start-Process -NoNewWindow hereda los handles de consola: stdin/stdout llegan a Python sin que
    # PowerShell los reinterprete (evita corromper el transporte stdio).
    $proc = Start-Process -FilePath $python.Source -ArgumentList @('-m', 'mcp_autocad') -NoNewWindow -Wait -PassThru
    exit $proc.ExitCode
}
catch {
    Write-McpError $_.Exception.Message
    exit 1
}
