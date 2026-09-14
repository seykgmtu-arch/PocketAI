using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PocketAI.Core.Configuration;
using PocketAI.Hardware;

namespace PocketAI.Inference;

public sealed class EmbeddingServerManager : IDisposable
{
    private readonly string _baseDirectory;
    private readonly PocketAiConfig _config;
    private readonly object _logLock = new();
    private readonly Queue<string> _recentLines = new();
    private Process? _process;
    private LlamaServerSession? _session;
    private string? _apiKey;

    public EmbeddingServerManager(string baseDirectory, PocketAiConfig config)
    {
        _baseDirectory = baseDirectory;
        _config = config;
    }

    public bool IsConfigured =>
        _config.Embeddings.Enabled &&
        File.Exists(ConfigLoader.ResolvePath(_baseDirectory, _config.Embeddings.ModelPath));

    public LlamaServerSession? Session => _session;

    public string RecentOutput
    {
        get
        {
            lock (_logLock)
            {
                return string.Join(Environment.NewLine, _recentLines);
            }
        }
    }

    public async Task<LlamaServerSession> StartAsync(HardwareProfile hardware, CancellationToken ct = default)
    {
        if (_process is { HasExited: false } && _session is not null)
            return _session;

        var model = ConfigLoader.ResolvePath(_baseDirectory, _config.Embeddings.ModelPath);
        if (!File.Exists(model))
            throw new FileNotFoundException("Embedding GGUF не найден.", model);

        var cuda = ConfigLoader.ResolvePath(_baseDirectory, _config.Runtime.CudaPath);
        var cpu = ConfigLoader.ResolvePath(_baseDirectory, _config.Runtime.CpuPath);
        var exe = hardware.HasNvidiaGpu && File.Exists(cuda) ? cuda : cpu;

        if (!File.Exists(exe))
            throw new FileNotFoundException("llama-server.exe для embeddings не найден.", exe);

        var backend =
            hardware.HasNvidiaGpu &&
            exe.Equals(cuda, StringComparison.OrdinalIgnoreCase)
                ? BackendKind.Cuda
                : BackendKind.Cpu;

        var port = ReservePort();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _apiKey = key;
        const string alias = "pocket-embedding";

        ClearRecentOutput();
        AppendLog($"Starting embedding server. Backend={backend}, Port={port}");
        AppendLog($"Model={Path.GetFileName(model)}, Context={_config.Embeddings.ContextSize}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in new[]
        {
            "--model", model,
            "--host", "127.0.0.1",
            "--port", port.ToString(),
            "--ctx-size", _config.Embeddings.ContextSize.ToString(),
            "--alias", alias,
            "--api-key", key,
            "--no-webui",
            "--offline",
            "--embedding",
            "--pooling", "mean",
            "--n-gpu-layers", backend == BackendKind.Cuda ? "all" : "0"
        })
        {
            psi.ArgumentList.Add(arg);
        }

        _process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AppendLog("[stdout] " + e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AppendLog("[stderr] " + e.Data);
        };

        _process.Exited += (_, _) =>
        {
            try
            {
                AppendLog($"Embedding server process exited. ExitCode={_process?.ExitCode}");
            }
            catch
            {
                AppendLog("Embedding server process exited.");
            }
        };

        if (!_process.Start())
            throw new InvalidOperationException("Не удалось запустить embedding llama-server.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var uri = new Uri($"http://127.0.0.1:{port}/");
        await WaitAsync(uri, ct);

        _session = new LlamaServerSession(uri, key, backend, port, alias);
        AppendLog("Embedding server health check: OK");
        return _session;
    }

    private async Task WaitAsync(Uri uri, CancellationToken ct)
    {
        using var http = new HttpClient
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(2)
        };

        var until = DateTime.UtcNow.AddMinutes(2);

        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                var exitCode = SafeExitCode();
                throw new InvalidOperationException(
                    "Embedding server завершился" +
                    (exitCode is null ? "." : $" с кодом {exitCode}.") +
                    Environment.NewLine +
                    "Последний вывод llama-server:" +
                    Environment.NewLine +
                    GetSanitizedLogTail());
            }

            try
            {
                using var response = await http.GetAsync("health", ct);
                if (response.IsSuccessStatusCode)
                    return;

                AppendLog($"Health check returned HTTP {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppendLog("Health check timeout.");
            }
            catch (HttpRequestException ex)
            {
                AppendLog("Health check failed: " + ex.Message);
            }

            await Task.Delay(300, ct);
        }

        throw new TimeoutException(
            "Embedding server не готов в течение 2 минут." +
            Environment.NewLine +
            "Последний вывод llama-server:" +
            Environment.NewLine +
            GetSanitizedLogTail());
    }

    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            _recentLines.Enqueue(line);

            while (_recentLines.Count > 80)
                _recentLines.Dequeue();
        }
    }

    private void ClearRecentOutput()
    {
        lock (_logLock)
        {
            _recentLines.Clear();
        }
    }

    private string GetSanitizedLogTail()
    {
        var text = RecentOutput;

        if (!string.IsNullOrWhiteSpace(_apiKey))
            text = text.Replace(_apiKey, "[API_KEY_OMITTED]", StringComparison.Ordinal);

        var fullBase = Path.GetFullPath(_baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        text = text.Replace(fullBase, "[PocketAI]", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(text))
            return "(llama-server не успел вывести диагностические строки)";

        if (text.Length > 5000)
            text = text[^5000..];

        return text;
    }

    private int? SafeExitCode()
    {
        try
        {
            return _process is { HasExited: true } ? _process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(true);
        }
        catch
        {
        }

        _process?.Dispose();
        _process = null;
        _session = null;
    }
}
