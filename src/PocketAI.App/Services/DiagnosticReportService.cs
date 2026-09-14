using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PocketAI.Core.Configuration;
using PocketAI.Core.Models;
using PocketAI.Hardware;

namespace PocketAI.App.Services;

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
        var report = new
        {
            createdAtLocal = DateTimeOffset.Now,
            appVersion = assembly?.GetName().Version?.ToString() ?? "unknown",
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            backend = backendText is "CPU" or "Cpu" ? "CPU" : backendText is "CUDA / GPU" or "Cuda" ? "CUDA" : "unknown",
            status = "Free-form status omitted for privacy",
            knowledgeDocumentCount,
            vectorCount,
            webEnabled,
            imageEnabled,
            milestone = 3,
            privacy = new
            {
                inferenceEndpoint = "loopback only",
                apiKeysIncluded = false,
                modelFilesIncluded = false,
                knowledgeContentsIncluded = false,
                rawLogsIncluded = false,
                systemPromptIncluded = false
            }
        };

        AddJson(archive, "report.json", report);
        AddJson(archive, "hardware.json", hardware);
        AddJson(archive, "models.json", models.Select((model, index) => new { id = index + 1, model.SizeBytes, model.LastWriteTimeUtc }));

        var sanitizedConfig = new
        {
            modelExists = File.Exists(ConfigLoader.ResolvePath(_baseDirectory, config.ModelPath)),
            cpuRuntimeExists = File.Exists(ConfigLoader.ResolvePath(_baseDirectory, config.Runtime.CpuPath)),
            config.ContextSize,
            config.MaxOutputTokens,
            config.Temperature,
            embeddingsConfigured = config.Embeddings.Enabled && File.Exists(ConfigLoader.ResolvePath(_baseDirectory, config.Embeddings.ModelPath)),
            preferVectorSearch = config.Knowledge.PreferVectorSearch,
            webConfigured = config.Web.Enabled,
            imagesConfigured = config.Images.Enabled,
            systemPrompt = "[omitted]"
        };
        AddJson(archive, "config.sanitized.json", sanitizedConfig);

        AddJson(archive, "logs.summary.json", new
        {
            capturedServerCharacters = recentServerOutput.Length,
            capturedServerLines = recentServerOutput.Count(c => c == '\n'),
            note = "Raw logs, names and status text are omitted because they may contain keys, prompts or documents."
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
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private string BuildSafeTree()
    {
        var builder = new StringBuilder("File contents and unrecognized names are omitted.\n");
        var directories = new[] { "", "runtime/llama/cpu", "runtime/llama/cuda", "models/chat", "models/embeddings", "runtime/image", "outputs/images", "logs" };
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PocketAI.exe", "PocketAI.dll", "PocketAI.deps.json", "PocketAI.runtimeconfig.json",
            "pocketai.json", "assets.lock.json", "llama-server.exe"
        };
        foreach (var relative in directories)
        {
            var directory = Path.Combine(_baseDirectory, relative);
            if (!Directory.Exists(directory)) continue;
            var cursor = new DirectoryInfo(directory);
            var linked = false;
            while (cursor is not null && cursor.FullName.StartsWith(_baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                linked |= (cursor.Attributes & FileAttributes.ReparsePoint) != 0;
                cursor = cursor.Parent;
            }
            if (linked) continue;
            builder.AppendLine(relative + "/");
            foreach (var path in Directory.EnumerateFiles(directory).Take(1000))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var name = knownFiles.Contains(info.Name) ? info.Name : "[name omitted]";
                builder.Append("  ").Append(name).Append(" | ").Append(info.Length).AppendLine(" bytes");
            }
        }
        return builder.ToString();
    }
}
