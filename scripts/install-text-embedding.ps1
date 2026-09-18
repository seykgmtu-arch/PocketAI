$ErrorActionPreference = "Stop"

$Pocket = "F:\Pocket"
$Zip = "$Pocket\llama-embedding-b10964-win-cpu-x64.zip"
$Target = "$Pocket\runtime\text\embedding\cpu"
$Manifest = "$Pocket\runtime\text\text-lora-manifest.json"

Write-Host ""
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host " POCKETAI TEXT LORA - INSTALL EMBEDDING CPU" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $Zip)) {
    throw "Artifact ZIP not found: $Zip"
}

Get-Process PocketAI -ErrorAction SilentlyContinue |
    Stop-Process -Force

if (Test-Path $Target) {
    Remove-Item $Target -Recurse -Force
}

New-Item `
    -ItemType Directory `
    -Path $Target `
    -Force |
    Out-Null

Expand-Archive `
    -Path $Zip `
    -DestinationPath $Target `
    -Force

$Exe = "$Target\llama-embedding.exe"

if (-not (Test-Path $Exe)) {
    throw "llama-embedding.exe not found after extraction."
}

Write-Host "[1/2] VERSION" -ForegroundColor Yellow

Push-Location $Target
try {
    & $Exe --version
    if ($LASTEXITCODE -ne 0) {
        throw "llama-embedding.exe --version failed: $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

if (Test-Path $Manifest) {
    try {
        $m = Get-Content $Manifest -Raw | ConvertFrom-Json

        if ($null -ne $m.existingRag) {
            $m.existingRag.embeddingExe = $Exe
            $m.existingRag.embeddingExeExists = $true
        }

        $m |
            ConvertTo-Json -Depth 20 |
            Set-Content `
                $Manifest `
                -Encoding UTF8
    }
    catch {
        Write-Warning "Manifest was not updated: $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "[2/2] INSTALLED" -ForegroundColor Yellow
Write-Host "EXE: $Exe"
Write-Host "MODEL: $Pocket\models\embeddings\model.gguf"
Write-Host "VECTORS: $Pocket\knowledge\vectors.json"

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host " TEXT EMBEDDING CPU RUNTIME READY" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Next:"
Write-Host "$Pocket\runtime\text\local-rag\RUN-LOCAL-NATIVE-RAG.cmd"
