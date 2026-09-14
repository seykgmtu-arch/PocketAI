using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PocketAI.Core.Configuration;
using PocketAI.Core.Models;
using PocketAI.Hardware;

namespace PocketAI.App.Services;

public sealed record RagDiagnosticSnapshot(
    string Mode,
    double VectorThreshold,
    double? TopSimilarity,
    double? AverageSimilarity,
    int HitCount);

public sealed class DiagnosticReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _baseDirectory;

    public DiagnosticReportService(string baseDirectory)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
    }

    // Compatibility overload used by existing self-tests and older callers.
    public Task<string> CreateAsync(
        PocketAiConfig config,
        HardwareProfile? hardware,
        string backendText,
        string statusText,
        string recentServerOutput,
        IReadOnlyList<LocalModelDescriptor> models,
        int knowledgeDocumentCount,
        int vectorCount,
        bool webEnabled,
        bool imageEnabled,
        CancellationToken cancellationToken = default)
    {
        return CreateAsync(
            config,
            hardware,
            backendText,
            statusText,
            recentServerOutput,
            models,
            knowledgeDocumentCount,
            vectorCount,
            webEnabled,
            imageEnabled,
            rag: null,
            cancellationToken);
    }

    public async Task<string> CreateAsync(
        PocketAiConfig config,
        HardwareProfile? hardware,
        string backendText,
        string statusText,
        string recentServerOutput,
        IReadOnlyList<LocalModelDescriptor> models,
        int knowledgeDocumentCount,
        int vectorCount,
        bool webEnabled,
        bool imageEnabled,
        RagDiagnosticSnapshot? rag,
        CancellationToken cancellationToken = default)
    {
        var outputDirectory = Path.Combine(_baseDirectory, "diagnostics");
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(
            outputDirectory,
            $"PocketAI-Diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");

        await using var fileStream = File.Create(outputPath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        var assembly = Assembly.GetEntryAssembly();
        var embeddingsConfigured =
            config.Embeddings.Enabled &&
            File.Exists(ConfigLoader.ResolvePath(_baseDirectory, config.Embeddings.ModelPath));

        var imageCudaRuntimeExists =
            File.Exists(Path.Combine(
                _baseDirectory,
                "runtime",
                "image",
                "cuda",
                "sd-server.exe"));

        var imageCpuRuntimeExists =
            File.Exists(Path.Combine(
                _baseDirectory,
                "runtime",
                "image",
                "cpu",
                "sd-server.exe"));

        var imageGenericRuntimeExists =
            File.Exists(Path.Combine(
                _baseDirectory,
                "runtime",
                "image",
                "sd-server.exe"));

        var imageRuntimeExists =
            imageCudaRuntimeExists ||
            imageCpuRuntimeExists ||
            imageGenericRuntimeExists;

        var imageModelsDirectory =
            Path.Combine(_baseDirectory, "models", "images");

        var imageModelCount =
            Directory.Exists(imageModelsDirectory)
                ? Directory.EnumerateFiles(imageModelsDirectory).Count(
                    path =>
                    {
                        var extension = Path.GetExtension(path);
                        return
                            extension.Equals(
                                ".safetensors",
                                StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(
                                ".ckpt",
                                StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(
                                ".gguf",
                                StringComparison.OrdinalIgnoreCase);
                    })
                : 0;

        var imageModelPresent = imageModelCount > 0;

        var imageServerUsesLoopback =
            Uri.TryCreate(config.Images.ServerUrl, UriKind.Absolute, out var imageServerUri) &&
            imageServerUri.IsLoopback;

        var report = new
        {
            createdAtLocal = DateTimeOffset.Now,
            appVersion = assembly?.GetName().Version?.ToString() ?? "unknown",
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processArchitecture =
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            operatingSystem =
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            backend = backendText is "CPU" or "Cpu"
                ? "CPU"
                : backendText is "CUDA / GPU" or "Cuda"
                    ? "CUDA"
                    : "unknown",
            status = "Free-form status omitted for privacy",
            knowledgeDocumentCount,
            vectorCount,
            webEnabled,
            imageEnabled,
            imageProvider = imageEnabled
                ? "stable-diffusion.cpp OpenAI-compatible /v1/images/generations"
                : "disabled",
            imageRuntimeExists,
            imageCudaRuntimeExists,
            imageCpuRuntimeExists,
            imageGenericRuntimeExists,
            imageModelPresent,
            imageModelCount,
            imageServerUsesLoopback,
            imagePreferredBackend =
                hardware?.HasNvidiaGpu == true ? "CUDA" : "auto/CPU",
            imageRecommendedProfile =
                hardware?.HasNvidiaGpu == true ? "SDXL 1024" : "SD 1.5 Fast 512",
            imageProfilesSupported =
                new[] { "SD 1.5 Fast 512", "SDXL 1024" },
            embeddingProvider = embeddingsConfigured
                ? "Qwen3-Embedding GGUF via llama.cpp"
                : "not configured",
            embeddingStrategy = embeddingsConfigured
                ? "query=Instruct+Query; documents=plain; pooling=last; similarity=cosine"
                : "lexical fallback",

            ragModeUsed = rag?.Mode ?? "not-yet-searched",
            vectorThreshold = rag?.VectorThreshold,
            lastTopSimilarity = rag?.TopSimilarity,
            lastAverageSimilarity = rag?.AverageSimilarity,
            lastHitCount = rag?.HitCount ?? 0,

            milestone = 3,
            privacy = new
            {
                inferenceEndpoint = "loopback only",
                apiKeysIncluded = false,
                modelFilesIncluded = false,
                knowledgeContentsIncluded = false,
                queryTextIncluded = false,
                sourceSnippetsIncluded = false,
                rawLogsIncluded = false,
                systemPromptIncluded = false
            }
        };

        AddJson(archive, "report.json", report);
        AddJson(archive, "hardware.json", hardware);
        AddJson(
            archive,
            "models.json",
            models.Select(
                (model, index) =>
                    new
                    {
                        id = index + 1,
                        model.SizeBytes,
                        model.LastWriteTimeUtc
                    }));

        var sanitizedConfig = new
        {
            modelExists =
                File.Exists(ConfigLoader.ResolvePath(_baseDirectory, config.ModelPath)),
            cpuRuntimeExists =
                File.Exists(ConfigLoader.ResolvePath(_baseDirectory, config.Runtime.CpuPath)),
            config.ContextSize,
            config.MaxOutputTokens,
            config.Temperature,
            embeddingsConfigured,
            embeddingPooling = embeddingsConfigured ? "last" : "n/a",
            embeddingQueryFormatting =
                embeddingsConfigured ? "Qwen3 Instruct/Query" : "n/a",
            preferVectorSearch = config.Knowledge.PreferVectorSearch,
            configuredTopK = config.Knowledge.TopK,
            ragQualityPolicy =
                "adaptive Top-K; absolute threshold=0.30; relative window=0.08; lexical fallback",
            webConfigured = config.Web.Enabled,
            imagesConfigured = config.Images.Enabled,
            imageRuntimeExists,
            imageCudaRuntimeExists,
            imageCpuRuntimeExists,
            imageGenericRuntimeExists,
            imageModelPresent,
            imageModelCount,
            imageServerUsesLoopback,
            imageRuntimeSearchOrder = new[]
            {
                "runtime/image/cuda/sd-server.exe",
                "runtime/image/sd-server.exe",
                "runtime/image/cpu/sd-server.exe"
            },
            imageProfilesSupported =
                new[] { "SD 1.5 Fast 512", "SDXL 1024" },
            imageOutputDirectoryConfigured =
                !string.IsNullOrWhiteSpace(config.Images.OutputDirectory),
            imageWidth = config.Images.Width,
            imageHeight = config.Images.Height,
            systemPrompt = "[omitted]"
        };

        AddJson(archive, "config.sanitized.json", sanitizedConfig);

        AddJson(
            archive,
            "logs.summary.json",
            new
            {
                capturedServerCharacters = recentServerOutput.Length,
                capturedServerLines = recentServerOutput.Count(c => c == '\n'),
                note =
                    "Raw logs, prompts, queries, source names and document text are omitted."
            });

        AddText(archive, "tree.txt", BuildSafeTree());
        return outputPath;
    }

    private static void AddJson<T>(ZipArchive archive, string name, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        AddText(archive, name, json);
    }

    private static void AddText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.Write(content);
    }

    private string BuildSafeTree()
    {
        var builder =
            new StringBuilder("File contents and unrecognized names are omitted.\n");

        var directories = new[]
        {
            "",
            "runtime/llama/cpu",
            "runtime/llama/cuda",
            "models/chat",
            "models/embeddings",
            "runtime/image",
            "runtime/image/cuda",
            "runtime/image/cpu",
            "models/images",
            "outputs/images",
            "logs"
        };

        var knownFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PocketAI.exe",
                "PocketAI.dll",
                "PocketAI.deps.json",
                "PocketAI.runtimeconfig.json",
                "pocketai.json",
                "assets.lock.json",
                "llama-server.exe",
                "sd-server.exe"
            };

        foreach (var relative in directories)
        {
            var directory = Path.Combine(_baseDirectory, relative);

            if (!Directory.Exists(directory))
                continue;

            var cursor = new DirectoryInfo(directory);
            var linked = false;

            while (cursor is not null &&
                   cursor.FullName.StartsWith(
                       _baseDirectory,
                       StringComparison.OrdinalIgnoreCase))
            {
                linked |=
                    (cursor.Attributes & FileAttributes.ReparsePoint) != 0;
                cursor = cursor.Parent;
            }

            if (linked)
                continue;

            builder.AppendLine(relative + "/");

            foreach (var path in Directory.EnumerateFiles(directory).Take(1000))
            {
                var info = new FileInfo(path);

                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var name =
                    knownFiles.Contains(info.Name)
                        ? info.Name
                        : "[name omitted]";

                builder
                    .Append("  ")
                    .Append(name)
                    .Append(" | ")
                    .Append(info.Length)
                    .AppendLine(" bytes");
            }
        }

        return builder.ToString();
    }
}
