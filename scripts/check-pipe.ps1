#Requires -Version 7.0
<#
.SYNOPSIS
    Lista las instancias de AutoCAD con el plugin MCPAutoCAD cargado (pipes \\.\pipe\mcp-autocad-<usuario>-<PID>).

.DESCRIPTION
    Solo lectura: no abre los pipes ni toca AutoCAD. Sirve para el gate 7 (dos instancias)
    y para diagnosticar si el plugin cargó. Ver protocol.md §1.1 y §1.3.

.PARAMETER AllUsers
    Muestra también pipes de otros usuarios (por defecto solo los del usuario actual).

.EXAMPLE
    pwsh -File scripts\check-pipe.ps1
#>
[CmdletBinding()]
param(
    [switch]$AllUsers
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    . (Join-Path $PSScriptRoot 'common.ps1')

    $pipes = @(Get-McpAutoCadPipe -CurrentUserOnly:(-not $AllUsers))

    if ($pipes.Count -eq 0) {
        Write-Host "No hay instancias: AutoCAD no está abierto o el plugin no cargó."
        Write-Host "  Usuario actual: $env:USERNAME"
        Write-Host "  Revise: AutoCAD abierto, comando MCPESTADO, log en $env:LOCALAPPDATA\MCPAutoCAD\plugin.log"
        exit 0
    }

    Write-Host "Instancias encontradas: $($pipes.Count)"
    $pipes | Format-Table -AutoSize -Property `
        @{ Label = 'PID'; Expression = { $_.Pid } },
        @{ Label = 'Usuario'; Expression = { $_.Usuario } },
        @{ Label = 'Proceso'; Expression = { if ($_.ProcesoVivo) { $_.Proceso } else { '(no existe)' } } },
        @{ Label = 'Pipe'; Expression = { $_.Ruta } } | Out-Host

    $stale = @($pipes | Where-Object { -not $_.ProcesoVivo })
    if ($stale.Count -gt 0) {
        Write-Host "Advertencia: $($stale.Count) pipe(s) apuntan a un PID sin proceso vivo (no debería ocurrir; revise el log del plugin)."
    }
    $notAcad = @($pipes | Where-Object { $_.ProcesoVivo -and $_.Proceso -ne 'acad' })
    if ($notAcad.Count -gt 0) {
        Write-Host "Advertencia: $($notAcad.Count) pipe(s) pertenecen a un proceso que no es acad.exe."
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine("ERROR: no se pudieron listar los pipes: $($_.Exception.Message)")
    exit 1
}
