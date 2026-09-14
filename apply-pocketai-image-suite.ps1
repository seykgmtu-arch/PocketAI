param(
    [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = "Stop"

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Text
    )

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $utf8)
}

$patchRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRootFull = [System.IO.Path]::GetFullPath($RepoRoot)

$sln = Join-Path $repoRootFull "PocketAI.sln"

if (-not (Test-Path $sln)) {
    throw "PocketAI.sln not found in: $repoRootFull"
}

Write-Host ""
Write-Host "======================================="
Write-Host " PocketAI Image Suite combined patch"
Write-Host "======================================="
Write-Host ""
Write-Host "Repo:"
Write-Host $repoRootFull
Write-Host ""

# ================================================================
# 1. Add source files
# ================================================================
$sourceFiles = @(
    "src\PocketAI.Images\ImageGenerator.cs",
    "src\PocketAI.Images\ImageEngineManager.cs",
    "src\PocketAI.App\ViewModels\ImageGeneratorViewModel.cs",
    "src\PocketAI.App\Views\ImageGeneratorView.xaml",
    "src\PocketAI.App\Views\ImageGeneratorView.xaml.cs",
    "src\PocketAI.App\Views\PerchanceBrowserView.xaml",
    "src\PocketAI.App\Views\PerchanceBrowserView.xaml.cs"
)

foreach ($relative in $sourceFiles) {
    $source = Join-Path $patchRoot $relative
    $target = Join-Path $repoRootFull $relative

    New-Item `
        -ItemType Directory `
        -Path (Split-Path -Parent $target) `
        -Force | Out-Null

    Copy-Item $source $target -Force
    Write-Host "Added/updated: $relative"
}

# ================================================================
# 2. Create image runtime/model folders
# ================================================================
$layoutDirs = @(
    "runtime\image\cuda",
    "runtime\image\cpu",
    "models\images",
    "outputs\perchance"
)

foreach ($relative in $layoutDirs) {
    New-Item `
        -ItemType Directory `
        -Path (Join-Path $repoRootFull $relative) `
        -Force | Out-Null
}

$placeholderFiles = @(
    "runtime\image\cuda\PUT_SD_SERVER_CUDA_FILES_HERE.txt",
    "runtime\image\cpu\PUT_SD_SERVER_CPU_FILES_HERE.txt",
    "models\images\PUT_IMAGE_MODEL_HERE.txt"
)

foreach ($relative in $placeholderFiles) {
    $source = Join-Path $patchRoot $relative
    $target = Join-Path $repoRootFull $relative

    if ((Test-Path $source) -and -not (Test-Path $target)) {
        Copy-Item $source $target
        Write-Host "Added: $relative"
    }
}

# ================================================================
# 3. PocketAI.Images -> PocketAI.Hardware
# ================================================================
$imagesProject = Join-Path `
    $repoRootFull `
    "src\PocketAI.Images\PocketAI.Images.csproj"

$imagesProjectText = Get-Content $imagesProject -Raw

if ($imagesProjectText -notmatch "PocketAI\.Hardware") {
    $reference = @'
  <ItemGroup>
    <ProjectReference Include="..\PocketAI.Hardware\PocketAI.Hardware.csproj" />
  </ItemGroup>
'@

    $imagesProjectText =
        $imagesProjectText.Replace(
            "</Project>",
            "$reference</Project>"
        )

    Write-Utf8NoBom `
        -Path $imagesProject `
        -Text $imagesProjectText

    Write-Host "Updated: PocketAI.Images.csproj"
}

# ================================================================
# 4. Add stable WebView2 package centrally
# ================================================================
$packagesFile = Join-Path `
    $repoRootFull `
    "Directory.Packages.props"

$packagesText = Get-Content $packagesFile -Raw

if ($packagesText -notmatch 'PackageVersion Include="Microsoft\.Web\.WebView2"') {
    $packageLine = @'
    <PackageVersion Include="Microsoft.Web.WebView2" Version="1.0.4191.47" />
'@

    $packagesText =
        $packagesText.Replace(
            "  </ItemGroup>",
            "$packageLine  </ItemGroup>"
        )

    Write-Utf8NoBom `
        -Path $packagesFile `
        -Text $packagesText

    Write-Host "Added central NuGet version: Microsoft.Web.WebView2 1.0.4191.47"
}
else {
    Write-Host "WebView2 central package version already exists."
}

