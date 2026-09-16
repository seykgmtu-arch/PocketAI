using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PocketAI.App.Services;

public enum LocalWorkflowStepKind
{
    Inventory,
    DuplicateReport,
    TextCorpus,
    OrganizeByExtension,
    ZipWorkspace
}

public sealed class LocalWorkflowStep
{
    public string Id { get; init; } =
        Guid.NewGuid().ToString("N");

    public LocalWorkflowStepKind Kind { get; init; }

    public string Title { get; init; } = "";

    public string Description { get; init; } = "";

    public bool CreatesFiles { get; init; }

    public string Status { get; set; } =
        "Ожидает";
}

public sealed class LocalWorkflowPlan
{
    public string Instruction { get; init; } = "";

    public string Workspace { get; init; } = "";

    public string OutputDirectory { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } =
        DateTimeOffset.Now;

    public IReadOnlyList<LocalWorkflowStep> Steps { get; init; } =
        Array.Empty<LocalWorkflowStep>();
}

public sealed class LocalWorkflowRunResult
{
    public string RunDirectory { get; init; } = "";

    public int CompletedSteps { get; init; }

    public bool WasCancelled { get; init; }
}

public sealed class LocalWorkflowService
{
    private const int MaxScannedFiles =
        50000;

    private const long MaxTextFileBytes =
        5L * 1024 * 1024;

    private const long MaxCorpusBytes =
        100L * 1024 * 1024;

