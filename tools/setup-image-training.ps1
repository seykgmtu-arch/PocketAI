param(
    [string]$PocketAIRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$Force,
    [switch]$NoUpdate
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = [System.IO.Path]::GetFullPath($PocketAIRoot)
$trainingRoot = Join-Path $root 'runtime\image\training'
$repo = Join-Path $trainingRoot 'sd-scripts'
$venvPython = Join-Path $repo 'venv\Scripts\python.exe'
$accelerateExe = Join-Path $repo 'venv\Scripts\accelerate.exe'
$accelerateConfig = Join-Path $repo 'accelerate-config.yaml'
$runtimeInfo = Join-Path $trainingRoot 'runtime-info.txt'

Write-Host ''
Write-Host 'PocketAI LoRA training runtime setup' -ForegroundColor Cyan
Write-Host "PocketAI root:    $root"
Write-Host "Training runtime: $repo"
Write-Host ''

New-Item -ItemType Directory -Force -Path $trainingRoot | Out-Null

function Resolve-Python310 {
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        try {
            $version = & $py.Source -3.10 -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')" 2>$null
            if ($LASTEXITCODE -eq 0 -and $version -like '3.10.*') {
                return @($py.Source, '-3.10')
            }
        } catch {}
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        try {
            $version = & $python.Source -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')" 2>$null
            if ($LASTEXITCODE -eq 0 -and $version -like '3.10.*') {
                return @($python.Source)
            }
        } catch {}
    }

    throw @"
Python 3.10 x64 не найден.

Установите Python 3.10.x (64-bit), затем снова запустите INSTALL-TRAINING.cmd.
При установке Python включите "Add python.exe to PATH".
"@
}

$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    throw @"
Git for Windows не найден.

Установите Git for Windows, затем снова запустите INSTALL-TRAINING.cmd.
"@
}

$pythonCmd = Resolve-Python310
$pythonExe = $pythonCmd[0]
$pythonPrefix = @()
if ($pythonCmd.Count -gt 1) {
    $pythonPrefix = $pythonCmd[1..($pythonCmd.Count - 1)]
}

if ($Force -and (Test-Path $repo)) {
    Write-Host 'Removing old training runtime (-Force)...' -ForegroundColor Yellow
    Remove-Item $repo -Recurse -Force
}

if (-not (Test-Path (Join-Path $repo '.git'))) {
    Write-Host 'Cloning kohya-ss/sd-scripts...' -ForegroundColor Yellow
    & $git.Source clone --depth 1 https://github.com/kohya-ss/sd-scripts.git $repo
    if ($LASTEXITCODE -ne 0) {
        throw 'git clone sd-scripts failed.'
    }
}
elseif (-not $NoUpdate) {
    Write-Host 'Updating sd-scripts (fast-forward only)...' -ForegroundColor Yellow
    Push-Location $repo
    try {
        & $git.Source pull --ff-only
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'sd-scripts update failed; continuing with the installed revision.'
        }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path $venvPython)) {
    Write-Host 'Creating Python 3.10 virtual environment...' -ForegroundColor Yellow
    & $pythonExe @pythonPrefix -m venv (Join-Path $repo 'venv')
    if ($LASTEXITCODE -ne 0) {
        throw 'Python venv creation failed.'
    }
}

Write-Host 'Updating pip / setuptools / wheel...' -ForegroundColor Yellow
& $venvPython -m pip install --upgrade pip setuptools wheel
if ($LASTEXITCODE -ne 0) {
    throw 'pip bootstrap failed.'
}

Write-Host 'Installing PyTorch 2.6.0 + CUDA 12.4 runtime...' -ForegroundColor Yellow
& $venvPython -m pip install `
    torch==2.6.0 torchvision==0.21.0 `
    --index-url https://download.pytorch.org/whl/cu124

if ($LASTEXITCODE -ne 0) {
    throw 'PyTorch installation failed.'
}

Write-Host 'Installing current sd-scripts requirements...' -ForegroundColor Yellow
& $venvPython -m pip install --upgrade -r (Join-Path $repo 'requirements.txt')
if ($LASTEXITCODE -ne 0) {
    throw 'sd-scripts requirements installation failed.'
}

Write-Host 'Creating non-interactive Accelerate fp16 config...' -ForegroundColor Yellow
$env:POCKETAI_ACCEL_CONFIG = $accelerateConfig
& $venvPython -c "import os; from accelerate.utils import write_basic_config; p=os.environ['POCKETAI_ACCEL_CONFIG']; write_basic_config(mixed_precision='fp16', save_location=p); print(p)"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $accelerateConfig)) {
    throw 'Accelerate configuration failed.'
}

Write-Host ''
Write-Host 'Checking CUDA from PyTorch...' -ForegroundColor Yellow
& $venvPython -c @"
import sys, torch
print('torch=', torch.__version__)
print('cuda_available=', torch.cuda.is_available())
print('cuda_runtime=', torch.version.cuda)
if not torch.cuda.is_available():
    raise SystemExit('CUDA is not available in PyTorch.')
print('device=', torch.cuda.get_device_name(0))
props = torch.cuda.get_device_properties(0)
print('vram_gb=', round(props.total_memory / 1024**3, 2))
"@
if ($LASTEXITCODE -ne 0) {
    throw 'PyTorch CUDA check failed. Update the NVIDIA driver and retry.'
}

if (Test-Path $accelerateExe) {
    Write-Host ''
    Write-Host 'Accelerate environment:' -ForegroundColor Yellow
    & $accelerateExe env --config_file $accelerateConfig
}

$commit = ''
try {
    Push-Location $repo
    $commit = (& $git.Source rev-parse HEAD).Trim()
}
finally {
    Pop-Location
}

@"
installed=$(Get-Date -Format o)
sd_scripts_commit=$commit
python=$venvPython
accelerate_config=$accelerateConfig
torch=2.6.0
torchvision=0.21.0
cuda_wheel=cu124
"@ | Set-Content -Encoding UTF8 $runtimeInfo

Write-Host ''
Write-Host 'TRAINING RUNTIME READY' -ForegroundColor Green
Write-Host "Python:            $venvPython"
Write-Host "Accelerate config: $accelerateConfig"
Write-Host "Info:              $runtimeInfo"
Write-Host ''
Write-Host 'Next: run DOWNLOAD-SD15-TRAINING-MODEL.cmd' -ForegroundColor Cyan
