#Requires -Version 7.0
<#
.SYNOPSIS
    Cliente JSON-RPC mínimo para el pipe del plugin MCPAutoCAD (sin servidor Python).

.DESCRIPTION
    Implementa el framing de protocol.md §1.2: [uint32 little-endian con la longitud N][N bytes JSON UTF-8 sin BOM].
    Descubre la instancia como protocol.md §1.3 (pipes \\.\pipe\mcp-autocad-<usuario>-<PID> del usuario actual).
    Imprime la respuesta y el tiempo de ida y vuelta en ms. Sirve para los gates 1, 2, 3 y 8 de la Fase 0,
    aislando el plugin del servidor Python.

    ATENCIÓN: métodos de escritura (p. ej. "linea") modifican el dibujo activo. Úselos solo en un dibujo de prueba.

    Códigos de salida: 0 = todas las respuestas con "result"; 2 = alguna respuesta con "error" JSON-RPC;
    1 = fallo local o de transporte (sin instancia, timeout, pipe roto, parámetros mal formados).

.PARAMETER Method
    Método JSON-RPC: ping, tools/manifest, estado, listar_capas, linea...

.PARAMETER ParamsJson
    Objeto JSON con los parámetros (por defecto '{}'). Debe ser un objeto, nunca un array.

.PARAMETER Pid
    PID de la instancia de AutoCAD. Obligatorio si hay varias. Si no se da, se usa $env:MCP_AUTOCAD_PID si existe.

.PARAMETER TimeoutSec
    Tiempo máximo de espera de cada respuesta (por defecto 15 s, como el servidor). La conexión usa 1 s.

.PARAMETER Repeat
    Repite la llamada N veces por la misma conexión (secuencial) e imprime mínimo, media y máximo en ms.

.PARAMETER AsObject
    Emite un objeto por llamada (Id, Pid, Ms, Ok, Codigo, Mensaje, Respuesta) en lugar de texto.

.PARAMETER Raw
    Imprime la respuesta JSON tal cual llega (sin sangría).

.EXAMPLE
    pwsh -File scripts\rpc-call.ps1 -Method ping
.EXAMPLE
    pwsh -File scripts\rpc-call.ps1 -Method estado -Repeat 100
.EXAMPLE
    pwsh -File scripts\rpc-call.ps1 -Method listar_capas -ParamsJson '{"limit":5}' -Pid 18244
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Method,
    [string]$ParamsJson = '{}',
    # $PID es una variable automática de PowerShell de solo lectura: el parámetro se llama TargetPid con alias -Pid.
    [Alias('Pid')][int]$TargetPid = 0,
    [ValidateRange(1, 600)][int]$TimeoutSec = 15,
    [ValidateRange(1, 100000)][int]$Repeat = 1,
    [switch]$AsObject,
    [switch]$Raw
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

$MaxMessageBytes = 16777216   # 16 MiB, protocol.md §1.2
$ConnectTimeoutMs = 1000      # protocol.md §6.1

class McpTransportException : System.Exception {
    [string]$Code
    McpTransportException([string]$code, [string]$message) : base($message) { $this.Code = $code }
}

function Resolve-TargetPipe {
    param([int]$RequestedPid)
    $pipes = @(Get-McpAutoCadPipe -CurrentUserOnly)
    if ($RequestedPid -le 0 -and $env:MCP_AUTOCAD_PID) {
        $parsed = 0
        if (-not [int]::TryParse($env:MCP_AUTOCAD_PID, [ref]$parsed)) {
            throw [McpTransportException]::new('NO_INSTANCE', "MCP_AUTOCAD_PID no es un entero: '$($env:MCP_AUTOCAD_PID)'.")
        }
        $RequestedPid = $parsed
    }
    if ($RequestedPid -gt 0) {
        $match = @($pipes | Where-Object { $_.Pid -eq $RequestedPid })
        if ($match.Count -eq 0) {
            $list = if ($pipes.Count) { ' Instancias disponibles (PID): ' + (($pipes | ForEach-Object Pid) -join ', ') + '.' } else { '' }
            $tail = if ($pipes.Count) { $list } else { " $script:NoInstanceText" }
            throw [McpTransportException]::new('NO_INSTANCE', "No hay ninguna instancia de AutoCAD con PID $RequestedPid.$tail")
        }
        return $match[0]
    }
    if ($pipes.Count -eq 0) {
        throw [McpTransportException]::new('NO_INSTANCE', $script:NoInstanceText)
    }
    if ($pipes.Count -gt 1) {
        $list = ($pipes | ForEach-Object Pid) -join ', '
        throw [McpTransportException]::new('NO_INSTANCE', "Hay varias instancias de AutoCAD abiertas (PID: $list). Use -Pid <n> para elegir una.")
    }
    return $pipes[0]
}

