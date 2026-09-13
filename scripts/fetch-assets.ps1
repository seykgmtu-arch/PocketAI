param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root '.cache\downloads'
$cpuDir = Join-Path $root 'runtime\llama\cpu'
$cudaDir = Join-Path $root 'runtime\llama\cuda'
$modelDir = Join-Path $root 'models\chat'
$licenseDir = Join-Path $root 'LICENSES'

$llamaTag = 'b10941'

$assets = @(
    [pscustomobject]@{
        Name = "llama-$llamaTag-bin-win-cpu-x64.zip"
        Url = "https://github.com/ggml-org/llama.cpp/releases/download/$llamaTag/llama-$llamaTag-bin-win-cpu-x64.zip"
        Sha256 = '033ab72aa6fc69059e7529affa383b93201b612abbe72ea39bba103560a81cc8'
        Destination = $cpuDir
        Kind = 'zip'
    },
    [pscustomobject]@{
        Name = "llama-$llamaTag-bin-win-cuda-12.4-x64.zip"
        Url = "https://github.com/ggml-org/llama.cpp/releases/download/$llamaTag/llama-$llamaTag-bin-win-cuda-12.4-x64.zip"
        Sha256 = 'ed4767e423cf629b180c9dab9fdd8abb242faa822402afb8fe586cc71a815eea'
        Destination = $cudaDir
        Kind = 'zip'
    },
    [pscustomobject]@{
        Name = 'cudart-llama-bin-win-cuda-12.4-x64.zip'
        Url = "https://github.com/ggml-org/llama.cpp/releases/download/$llamaTag/cudart-llama-bin-win-cuda-12.4-x64.zip"
        Sha256 = '8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6'
        Destination = $cudaDir
        Kind = 'zip'
    },
    [pscustomobject]@{
        Name = 'Qwen3-0.6B-Q8_0.gguf'
        Url = 'https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q8_0.gguf?download=true'
        Sha256 = '9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031'
        Destination = (Join-Path $modelDir 'model.gguf')
        Kind = 'file'
    }
)

function Ensure-Directory([string]$Path) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Download-Verified([object]$Asset) {
    $downloadPath = Join-Path $cache $Asset.Name

    if ((Test-Path $downloadPath) -and -not $Force) {
        $hash = Get-Sha256 $downloadPath
        if ($hash -eq $Asset.Sha256) {
            Write-Host "Using cached $($Asset.Name)" -ForegroundColor DarkGray
            return $downloadPath
        }
        Remove-Item $downloadPath -Force
    }

    Write-Host "Downloading $($Asset.Name)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $Asset.Url -OutFile $downloadPath -MaximumRedirection 10

    $actual = Get-Sha256 $downloadPath
    if ($actual -ne $Asset.Sha256) {
        Remove-Item $downloadPath -Force -ErrorAction SilentlyContinue
        throw "SHA-256 mismatch for $($Asset.Name). Expected $($Asset.Sha256), got $actual"
    }

    Write-Host "SHA-256 OK: $actual" -ForegroundColor Green
    return $downloadPath
}

Ensure-Directory $cache

if ($Force) {
    foreach ($dir in @($cpuDir, $cudaDir)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
    $modelFile = Join-Path $modelDir 'model.gguf'
    if (Test-Path $modelFile) { Remove-Item $modelFile -Force }
}

Ensure-Directory $cpuDir
Ensure-Directory $cudaDir
Ensure-Directory $modelDir
Ensure-Directory $licenseDir

# Clean placeholder files so a release tree contains only real assets.
Get-ChildItem $cpuDir -Filter 'PUT_*' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $cudaDir -Filter 'PUT_*' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $modelDir -Filter 'PUT_*' -ErrorAction SilentlyContinue | Remove-Item -Force

foreach ($asset in $assets) {
    $downloadPath = Download-Verified $asset

    if ($asset.Kind -eq 'zip') {
        Write-Host "Extracting $($asset.Name) -> $($asset.Destination)" -ForegroundColor DarkCyan
        Expand-Archive -Path $downloadPath -DestinationPath $asset.Destination -Force
    }
    else {
        Copy-Item $downloadPath $asset.Destination -Force
    }
}

# Include licenses/attribution required for redistributed third-party components.
$licenseDownloads = @(
    @{
        Url = "https://raw.githubusercontent.com/ggml-org/llama.cpp/$llamaTag/LICENSE"
        Path = (Join-Path $licenseDir 'llama.cpp-LICENSE')
    },
    @{
        Url = 'https://huggingface.co/Qwen/Qwen3-0.6B-GGUF/resolve/main/LICENSE'
        Path = (Join-Path $licenseDir 'Qwen3-0.6B-LICENSE')
    }
)

foreach ($license in $licenseDownloads) {
    try {
        Invoke-WebRequest -Uri $license.Url -OutFile $license.Path -MaximumRedirection 10
    }
    catch {
        Write-Warning "Could not download license file: $($license.Url). $($_.Exception.Message)"
    }
}

$manifest = [ordered]@{
    generatedUtc = [DateTime]::UtcNow.ToString('O')
    llamaCpp = [ordered]@{
        tag = $llamaTag
        commit = '4a89937354190cef5a97baf8eeb17336105eb72d'
        cpuSha256 = $assets[0].Sha256
        cudaSha256 = $assets[1].Sha256
        cudaRuntimeSha256 = $assets[2].Sha256
    }
    model = [ordered]@{
        repository = 'Qwen/Qwen3-0.6B-GGUF'
        file = 'Qwen3-0.6B-Q8_0.gguf'
        localFile = 'models/chat/model.gguf'
        sha256 = $assets[3].Sha256
        license = 'Apache-2.0'
    }
}

$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'assets.lock.json') -Encoding UTF8

Write-Host ''
Write-Host 'Assets ready.' -ForegroundColor Green
Write-Host "CPU:   $cpuDir"
Write-Host "CUDA:  $cudaDir"
Write-Host "Model: $(Join-Path $modelDir 'model.gguf')"
Write-Host "Lock:  $(Join-Path $root 'assets.lock.json')"
