using System.Diagnostics;
using System.Net;

namespace PocketAI.Images;

public sealed class ImageServerManager : IDisposable
{
    private readonly string _baseDirectory;
    private readonly object _logLock = new();
    private readonly Queue<string> _recentLines = new();

    private Process? _process;

    public ImageServerManager(string baseDirectory)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
    }

    public string RuntimePath =>
        Path.Combine(_baseDirectory, "runtime", "image", "sd-server.exe");

    public string ModelsDirectory =>
        Path.Combine(_baseDirectory, "models", "images");

    public string? FindLocalModel()
    {
        if (!Directory.Exists(ModelsDirectory))
            return null;

        var supported = new HashSet<string>(
            new[] { ".safetensors", ".ckpt", ".gguf" },
            StringComparer.OrdinalIgnoreCase);

        return Directory
            .EnumerateFiles(ModelsDirectory)
            .Where(path => supported.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public async Task<bool> TryStartAsync(
        Uri serverUri,
        int threads,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (_process is { HasExited: false })
            return true;

        if (!serverUri.IsLoopback)
            return false;

        if (!File.Exists(RuntimePath))
            return false;

        var model = FindLocalModel();
        if (model is null)
            return false;

        var port = serverUri.Port;
        if (port <= 0)
            return false;

        ClearOutput();

        var psi = new ProcessStartInfo
        {
            FileName = RuntimePath,
            WorkingDirectory = Path.GetDirectoryName(RuntimePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[]
        {
            "--listen-ip", "127.0.0.1",
            "--listen-port", port.ToString(),
            "-m", model,
            "--threads", Math.Max(1, threads).ToString()
        })
        {
            psi.ArgumentList.Add(argument);
        }

        _process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AppendOutput("[stdout] " + e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AppendOutput("[stderr] " + e.Data);
        };

        progress?.Report(
            $"Запускаем локальный image server · {Path.GetFileName(model)}…");

        if (!_process.Start())
            throw new InvalidOperationException("Не удалось запустить sd-server.exe.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddMinutes(5);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    "sd-server.exe завершился во время запуска." +
                    Environment.NewLine +
                    GetSanitizedTail());
            }

            try
            {
                using var client = new HttpClient
                {
                    BaseAddress = serverUri,
                    Timeout = TimeSpan.FromSeconds(2)
                };

                using var response = await client.GetAsync("v1/models", ct);

                if (response.IsSuccessStatusCode)
                {
                    progress?.Report("Локальный image server готов");
                    return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(500, ct);
        }

        throw new TimeoutException(
            "sd-server.exe не стал готов за 5 минут." +
            Environment.NewLine +
            GetSanitizedTail());
    }

    private void AppendOutput(string line)
    {
        lock (_logLock)
        {
            _recentLines.Enqueue(line);

            while (_recentLines.Count > 80)
                _recentLines.Dequeue();
        }
    }

    private void ClearOutput()
    {
        lock (_logLock)
            _recentLines.Clear();
    }

    private string GetSanitizedTail()
    {
        string text;

        lock (_logLock)
            text = string.Join(Environment.NewLine, _recentLines);

        text = text.Replace(
            _baseDirectory,
            "[PocketAI]",
            StringComparison.OrdinalIgnoreCase);

        if (text.Length > 5000)
            text = text[^5000..];

        return string.IsNullOrWhiteSpace(text)
            ? "(sd-server не успел вывести диагностические строки)"
            : text;
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
    }
}
