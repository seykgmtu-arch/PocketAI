using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace PocketAI.App.Views;

public partial class PerchanceBrowserView : UserControl
{
    private static readonly Uri HomeUri =
        new("https://perchance.org/ai-drawing-generator");

    private readonly string _baseDirectory =
        AppContext.BaseDirectory;

    private readonly string _outputDirectory;
    private readonly string _profileDirectory;
    private readonly HashSet<string> _capturedKeys =
        new(StringComparer.Ordinal);

    private readonly HttpClient _http =
        new()
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

    private bool _initialized;
    private bool _autoCaptureEnabled;

    public PerchanceBrowserView()
    {
        InitializeComponent();

        _outputDirectory = Path.Combine(
            _baseDirectory,
            "outputs",
            "perchance");

        _profileDirectory = Path.Combine(
            _baseDirectory,
            "webview2",
            "perchance-profile");

        Directory.CreateDirectory(_outputDirectory);
        Directory.CreateDirectory(_profileDirectory);

        Loaded += PerchanceBrowserView_OnLoaded;
        Unloaded += PerchanceBrowserView_OnUnloaded;
    }

    private async void PerchanceBrowserView_OnLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialized)
            return;

        try
        {
            StatusText.Text =
                "Запускаем изолированный профиль Perchance…";

            var environment =
                await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: _profileDirectory);

            await Browser.EnsureCoreWebView2Async(environment);

            var core = Browser.CoreWebView2;

            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;

            core.NavigationStarting += Core_NavigationStarting;
            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.DownloadStarting += Core_DownloadStarting;
            core.WebMessageReceived += Core_WebMessageReceived;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                AutoCaptureScript);

            _initialized = true;
            Browser.Source = HomeUri;

            StatusText.Text =
                "Perchance Online загружается. " +
                "Кнопка Download на сайте сохраняет файлы локально.";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "WebView2 не запустился: " +
                Friendly(ex) +
                ". Установите Microsoft Edge WebView2 Runtime.";
        }
    }

    private void PerchanceBrowserView_OnUnloaded(
        object sender,
        RoutedEventArgs e)
    {
        // WebView2 keeps its local profile between tab switches.
        // The HttpClient is intentionally kept alive with the control.
    }

    private void Core_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(
                e.Uri,
                UriKind.Absolute,
                out var uri))
        {
            return;
        }

        if (IsAllowedPerchanceUri(uri))
        {
            StatusText.Text =
                "Загрузка: " + uri.Host;
            return;
        }

        if (uri.Scheme is "http" or "https")
        {
            e.Cancel = true;

            try
            {
                Process.Start(
                    new ProcessStartInfo(uri.AbsoluteUri)
                    {
                        UseShellExecute = true
                    });

                StatusText.Text =
                    "Внешняя ссылка открыта в системном браузере.";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    "Не удалось открыть внешнюю ссылку: " +
                    Friendly(ex);
            }
        }
    }

    private void Core_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        StatusText.Text =
            e.IsSuccess
                ? "Perchance Online готов. Download → outputs\\perchance."
                : $"Ошибка навигации: {e.WebErrorStatus}.";
    }

    private void Core_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (!Uri.TryCreate(
                e.Uri,
                UriKind.Absolute,
                out var uri))
        {
            return;
        }

        if (IsAllowedPerchanceUri(uri))
        {
            Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
            return;
        }

        try
        {
            Process.Start(
                new ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
        }
        catch
        {
        }
    }

    private void Core_DownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        Directory.CreateDirectory(_outputDirectory);

        var originalName =
            Path.GetFileName(e.ResultFilePath);

        var extension =
            Path.GetExtension(originalName);

        if (string.IsNullOrWhiteSpace(extension))
            extension = ".png";

        var targetPath = NextPath(extension);

        e.ResultFilePath = targetPath;
        e.Handled = true;

        StatusText.Text =
            "Сохраняем: " + Path.GetFileName(targetPath);

        var operation = e.DownloadOperation;

        operation.StateChanged += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                switch (operation.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        StatusText.Text =
                            "Сохранено: " + targetPath;

                        SaveMetadata(
                            targetPath,
                            operation.Uri,
                            "webview2-download");
                        break;

                    case CoreWebView2DownloadState.Interrupted:
                        StatusText.Text =
                            "Загрузка прервана: " +
                            operation.InterruptReason;
                        break;
                }
            });
        };
    }

    private async void Core_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_autoCaptureEnabled)
            return;

        try
        {
            using var document =
                JsonDocument.Parse(e.WebMessageAsJson);

            var root = document.RootElement;

            if (!root.TryGetProperty(
                    "type",
                    out var typeElement) ||
                typeElement.GetString() !=
                "pocketai-perchance-image")
            {
                return;
            }

            var src =
                root.TryGetProperty(
                    "src",
                    out var srcElement)
                    ? srcElement.GetString()
                    : null;

            var dataUrl =
                root.TryGetProperty(
                    "dataUrl",
                    out var dataElement)
                    ? dataElement.GetString()
                    : null;

            var width =
                root.TryGetProperty(
                    "width",
                    out var widthElement)
                    ? widthElement.GetInt32()
                    : 0;

            var height =
                root.TryGetProperty(
                    "height",
                    out var heightElement)
                    ? heightElement.GetInt32()
                    : 0;

            if (width < 256 || height < 256)
                return;

            var key =
                $"{src}|{width}x{height}";

            if (!_capturedKeys.Add(key))
                return;

            string? savedPath = null;

            if (!string.IsNullOrWhiteSpace(dataUrl) &&
                dataUrl.StartsWith(
                    "data:image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                savedPath =
                    await SaveDataUrlAsync(dataUrl);
            }
            else if (
                !string.IsNullOrWhiteSpace(src) &&
                Uri.TryCreate(
                    src,
                    UriKind.Absolute,
                    out var imageUri) &&
                imageUri.Scheme is "http" or "https")
            {
                savedPath =
                    await SaveHttpImageAsync(imageUri);
            }

            if (!string.IsNullOrWhiteSpace(savedPath))
            {
                StatusText.Text =
                    "Автосохранение: " +
                    Path.GetFileName(savedPath);

                SaveMetadata(
                    savedPath,
                    src ?? string.Empty,
                    "webview2-auto-capture");
            }
        }
        catch
        {
            // Auto-capture is best-effort and must never
            // interfere with the Perchance page itself.
        }
    }

    private async Task<string?> SaveDataUrlAsync(
        string dataUrl)
    {
        var commaIndex = dataUrl.IndexOf(',');

        if (commaIndex <= 0)
            return null;

        var header = dataUrl[..commaIndex];
        var payload = dataUrl[(commaIndex + 1)..];

        if (!header.Contains(
                ";base64",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var extension =
            header.Contains(
                "image/jpeg",
                StringComparison.OrdinalIgnoreCase)
                ? ".jpg"
                : header.Contains(
                    "image/webp",
                    StringComparison.OrdinalIgnoreCase)
                    ? ".webp"
                    : ".png";

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length < 1024)
            return null;

        var path = NextPath(extension);

        await File.WriteAllBytesAsync(
            path,
            bytes);

        return path;
    }

    private async Task<string?> SaveHttpImageAsync(
        Uri imageUri)
    {
        using var response =
            await _http.GetAsync(imageUri);

        if (!response.IsSuccessStatusCode)
            return null;

        var mediaType =
            response.Content.Headers.ContentType?.MediaType;

        var extension =
            mediaType?.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".png"
            };

        var bytes =
            await response.Content.ReadAsByteArrayAsync();

        if (bytes.Length < 1024)
            return null;

        var path = NextPath(extension);

        await File.WriteAllBytesAsync(
            path,
            bytes);

        return path;
    }

    private string NextPath(string extension)
    {
        var dayDirectory =
            Path.Combine(
                _outputDirectory,
                DateTime.Now.ToString("yyyy-MM-dd"));

        Directory.CreateDirectory(dayDirectory);

        return Path.Combine(
            dayDirectory,
            $"Perchance-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{extension}");
    }

    private void SaveMetadata(
        string imagePath,
        string source,
        string captureMode)
    {
        try
        {
            var metadata = new
            {
                source = "Perchance AI Drawing Generator",
                page = HomeUri.AbsoluteUri,
                capturedAt = DateTimeOffset.Now,
                captureMode,
                originalSource = source,
                localFile = imagePath
            };

            var jsonPath =
                Path.ChangeExtension(
                    imagePath,
                    ".json");

            File.WriteAllText(
                jsonPath,
                JsonSerializer.Serialize(
                    metadata,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
        }
        catch
        {
        }
    }

    private async void AutoCapture_OnChanged(
        object sender,
        RoutedEventArgs e)
    {
        _autoCaptureEnabled =
            AutoCaptureCheckBox.IsChecked == true;

        if (!_initialized)
            return;

        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(
                $"window.__pocketAiAutoCaptureEnabled = {(_autoCaptureEnabled ? "true" : "false")};");

            StatusText.Text =
                _autoCaptureEnabled
                    ? "Автосохранение включено (экспериментально). Download-перехват работает всегда."
                    : "Автосохранение выключено. Download-перехват работает всегда.";
        }
        catch
        {
        }
    }

    private void Back_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialized && Browser.CanGoBack)
            Browser.GoBack();
    }

    private void Forward_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialized && Browser.CanGoForward)
            Browser.GoForward();
    }

    private void Reload_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialized)
            Browser.Reload();
    }

    private void Home_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialized)
            Browser.CoreWebView2.Navigate(
                HomeUri.AbsoluteUri);
    }

    private void OpenExternal_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        Process.Start(
            new ProcessStartInfo(
                Browser.Source?.AbsoluteUri ??
                HomeUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
    }

    private void OpenFolder_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        Directory.CreateDirectory(_outputDirectory);

        Process.Start(
            new ProcessStartInfo(
                "explorer.exe",
                _outputDirectory)
            {
                UseShellExecute = true
            });
    }

    private static bool IsAllowedPerchanceUri(
        Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
            return false;

        return
            uri.Host.Equals(
                "perchance.org",
                StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(
                ".perchance.org",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string Friendly(Exception exception)
    {
        var root = exception.GetBaseException();

        return string.IsNullOrWhiteSpace(root.Message)
            ? root.GetType().Name
            : root.Message;
    }

    private const string AutoCaptureScript = """
(() => {
  if (window.__pocketAiCaptureInstalled) return;
  window.__pocketAiCaptureInstalled = true;
  window.__pocketAiAutoCaptureEnabled = false;

  const seen = new Set();

  async function toDataUrl(src) {
    if (!src) return null;
    if (src.startsWith('data:image/')) return src;

    try {
      const response = await fetch(src);
      const blob = await response.blob();

      if (!blob.type || !blob.type.startsWith('image/')) {
        return null;
      }

      return await new Promise((resolve) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => resolve(null);
        reader.readAsDataURL(blob);
      });
    } catch {
      return null;
    }
  }

  async function inspectImage(img) {
    if (!window.__pocketAiAutoCaptureEnabled) return;
    if (!(img instanceof HTMLImageElement)) return;

    const width = img.naturalWidth || img.width || 0;
    const height = img.naturalHeight || img.height || 0;

    if (width < 256 || height < 256) return;

    const src = img.currentSrc || img.src || '';
    if (!src) return;

    const key = `${src}|${width}x${height}`;
    if (seen.has(key)) return;
    seen.add(key);

    const dataUrl = await toDataUrl(src);

    try {
      chrome.webview.postMessage({
        type: 'pocketai-perchance-image',
        src,
        dataUrl,
        width,
        height
      });
    } catch {}
  }

  function scan(root) {
    if (!root) return;

    if (root instanceof HTMLImageElement) {
      inspectImage(root);
    }

    if (root.querySelectorAll) {
      root.querySelectorAll('img').forEach(inspectImage);
    }
  }

  document.addEventListener('load', (event) => {
    if (event.target instanceof HTMLImageElement) {
      inspectImage(event.target);
    }
  }, true);

  const observer = new MutationObserver((mutations) => {
    if (!window.__pocketAiAutoCaptureEnabled) return;

    for (const mutation of mutations) {
      mutation.addedNodes.forEach(scan);
    }
  });

  const begin = () => {
    if (!document.documentElement) {
      setTimeout(begin, 100);
      return;
    }

    observer.observe(document.documentElement, {
      childList: true,
      subtree: true
    });

    scan(document);
  };

  begin();
})();
""";
}
