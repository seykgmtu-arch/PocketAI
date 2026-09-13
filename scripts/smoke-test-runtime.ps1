param(
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root 'runtime\llama\cpu\llama-server.exe'
$model = Join-Path $root 'models\chat\model.gguf'
$logDir = Join-Path $root 'logs'
$stdout = Join-Path $logDir 'smoke-llama-stdout.log'
$stderr = Join-Path $logDir 'smoke-llama-stderr.log'

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

if (-not (Test-Path $server)) { throw "CPU llama-server.exe not found: $server" }
if (-not (Test-Path $model)) { throw "GGUF model not found: $model" }

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

$port = Get-FreeTcpPort
$apiKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$args = @(
    '--model', $model,
    '--host', '127.0.0.1',
    '--port', $port,
    '--ctx-size', '4096',
    '--parallel', '1',
    '--alias', 'pocket-ai-smoke',
    '--api-key', $apiKey,
    '--no-webui',
    '--offline',
    '--n-gpu-layers', '0'
)

Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
Write-Host "Starting CPU llama-server on 127.0.0.1:$port ..." -ForegroundColor Cyan
$process = Start-Process -FilePath $server -ArgumentList $args -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr

try {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $healthUri = "http://127.0.0.1:$port/health"
    $ready = $false

    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            $err = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { '' }
            throw "llama-server exited with code $($process.ExitCode).`n$err"
        }

        try {
            $health = Invoke-RestMethod -Uri $healthUri -Method Get -TimeoutSec 2
            $ready = $true
            break
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }

    if (-not $ready) { throw "llama-server did not become healthy in $TimeoutSeconds seconds." }
    Write-Host 'Health check: OK' -ForegroundColor Green

    $headers = @{ Authorization = "Bearer $apiKey" }
    $payload = @{
        model = 'pocket-ai-smoke'
        messages = @(
            @{ role = 'system'; content = 'Ты локальный помощник. Отвечай кратко. /no_think' },
            @{ role = 'user'; content = 'Ответь одним словом: работает' }
        )
        stream = $false
        temperature = 0.1
        max_tokens = 32
    } | ConvertTo-Json -Depth 8

    $reply = Invoke-RestMethod -Uri "http://127.0.0.1:$port/v1/chat/completions" `
        -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $payload -TimeoutSec 120

    $text = $reply.choices[0].message.content
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw 'Chat completion returned empty content.'
    }

    Write-Host "Chat completion: OK -> $text" -ForegroundColor Green

    [ordered]@{
        passed = $true
        testedUtc = [DateTime]::UtcNow.ToString('O')
        runtime = 'CPU'
        port = $port
        response = $text
    } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'smoke-test-runtime.json') -Encoding UTF8
}
finally {
    if ($process -and -not $process.HasExited) {
        & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
    }
    $process.Dispose()
}
