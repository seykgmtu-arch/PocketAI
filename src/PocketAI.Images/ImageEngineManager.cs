using PocketAI.Hardware;

namespace PocketAI.Images;

public sealed record ImageEngineSession(
    ImageServerSession Server,
    string ApiModel,
    bool RequestedCuda,
    bool FellBackToCpu)
{
    public ImageRuntimeBackend Backend => Server.Backend;
    public string ModelLabel => Server.ModelLabel;
    public Uri ServerUri => Server.ServerUri;

    public string Summary
    {
        get
        {
            var fallback = FellBackToCpu
                ? " · CUDA→CPU fallback"
                : string.Empty;

            return $"{Backend} · {ModelLabel} · {ApiModel}{fallback}";
        }
    }
}

public sealed class ImageEngineManager : IDisposable
{
    private readonly string _baseDirectory;
    private readonly ImageServerManager _serverManager;

    public ImageEngineManager(string baseDirectory)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _serverManager = new ImageServerManager(_baseDirectory);
    }

    public ImageServerSession? CurrentServerSession =>
        _serverManager.CurrentSession;

    public ImageProfile ChooseDefaultProfile(
        HardwareProfile hardware,
        int width,
        int height)
    {
        var largest = Math.Max(width, height);

        if (!hardware.HasNvidiaGpu || largest <= 640)
            return ImageProfile.Sd15Fast512;

        return ImageProfile.Sdxl1024;
    }

    public async Task<ImageEngineSession> EnsureReadyAsync(
        Uri serverUri,
        HardwareProfile hardware,
        ImageProfile profile,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var threads = Math.Max(
            1,
            hardware.LogicalProcessors > 0
                ? hardware.LogicalProcessors
                : Environment.ProcessorCount);

        var explicitCudaRuntime = Path.Combine(
            _baseDirectory,
            "runtime",
            "image",
            "cuda",
            "sd-server.exe");

        var explicitCpuRuntime = Path.Combine(
            _baseDirectory,
            "runtime",
            "image",
            "cpu",
            "sd-server.exe");

        var preferCuda =
            hardware.HasNvidiaGpu &&
            File.Exists(explicitCudaRuntime);

        ImageServerSession session;
        var fellBackToCpu = false;

        try
        {
            progress?.Report(
                preferCuda
                    ? "ImageEngineManager: выбран CUDA backend"
                    : "ImageEngineManager: выбран CPU/auto backend");

            session = await _serverManager.EnsureStartedAsync(
                serverUri,
                preferCuda,
                profile,
                threads,
                progress,
                ct);
        }
        catch (Exception ex)
            when (
                ex is not OperationCanceledException &&
                preferCuda &&
                File.Exists(explicitCpuRuntime))
        {
            progress?.Report(
                $"CUDA backend не запустился ({ex.GetBaseException().Message}). " +
                "Автоматически переключаемся на CPU…");

            _serverManager.StopManagedServer();

            session = await _serverManager.EnsureStartedAsync(
                serverUri,
                preferCuda: false,
                profile,
                threads,
                progress,
                ct);

            fellBackToCpu = true;
        }

        using var client = new ImageGenerationClient(serverUri);
        var apiModel = await client.CheckAsync(ct);

        return new ImageEngineSession(
            session,
            apiModel,
            preferCuda,
            fellBackToCpu);
    }

    public async Task<ImageGenerationResult> GenerateAsync(
        Uri serverUri,
        ImageGenerationRequest request,
        string outputDirectory,
        CancellationToken ct = default)
    {
        using var generator = new ImageGenerator(serverUri);

        return await generator.GenerateAsync(
            request,
            outputDirectory,
            ct);
    }

    public void Stop() => _serverManager.StopManagedServer();

    public void Dispose() => _serverManager.Dispose();
}
