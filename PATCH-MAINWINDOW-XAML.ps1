param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$path = Join-Path $RepoRoot 'src\PocketAI.App\MainWindow.xaml'

if (-not (Test-Path $path)) {
    throw "MainWindow.xaml not found: $path"
}

$text = [System.IO.File]::ReadAllText($path)

if ($text.Contains('x:Name="TextTab"')) {
    Write-Host 'MainWindow.xaml already contains safe module tabs.' -ForegroundColor Green
    exit 0
}

$anchor = @'
                    <TabItem Header="🌐 Perchance Online">
                        <views:PerchanceBrowserView/>
                    </TabItem>
'@

$insert = @'
                    <TabItem x:Name="TextTab"
                             Header="📝 Text LoRA">
                        <ContentControl x:Name="TextHost">
                            <TextBlock Text="Text LoRA будет загружен при выборе вкладки."
                                       Foreground="{StaticResource MutedTextBrush}"
                                       Margin="12"
                                       TextWrapping="Wrap"/>
                        </ContentControl>
                    </TabItem>

                    <TabItem x:Name="AudioTab"
                             Header="🎵 Audio">
                        <ContentControl x:Name="AudioHost">
                            <TextBlock Text="Audio будет загружен при выборе вкладки."
                                       Foreground="{StaticResource MutedTextBrush}"
                                       Margin="12"
                                       TextWrapping="Wrap"/>
                        </ContentControl>
                    </TabItem>

                    <TabItem x:Name="VideoTab"
                             Header="🎬 Video">
                        <ContentControl x:Name="VideoHost">
                            <TextBlock Text="Video будет загружен при выборе вкладки."
                                       Foreground="{StaticResource MutedTextBrush}"
                                       Margin="12"
                                       TextWrapping="Wrap"/>
                        </ContentControl>
                    </TabItem>

                    <TabItem Header="🌐 Perchance Online">
                        <views:PerchanceBrowserView/>
                    </TabItem>
'@

if (-not $text.Contains($anchor)) {
    throw 'Perchance tab anchor not found. MainWindow.xaml differs from expected current version.'
}

$text = $text.Replace($anchor, $insert)

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($path, $text, $utf8)

Write-Host 'MainWindow.xaml safe module tabs inserted.' -ForegroundColor Green
