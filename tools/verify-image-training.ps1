param(
    [string]$PocketAIRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PocketAIRoot)) {
    $PocketAIRoot = Split-Path -Parent $PSScriptRoot
}

# powershell.exe + a quoted path ending with "\" may leave a literal quote
# in a parameter on some Windows command-line combinations.
$PocketAIRoot = $PocketAIRoot.Trim().Trim([char]34)
$root = [System.IO.Path]::GetFullPath($PocketAIRoot)
$repo = Join-Path $root 'runtime\image\training\sd-scripts'
$python = Join-Path $repo 'venv\Scripts\python.exe'
$config = Join-Path $repo 'accelerate-config.yaml'
$model = Join-Path $root 'models\training\sd15\v1-5-pruned.safetensors'

Write-Host ''
Write-Host 'PocketAI image training verification' -ForegroundColor Cyan

foreach ($path in @(
    $python,
    (Join-Path $repo 'train_network.py'),
    (Join-Path $repo 'sdxl_train_network.py'),
    $config
)) {
    if (-not (Test-Path $path)) {
        throw "Missing: $path"
    }
    Write-Host "OK: $path" -ForegroundColor Green
}

& $python -c @"
import torch, accelerate, diffusers, safetensors
print('torch=', torch.__version__)
print('accelerate=', accelerate.__version__)
print('diffusers=', diffusers.__version__)
print('cuda=', torch.cuda.is_available())
if not torch.cuda.is_available():
    raise SystemExit(2)
print('gpu=', torch.cuda.get_device_name(0))
print('vram_gb=', round(torch.cuda.get_device_properties(0).total_memory/1024**3, 2))
"@

if ($LASTEXITCODE -ne 0) {
    throw 'Python/CUDA training environment verification failed.'
}

if (Test-Path $model) {
    Write-Host "Training model: $model" -ForegroundColor Green
}
else {
    Write-Warning 'SD1.5 training checkpoint not found yet. Run DOWNLOAD-SD15-TRAINING-MODEL.cmd.'
}

Write-Host ''
Write-Host 'TRAINING_RUNTIME_OK' -ForegroundColor Green