# ================================================================
# 5. Add WebView2 PackageReference to PocketAI.App
# ================================================================
$appProject = Join-Path `
    $repoRootFull `
    "src\PocketAI.App\PocketAI.App.csproj"

$appProjectText = Get-Content $appProject -Raw

if ($appProjectText -notmatch 'PackageReference Include="Microsoft\.Web\.WebView2"') {
    $packageReference = @'
  <ItemGroup>
    <PackageReference Include="Microsoft.Web.WebView2" />
  </ItemGroup>
'@

    $appProjectText =
        $appProjectText.Replace(
            "</Project>",
            "$packageReference</Project>"
        )

    Write-Utf8NoBom `
        -Path $appProject `
        -Text $appProjectText

    Write-Host "Updated: PocketAI.App.csproj"
}

# ================================================================
# 6. Replace local image tab and add Perchance Online tab
# ================================================================
$mainWindow = Join-Path `
    $repoRootFull `
    "src\PocketAI.App\MainWindow.xaml"

$mainWindowText = Get-Content $mainWindow -Raw

if ($mainWindowText -notmatch 'xmlns:views="clr-namespace:PocketAI\.App\.Views"') {
    $mainWindowText =
        $mainWindowText.Replace(
            'xmlns:vm="clr-namespace:PocketAI.App.ViewModels"',
            'xmlns:vm="clr-namespace:PocketAI.App.ViewModels" xmlns:views="clr-namespace:PocketAI.App.Views"'
        )
}

$imageTabPattern =
    '(?s)<TabItem Header="Изображения">.*?</TabItem>'

$imageTabReplacement =
    '<TabItem Header="🎨 LOCAL"><views:ImageGeneratorView/></TabItem>'

if ($mainWindowText -match $imageTabPattern) {
    $mainWindowText =
        [System.Text.RegularExpressions.Regex]::Replace(
            $mainWindowText,
            $imageTabPattern,
            $imageTabReplacement,
            1
        )
}
elseif ($mainWindowText -match '<TabItem Header="🎨 LOCAL">') {
    # Previous combined/local patch already applied.
}
else {
    throw "Could not find the existing image tab in MainWindow.xaml."
}

if ($mainWindowText -notmatch 'Header="🌐 Perchance Online"') {
    $localTab =
        '<TabItem Header="🎨 LOCAL"><views:ImageGeneratorView/></TabItem>'

    $onlineTab =
        '<TabItem Header="🌐 Perchance Online"><views:PerchanceBrowserView/></TabItem>'

    $mainWindowText =
        $mainWindowText.Replace(
            $localTab,
            $localTab + $onlineTab
        )
}

Write-Utf8NoBom `
    -Path $mainWindow `
    -Text $mainWindowText

Write-Host "Updated: MainWindow.xaml"
Write-Host "Tabs: Chat / LOCAL / Perchance Online"

# ================================================================
# 7. Restore and build
# ================================================================
Write-Host ""
Write-Host "Restoring and building..."

Push-Location $repoRootFull

try {
    dotnet restore .\PocketAI.sln

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed"
    }

    dotnet build `
        .\PocketAI.sln `
        -c Release `
        --no-restore

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed"
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "======================================="
Write-Host " COMBINED PATCH READY"
Write-Host "======================================="
Write-Host ""
Write-Host "LOCAL:"
Write-Host "  ImageGenerator + ImageEngineManager"
Write-Host "  CUDA auto / CPU fallback"
Write-Host "  backend size validation"
Write-Host ""
Write-Host "ONLINE:"
Write-Host "  WebView2 -> perchance.org/ai-drawing-generator"
Write-Host "  Download -> outputs\perchance\YYYY-MM-DD"
Write-Host "  Optional experimental auto-capture"
Write-Host ""
