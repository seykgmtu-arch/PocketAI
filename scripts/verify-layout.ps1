param(
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$config = Get-Content (Join-Path $root 'pocketai.json') -Raw | ConvertFrom-Json

$model = Join-Path $root $config.modelPath
$cpu = Join-Path $root $config.runtime.cpuPath
$cuda = Join-Path $root $config.runtime.cudaPath

$checks = @(
    @{ Name = 'Model'; Path = $model; Required = $true },
    @{ Name = 'CPU runtime'; Path = $cpu; Required = $true },
    @{ Name = 'CUDA runtime'; Path = $cuda; Required = $false }
)

$failed = $false
foreach ($check in $checks) {
    $exists = Test-Path $check.Path
    Write-Host "$($check.Name): $($check.Path)"
    Write-Host "  exists: $exists"
    if ($check.Required -and -not $exists) { $failed = $true }
}

$lockPath = Join-Path $root 'assets.lock.json'
if (Test-Path $lockPath) {
    $lock = Get-Content $lockPath -Raw | ConvertFrom-Json
    if (Test-Path $model) {
        $modelHash = (Get-FileHash $model -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "Model SHA-256: $modelHash"
        if ($modelHash -ne $lock.model.sha256) {
            Write-Error 'Model SHA-256 does not match assets.lock.json.'
            $failed = $true
        }
    }
}
else {
    Write-Warning 'assets.lock.json is missing; run scripts\fetch-assets.ps1.'
    if ($Strict) { $failed = $true }
}

if ($Strict -and $failed) { throw 'Pocket AI layout verification failed.' }
if ($failed) { Write-Warning 'Pocket AI layout is incomplete.' }
else { Write-Host 'Layout verification: OK' -ForegroundColor Green }