function Read-Exact {
    param(
        [System.IO.Stream]$Stream,
        [byte[]]$Buffer,
        [int]$Count,
        [System.Diagnostics.Stopwatch]$Clock,
        [int]$TimeoutMs
    )
    $offset = 0
    while ($offset -lt $Count) {
        $remaining = $TimeoutMs - [int]$Clock.ElapsedMilliseconds
        if ($remaining -le 0) {
            throw [McpTransportException]::new('PIPE_TIMEOUT', "El plugin no respondió en $([int]($TimeoutMs / 1000)) s.")
        }
        $task = $Stream.ReadAsync($Buffer, $offset, $Count - $offset)
        try {
            $completed = $task.Wait($remaining)
        }
        catch {
            throw [McpTransportException]::new('PIPE_BROKEN', "Se perdió la conexión con AutoCAD (¿se cerró?). Detalle: $($_.Exception.InnerException.Message)")
        }
        if (-not $completed) {
            throw [McpTransportException]::new('PIPE_TIMEOUT', "El plugin no respondió en $([int]($TimeoutMs / 1000)) s.")
        }
        $n = $task.Result
        if ($n -le 0) {
            throw [McpTransportException]::new('PIPE_BROKEN', 'Se perdió la conexión con AutoCAD (¿se cerró?).')
        }
        $offset += $n
    }
}

function Invoke-McpRpc {
    param(
        [System.IO.Pipes.NamedPipeClientStream]$Client,
        [string]$RequestJson,
        [int]$TimeoutMs
    )
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $body = $utf8.GetBytes($RequestJson)
    if ($body.Length -gt $MaxMessageBytes) {
        throw [McpTransportException]::new('INTERNAL', "La petición supera 16 MiB ($($body.Length) bytes).")
    }
    $prefix = [BitConverter]::GetBytes([uint32]$body.Length)
    if (-not [BitConverter]::IsLittleEndian) { [Array]::Reverse($prefix) }
    $frame = [byte[]]::new(4 + $body.Length)
    [Array]::Copy($prefix, 0, $frame, 0, 4)
    [Array]::Copy($body, 0, $frame, 4, $body.Length)

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $Client.Write($frame, 0, $frame.Length)
        $Client.Flush()
    }
    catch {
        throw [McpTransportException]::new('PIPE_BROKEN', "Se perdió la conexión con AutoCAD (¿se cerró?). Detalle: $($_.Exception.Message)")
    }

    $header = [byte[]]::new(4)
    Read-Exact -Stream $Client -Buffer $header -Count 4 -Clock $clock -TimeoutMs $TimeoutMs
    if (-not [BitConverter]::IsLittleEndian) { [Array]::Reverse($header) }
    $length = [BitConverter]::ToUInt32($header, 0)
    if ($length -gt $MaxMessageBytes) {
        throw [McpTransportException]::new('INTERNAL', "Respuesta con longitud declarada $length > 16 MiB: framing roto.")
    }
    $payload = [byte[]]::new([int]$length)
    if ($length -gt 0) {
        Read-Exact -Stream $Client -Buffer $payload -Count ([int]$length) -Clock $clock -TimeoutMs $TimeoutMs
    }
    $clock.Stop()
    return [pscustomobject]@{
        Json = $utf8.GetString($payload)
        Ms   = [math]::Round($clock.Elapsed.TotalMilliseconds, 1)
    }
}

function Format-JsonText {
    param([string]$Json)
    try {
        $opts = [System.Text.Json.JsonSerializerOptions]::new()
        $opts.WriteIndented = $true
        $opts.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
        return [System.Text.Json.Nodes.JsonNode]::Parse($Json).ToJsonString($opts)
    }
    catch {
        return $Json
    }
}

