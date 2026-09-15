using System.Diagnostics;
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
                            extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".ckpt", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase);
                    })
                : 0;

        var imageModelPresent = imageModelCount > 0;

        var imageServerUsesLoopback =
            Uri.TryCreate(config.Images.ServerUrl, UriKind.Absolute, out var imageServerUri) &&
            imageServerUri.IsLoopback;

        var trainingSnapshot =
            BuildTrainingSnapshot();

        var nvidiaSnapshot =
            await TryGetNvidiaSmiSnapshotAsync(cancellationToken);

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

            trainingRuntimeReady = trainingSnapshot.RuntimeReady,
            trainingCheckpointCount = trainingSnapshot.TrainingCheckpointCount,
            loraCount = trainingSnapshot.LoraCount,
            trainingProjectCount = trainingSnapshot.ProjectCount,
            nvidiaSmiAvailable = nvidiaSnapshot.Available,
            nvidiaReportedVramMiB = nvidiaSnapshot.TotalMemoryMiB,
            nvidiaFreeVramMiB = nvidiaSnapshot.FreeMemoryMiB,

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
                systemPromptIncluded = false,
                trainingCaptionsIncluded = false,
                trainingImageNamesIncluded = false,
                trainingPromptTextIncluded = false
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

        AddJson(
            archive,
            "training.json",
            new
            {
                trainingSnapshot.RuntimeDirectoryExists,
                trainingSnapshot.PythonVenvExists,
                trainingSnapshot.TrainNetworkScriptExists,
                trainingSnapshot.SdxlTrainNetworkScriptExists,
                trainingSnapshot.AccelerateConfigExists,
                trainingSnapshot.RuntimeInfoExists,
                trainingSnapshot.RuntimeReady,
                trainingSnapshot.TrainingModelsDirectoryExists,
                trainingSnapshot.TrainingCheckpointCount,
                trainingSnapshot.Sd15CheckpointPresent,
                trainingSnapshot.LoraDirectoryExists,
                trainingSnapshot.LoraCount,
                trainingSnapshot.ProjectsDirectoryExists,
                trainingSnapshot.ProjectCount,
                trainingSnapshot.TrainingLogCount,
                note =
                    "Training prompts, captions, image names, project names and raw logs are omitted."
            });

        AddJson(
            archive,
            "nvidia.json",
            new
            {
                nvidiaSnapshot.Available,
                nvidiaSnapshot.DriverVersion,
                nvidiaSnapshot.GpuName,
                nvidiaSnapshot.TotalMemoryMiB,
                nvidiaSnapshot.FreeMemoryMiB,
                nvidiaSnapshot.TemperatureC,
                nvidiaSnapshot.UtilizationPercent,
                note =
                    "nvidia-smi values are preferred over WMI AdapterRAM for modern GPUs."
            });

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

            trainingRuntimeReady = trainingSnapshot.RuntimeReady,
            trainingCheckpointCount = trainingSnapshot.TrainingCheckpointCount,
            loraCount = trainingSnapshot.LoraCount,

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
                trainingLogCount = trainingSnapshot.TrainingLogCount,
                note =
                    "Raw logs, prompts, captions, queries, source names and document text are omitted."
            });

        AddText(archive, "tree.txt", BuildSafeTree());
        return outputPath;
    }

    private TrainingDiagnosticSnapshot BuildTrainingSnapshot()
    {
        var runtimeDirectory =
            Path.Combine(
                _baseDirectory,
                "runtime",
                "image",
                "training",
                "sd-scripts");

        var pythonPath =
            Path.Combine(
                runtimeDirectory,
                "venv",
                "Scripts",
                "python.exe");

        var trainNetwork =
            Path.Combine(
                runtimeDirectory,
                "train_network.py");

        var sdxlTrainNetwork =
            Path.Combine(
                runtimeDirectory,
                "sdxl_train_network.py");

        var accelerateConfig =
            Path.Combine(
                runtimeDirectory,
                "accelerate-config.yaml");

        var runtimeInfo =
            Path.Combine(
                _baseDirectory,
                "runtime",
                "image",
                "training",
                "runtime-info.txt");

        var trainingModelsDirectory =
            Path.Combine(
                _baseDirectory,
                "models",
                "training");

        var trainingCheckpointCount =
            CountFilesSafe(
                trainingModelsDirectory,
                path =>
                {
                    var extension =
                        Path.GetExtension(path);

                    return
                        extension.Equals(
                            ".safetensors",
                            StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(
                            ".ckpt",
                            StringComparison.OrdinalIgnoreCase);
                });

        var sd15Checkpoint =
            Path.Combine(
                trainingModelsDirectory,
                "sd15",
                "v1-5-pruned.safetensors");

        var loraDirectory =
            Path.Combine(
                _baseDirectory,
                "models",
                "images",
                "loras");

        var loraCount =
            CountFilesSafe(
                loraDirectory,
                path =>
                    Path.GetExtension(path).Equals(
                        ".safetensors",
                        StringComparison.OrdinalIgnoreCase));

        var projectsDirectory =
            Path.Combine(
                _baseDirectory,
                "training",
                "image",
                "projects");

        var projectCount =
            CountDirectoriesSafe(
                projectsDirectory);

        var trainingLogCount =
            Directory.Exists(projectsDirectory)
                ? Directory
                    .EnumerateFiles(
                        projectsDirectory,
                        "*.log",
                        SearchOption.AllDirectories)
                    .Take(10000)
                    .Count()
                : 0;

        var runtimeReady =
            File.Exists(pythonPath) &&
            File.Exists(trainNetwork) &&
            File.Exists(sdxlTrainNetwork) &&
            File.Exists(accelerateConfig);

        return new TrainingDiagnosticSnapshot(
            Directory.Exists(runtimeDirectory),
            File.Exists(pythonPath),
            File.Exists(trainNetwork),
            File.Exists(sdxlTrainNetwork),
            File.Exists(accelerateConfig),
            File.Exists(runtimeInfo),
            runtimeReady,
            Directory.Exists(trainingModelsDirectory),
            trainingCheckpointCount,
            File.Exists(sd15Checkpoint),
            Directory.Exists(loraDirectory),
            loraCount,
            Directory.Exists(projectsDirectory),
            projectCount,
            trainingLogCount);
    }

    private static int CountFilesSafe(
        string directory,
        Func<string, bool> predicate)
    {
        if (!Directory.Exists(directory))
            return 0;

        try
        {
            return Directory
                .EnumerateFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories)
                .Take(10000)
                .Count(predicate);
        }
        catch
        {
            return 0;
        }
    }

    private static int CountDirectoriesSafe(
        string directory)
    {
        if (!Directory.Exists(directory))
            return 0;

        try
        {
            return Directory
                .EnumerateDirectories(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Take(10000)
                .Count();
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<NvidiaSmiSnapshot> TryGetNvidiaSmiSnapshotAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var psi =
                new ProcessStartInfo
                {
                    FileName = "nvidia-smi.exe",
                    Arguments =
                        "--query-gpu=name,driver_version,memory.total,memory.free,temperature.gpu,utilization.gpu " +
                        "--format=csv,noheader,nounits",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

            using var process =
                new Process
                {
                    StartInfo = psi
                };

            if (!process.Start())
                return NvidiaSmiSnapshot.Empty;

            var outputTask =
                process.StandardOutput.ReadToEndAsync(
                    cancellationToken);

            var errorTask =
                process.StandardError.ReadToEndAsync(
                    cancellationToken);

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeoutCts.CancelAfter(
                TimeSpan.FromSeconds(5));

            await process.WaitForExitAsync(
                timeoutCts.Token);

            var output =
                await outputTask;

            _ =
                await errorTask;

            if (process.ExitCode != 0 ||
                string.IsNullOrWhiteSpace(output))
            {
                return NvidiaSmiSnapshot.Empty;
            }

            var line =
                output
                    .Split(
                        new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(line))
                return NvidiaSmiSnapshot.Empty;

            var parts =
                line
                    .Split(',')
                    .Select(
                        part => part.Trim())
                    .ToArray();

            if (parts.Length < 6)
                return NvidiaSmiSnapshot.Empty;

            static int? ParseInt(
                string value)
            {
                return int.TryParse(
                    value,
                    out var parsed)
                    ? parsed
                    : null;
            }

            return new NvidiaSmiSnapshot(
                true,
                parts[1],
                parts[0],
                ParseInt(parts[2]),
                ParseInt(parts[3]),
                ParseInt(parts[4]),
                ParseInt(parts[5]));
        }
        catch
        {
            return NvidiaSmiSnapshot.Empty;
        }
    }

    private static void AddJson<T>(
        ZipArchive archive,
        string name,
        T value)
    {
        var json =
            JsonSerializer.Serialize(
                value,
                JsonOptions);

        AddText(
            archive,
            name,
            json);
    }

    private static void AddText(
        ZipArchive archive,
        string name,
        string content)
    {
        var entry =
            archive.CreateEntry(
                name,
                CompressionLevel.Optimal);

        using var stream =
            entry.Open();

        using var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));

        writer.Write(content);
    }

    private string BuildSafeTree()
    {
        var builder =
            new StringBuilder(
                "File contents and unrecognized names are omitted.\n");

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
            "runtime/image/training",
            "runtime/image/training/sd-scripts",
            "models/images",
            "models/images/loras",
            "models/training",
            "models/training/sd15",
            "training/image/projects",
            "outputs/images",
            "outputs/perchance",
            "logs"
        };

        var knownFiles =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                "PocketAI.exe",
                "PocketAI.dll",
                "PocketAI.deps.json",
                "PocketAI.runtimeconfig.json",
                "pocketai.json",
                "assets.lock.json",
                "llama-server.exe",
                "sd-server.exe",
                "python.exe",
                "train_network.py",
                "sdxl_train_network.py",
                "accelerate-config.yaml",
                "runtime-info.txt"
            };

        foreach (var relative in directories)
        {
            var directory =
                Path.Combine(
                    _baseDirectory,
                    relative);

            if (!Directory.Exists(directory))
                continue;

            var cursor =
                new DirectoryInfo(directory);

            var linked =
                false;

            while (cursor is not null &&
                   cursor.FullName.StartsWith(
                       _baseDirectory,
                       StringComparison.OrdinalIgnoreCase))
            {
                linked |=
                    (cursor.Attributes &
                     FileAttributes.ReparsePoint) != 0;

                cursor =
                    cursor.Parent;
            }

            if (linked)
                continue;

            builder.AppendLine(
                relative + "/");

            foreach (var path in
                     Directory
                         .EnumerateFiles(directory)
                         .Take(1000))
            {
                var info =
                    new FileInfo(path);

                if ((info.Attributes &
                     FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

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

    private sealed record TrainingDiagnosticSnapshot(
        bool RuntimeDirectoryExists,
        bool PythonVenvExists,
        bool TrainNetworkScriptExists,
        bool SdxlTrainNetworkScriptExists,
        bool AccelerateConfigExists,
        bool RuntimeInfoExists,
        bool RuntimeReady,
        bool TrainingModelsDirectoryExists,
        int TrainingCheckpointCount,
        bool Sd15CheckpointPresent,
        bool LoraDirectoryExists,
        int LoraCount,
        bool ProjectsDirectoryExists,
        int ProjectCount,
        int TrainingLogCount);

    private sealed record NvidiaSmiSnapshot(
        bool Available,
        string? DriverVersion,
        string? GpuName,
        int? TotalMemoryMiB,
        int? FreeMemoryMiB,
        int? TemperatureC,
        int? UtilizationPercent)
    {
        public static NvidiaSmiSnapshot Empty =>
            new(
                false,
                null,
                null,
                null,
                null,
                null,
                null);
    }
}
