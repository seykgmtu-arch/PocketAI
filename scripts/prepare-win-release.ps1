param(
    [switch]$SkipRuntimeSmokeTest,
    [switch]$SkipAppSmokeTest,
    [switch]$ForceDownload
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot 'fetch-assets.ps1') -Force:$ForceDownload
& (Join-Path $PSScriptRoot 'verify-layout.ps1') -Strict

if (-not $SkipRuntimeSmokeTest) {
    & (Join-Path $PSScriptRoot 'smoke-test-runtime.ps1')
}

& (Join-Path $PSScriptRoot 'publish-win-x64.ps1')

if (-not $SkipAppSmokeTest) {
    & (Join-Path $PSScriptRoot 'smoke-test-pocketai.ps1')
}

$dist = Join-Path $root 'dist\PocketAI'
$zip = Join-Path $root 'dist\PocketAI-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Release package: $zip" -ForegroundColor Green