# --- Validación de parámetros locales ---
try {
    $doc = [System.Text.Json.JsonDocument]::Parse($ParamsJson)
    $kind = $doc.RootElement.ValueKind
    $doc.Dispose()
}
catch {
    Write-McpError "ParamsJson no es JSON válido: $($_.Exception.InnerException.Message ?? $_.Exception.Message)"
    exit 1
}
if ($kind -ne [System.Text.Json.JsonValueKind]::Object) {
    Write-McpError "ParamsJson debe ser un objeto JSON ({...}), no $kind (protocol.md §2)."
    exit 1
}
$methodJson = ConvertTo-Json -InputObject $Method -Compress

$client = $null
$exitCode = 0
$times = [System.Collections.Generic.List[double]]::new()
$errorCount = 0
try {
    $target = Resolve-TargetPipe -RequestedPid $TargetPid

    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $target.Nombre, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $client.Connect($ConnectTimeoutMs)
    }
    catch [System.TimeoutException] {
        throw [McpTransportException]::new('PIPE_TIMEOUT', "No se pudo conectar a $($target.Ruta) en 1 s (¿4 conexiones ocupadas o plugin colgado?).")
    }
    catch [System.UnauthorizedAccessException] {
        throw [McpTransportException]::new('PIPE_BROKEN', "Acceso denegado a $($target.Ruta): la ACL solo admite al usuario que abrió AutoCAD.")
    }

    if (-not $AsObject) {
        Write-Host "Conectado a $($target.Ruta) (PID $($target.Pid)). Método: $Method. Llamadas: $Repeat."
    }

    for ($i = 1; $i -le $Repeat; $i++) {
        $request = '{"jsonrpc":"2.0","id":' + $i + ',"method":' + $methodJson + ',"params":' + $ParamsJson + '}'
        $resp = Invoke-McpRpc -Client $client -RequestJson $request -TimeoutMs ($TimeoutSec * 1000)
        $times.Add($resp.Ms)

        $ok = $false; $code = $null; $message = $null
        try {
            $parsed = $resp.Json | ConvertFrom-Json -AsHashtable
            if ($parsed.ContainsKey('result')) { $ok = $true }
            elseif ($parsed.ContainsKey('error')) {
                $err = $parsed['error']
                $message = $err['message']
                $code = if ($err.ContainsKey('data') -and $err['data'] -is [System.Collections.IDictionary]) { $err['data']['code'] } else { "$($err['code'])" }
            }
            else { $code = 'SIN_RESULT_NI_ERROR' }
        }
        catch {
            $code = 'RESPUESTA_NO_JSON'
            $message = $_.Exception.Message
        }
        if (-not $ok) { $errorCount++ }

        if ($AsObject) {
            [pscustomobject]@{
                Id        = $i
                Pid       = $target.Pid
                Metodo    = $Method
                Ms        = $resp.Ms
                Ok        = $ok
                Codigo    = $code
                Mensaje   = $message
                Respuesta = $resp.Json
            }
        }
        elseif ($Repeat -eq 1) {
            if ($Raw) { Write-Output $resp.Json } else { Write-Output (Format-JsonText $resp.Json) }
            if (-not $ok) { Write-Host "Error JSON-RPC: [$code] $message" }
            Write-Host "Ida y vuelta: $($resp.Ms) ms"
        }
        elseif (-not $ok) {
            Write-Host "Llamada ${i}: [$code] $message ($($resp.Ms) ms)"
        }
    }

    if (-not $AsObject -and $Repeat -gt 1) {
        $stats = $times | Measure-Object -Minimum -Maximum -Average
        Write-Host ("Resumen: {0} llamadas, {1} con error. ms mín {2:N1} / media {3:N1} / máx {4:N1}" -f `
                $Repeat, $errorCount, $stats.Minimum, $stats.Average, $stats.Maximum)
    }
    if ($errorCount -gt 0) { $exitCode = 2 }
}
catch [McpTransportException] {
    Write-McpError "[$($_.Exception.Code)] $($_.Exception.Message)"
    if ($times.Count -gt 0) { Write-McpInfo "Llamadas completadas antes del fallo: $($times.Count)." }
    $exitCode = 1
}
catch {
    Write-McpError "Fallo inesperado: $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    if ($null -ne $client) { $client.Dispose() }
}
exit $exitCode
