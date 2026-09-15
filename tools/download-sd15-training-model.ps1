param(
    [string]$PocketAIRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipHash
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([string]::IsNullOrWhiteSpace($PocketAIRoot)) {
    $PocketAIRoot = Split-Path -Parent $PSScriptRoot
}

# powershell.exe + a quoted path ending with "\" may leave a literal quote
# in a parameter on some Windows command-line combinations.
$PocketAIRoot = $PocketAIRoot.Trim().Trim([char]34)
$root = [System.IO.Path]::GetFullPath($PocketAIRoot)
$modelDirectory = Join-Path $root 'models\training\sd15'
$modelPath = Join-Path $modelDirectory 'v1-5-pruned.safetensors'
$url = 'https://huggingface.co/stable-diffusion-v1-5/stable-diffusion-v1-5/resolve/main/v1-5-pruned.safetensors?download=true'
$expectedSha256 = '1a189f0be69d6106a48548e7626207dddd7042a418dbf372cefd05e0cdba61b6'

New-Item -ItemType Directory -Force -Path $modelDirectory | Out-Null

Write-Host ''
Write-Host 'SD 1.5 TRAINING CHECKPOINT' -ForegroundColor Cyan
Write-Host 'File: v1-5-pruned.safetensors'
Write-Host 'Purpose: LoRA / fine-tuning base model (not GGUF, not LoRA)'
Write-Host "Destination: $modelPath"
Write-Host ''
Write-Host 'The file is about 7.7 GB. Existing partial downloads are resumed when curl.exe is available.' -ForegroundColor Yellow
Write-Host ''

$curl = Get-Command curl.exe -ErrorAction SilentlyContinue

if ($curl) {
    & $curl.Source `
        --location `
        --fail `
        --retry 5 `
        --retry-delay 3 `
        --continue-at - `
        --output $modelPath `
        $url

    if ($LASTEXITCODE -ne 0) {
        throw 'SD 1.5 checkpoint download failed.'
    }
}
else {
    Write-Warning 'curl.exe not found. Falling back to Invoke-WebRequest (no resume support).'
    Invoke-WebRequest -Uri $url -OutFile $modelPath -UseBasicParsing
}

if (-not (Test-Path $modelPath)) {
    throw 'Downloaded checkpoint is missing.'
}

$size = (Get-Item $modelPath).Length
Write-Host "Downloaded bytes: $size"

if ($size -lt 7000000000) {
    throw 'Checkpoint is unexpectedly small. The download is probably incomplete.'
}

if (-not $SkipHash) {
    Write-Host 'Verifying SHA-256...' -ForegroundColor Yellow
    $actual = (Get-FileHash -Algorithm SHA256 -Path $modelPath).Hash.ToLowerInvariant()
    if ($actual -ne $expectedSha256) {
        throw "SHA-256 mismatch.`nExpected: $expectedSha256`nActual:   $actual"
    }
    Write-Host 'SHA-256: OK' -ForegroundColor Green
}

Write-Host ''
Write-Host 'SD 1.5 training checkpoint is ready.' -ForegroundColor Green
Write-Host ''
Write-Host 'In PocketAI -> Training -> Базовая модель choose:' -ForegroundColor Cyan
Write-Host $modelPath
