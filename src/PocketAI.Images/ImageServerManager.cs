using System.Diagnostics;

namespace PocketAI.Images;

public enum ImageRuntimeBackend
{
    Unknown = 0,
    Cpu = 1,
    Cuda = 2
}

public enum ImageModelFamily
{
    Unknown = 0,
    Sd15 = 1,
    Sdxl = 2
}

public enum ImageGenerationMode
{
    Native = 0,
    HiRes = 1,
    Auto = 2
}

public sealed class ImageProfile
{
    public static readonly ImageProfile Sd15Fast512 =
        new(
            "SD 1.5 / compatible",
            ImageModelFamily.Sd15,
            512,
            512,
            new[]
            {
                "sd15", "sd-1.5", "sd1.5", "sd_1_5",
                "v1-5", "v1_5", "1.5", "sd-v1"
            });

    public static readonly ImageProfile Sdxl1024 =
        new(
            "SDXL 1024",
            ImageModelFamily.Sdxl,
            1024,
            1024,
            new[]
            {
                "sdxl", "xl-base", "xl_base",
                "sd_xl", "sd-xl"
            });

    public ImageProfile(
        string name,
        ImageModelFamily family,
        int width,
        int height,
        IReadOnlyList<string> preferredKeywords)
    {
        Name = name;
        Family = family;
        Width = width;
        Height = height;
        PreferredKeywords = preferredKeywords;
    }

    public string Name { get; }
    public ImageModelFamily Family { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<string> PreferredKeywords { get; }

    public override string ToString() => Name;
}

public sealed record ImageModelInfo(
    string FilePath,
    string FileName,
    ImageModelFamily Family);

public sealed record ImageLoraInfo(
    string FilePath,
    string ApiPath,
    string FileName)
{
    public override string ToString() => FileName;
}

public sealed record ImageServerSession(
    Uri ServerUri,
    string RuntimePath,
    string ModelPath,
    ImageRuntimeBackend Backend,
    string ModelLabel,
    bool ReusedExistingServer);

public sealed class ImageServerManager : IDisposable
{
    private readonly string _baseDirectory;
    private readonly object _sync = new();

    private Process? _process;

    public ImageServerManager(string baseDirectory)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
    }

    public ImageServerSession? CurrentSession { get; private set; }

    public string ImageModelsDirectory =>
        Path.Combine(_baseDirectory, "models", "images");

    public string ImageLorasDirectory =>
        Path.Combine(ImageModelsDirectory, "loras");

    public string? ResolveRuntimePath(bool preferCuda)
    {
        var candidates = preferCuda
            ? new[]
            {
                Path.Combine(
                    _baseDirectory,
                    "runtime",
                    "image",
                    "cuda",
                    "sd-server.exe"),
                Path.Combine(
                    _baseDirectory,
                    "runtime",
                    "image",
                    "sd-server.exe"),
                Path.Combine(
                    _baseDirectory,
                    "runtime",
                    "image",
                    "cpu",
                    "sd-server.exe")
            }
            : new[]
            {
                Path.Combine(
                    _baseDirectory,
                    "runtime",
                    "image",
                    "cpu",
                    "sd-server.exe"),
                Path.Combine(
                    _baseDirectory,
                    "runtime",
                    "image",
                    "sd-server.exe")
            };

        return candidates.FirstOrDefault(File.Exists);
    }

