param(
    [string]$PublishDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $root 'dist\PocketAI'
}

$exe = Join-Path $PublishDirectory 'PocketAI.exe'
$output = Join-Path $PublishDirectory 'smoke-test-pocketai.json'

if (-not (Test-Path $exe)) { throw "PocketAI.exe not found: $exe" }
Remove-Item $output -Force -ErrorAction SilentlyContinue

Write-Host 'Running PocketAI.exe headless self-test (forced CPU)...' -ForegroundColor Cyan
$process = Start-Process -FilePath $exe -ArgumentList @('--self-test', '--force-cpu', '--self-test-output', $output) -PassThru -Wait

if ($process.ExitCode -ne 0) {
    $details = if (Test-Path $output) { Get-Content $output -Raw } else { 'No self-test JSON was produced.' }
    throw "PocketAI.exe self-test failed with exit code $($process.ExitCode).`n$details"
}

if (-not (Test-Path $output)) { throw 'PocketAI.exe exited successfully but did not create a self-test report.' }
$result = Get-Content $output -Raw | ConvertFrom-Json
if (-not $result.passed) { throw "PocketAI self-test report says passed=false.`n$(Get-Content $output -Raw)" }

Write-Host "PocketAI.exe self-test: OK ($($result.backend)) -> $($result.response)" -ForegroundColor Green
