using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PocketAI.App.Services;

public sealed class ControlCenterModuleStatus
{
    public string Module { get; init; } = "";
    public string State { get; init; } = "";
    public string Runtime { get; init; } = "";
    public string Model { get; init; } = "";
    public string Details { get; init; } = "";
}

public sealed class ControlCenterScanResult
{
    public string RootDirectory { get; init; } = "";
    public string HardwareSummary { get; init; } = "";
    public string DiskSummary { get; init; } = "";
    public string VersionLockSummary { get; init; } = "";
    public double FreeDiskGb { get; init; }
    public bool HasNvidiaGpu { get; init; }

    public IReadOnlyList<ControlCenterModuleStatus> Modules { get; init; } =
        Array.Empty<ControlCenterModuleStatus>();

    public DateTimeOffset CreatedAt { get; init; } =
        DateTimeOffset.Now;
}

public sealed class ControlCenterService
{
    private sealed class NvidiaProbeResult
    {
        public bool Available { get; init; }
        public string Summary { get; init; } = "GPU: NVIDIA не обнаружена";
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true
        };

    private readonly string _root;

    public ControlCenterService()
    {
        _root = ResolvePocketAiRoot();
    }

    public string RootDirectory =>
        _root;

    public async Task<ControlCenterScanResult> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        var nvidia =
            await ProbeNvidiaAsync(
                cancellationToken);

        var freeDiskGb =
            GetAvailableDiskGb();

        var fileScan =
            await Task.Run(
                () => ScanFiles(
                    nvidia.Available,
                    freeDiskGb,
                    cancellationToken),
                cancellationToken);

