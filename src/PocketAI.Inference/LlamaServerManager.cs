using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PocketAI.Core.Configuration;
using PocketAI.Hardware;
using PocketAI.Inference.ProcessControl;

namespace PocketAI.Inference;

public sealed class LlamaServerManager : IDisposable
{
    private readonly string _baseDirectory;
    private readonly PocketAiConfig _config;
    private readonly RingTextBuffer _recentOutput = new();

    private Process? _process;
    private WindowsJob? _job;
    private LlamaServerSession? _session;
    private bool _disposed;

    public LlamaServerManager(string baseDirectory, PocketAiConfig config)
    {
        _baseDirectory = baseDirectory;
        _config = config;
    }

    public LlamaServerSession? Session => _session;
    public string RecentServerOutput => _recentOutput.Snapshot();

    public async Task<LlamaServerSession> StartAsync(
        HardwareProfile hardware,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_process is { HasExited: false } && _session is not null)
            return _session;

        var modelPath = ConfigLoader.ResolvePath(_baseDirectory, _config.ModelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Не найдена модель: {modelPath}\n\nПоложите GGUF-модель по пути из pocketai.json.",
                modelPath);
        }

        var candidates = BuildCandidates(hardware).ToList();
        if (candidates.Count == 0)
        {
            throw new FileNotFoundException(
                "Не найден llama-server.exe. Ожидается CPU runtime в runtime\\llama\\cpu\\ или CUDA runtime в runtime\\llama\\cuda\\.");
        }

        Exception? lastError = null;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(candidate.Backend == BackendKind.Cuda
                ? "Запускаем GPU-ускорение…"
                : "Запускаем локальный AI на процессоре…");

            try
            {
                return await StartCandidateAsync(candidate, modelPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
                StopInternal();
                _recentOutput.Add($"Backend {candidate.Backend} failed: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "Pocket AI не смог запустить локальный AI runtime. " +
            "Последние сообщения llama-server:\n" + RecentServerOutput,
            lastError);
    }

    public void Stop()
    {
        if (_disposed)
            return;
        StopInternal();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopInternal();
        GC.SuppressFinalize(this);
    }

    private IEnumerable<RuntimeCandidate> BuildCandidates(HardwareProfile hardware)
    {
        var cudaPath = ConfigLoader.ResolvePath(_baseDirectory, _config.Runtime.CudaPath);
        var cpuPath = ConfigLoader.ResolvePath(_baseDirectory, _config.Runtime.CpuPath);

        if (hardware.HasNvidiaGpu && File.Exists(cudaPath))
            yield return new RuntimeCandidate(BackendKind.Cuda, cudaPath);

        if (File.Exists(cpuPath))
            yield return new RuntimeCandidate(BackendKind.Cpu, cpuPath);

        // If WMI failed to identify NVIDIA, still let a supplied CUDA build try after CPU is absent.
        if (!hardware.HasNvidiaGpu && !File.Exists(cpuPath) && File.Exists(cudaPath))
            yield return new RuntimeCandidate(BackendKind.Cuda, cudaPath);
    }

    private async Task<LlamaServerSession> StartCandidateAsync(
        RuntimeCandidate candidate,
        string modelPath,
        CancellationToken cancellationToken)
    {
        var port = ReserveEphemeralPort();
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        const string alias = "pocket-ai";

        var psi = new ProcessStartInfo
        {
            FileName = candidate.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(candidate.ExecutablePath) ?? _baseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(modelPath);
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--ctx-size");
        psi.ArgumentList.Add(_config.ContextSize.ToString());
        psi.ArgumentList.Add("--parallel");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--alias");
        psi.ArgumentList.Add(alias);
        psi.ArgumentList.Add("--api-key");
        psi.ArgumentList.Add(apiKey);
        psi.ArgumentList.Add("--no-webui");
        psi.ArgumentList.Add("--offline");
        psi.ArgumentList.Add("--log-verbosity");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("--n-gpu-layers");
        psi.ArgumentList.Add(candidate.Backend == BackendKind.Cuda ? "all" : "0");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить llama-server.exe.");

        _process = process;
        TryAttachToJob(process);
        _ = DrainAsync(process.StandardOutput, cancellationToken);
        _ = DrainAsync(process.StandardError, cancellationToken);

        var baseUri = new Uri($"http://127.0.0.1:{port}/");
        await WaitForHealthAsync(baseUri, process, cancellationToken).ConfigureAwait(false);

        _session = new LlamaServerSession(baseUri, apiKey, candidate.Backend, port, alias);
        return _session;
    }

    private void TryAttachToJob(Process process)
    {
        try
        {
            _job?.Dispose();
            _job = new WindowsJob();
            _job.Add(process);
        }
        catch (Exception ex)
        {
            _recentOutput.Add("Windows Job Object unavailable: " + ex.Message);
            _job?.Dispose();
            _job = null;
        }
    }

    private async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                _recentOutput.Add(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task WaitForHealthAsync(
        Uri baseUri,
        Process process,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(2)
        };

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"llama-server завершился с кодом {process.ExitCode}.\n{RecentServerOutput}");
            }

            try
            {
                using var response = await http.GetAsync("health", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Model is still loading.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-request timeout while model is loading.
            }

            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "llama-server не стал готов за 3 минуты.\n" + RecentServerOutput);
    }

    private void StopInternal()
    {
        var process = _process;
        _process = null;
        _session = null;

        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        finally
        {
            try { process?.Dispose(); } catch { }
            try { _job?.Dispose(); } catch { }
            _job = null;
        }
    }

    private static int ReserveEphemeralPort()
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record RuntimeCandidate(BackendKind Backend, string ExecutablePath);
}