    private const long MaxZipInputBytes =
        20L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true
        };

    private static readonly string[] TextExtensions =
    {
        ".txt",
        ".md",
        ".csv",
        ".json",
        ".log",
        ".xml",
        ".yaml",
        ".yml",
        ".ini",
        ".cfg",
        ".cs",
        ".xaml",
        ".ps1",
        ".bat",
        ".cmd",
        ".py"
    };

    private static readonly EnumerationOptions EnumerationOptions =
        new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip =
                FileAttributes.ReparsePoint
        };

    public LocalWorkflowPlan BuildPlan(
        string workspace,
        string instruction)
    {
        var normalizedWorkspace =
            NormalizeWorkspace(
                workspace);

        if (string.IsNullOrWhiteSpace(
                instruction))
        {
            throw new ArgumentException(
                "Введите задачу для Local Agent.");
        }

        var lower =
            instruction
                .Trim()
                .ToLowerInvariant();

        var steps =
            new List<LocalWorkflowStep>();

        AddStep(
            steps,
            LocalWorkflowStepKind.Inventory,
            "Инвентаризация",
            "Просканировать рабочую папку и создать CSV/JSON-реестр файлов.",
            createsFiles: true);

        if (ContainsAny(
                lower,
                "дублик",
                "duplicate",
                "одинаков",
                "повтор"))
        {
            AddStep(
                steps,
                LocalWorkflowStepKind.DuplicateReport,
                "Поиск дубликатов",
                "Сгруппировать файлы по размеру, затем сравнить SHA-256 и создать отчёт.",
                createsFiles: true);
        }

        if (ContainsAny(
                lower,
                "текст",
                "corpus",
                "корпус",
                "объедин",
                "собери текст",
                "собрать текст",
                "исходник"))
        {
            AddStep(
                steps,
                LocalWorkflowStepKind.TextCorpus,
                "Текстовый корпус",
                "Объединить поддерживаемые небольшие текстовые файлы в один локальный corpus.txt.",
                createsFiles: true);
        }

        if (ContainsAny(
                lower,
                "разлож",
                "сортир",
                "организ",
                "по расшир",
                "по тип"))
        {
            AddStep(
                steps,
                LocalWorkflowStepKind.OrganizeByExtension,
                "Копирование по расширениям",
                "Создать безопасную копию файлов, разложенную по расширениям. Исходники не изменяются.",
                createsFiles: true);
        }

        if (ContainsAny(
                lower,
                "архив",
                "zip",
                "заархив"))
        {
            AddStep(
                steps,
                LocalWorkflowStepKind.ZipWorkspace,
                "ZIP рабочей папки",
                "Создать ZIP рабочей папки без каталога результатов Local Agent.",
                createsFiles: true);
        }

        if (ContainsAny(
                lower,
                "полный анализ",
                "полностью проанализ",
                "все файлы"))
        {
            EnsureStep(
                steps,
                LocalWorkflowStepKind.DuplicateReport,
                "Поиск дубликатов",
                "Сгруппировать файлы по размеру, затем сравнить SHA-256 и создать отчёт.",
                createsFiles: true);

            EnsureStep(
                steps,
                LocalWorkflowStepKind.TextCorpus,
                "Текстовый корпус",
                "Объединить поддерживаемые небольшие текстовые файлы в один локальный corpus.txt.",
                createsFiles: true);
        }

        var outputRoot =
            Path.Combine(
                normalizedWorkspace,
                "_PocketAI_Agent_Output");

        return new LocalWorkflowPlan
        {
            Instruction =
                instruction.Trim(),

            Workspace =
                normalizedWorkspace,

            OutputDirectory =
                outputRoot,

            Steps =
                steps
        };
    }

    public async Task<LocalWorkflowRunResult> ExecuteAsync(
        LocalWorkflowPlan plan,
        IProgress<string>? progress,
        IProgress<LocalWorkflowStep>? stepProgress,
        CancellationToken cancellationToken)
    {
        ValidatePlan(
            plan);

        var runDirectory =
            Path.Combine(
                plan.OutputDirectory,
                "run-" +
                DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture));

        Directory.CreateDirectory(
            runDirectory);

        await WriteJsonAsync(
            Path.Combine(
                runDirectory,
                "workflow-plan.json"),
            plan,
            cancellationToken);

        var logPath =
            Path.Combine(
                runDirectory,
                "workflow.log");

        var completed =
            0;

        try
        {
            await LogAsync(
                logPath,
                progress,
                "START Local Agent workflow",
                cancellationToken);

            await LogAsync(
                logPath,
                progress,
                "Workspace: " +
                plan.Workspace,
                cancellationToken);

            await LogAsync(
                logPath,
                progress,
                "Instruction: " +
                plan.Instruction,
                cancellationToken);

            foreach (var step in
                     plan.Steps)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                step.Status =
                    "Выполняется";

                stepProgress?.Report(
                    step);

                await LogAsync(
                    logPath,
                    progress,
                    "STEP START: " +
                    step.Title,
                    cancellationToken);

                try
                {
                    switch (step.Kind)
                    {
                        case LocalWorkflowStepKind.Inventory:
                            await CreateInventoryAsync(
                                plan,
                                runDirectory,
                                progress,
                                cancellationToken);
                            break;

                        case LocalWorkflowStepKind.DuplicateReport:
                            await CreateDuplicateReportAsync(
                                plan,
                                runDirectory,
                                progress,
                                cancellationToken);
                            break;

                        case LocalWorkflowStepKind.TextCorpus:
                            await CreateTextCorpusAsync(
                                plan,
                                runDirectory,
                                progress,
                                cancellationToken);
                            break;

                        case LocalWorkflowStepKind.OrganizeByExtension:
                            await OrganizeByExtensionAsync(
                                plan,
                                runDirectory,
                                progress,
                                cancellationToken);
                            break;

                        case LocalWorkflowStepKind.ZipWorkspace:
                            await CreateWorkspaceZipAsync(
                                plan,
                                runDirectory,
                                progress,
                                cancellationToken);
                            break;

                        default:
                            throw new InvalidOperationException(
                                "Неизвестный шаг workflow.");
                    }

                    step.Status =
                        "Готово";

                    completed++;

                    stepProgress?.Report(
                        step);

                    await LogAsync(
                        logPath,
                        progress,
                        "STEP OK: " +
                        step.Title,
                        cancellationToken);
                }
                catch
                {
                    step.Status =
                        "Ошибка";

                    stepProgress?.Report(
                        step);

                    throw;
                }
            }

            await WriteJsonAsync(
                Path.Combine(
                    runDirectory,
                    "workflow-summary.json"),
                new
                {
                    plan.Instruction,
                    plan.Workspace,
                    CompletedSteps =
                        completed,
                    TotalSteps =
                        plan.Steps.Count,
                    FinishedAt =
                        DateTimeOffset.Now,
                    Cancelled =
                        false
                },
                cancellationToken);

            await LogAsync(
                logPath,
                progress,
                "WORKFLOW COMPLETED",
                cancellationToken);

            return new LocalWorkflowRunResult
            {
                RunDirectory =
                    runDirectory,

                CompletedSteps =
                    completed,

                WasCancelled =
                    false
            };
        }
        catch (OperationCanceledException)
        {
            await SafeLogAsync(
                logPath,
                progress,
                "WORKFLOW CANCELLED");

            await SafeWriteJsonAsync(
                Path.Combine(
                    runDirectory,
                    "workflow-summary.json"),
                new
                {
                    plan.Instruction,
                    plan.Workspace,
                    CompletedSteps =
                        completed,
                    TotalSteps =
                        plan.Steps.Count,
                    FinishedAt =
                        DateTimeOffset.Now,
                    Cancelled =
                        true
                });

            return new LocalWorkflowRunResult
            {
                RunDirectory =
                    runDirectory,

                CompletedSteps =
                    completed,

                WasCancelled =
                    true
            };
        }
        catch (Exception ex)
        {
            await SafeLogAsync(
                logPath,
                progress,
                "WORKFLOW ERROR: " +
                ex);

            ModuleErrorService.WriteException(
                "local-agent-error.log",
                ex);

            throw;
        }
    }

    public string GetOutputRoot(
        string workspace)
    {
        return Path.Combine(
            NormalizeWorkspace(
                workspace),
            "_PocketAI_Agent_Output");
    }

    private async Task CreateInventoryAsync(
        LocalWorkflowPlan plan,
        string runDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var files =
            EnumerateWorkspaceFiles(
                plan)
                .ToList();

        var rows =
            new List<object>(
                files.Count);

        var csv =
            new StringBuilder();

        csv.AppendLine(
            "relative_path,extension,size_bytes,last_write_utc");

        var index =
            0;

        foreach (var file in files)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var info =
                new FileInfo(
                    file);

            var relative =
                Path.GetRelativePath(
                    plan.Workspace,
                    file);

            rows.Add(
                new
                {
                    RelativePath =
                        relative,
                    Extension =
                        info.Extension,
                    SizeBytes =
                        info.Length,
                    LastWriteUtc =
                        info.LastWriteTimeUtc
                });

            csv.Append(
                Csv(
                    relative));

            csv.Append(',');

            csv.Append(
                Csv(
                    info.Extension));

            csv.Append(',');

            csv.Append(
                info.Length.ToString(
                    CultureInfo.InvariantCulture));

            csv.Append(',');

            csv.AppendLine(
                Csv(
                    info.LastWriteTimeUtc
                        .ToString(
                            "O",
                            CultureInfo.InvariantCulture)));

            index++;

            if (index % 500 ==
                0)
            {
                progress?.Report(
                    $"Inventory: {index}/{files.Count}");
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(
                runDirectory,
                "inventory.csv"),
            csv.ToString(),
            new UTF8Encoding(
                false),
            cancellationToken);

        await WriteJsonAsync(
            Path.Combine(
                runDirectory,
                "inventory.json"),
            rows,
            cancellationToken);

        progress?.Report(
            $"Inventory: найдено файлов {files.Count}");
    }

    private async Task CreateDuplicateReportAsync(
        LocalWorkflowPlan plan,
        string runDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var files =
            EnumerateWorkspaceFiles(
                plan)
                .Select(
                    path =>
                        new FileInfo(
                            path))
                .Where(
                    info =>
                        info.Exists)
                .ToList();

        var candidates =
            files
                .GroupBy(
                    info =>
                        info.Length)
                .Where(
                    group =>
                        group.Count() >
                        1)
                .SelectMany(
                    group =>
                        group)
                .ToList();

        var hashGroups =
            new Dictionary<
                string,
                List<string>>(
                StringComparer.OrdinalIgnoreCase);

        var index =
            0;

        foreach (var info in candidates)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var hash =
                await ComputeSha256Async(
                    info.FullName,
                    cancellationToken);

            if (!hashGroups.TryGetValue(
                    hash,
                    out var paths))
            {
                paths =
                    new List<string>();

                hashGroups[hash] =
                    paths;
            }

            paths.Add(
                Path.GetRelativePath(
                    plan.Workspace,
                    info.FullName));

            index++;

            if (index % 50 ==
                0)
            {
                progress?.Report(
                    $"Duplicates: hash {index}/{candidates.Count}");
            }
        }

        var duplicates =
            hashGroups
                .Where(
                    pair =>
                        pair.Value.Count >
                        1)
                .Select(
                    pair =>
                        new
                        {
                            Sha256 =
                                pair.Key,

                            Count =
                                pair.Value.Count,

                            Files =
                                pair.Value
                        })
                .ToList();

        await WriteJsonAsync(
            Path.Combine(
                runDirectory,
                "duplicates.json"),
            duplicates,
            cancellationToken);

        var text =
            new StringBuilder();

        text.AppendLine(
            "PocketAI duplicate report");

        text.AppendLine(
            "Groups: " +
            duplicates.Count);

        text.AppendLine();

        foreach (var group in duplicates)
        {
            text.AppendLine(
                group.Sha256);

            foreach (var file in
                     group.Files)
            {
                text.AppendLine(
                    "  " +
                    file);
            }

            text.AppendLine();
        }

        await File.WriteAllTextAsync(
            Path.Combine(
                runDirectory,
                "duplicates.txt"),
            text.ToString(),
            new UTF8Encoding(
                false),
            cancellationToken);

        progress?.Report(
            $"Duplicates: групп {duplicates.Count}");
    }

    private async Task CreateTextCorpusAsync(
        LocalWorkflowPlan plan,
        string runDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var textFiles =
            EnumerateWorkspaceFiles(
                plan)
                .Where(
                    path =>
                        TextExtensions.Contains(
                            Path.GetExtension(
                                path),
                            StringComparer.OrdinalIgnoreCase))
                .Select(
                    path =>
                        new FileInfo(
                            path))
                .Where(
                    info =>
                        info.Exists &&
                        info.Length <=
                        MaxTextFileBytes)
                .ToList();

        var corpusPath =
            Path.Combine(
                runDirectory,
                "corpus.txt");

        await using var output =
            new FileStream(
                corpusPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                useAsync: true);

        await using var writer =
            new StreamWriter(
                output,
                new UTF8Encoding(
                    false));

        long totalBytes =
            0;

        var included =
            0;

        foreach (var info in textFiles)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            if (totalBytes +
                info.Length >
                MaxCorpusBytes)
            {
                progress?.Report(
                    "Corpus: достигнут лимит 100 MB.");
                break;
            }

            string content;

            try
            {
                content =
                    await File.ReadAllTextAsync(
                        info.FullName,
                        cancellationToken);
            }
            catch
            {
                continue;
            }

            await writer.WriteLineAsync(
                "===== FILE: " +
                Path.GetRelativePath(
                    plan.Workspace,
                    info.FullName) +
                " =====");

            await writer.WriteLineAsync(
                content);

            await writer.WriteLineAsync();

            totalBytes +=
                info.Length;

            included++;

            if (included % 50 ==
                0)
            {
                progress?.Report(
                    $"Corpus: {included}/{textFiles.Count}");
            }
        }

        progress?.Report(
            $"Corpus: включено файлов {included}");
    }

    private async Task OrganizeByExtensionAsync(
        LocalWorkflowPlan plan,
        string runDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var destinationRoot =
            Path.Combine(
                runDirectory,
                "organized-by-extension");

        Directory.CreateDirectory(
            destinationRoot);

        var files =
            EnumerateWorkspaceFiles(
                plan)
                .ToList();

        var copied =
            0;

        foreach (var file in files)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var extension =
                Path.GetExtension(
                    file);

            var folder =
                string.IsNullOrWhiteSpace(
                    extension)
                    ? "_no_extension"
                    : extension
                        .TrimStart('.')
                        .ToLowerInvariant();

            folder =
                SanitizeName(
                    folder);

            var targetDirectory =
                Path.Combine(
                    destinationRoot,
                    folder);

            Directory.CreateDirectory(
                targetDirectory);

            var target =
                GetUniqueFilePath(
                    targetDirectory,
                    Path.GetFileName(
                        file));

            await CopyFileAsync(
                file,
                target,
                cancellationToken);

            copied++;

            if (copied % 100 ==
                0)
            {
                progress?.Report(
                    $"Organize: {copied}/{files.Count}");
            }
        }

        progress?.Report(
            $"Organize: скопировано {copied}. Исходники не изменены.");
    }

    private async Task CreateWorkspaceZipAsync(
        LocalWorkflowPlan plan,
        string runDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var files =
            EnumerateWorkspaceFiles(
                plan)
                .Select(
                    path =>
                        new FileInfo(
                            path))
                .Where(
                    info =>
                        info.Exists)
                .ToList();

        var totalBytes =
            files.Sum(
                info =>
                    info.Length);

        if (totalBytes >
            MaxZipInputBytes)
        {
            throw new InvalidOperationException(
                "Объём входных файлов для ZIP превышает 20 GB. " +
                "Архивация остановлена для защиты диска.");
        }

        var zipPath =
            Path.Combine(
                runDirectory,
                "workspace.zip");

        await using var zipStream =
            new FileStream(
                zipPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                useAsync: true);

        using var archive =
            new ZipArchive(
                zipStream,
                ZipArchiveMode.Create,
                leaveOpen: true);

        var index =
            0;

        foreach (var info in files)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var relative =
                Path.GetRelativePath(
                    plan.Workspace,
                    info.FullName)
                    .Replace(
                        '\\',
                        '/');

            var entry =
                archive.CreateEntry(
                    relative,
                    CompressionLevel.Fastest);

            await using var input =
                new FileStream(
                    info.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite |
                    FileShare.Delete,
                    64 * 1024,
                    useAsync: true);

            await using var output =
                entry.Open();

            await input.CopyToAsync(
                output,
                64 * 1024,
                cancellationToken);

            index++;

            if (index % 100 ==
                0)
            {
                progress?.Report(
                    $"ZIP: {index}/{files.Count}");
            }
        }

        progress?.Report(
            $"ZIP: добавлено файлов {files.Count}");
    }

    private IEnumerable<string> EnumerateWorkspaceFiles(
        LocalWorkflowPlan plan)
    {
        var outputRoot =
            Path.GetFullPath(
                plan.OutputDirectory);

        var count =
            0;

        foreach (var file in
                 Directory.EnumerateFiles(
                     plan.Workspace,
                     "*",
                     EnumerationOptions))
        {
            if (IsUnderDirectory(
                    file,
                    outputRoot))
            {
                continue;
            }

            count++;

            if (count >
                MaxScannedFiles)
            {
                throw new InvalidOperationException(
                    $"Превышен безопасный лимит сканирования: {MaxScannedFiles} файлов.");
            }

            yield return file;
        }
    }

    private static string NormalizeWorkspace(
        string workspace)
    {
        if (string.IsNullOrWhiteSpace(
                workspace))
        {
            throw new ArgumentException(
                "Выберите рабочую папку.");
        }

        var full =
            Path.GetFullPath(
                workspace.Trim());

        if (!Directory.Exists(
                full))
        {
            throw new DirectoryNotFoundException(
                full);
        }

        return full
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
    }

    private static void ValidatePlan(
        LocalWorkflowPlan plan)
    {
        if (!Directory.Exists(
                plan.Workspace))
        {
            throw new DirectoryNotFoundException(
                plan.Workspace);
        }

        var workspace =
            Path.GetFullPath(
                plan.Workspace);

        var output =
            Path.GetFullPath(
                plan.OutputDirectory);

        if (!IsUnderDirectory(
                output,
                workspace))
        {
            throw new InvalidOperationException(
                "В v1 каталог результатов должен находиться внутри рабочей папки.");
        }

        if (plan.Steps.Count ==
            0)
        {
            throw new InvalidOperationException(
                "Workflow не содержит шагов.");
        }
    }

    private static bool IsUnderDirectory(
        string candidate,
        string directory)
    {
        var fullCandidate =
            Path.GetFullPath(
                candidate)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        var fullDirectory =
            Path.GetFullPath(
                directory)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(
            fullDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite |
                FileShare.Delete,
                1024 * 1024,
                useAsync: true);

        using var sha =
            SHA256.Create();

        var hash =
            await sha.ComputeHashAsync(
                stream,
                cancellationToken);

        return Convert.ToHexString(
            hash);
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input =
            new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite |
                FileShare.Delete,
                1024 * 1024,
                useAsync: true);

        await using var output =
            new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                useAsync: true);

        await input.CopyToAsync(
            output,
            1024 * 1024,
            cancellationToken);
    }

    private static string GetUniqueFilePath(
        string directory,
        string fileName)
    {
        var candidate =
            Path.Combine(
                directory,
                fileName);

        if (!File.Exists(
                candidate))
        {
            return candidate;
        }

        var baseName =
            Path.GetFileNameWithoutExtension(
                fileName);

        var extension =
            Path.GetExtension(
                fileName);

        for (var i = 2;
             i < 100000;
             i++)
        {
            candidate =
                Path.Combine(
                    directory,
                    $"{baseName}_{i}{extension}");

            if (!File.Exists(
                    candidate))
            {
                return candidate;
            }
        }

        throw new IOException(
            "Не удалось подобрать уникальное имя файла.");
    }

    private static string SanitizeName(
        string value)
    {
        var invalid =
            Path.GetInvalidFileNameChars();

        var chars =
            value
                .Select(
                    ch =>
                        invalid.Contains(
                            ch)
                            ? '_'
                            : ch)
                .ToArray();

        var result =
            new string(
                chars);

        return string.IsNullOrWhiteSpace(
            result)
            ? "_unknown"
            : result;
    }

    private static string Csv(
        string value)
    {
        return "\"" +
               value.Replace(
                   "\"",
                   "\"\"") +
               "\"";
    }

    private static bool ContainsAny(
        string text,
        params string[] terms)
    {
        return terms.Any(
            term =>
                text.Contains(
                    term,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static void AddStep(
        ICollection<LocalWorkflowStep> steps,
        LocalWorkflowStepKind kind,
        string title,
        string description,
        bool createsFiles)
    {
        steps.Add(
            new LocalWorkflowStep
            {
                Kind =
                    kind,

                Title =
                    title,

                Description =
                    description,

                CreatesFiles =
                    createsFiles
            });
    }

    private static void EnsureStep(
        ICollection<LocalWorkflowStep> steps,
        LocalWorkflowStepKind kind,
        string title,
        string description,
        bool createsFiles)
    {
        if (steps.Any(
                step =>
                    step.Kind ==
                    kind))
        {
            return;
        }

        AddStep(
            steps,
            kind,
            title,
            description,
            createsFiles);
    }

    private static async Task WriteJsonAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        var json =
            JsonSerializer.Serialize(
                value,
                JsonOptions);

        await File.WriteAllTextAsync(
            path,
            json,
            new UTF8Encoding(
                false),
            cancellationToken);
    }

    private static async Task SafeWriteJsonAsync(
        string path,
        object value)
    {
        try
        {
            var json =
                JsonSerializer.Serialize(
                    value,
                    JsonOptions);

            await File.WriteAllTextAsync(
                path,
                json,
                new UTF8Encoding(
                    false));
        }
        catch
        {
        }
    }

    private static async Task LogAsync(
        string logPath,
        IProgress<string>? progress,
        string message,
        CancellationToken cancellationToken)
    {
        var line =
            $"[{DateTimeOffset.Now:O}] {message}";

        progress?.Report(
            line);

        await File.AppendAllTextAsync(
            logPath,
            line +
            Environment.NewLine,
            new UTF8Encoding(
                false),
            cancellationToken);
    }

    private static async Task SafeLogAsync(
        string logPath,
        IProgress<string>? progress,
        string message)
    {
        try
        {
            var line =
                $"[{DateTimeOffset.Now:O}] {message}";

            progress?.Report(
                line);

            await File.AppendAllTextAsync(
                logPath,
                line +
                Environment.NewLine,
                new UTF8Encoding(
                    false));
        }
        catch
        {
        }
    }
}
