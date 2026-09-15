$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist\PocketAI'
if (-not [IO.Path]::GetFullPath($dist).StartsWith([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar)) {
    throw 'Publish directory must remain inside the repository.'
}

if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Push-Location $root
try {
    dotnet restore .\PocketAI.sln
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    dotnet publish .\src\PocketAI.App\PocketAI.App.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -o $dist

    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    New-Item -ItemType Directory -Force -Path (Join-Path $dist 'runtime') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dist 'models') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dist 'logs') | Out-Null

    if (Test-Path (Join-Path $root 'runtime')) {
        Copy-Item (Join-Path $root 'runtime\*') (Join-Path $dist 'runtime') -Recurse -Force
    }

    if (Test-Path (Join-Path $root 'models')) {
        Copy-Item (Join-Path $root 'models\*') (Join-Path $dist 'models') -Recurse -Force
    }

    foreach ($directory in @('tools', 'presets')) {
        $source = Join-Path $root $directory
        if (Test-Path $source) {
            Copy-Item -LiteralPath $source -Destination $dist -Recurse -Force
        }
    }

    foreach ($item in @(
        'INSTALL-TRAINING.cmd',
        'DOWNLOAD-SD15-TRAINING-MODEL.cmd',
        'VERIFY-TRAINING.cmd',
        'FIRST_LORA_RU.md',
        'LICENSES',
        'assets.lock.json',
        'THIRD_PARTY.md',
        'MILESTONE2.md',
        'MILESTONE3.md'
    )) {
        $source = Join-Path $root $item
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $dist -Recurse -Force
        }
    }

    Write-Host ''
    Write-Host 'Publish complete:' -ForegroundColor Green
    Write-Host $dist
    Write-Host ''
    Write-Host 'Runtime/model assets and training setup tools copied into the portable publish directory.'
}
finally {
    Pop-Location
}
