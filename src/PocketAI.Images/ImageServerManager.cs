using System.Diagnostics;
using System.Net;

namespace PocketAI.Images;

public enum ImageRuntimeBackend
{
    Unknown = 0,
    Cpu = 1,
    Cuda = 2
}

public sealed class ImageProfile
{
    public static readonly ImageProfile Sd15Fast512 =
        new("SD 1.5 Fast 512", 512, 512, new[] { "sd15", "sd-1.5", "v1-5" });

    public static readonly ImageProfile Sdxl1024 =
        new("SDXL 1024", 1024, 1024, new[] { "sdxl" });

    public ImageProfile(string name, int width, int height, IReadOnlyList<string> preferredKeywords)
    {
        Name = name;
        Width = width;
        Height = height;
        PreferredKeywords = preferredKeywords;
    }

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<string> PreferredKeywords { get; }

    public override string ToString() => Name;
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

    public string? ResolveRuntimePath(bool preferCuda)
    {
        var candidates = preferCuda
            ? new[]
            {
                Path.Combine(_baseDirectory, "runtime", "image", "cuda", "sd-server.exe"),
                Path.Combine(_baseDirectory, "runtime", "image", "sd-server.exe"),
                Path.Combine(_baseDirectory, "runtime", "image", "cpu", "sd-server.exe")
            }
            : new[]
            {
                Path.Combine(_baseDirectory, "runtime", "image", "cpu", "sd-server.exe"),
                Path.Combine(_baseDirectory, "runtime", "image", "sd-server.exe")
            };

        return candidates.FirstOrDefault(File.Exists);
    }

    public string? ResolveModel(ImageProfile profile)
    {
        if (!Directory.Exists(ImageModelsDirectory))
            return null;

        var supported = new HashSet<string>(
            new[] { ".safetensors", ".ckpt", ".gguf" },
            StringComparer.OrdinalIgnoreCase);

        var files = Directory
            .EnumerateFiles(ImageModelsDirectory)
            .Where(path => supported.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var keyword in profile.PreferredKeywords)
        {
            var hit = files.FirstOrDefault(path =>
                Path.GetFileName(path).Contains(keyword, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(hit))
                return hit;
        }

        return files.FirstOrDefault();
    }

    public async Task<ImageServerSession> EnsureStartedAsync(
        Uri serverUri,
        bool preferCuda,
        ImageProfile profile,
        int threads,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // 1) If an external or already-running server is already reachable,
        //    reuse it and do not start another CUDA server.
        if (await IsServerAliveAsync(serverUri, ct))
        {
            var reused = new ImageServerSession(
                serverUri,
                RuntimePath: CurrentSession?.RuntimePath ?? "(external)",
                ModelPath: CurrentSession?.ModelPath ?? "(external)",
                Backend: CurrentSession?.Backend ?? (preferCuda ? ImageRuntimeBackend.Cuda : ImageRuntimeBackend.Cpu),
                ModelLabel: CurrentSession?.ModelLabel ?? "(external server)",
                ReusedExistingServer: true);

            CurrentSession = reused;
            progress?.Report("Используем уже доступный image server");
            return reused;
        }

        // 2) If our own local process is already alive, wait for it instead of spawning another one.
        lock (_sync)
        {
            if (_process is { HasExited: false } && CurrentSession is not null)
            {
                progress?.Report("Локальный image server уже запущен");
            }
        }

        if (_process is { HasExited: false } && CurrentSession is not null)
        {
            await WaitForServerAsync(serverUri, ct);
            return CurrentSession with { ReusedExistingServer = true };
        }

        // 3) Start local process only if no reachable server exists.
        var runtime = ResolveRuntimePath(preferCuda)
            ?? throw new FileNotFoundException("sd-server.exe не найден ни в CUDA, ни в CPU runtime layout.");

        var backend = runtime.Contains(Path.DirectorySeparatorChar + "cuda" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? ImageRuntimeBackend.Cuda
            : runtime.Contains(Path.DirectorySeparatorChar + "cpu" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? ImageRuntimeBackend.Cpu
                : (preferCuda ? ImageRuntimeBackend.Cuda : ImageRuntimeBackend.Cpu);

        var model = ResolveModel(profile)
            ?? throw new FileNotFoundException("Image-модель не найдена в models\\images.");

        progress?.Report($"Запуск {backend} image server · {Path.GetFileName(model)}");

        var psi = new ProcessStartInfo
        {
            FileName = runtime,
            WorkingDirectory = Path.GetDirectoryName(runtime)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in new[]
        {
            "--listen-ip", "127.0.0.1",
            "--listen-port", serverUri.Port.ToString(),
            "-m", model,
            "--threads", Math.Max(1, threads).ToString()
        })
        {
            psi.ArgumentList.Add(arg);
        }

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить sd-server.exe.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_sync)
        {
            _process = process;
            CurrentSession = new ImageServerSession(
                serverUri,
                runtime,
                model,
                backend,
                Path.GetFileName(model),
                ReusedExistingServer: false);
        }

        await WaitForServerAsync(serverUri, ct);
        progress?.Report($"Image server готов · {backend} · {Path.GetFileName(model)}");
        return CurrentSession!;
    }

    private static async Task<bool> IsServerAliveAsync(Uri serverUri, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = serverUri,
                Timeout = TimeSpan.FromSeconds(2)
            };

            using var response = await client.GetAsync("v1/models", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForServerAsync(Uri serverUri, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await IsServerAliveAsync(serverUri, ct))
                return;

            await Task.Delay(500, ct);
        }

        throw new TimeoutException("Image server не стал готов за 5 минут.");
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        _process?.Dispose();
        _process = null;
        CurrentSession = null;
    }
}