        return new ControlCenterScanResult
        {
            RootDirectory =
                _root,

            HardwareSummary =
                $"OS: {Environment.OSVersion.VersionString} · " +
                $"CPU threads: {Environment.ProcessorCount} · " +
                nvidia.Summary,

            DiskSummary =
                BuildDiskSummary(
                    freeDiskGb),

            VersionLockSummary =
                fileScan.VersionLockSummary,

            FreeDiskGb =
                freeDiskGb,

            HasNvidiaGpu =
                nvidia.Available,

            Modules =
                fileScan.Modules,

            CreatedAt =
                DateTimeOffset.Now
        };
    }

    public async Task<ControlCenterModuleStatus> CheckModuleAsync(
        string module,
        CancellationToken cancellationToken = default)
    {
        var scan =
            await ScanAsync(
                cancellationToken);

        var status =
            scan.Modules.FirstOrDefault(
                item =>
                    string.Equals(
                        item.Module,
                        module,
                        StringComparison.OrdinalIgnoreCase));

        return status ??
               new ControlCenterModuleStatus
               {
                   Module = module,
                   State = "🔴 Error",
                   Runtime = "—",
                   Model = "—",
                   Details = "Модуль не найден Control Center."
               };
    }

    public async Task<string> CreateConfigurationSnapshotAsync(
        ControlCenterScanResult scan,
        CancellationToken cancellationToken = default)
    {
        var stamp =
            DateTime.Now.ToString(
                "yyyyMMdd-HHmmss");

        var snapshotsRoot =
            Path.Combine(
                _root,
                "snapshots");

        var snapshotDirectory =
            Path.Combine(
                snapshotsRoot,
                $"PocketAI-Snapshot-{stamp}");

        Directory.CreateDirectory(
            snapshotDirectory);

        var snapshotJson =
            JsonSerializer.Serialize(
                scan,
                JsonOptions);

        await File.WriteAllTextAsync(
            Path.Combine(
                snapshotDirectory,
                "snapshot.json"),
            snapshotJson,
            cancellationToken);

        await CopyIfExistsAsync(
            Path.Combine(
                _root,
                "pocketai.json"),
            Path.Combine(
                snapshotDirectory,
                "pocketai.json"),
            cancellationToken);

        var lockDirectory =
            Path.Combine(
                _root,
                "dev",
                "version-lock");

        if (Directory.Exists(
                lockDirectory))
        {
            CopyDirectoryFiles(
                lockDirectory,
                Path.Combine(
                    snapshotDirectory,
                    "version-lock"));
        }

        CopyRuntimeMetadata(
            snapshotDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(
                snapshotDirectory,
                "file-manifest.txt"),
            BuildManifest(),
            cancellationToken);

        return snapshotDirectory;
    }

    public async Task<string> CreateControlCenterReportAsync(
        ControlCenterScanResult scan,
        CancellationToken cancellationToken = default)
    {
        var diagnostics =
            Path.Combine(
                _root,
                "diagnostics");

        Directory.CreateDirectory(
            diagnostics);

        var stamp =
            DateTime.Now.ToString(
                "yyyyMMdd-HHmmss");

        var zipPath =
            Path.Combine(
                diagnostics,
                $"PocketAI-ControlCenter-{stamp}.zip");

        if (File.Exists(
                zipPath))
        {
            File.Delete(
                zipPath);
        }

        await using var output =
            new FileStream(
                zipPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                useAsync: true);

        using var archive =
            new ZipArchive(
                output,
                ZipArchiveMode.Create,
                leaveOpen: true);

        await AddTextEntryAsync(
            archive,
            "control-center-report.json",
            JsonSerializer.Serialize(
                scan,
                JsonOptions),
            cancellationToken);

        await AddTextEntryAsync(
            archive,
            "file-manifest.txt",
            BuildManifest(),
            cancellationToken);

        AddDirectoryFilesSafe(
            archive,
            Path.Combine(
                _root,
                "dev",
                "version-lock"),
            "version-lock",
            maxFileBytes: 2 * 1024 * 1024);

        AddDirectoryFilesSafe(
            archive,
            Path.Combine(
                _root,
                "logs"),
            "logs",
            maxFileBytes: 4 * 1024 * 1024);

        AddSelectedFileSafe(
            archive,
            Path.Combine(
                _root,
                "pocketai.json"),
            "pocketai.json",
            2 * 1024 * 1024);

        return zipPath;
    }

    public async Task<int> StopPocketAiWorkersAsync(
        CancellationToken cancellationToken = default)
    {
        var escapedRoot =
            _root.Replace(
                "'",
                "''");

        var script =
            "$root='" +
            escapedRoot +
            "';" +
            "$names=@('llama-server.exe','sd-server.exe'," +
            "'python.exe','pythonw.exe','ffmpeg.exe');" +
            "$p=Get-CimInstance Win32_Process | Where-Object {" +
            "$_.CommandLine -and " +
            "$_.CommandLine.IndexOf($root,[StringComparison]::OrdinalIgnoreCase)-ge 0 " +
            "-and $names -contains $_.Name};" +
            "$ids=@($p.ProcessId);" +
            "foreach($id in $ids){" +
            "try{Stop-Process -Id $id -Force -ErrorAction Stop}catch{}};" +
            "Write-Output $ids.Count;";

        var result =
            await RunProcessAsync(
                "powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                script.Replace(
                    "\"",
                    "\\\"") +
                "\"",
                cancellationToken,
                timeoutSeconds: 15);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Не удалось остановить AI workers. " +
                result.Output);
        }

        var lastLine =
            result.Output
                .Split(
                    new[]
                    {
                        '\r',
                        '\n'
                    },
                    StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();

        return int.TryParse(
            lastLine,
            out var count)
            ? count
            : 0;
    }

    public void OpenLogsFolder() =>
        OpenFolder(
            Path.Combine(
                _root,
                "logs"));

    public void OpenSnapshotsFolder() =>
        OpenFolder(
            Path.Combine(
                _root,
                "snapshots"));

    public void OpenVersionLockFolder() =>
        OpenFolder(
            Path.Combine(
                _root,
                "dev",
                "version-lock"));

    public void OpenDiagnosticsFolder() =>
        OpenFolder(
            Path.Combine(
                _root,
                "diagnostics"));

    private (
        IReadOnlyList<ControlCenterModuleStatus> Modules,
        string VersionLockSummary)
        ScanFiles(
            bool hasNvidiaGpu,
            double freeDiskGb,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modules =
            new List<ControlCenterModuleStatus>();

        var llamaCpu =
            Exists(
                "runtime",
                "llama",
                "cpu",
                "llama-server.exe");

        var llamaCuda =
            Exists(
                "runtime",
                "llama",
                "cuda",
                "llama-server.exe");

        var chatModels =
            SafeCountFiles(
                PathOf(
                    "models",
                    "chat"),
                "*.gguf",
                SearchOption.AllDirectories);

        var runningLlama =
            TryGetRunningExecutablePath(
                "llama-server");

        var chatRuntime =
            ResolveBackendText(
                runningLlama,
                hasNvidiaGpu,
                llamaCpu,
                llamaCuda,
                "llama.cpp");

        var chatRunnable =
            IsRunnableBackend(
                hasNvidiaGpu,
                llamaCpu,
                llamaCuda);

        modules.Add(
            new ControlCenterModuleStatus
            {
                Module =
                    "Chat",

                State =
                    BuildCoreState(
                        chatRunnable,
                        chatModels > 0),

                Runtime =
                    chatRuntime,

                Model =
                    chatModels > 0
                        ? $"{chatModels} GGUF"
                        : "GGUF не найден",

                Details =
                    BuildBackendDetails(
                        hasNvidiaGpu,
                        llamaCpu,
                        llamaCuda)
            });

        cancellationToken.ThrowIfCancellationRequested();

        var embeddingModel =
            Exists(
                "models",
                "embeddings",
                "model.gguf");

        var lexicalIndex =
            Exists(
                "knowledge",
                "index.json");

        var vectorIndex =
            Exists(
                "knowledge",
                "vectors.json");

        modules.Add(
            new ControlCenterModuleStatus
            {
                Module =
                    "RAG",

                State =
                    embeddingModel &&
                    vectorIndex
                        ? "✅ Ready"
                        : lexicalIndex
                            ? "🟡 Partial"
                            : "🔴 Needs setup",

                Runtime =
                    embeddingModel
                        ? "llama embeddings"
                        : "embedding model отсутствует",

                Model =
                    embeddingModel
                        ? "models\\embeddings\\model.gguf"
                        : "—",

                Details =
                    $"lexical={(lexicalIndex ? "yes" : "no")} · " +
                    $"vector={(vectorIndex ? "yes" : "no")}"
            });

        cancellationToken.ThrowIfCancellationRequested();

        var sdCpu =
            Exists(
                "runtime",
                "image",
                "cpu",
                "sd-server.exe");

        var sdCuda =
            Exists(
                "runtime",
                "image",
                "cuda",
                "sd-server.exe");

        var imageModels =
            SafeCountFiles(
                PathOf(
                    "models",
                    "images"),
                "*.safetensors",
                SearchOption.AllDirectories);

        var runningSd =
            TryGetRunningExecutablePath(
                "sd-server");

        var imageRuntime =
            ResolveBackendText(
                runningSd,
                hasNvidiaGpu,
                sdCpu,
                sdCuda,
                "stable-diffusion.cpp");

        var imageRunnable =
            IsRunnableBackend(
                hasNvidiaGpu,
                sdCpu,
                sdCuda);

        modules.Add(
            new ControlCenterModuleStatus
            {
                Module =
                    "Image",

                State =
                    BuildCoreState(
                        imageRunnable,
                        imageModels > 0),

                Runtime =
                    imageRuntime,

                Model =
                    imageModels > 0
                        ? $"{imageModels} safetensors"
                        : "модели не найдены",

                Details =
                    BuildBackendDetails(
                        hasNvidiaGpu,
                        sdCpu,
                        sdCuda)
            });

        cancellationToken.ThrowIfCancellationRequested();

        var imageTraining =
            Exists(
                "runtime",
                "image",
                "training",
                "sd-scripts",
                "venv",
                "Scripts",
                "python.exe") &&
            Exists(
                "runtime",
                "image",
                "training",
                "sd-scripts",
                "train_network.py");

        var imageLoras =
            SafeCountFiles(
                PathOf(
                    "models",
                    "images",
                    "loras"),
                "*.safetensors",
                SearchOption.AllDirectories);

        modules.Add(
            new ControlCenterModuleStatus
            {
                Module =
                    "Image LoRA",

                State =
                    imageTraining
                        ? "✅ Ready"
                        : "🔴 Not installed",

                Runtime =
                    imageTraining
                        ? "sd-scripts"
                        : "training runtime отсутствует",

                Model =
                    $"{imageLoras} LoRA",

                Details =
                    "runtime\\image\\training"
            });

        cancellationToken.ThrowIfCancellationRequested();

        modules.Add(
            ScanFutureModule(
                module:
                    "Text LoRA",
                compatSummaryRelative:
                    new[]
                    {
                        "runtime",
                        "text",
                        "compat",
                        "text-cpu-compat-v21.txt"
                    },
                compatMarker:
                    "TEXT_CPU_COMPAT_OK",
                gpuVenvRelative:
                    new[]
                    {
                        "runtime",
                        "text",
                        "venv",
                        "Scripts",
                        "python.exe"
                    },
                modelDirectoryRelative:
                    new[]
                    {
                        "models",
                        "training",
                        "text"
                    },
                modelPattern:
                    "config.json",
                freeDiskGb:
                    freeDiskGb,
                recommendedFreeGb:
                    8));

        cancellationToken.ThrowIfCancellationRequested();

        modules.Add(
            ScanFutureModule(
                module:
                    "Audio",
                compatSummaryRelative:
                    new[]
                    {
                        "runtime",
                        "audio",
                        "compat",
                        "audio-cpu-compat-v21.txt"
                    },
                compatMarker:
                    "AUDIO_CPU_COMPAT_OK",
                gpuVenvRelative:
                    new[]
                    {
                        "runtime",
                        "audio",
                        "venv",
                        "Scripts",
                        "python.exe"
                    },
                modelDirectoryRelative:
                    new[]
                    {
                        "models",
                        "audio"
                    },
                modelPattern:
                    "*",
                freeDiskGb:
                    freeDiskGb,
                recommendedFreeGb:
                    10));

        cancellationToken.ThrowIfCancellationRequested();

        modules.Add(
            ScanFutureModule(
                module:
                    "Video",
                compatSummaryRelative:
                    new[]
                    {
                        "runtime",
                        "video",
                        "compat-v22",
                        "video-cpu-compat-v22.txt"
                    },
                compatMarker:
                    "VIDEO_CPU_COMPAT_OK",
                gpuVenvRelative:
                    new[]
                    {
                        "runtime",
                        "video",
                        "venv",
                        "Scripts",
                        "python.exe"
                    },
                modelDirectoryRelative:
                    new[]
                    {
                        "models",
                        "video"
                    },
                modelPattern:
                    "config.json",
                freeDiskGb:
                    freeDiskGb,
                recommendedFreeGb:
                    30));

        return (
            modules,
            ReadVersionLockSummary());
    }

    private ControlCenterModuleStatus ScanFutureModule(
        string module,
        string[] compatSummaryRelative,
        string compatMarker,
        string[] gpuVenvRelative,
        string[] modelDirectoryRelative,
        string modelPattern,
        double freeDiskGb,
        double recommendedFreeGb)
    {
        var compat =
            FileContains(
                Path.Combine(
                    new[]
                    {
                        _root
                    }
                    .Concat(
                        compatSummaryRelative)
                    .ToArray()),
                compatMarker);

        var gpuRuntime =
            File.Exists(
                Path.Combine(
                    new[]
                    {
                        _root
                    }
                    .Concat(
                        gpuVenvRelative)
                    .ToArray()));

        var modelDirectory =
            Path.Combine(
                new[]
                {
                    _root
                }
                .Concat(
                    modelDirectoryRelative)
                .ToArray());

        var modelCount =
            modelPattern == "*"
                ? SafeCountAllFiles(
                    modelDirectory)
                : SafeCountFiles(
                    modelDirectory,
                    modelPattern,
                    SearchOption.AllDirectories);

        var diskOk =
            freeDiskGb >=
            recommendedFreeGb;

        var state =
            gpuRuntime &&
            modelCount > 0
                ? "✅ Ready"
                : compat &&
                  !diskOk
                    ? "🔴 Disk low"
                    : compat
                        ? "🟡 CPU verified"
                        : gpuRuntime
                            ? "🟡 Runtime only"
                            : "🔴 Not installed";

        var diskText =
            diskOk
                ? $"disk OK ({freeDiskGb:0.0} GB free)"
                : $"⚠ рекомендуется ≥{recommendedFreeGb:0} GB free; " +
                  $"доступно {freeDiskGb:0.0} GB";

        return new ControlCenterModuleStatus
        {
            Module =
                module,

            State =
                state,

            Runtime =
                gpuRuntime
                    ? "GPU runtime present"
                    : compat
                        ? "CPU compatibility OK"
                        : "runtime отсутствует",

            Model =
                modelCount > 0
                    ? $"{modelCount} model files"
                    : "модель не установлена",

            Details =
                (compat
                    ? "version-lock verified"
                    : "compatibility check не найден") +
                " · " +
                diskText
        };
    }

    private static string BuildCoreState(
        bool runtimeRunnable,
        bool modelReady)
    {
        if (runtimeRunnable &&
            modelReady)
        {
            return "✅ Ready";
        }

        if (runtimeRunnable ||
            modelReady)
        {
            return "🟡 Partial";
        }

        return "🔴 Not runnable";
    }

    private static bool IsRunnableBackend(
        bool hasNvidiaGpu,
        bool cpuFiles,
        bool cudaFiles)
    {
        return cpuFiles ||
               (hasNvidiaGpu &&
                cudaFiles);
    }

    private static string ResolveBackendText(
        string? runningExecutable,
        bool hasNvidiaGpu,
        bool cpuFiles,
        bool cudaFiles,
        string engine)
    {
        if (!string.IsNullOrWhiteSpace(
                runningExecutable))
        {
            if (ContainsPathSegment(
                    runningExecutable,
                    "cuda"))
            {
                return
                    $"{engine} CUDA · running";
            }

            if (ContainsPathSegment(
                    runningExecutable,
                    "cpu"))
            {
                return
                    $"{engine} CPU · running";
            }

            return
                $"{engine} · running";
        }

        if (hasNvidiaGpu &&
            cudaFiles)
        {
            return
                $"{engine} CUDA · available";
        }

        if (cpuFiles)
        {
            return
                $"{engine} CPU · available";
        }

        if (cudaFiles &&
            !hasNvidiaGpu)
        {
            return
                $"{engine} CUDA files present · GPU unavailable";
        }

        return
            "runtime не найден";
    }

    private static string BuildBackendDetails(
        bool hasNvidiaGpu,
        bool cpuFiles,
        bool cudaFiles)
    {
        return
            $"GPU={(hasNvidiaGpu ? "NVIDIA available" : "unavailable")} · " +
            $"CPU files={(cpuFiles ? "yes" : "no")} · " +
            $"CUDA files={(cudaFiles ? "yes" : "no")}";
    }

    private static bool ContainsPathSegment(
        string path,
        string segment)
    {
        var normalized =
            path.Replace(
                '/',
                '\\');

        return normalized.Contains(
            "\\" +
            segment +
            "\\",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetRunningExecutablePath(
        string processName)
    {
        try
        {
            foreach (var process in
                     Process.GetProcessesByName(
                         processName))
            {
                using (process)
                {
                    try
                    {
                        var fileName =
                            process.MainModule?
                                .FileName;

                        if (!string.IsNullOrWhiteSpace(
                                fileName))
                        {
                            return fileName;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private string ReadVersionLockSummary()
    {
        var candidates =
            new[]
            {
                PathOf(
                    "dev",
                    "version-lock",
                    "VERSION-MATRIX-v2.json"),

                Path.Combine(
                    AppContext.BaseDirectory,
                    "dev",
                    "version-lock",
                    "VERSION-MATRIX-v2.json"),

                Path.Combine(
                    AppContext.BaseDirectory,
                    "ControlCenter",
                    "VERSION-MATRIX-v2.json"),

                PathOf(
                    "dev",
                    "version-lock",
                    "VERSION-MATRIX.json"),

                PathOf(
                    "dev",
                    "version-lock",
                    "VERSION-MATRIX-v2.txt")
            };

        var file =
            candidates
                .FirstOrDefault(
                    File.Exists);

        if (file is null)
        {
            return
                "🔴 Version lock: не найден";
        }

        try
        {
            if (file.EndsWith(
                    ".json",
                    StringComparison.OrdinalIgnoreCase))
            {
                using var stream =
                    File.OpenRead(
                        file);

                using var document =
                    JsonDocument.Parse(
                        stream);

                var root =
                    document.RootElement;

                var python =
                    TryGetNestedString(
                        root,
                        "baseline",
                        "python");

                var status =
                    root.TryGetProperty(
                        "status",
                        out var statusNode)
                        ? statusNode.ToString()
                        : "loaded";

                return
                    $"✅ Version lock: {Path.GetFileName(file)} · " +
                    $"Python {python ?? "?"} · {status}";
            }

            return
                $"✅ Version lock: {Path.GetFileName(file)}";
        }
        catch (Exception ex)
        {
            return
                "🔴 Version lock: ошибка чтения · " +
                ex.Message;
        }
    }

    private static string? TryGetNestedString(
        JsonElement root,
        string parent,
        string child)
    {
        if (!root.TryGetProperty(
                parent,
                out var parentNode))
        {
            return null;
        }

        if (!parentNode.TryGetProperty(
                child,
                out var childNode))
        {
            return null;
        }

        return childNode.ToString();
    }

    private double GetAvailableDiskGb()
    {
        try
        {
            var rootPath =
                Path.GetPathRoot(
                    _root);

            if (string.IsNullOrWhiteSpace(
                    rootPath))
            {
                return 0;
            }

            var drive =
                new DriveInfo(
                    rootPath);

            return ToGb(
                drive.AvailableFreeSpace);
        }
        catch
        {
            return 0;
        }
    }

    private string BuildDiskSummary(
        double freeDiskGb)
    {
        try
        {
            var rootPath =
                Path.GetPathRoot(
                    _root);

            if (string.IsNullOrWhiteSpace(
                    rootPath))
            {
                return "Disk: —";
            }

            var drive =
                new DriveInfo(
                    rootPath);

            var warning =
                freeDiskGb <
                30
                    ? " · ⚠ Video рекомендуется ≥30 GB free"
                    : "";

            return
                $"Disk {drive.Name}: " +
                $"{freeDiskGb:0.0} GB free / " +
                $"{ToGb(drive.TotalSize):0.0} GB" +
                warning;
        }
        catch
        {
            return "Disk: недоступно";
        }
    }

    private async Task<NvidiaProbeResult> ProbeNvidiaAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var result =
                await RunProcessAsync(
                    "nvidia-smi.exe",
                    "--query-gpu=name,memory.total,memory.free,driver_version " +
                    "--format=csv,noheader,nounits",
                    cancellationToken,
                    timeoutSeconds: 5);

            if (result.ExitCode != 0 ||
                string.IsNullOrWhiteSpace(
                    result.Output))
            {
                return
                    new NvidiaProbeResult();
            }

            var line =
                result.Output
                    .Split(
                        new[]
                        {
                            '\r',
                            '\n'
                        },
                        StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(
                    line))
            {
                return
                    new NvidiaProbeResult();
            }

            return new NvidiaProbeResult
            {
                Available =
                    true,

                Summary =
                    "GPU: " +
                    line.Trim()
            };
        }
        catch
        {
            return
                new NvidiaProbeResult();
        }
    }

    private static async Task<(
        int ExitCode,
        string Output)>
        RunProcessAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken,
            int timeoutSeconds)
    {
        using var process =
            new Process
            {
                StartInfo =
                    new ProcessStartInfo
                    {
                        FileName =
                            fileName,

                        Arguments =
                            arguments,

                        UseShellExecute =
                            false,

                        RedirectStandardOutput =
                            true,

                        RedirectStandardError =
                            true,

                        CreateNoWindow =
                            true
                    }
            };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Не удалось запустить {fileName}.");
        }

        var stdoutTask =
            process.StandardOutput
                .ReadToEndAsync(
                    cancellationToken);

        var stderrTask =
            process.StandardError
                .ReadToEndAsync(
                    cancellationToken);

        using var timeout =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        timeout.CancelAfter(
            TimeSpan.FromSeconds(
                timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(
                timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(
                    entireProcessTree: true);
            }
            catch
            {
            }

            throw;
        }

        var stdout =
            await stdoutTask;

        var stderr =
            await stderrTask;

        return (
            process.ExitCode,
            stdout +
            (string.IsNullOrWhiteSpace(
                stderr)
                ? ""
                : Environment.NewLine +
                  stderr));
    }

    private string BuildManifest()
    {
        var builder =
            new StringBuilder();

        builder.AppendLine(
            $"PocketAI root: {_root}");

        builder.AppendLine(
            $"Created: {DateTimeOffset.Now:O}");

        builder.AppendLine();

        foreach (var relative in
                 new[]
                 {
                     "pocketai.json",
                     "runtime",
                     "models",
                     "knowledge",
                     "training",
                     "dev\\version-lock"
                 })
        {
            var path =
                Path.Combine(
                    _root,
                    relative);

            builder.AppendLine(
                $"[{relative}]");

            if (File.Exists(
                    path))
            {
                var info =
                    new FileInfo(
                        path);

                builder.AppendLine(
                    $"{info.Length} bytes · {info.LastWriteTimeUtc:O}");
            }
            else if (Directory.Exists(
                         path))
            {
                try
                {
                    foreach (var file in
                             Directory.EnumerateFiles(
                                 path,
                                 "*",
                                 SearchOption.AllDirectories)
                             .Take(
                                 4000))
                    {
                        try
                        {
                            var info =
                                new FileInfo(
                                    file);

                            builder.AppendLine(
                                Path.GetRelativePath(
                                    _root,
                                    file) +
                                $" · {info.Length} bytes");
                        }
                        catch
                        {
                        }
                    }
                }
                catch (Exception ex)
                {
                    builder.AppendLine(
                        "ERROR: " +
                        ex.Message);
                }
            }
            else
            {
                builder.AppendLine(
                    "NOT FOUND");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private void CopyRuntimeMetadata(
        string snapshotDirectory)
    {
        var candidates =
            new[]
            {
                PathOf(
                    "runtime",
                    "text",
                    "runtime-info.txt"),

                PathOf(
                    "runtime",
                    "audio",
                    "runtime-info.txt"),

                PathOf(
                    "runtime",
                    "video",
                    "runtime-info.txt"),

                PathOf(
                    "runtime",
                    "text",
                    "compat",
                    "text-cpu-compat-v21.txt"),

                PathOf(
                    "runtime",
                    "audio",
                    "compat",
                    "audio-cpu-compat-v21.txt"),

                PathOf(
                    "runtime",
                    "video",
                    "compat-v22",
                    "video-cpu-compat-v22.txt")
            };

        var target =
            Path.Combine(
                snapshotDirectory,
                "runtime-metadata");

        foreach (var file in
                 candidates.Where(
                     File.Exists))
        {
            Directory.CreateDirectory(
                target);

            File.Copy(
                file,
                Path.Combine(
                    target,
                    Path.GetFileName(
                        file)),
                overwrite: true);
        }
    }

    private static void CopyDirectoryFiles(
        string source,
        string destination)
    {
        Directory.CreateDirectory(
            destination);

        foreach (var file in
                 Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Copy(
                    file,
                    Path.Combine(
                        destination,
                        Path.GetFileName(
                            file)),
                    overwrite: true);
            }
            catch
            {
            }
        }
    }

    private static async Task CopyIfExistsAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(
                source))
        {
            return;
        }

        await using var input =
            new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                useAsync: true);

        await using var output =
            new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                useAsync: true);

        await input.CopyToAsync(
            output,
            cancellationToken);
    }

    private static async Task AddTextEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry =
            archive.CreateEntry(
                name,
                CompressionLevel.Optimal);

        await using var stream =
            entry.Open();

        await using var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier:
                    false));

        await writer.WriteAsync(
            content.AsMemory(),
            cancellationToken);
    }

    private static void AddDirectoryFilesSafe(
        ZipArchive archive,
        string directory,
        string entryPrefix,
        long maxFileBytes)
    {
        if (!Directory.Exists(
                directory))
        {
            return;
        }

        string[] files;

        try
        {
            files =
                Directory.EnumerateFiles(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                    .ToArray();
        }
        catch
        {
            return;
        }

        foreach (var file in
                 files)
        {
            AddSelectedFileSafe(
                archive,
                file,
                entryPrefix +
                "/" +
                Path.GetFileName(
                    file),
                maxFileBytes);
        }
    }

    private static void AddSelectedFileSafe(
        ZipArchive archive,
        string file,
        string entryName,
        long maxFileBytes)
    {
        try
        {
            var info =
                new FileInfo(
                    file);

            if (!info.Exists ||
                info.Length >
                maxFileBytes)
            {
                return;
            }

            using var input =
                new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite |
                    FileShare.Delete);

            var entry =
                archive.CreateEntry(
                    entryName,
                    CompressionLevel.Optimal);

            using var output =
                entry.Open();

            input.CopyTo(
                output);
        }
        catch
        {
        }
    }

    private static bool FileContains(
        string path,
        string marker)
    {
        try
        {
            return
                File.Exists(
                    path) &&
                File.ReadAllText(
                    path)
                    .Contains(
                        marker,
                        StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private bool Exists(
        params string[] parts)
    {
        return File.Exists(
            Path.Combine(
                new[]
                {
                    _root
                }
                .Concat(
                    parts)
                .ToArray()));
    }

    private string PathOf(
        params string[] parts)
    {
        return Path.Combine(
            new[]
            {
                _root
            }
            .Concat(
                parts)
            .ToArray());
    }

    private static int SafeCountFiles(
        string directory,
        string pattern,
        SearchOption option)
    {
        try
        {
            return
                Directory.Exists(
                    directory)
                    ? Directory
                        .EnumerateFiles(
                            directory,
                            pattern,
                            option)
                        .Count()
                    : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int SafeCountAllFiles(
        string directory)
    {
        try
        {
            return
                Directory.Exists(
                    directory)
                    ? Directory
                        .EnumerateFiles(
                            directory,
                            "*",
                            SearchOption.AllDirectories)
                        .Count()
                    : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double ToGb(
        long bytes)
    {
        return
            bytes /
            1024d /
            1024d /
            1024d;
    }

    private static string ResolvePocketAiRoot()
    {
        var current =
            new DirectoryInfo(
                AppContext.BaseDirectory);

        for (var i = 0;
             i < 8 &&
             current is not null;
             i++)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "pocketai.json")))
            {
                return current.FullName;
            }

            current =
                current.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static void OpenFolder(
        string path)
    {
        Directory.CreateDirectory(
            path);

        Process.Start(
            new ProcessStartInfo
            {
                FileName =
                    "explorer.exe",

                Arguments =
                    "\"" +
                    path +
                    "\"",

                UseShellExecute =
                    true
            });
    }
}
