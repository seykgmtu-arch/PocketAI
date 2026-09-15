param(
    [string]$PocketAIRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = [System.IO.Path]::GetFullPath($PocketAIRoot)
$trainingRoot = Join-Path $root 'runtime\image\training'
$repo = Join-Path $trainingRoot 'sd-scripts'
$venvPython = Join-Path $repo 'venv\Scripts\python.exe'

Write-Host "PocketAI root: $root" -ForegroundColor Cyan
Write-Host "Training runtime: $repo" -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $trainingRoot | Out-Null

function Resolve-Python {
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        try {
            & $py.Source -3.10 -c "import sys; print(sys.executable)" *> $null
            if ($LASTEXITCODE -eq 0) {
                return @($py.Source, '-3.10')
            }
        } catch {}
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        return @($python.Source)
    }

    throw "Python not found. Install Python 3.10 x64 first."
}

if ($Force -and (Test-Path $repo)) {
    Remove-Item $repo -Recurse -Force
}

if (-not (Test-Path (Join-Path $repo '.git'))) {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) {
        throw "Git not found. Install Git for Windows first."
    }

    Write-Host 'Cloning kohya-ss/sd-scripts...' -ForegroundColor Yellow
    & $git.Source clone --depth 1 https://github.com/kohya-ss/sd-scripts.git $repo

    if ($LASTEXITCODE -ne 0) {
        throw "git clone failed."
    }
}

$pythonCmd = Resolve-Python
$pythonExe = $pythonCmd[0]
$pythonPrefix = @()
if ($pythonCmd.Count -gt 1) {
    $pythonPrefix = $pythonCmd[1..($pythonCmd.Count - 1)]
}

if (-not (Test-Path $venvPython)) {
    Write-Host 'Creating Python venv...' -ForegroundColor Yellow
    & $pythonExe @pythonPrefix -m venv (Join-Path $repo 'venv')

    if ($LASTEXITCODE -ne 0) {
        throw "venv creation failed."
    }
}

Write-Host 'Updating pip...' -ForegroundColor Yellow
& $venvPython -m pip install --upgrade pip setuptools wheel

Write-Host 'Installing PyTorch CUDA 12.4 build...' -ForegroundColor Yellow
& $venvPython -m pip install `
    torch==2.6.0 torchvision==0.21.0 `
    --index-url https://download.pytorch.org/whl/cu124

if ($LASTEXITCODE -ne 0) {
    throw "PyTorch installation failed."
}

Write-Host 'Installing sd-scripts requirements...' -ForegroundColor Yellow
& $venvPython -m pip install --upgrade -r (Join-Path $repo 'requirements.txt')

if ($LASTEXITCODE -ne 0) {
    throw "sd-scripts requirements installation failed."
}

Write-Host ''
Write-Host 'Checking CUDA from PyTorch...' -ForegroundColor Yellow
& $venvPython -c "import torch; print('torch=', torch.__version__); print('cuda=', torch.cuda.is_available()); print('device=', torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'CPU')"

Write-Host ''
Write-Host 'PocketAI image training runtime is ready.' -ForegroundColor Green
Write-Host "Python: $venvPython"