    public IReadOnlyList<ImageModelInfo> GetAvailableModels()
    {
        if (!Directory.Exists(ImageModelsDirectory))
            return Array.Empty<ImageModelInfo>();

        var supported =
            new HashSet<string>(
                new[] { ".safetensors", ".ckpt", ".gguf" },
                StringComparer.OrdinalIgnoreCase);

        return Directory
            .EnumerateFiles(ImageModelsDirectory)
            .Where(path => supported.Contains(Path.GetExtension(path)))
            .Select(path =>
                new ImageModelInfo(
                    path,
                    Path.GetFileName(path),
                    DetectModelFamily(path)))
            .OrderBy(
                x => x.FileName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ImageLoraInfo> GetAvailableLoras()
    {
        if (!Directory.Exists(ImageLorasDirectory))
            return Array.Empty<ImageLoraInfo>();

        return Directory
            .EnumerateFiles(
                ImageLorasDirectory,
                "*.safetensors",
                SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative =
                    Path.GetRelativePath(
                        ImageLorasDirectory,
                        path)
                    .Replace('\\', '/');

                return new ImageLoraInfo(
                    path,
                    relative,
                    Path.GetFileNameWithoutExtension(path));
            })
            .OrderBy(
                x => x.FileName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string? ResolveModel(ImageProfile profile)
    {
        var models = GetAvailableModels();

        var familyMatches = models
            .Where(x => x.Family == profile.Family)
            .ToArray();

        foreach (var keyword in profile.PreferredKeywords)
        {
            var match = familyMatches.FirstOrDefault(
                x => x.FileName.Contains(
                    keyword,
                    StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return match.FilePath;
        }

        if (familyMatches.Length > 0)
            return familyMatches[0].FilePath;

        // Совместимость со старым PocketAI:
        // если установлена ровно одна модель неизвестного семейства,
        // разрешаем использовать её только как SD1.5-compatible.
        if (profile.Family == ImageModelFamily.Sd15)
        {
            var unknown = models
                .Where(x => x.Family == ImageModelFamily.Unknown)
                .ToArray();

            if (unknown.Length == 1)
                return unknown[0].FilePath;
        }

        // ВАЖНО: для SDXL больше нет fallback на "первый файл".
        return null;
    }

    public async Task<ImageServerSession> EnsureStartedAsync(
        Uri serverUri,
        bool preferCuda,
        ImageProfile profile,
        int threads,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!serverUri.IsLoopback)
        {
            if (await IsServerAliveAsync(serverUri, ct))
            {
                var external =
                    CreateExternalSession(serverUri);

                CurrentSession = external;
                progress?.Report(
                    "Используем внешний image server");

                return external;
            }

            throw new InvalidOperationException(
                "Внешний image server недоступен.");
        }

        Directory.CreateDirectory(ImageLorasDirectory);

        var desiredRuntime =
            ResolveRuntimePath(preferCuda);

        var desiredModel =
            ResolveModel(profile);

        if (IsLocalProcessRunning() &&
            CurrentSession is not null)
        {
            var sameRuntime =
                desiredRuntime is not null &&
                CurrentSession.RuntimePath.Equals(
                    desiredRuntime,
                    StringComparison.OrdinalIgnoreCase);

            var sameModel =
                desiredModel is not null &&
                CurrentSession.ModelPath.Equals(
                    desiredModel,
                    StringComparison.OrdinalIgnoreCase);

            var sameUri =
                CurrentSession.ServerUri == serverUri;

            if (sameRuntime && sameModel && sameUri)
            {
                if (!await IsServerAliveAsync(serverUri, ct))
                    await WaitForServerAsync(serverUri, ct);

                progress?.Report(
                    "Используем уже запущенный локальный image server");

                return CurrentSession with
                {
                    ReusedExistingServer = true
                };
            }

            progress?.Report(
                "Перезапускаем image server для выбранной модели…");

            StopLocalProcess();
            await WaitUntilServerStopsAsync(
                serverUri,
                ct);
        }

        // Если сервер уже доступен, но не запущен этим экземпляром PocketAI,
        // второй CUDA-процесс не создаём.
        if (await IsServerAliveAsync(serverUri, ct))
        {
            var external =
                CreateExternalSession(serverUri);

            CurrentSession = external;

            progress?.Report(
                "Используем уже доступный image server; второй CUDA server не запускается");

            return external;
        }

        if (desiredRuntime is null)
        {
            throw new FileNotFoundException(
                "sd-server.exe не найден. Для CUDA ожидается " +
                "runtime\\image\\cuda\\sd-server.exe.");
        }

        if (desiredModel is null)
        {
            var family =
                profile.Family == ImageModelFamily.Sdxl
                    ? "SDXL"
                    : "SD 1.5";

            throw new FileNotFoundException(
                $"Модель семейства {family} для профиля «{profile.Name}» " +
                "не найдена в models\\images. " +
                "PocketAI больше не подставляет модель другого семейства автоматически.");
        }

        var backend =
            DetectBackend(desiredRuntime);

        progress?.Report(
            $"Запуск {backend} image server · " +
            $"{Path.GetFileName(desiredModel)}");

        var psi = new ProcessStartInfo
        {
            FileName = desiredRuntime,
            WorkingDirectory =
                Path.GetDirectoryName(desiredRuntime)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[]
        {
            "--listen-ip", "127.0.0.1",
            "--listen-port", serverUri.Port.ToString(),
            "-m", desiredModel,
            "--threads", Math.Max(1, threads).ToString(),

            // Экономит VRAM при VAE decode больших изображений.
            "--vae-tiling",

            // Каталог LoRA для структурированного sdapi-параметра lora[].
            "--lora-model-dir", ImageLorasDirectory
        })
        {
            psi.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        // Не даём серверу зависнуть из-за заполненных stdout/stderr pipes.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Не удалось запустить sd-server.exe.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_sync)
        {
            _process = process;

            CurrentSession =
                new ImageServerSession(
                    serverUri,
                    desiredRuntime,
                    desiredModel,
                    backend,
                    Path.GetFileName(desiredModel),
                    ReusedExistingServer: false);
        }

        try
        {
            await WaitForServerAsync(
                serverUri,
                ct);
        }
        catch
        {
            StopLocalProcess();
            throw;
        }

        progress?.Report(
            $"Image server готов · {backend} · " +
            $"{Path.GetFileName(desiredModel)}");

        return CurrentSession!;
    }

    private ImageServerSession CreateExternalSession(
        Uri serverUri)
    {
        return new ImageServerSession(
            serverUri,
            RuntimePath: "(external)",
            ModelPath: "(external)",
            Backend: ImageRuntimeBackend.Unknown,
            ModelLabel: "(external server)",
            ReusedExistingServer: true);
    }

    private static ImageModelFamily DetectModelFamily(
        string modelPath)
    {
        var name =
            Path.GetFileName(modelPath);

        if (name.Contains(
                "sdxl",
                StringComparison.OrdinalIgnoreCase) ||
            name.Contains(
                "xl-base",
                StringComparison.OrdinalIgnoreCase) ||
            name.Contains(
                "xl_base",
                StringComparison.OrdinalIgnoreCase) ||
            name.Contains(
                "sd_xl",
                StringComparison.OrdinalIgnoreCase) ||
            name.Contains(
                "sd-xl",
                StringComparison.OrdinalIgnoreCase))
        {
            return ImageModelFamily.Sdxl;
        }

        if (name.Contains(
                "sd15",
                StringComparison.OrdinalIgnoreCase) ||
            name.Contains(
                "sd-1.5",
                StringComparison.OrdinalIgnoreCase) ||
            name.Contains(
                "sd1.5",
                StringComparison.OrdinalIgnoreCase) ||
            name.Contains(
                "v1-5",
                StringComparison.OrdinalIgnoreCase) ||
            name.Contains(
                "v1_5",
                StringComparison.OrdinalIgnoreCase) ||
            name.Contains(
                "sd-v1",
                StringComparison.OrdinalIgnoreCase))
        {
            return ImageModelFamily.Sd15;
        }

        return ImageModelFamily.Unknown;
    }

    private static ImageRuntimeBackend DetectBackend(
        string runtimePath)
    {
        var cudaMarker =
            Path.DirectorySeparatorChar +
            "cuda" +
            Path.DirectorySeparatorChar;

        var cpuMarker =
            Path.DirectorySeparatorChar +
            "cpu" +
            Path.DirectorySeparatorChar;

        if (runtimePath.Contains(
                cudaMarker,
                StringComparison.OrdinalIgnoreCase))
        {
            return ImageRuntimeBackend.Cuda;
        }

        if (runtimePath.Contains(
                cpuMarker,
                StringComparison.OrdinalIgnoreCase))
        {
            return ImageRuntimeBackend.Cpu;
        }

        return ImageRuntimeBackend.Unknown;
    }

    private bool IsLocalProcessRunning()
    {
        lock (_sync)
        {
            return _process is { HasExited: false };
        }
    }

    public void StopManagedServer()
    {
        StopLocalProcess();
    }

    private void StopLocalProcess()
    {
        Process? process;

        lock (_sync)
        {
            process = _process;
            _process = null;
            CurrentSession = null;
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);

                process.WaitForExit(3000);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task<bool> IsServerAliveAsync(
        Uri serverUri,
        CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = serverUri,
                Timeout = TimeSpan.FromSeconds(2)
            };

            using var response =
                await client.GetAsync(
                    "v1/models",
                    ct);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task WaitForServerAsync(
        Uri serverUri,
        CancellationToken ct)
    {
        var deadline =
            DateTime.UtcNow.AddMinutes(5);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await IsServerAliveAsync(
                    serverUri,
                    ct))
            {
                return;
            }

            await Task.Delay(
                500,
                ct);
        }

        throw new TimeoutException(
            "Image server не стал готов за 5 минут.");
    }

    private static async Task WaitUntilServerStopsAsync(
        Uri serverUri,
        CancellationToken ct)
    {
        var deadline =
            DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!await IsServerAliveAsync(
                    serverUri,
                    ct))
            {
                return;
            }

            await Task.Delay(
                250,
                ct);
        }
    }

    public void Dispose()
    {
        StopLocalProcess();
    }
}
