using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PocketAI.App.Services;

public sealed class ImageTrainingDatasetService
{
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp"
    };

    private readonly string _baseDirectory;

    public ImageTrainingDatasetService(string baseDirectory)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
    }

    public string ProjectsRoot =>
        Path.Combine(_baseDirectory, "training", "image", "projects");

    public ImageTrainingProject CreateProject(
        string name,
        string triggerWord,
        ImageTrainingModelFamily family,
        string baseModelPath)
    {
        var safeName = SanitizeFileName(
            string.IsNullOrWhiteSpace(name) ? "my-lora" : name.Trim());

        var projectDirectory =
            Path.Combine(ProjectsRoot, safeName);

        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(Path.Combine(projectDirectory, "images"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "output"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "logs"));

        var project = new ImageTrainingProject
        {
            Name = safeName,
            TriggerWord = triggerWord.Trim(),
            Family = family,
            BaseModelPath = baseModelPath.Trim(),
            ProjectDirectory = projectDirectory,
            CreatedLocal = DateTime.Now
        };

        SaveProject(project);
        return project;
    }

    public void SaveProject(ImageTrainingProject project)
    {
        Directory.CreateDirectory(project.ProjectDirectory);

        var path =
            Path.Combine(project.ProjectDirectory, "project.json");

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                project,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }),
            Encoding.UTF8);
    }

    public IReadOnlyList<ImageTrainingPrompt> BuildPromptPlan(
        string multilinePrompts,
        string negativePrompt,
        int width,
        int height)
    {
        var prompts =
            multilinePrompts
                .Split(
                    new[] { "\r\n", "\n" },
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

        var result = new List<ImageTrainingPrompt>();

        for (var i = 0; i < prompts.Length; i++)
        {
            result.Add(
                new ImageTrainingPrompt(
                    i + 1,
                    $"train-{i + 1:0000}.png",
                    prompts[i],
                    negativePrompt?.Trim() ?? string.Empty,
                    width,
                    height));
        }

        return result;
    }

    public string ExportPromptsCsv(
        ImageTrainingProject project,
        IReadOnlyList<ImageTrainingPrompt> prompts)
    {
        var path =
            Path.Combine(project.ProjectDirectory, "prompts.csv");

        var sb = new StringBuilder();
        sb.AppendLine(
            "id,file_name,prompt,negative_prompt,width,height");

        foreach (var item in prompts)
        {
            sb.Append(item.Id.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(Csv(item.FileName));
            sb.Append(',');
            sb.Append(Csv(item.Prompt));
            sb.Append(',');
            sb.Append(Csv(item.NegativePrompt));
            sb.Append(',');
            sb.Append(item.Width.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(item.Height.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

        project.Prompts = prompts.ToList();
        SaveProject(project);

        return path;
    }

    public IReadOnlyList<ImageTrainingPrompt> ImportPromptsCsv(
        string csvPath)
    {
        var lines =
            File.ReadAllLines(csvPath, Encoding.UTF8);

        if (lines.Length <= 1)
            return Array.Empty<ImageTrainingPrompt>();

        var result =
            new List<ImageTrainingPrompt>();

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var columns = ParseCsvLine(line);

            if (columns.Count < 6)
                continue;

            if (!int.TryParse(
                    columns[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var id))
            {
                continue;
            }

            var width =
                int.TryParse(
                    columns[4],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var w)
                    ? w
                    : 1024;

            var height =
                int.TryParse(
                    columns[5],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var h)
                    ? h
                    : 1024;

            result.Add(
                new ImageTrainingPrompt(
                    id,
                    columns[1],
                    columns[2],
                    columns[3],
                    width,
                    height));
        }

        return result;
    }

    public IReadOnlyList<ImageTrainingItem> ImportImages(
        ImageTrainingProject project,
        IEnumerable<string> sourcePaths,
        string fallbackCaption)
    {
        var imageDirectory =
            Path.Combine(project.ProjectDirectory, "images");

        Directory.CreateDirectory(imageDirectory);

        var promptLookup =
            project.Prompts
                .GroupBy(
                    x => Path.GetFileNameWithoutExtension(x.FileName),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Key,
                    x => x.First(),
                    StringComparer.OrdinalIgnoreCase);

        var imported =
            new List<ImageTrainingItem>();

        foreach (var source in sourcePaths)
        {
            if (!File.Exists(source))
                continue;

            var ext =
                Path.GetExtension(source);

            if (!ImageExtensions.Contains(
                    ext,
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationName =
                MakeUniqueName(
                    imageDirectory,
                    Path.GetFileName(source));

            var destination =
                Path.Combine(
                    imageDirectory,
                    destinationName);

            File.Copy(
                source,
                destination,
                overwrite: false);

            var sourceCaption =
                Path.ChangeExtension(source, ".txt");

            string caption;
            string sourceLabel;

            if (File.Exists(sourceCaption))
            {
                caption =
                    File.ReadAllText(
                        sourceCaption,
                        Encoding.UTF8)
                    .Trim();

                sourceLabel = "sidecar";
            }
            else
            {
                var stem =
                    Path.GetFileNameWithoutExtension(
                        source);

                if (promptLookup.TryGetValue(
                        stem,
                        out var prompt))
                {
                    caption =
                        prompt.Prompt;

                    sourceLabel = "prompts.csv";
                }
                else
                {
                    caption =
                        fallbackCaption?.Trim() ??
                        string.Empty;

                    sourceLabel = "manual";
                }
            }

            var captionPath =
                Path.ChangeExtension(
                    destination,
                    ".txt");

            File.WriteAllText(
                captionPath,
                caption + Environment.NewLine,
                Encoding.UTF8);

            imported.Add(
                new ImageTrainingItem(
                    destination,
                    captionPath,
                    caption,
                    sourceLabel));
        }

        project.Items.AddRange(imported);
        SaveProject(project);

        return imported;
    }

    public string BuildDatasetToml(
        ImageTrainingProject project,
        ImageTrainingPreset preset)
    {
        var imageDirectory =
            Path.Combine(
                project.ProjectDirectory,
                "images");

        var path =
            Path.Combine(
                project.ProjectDirectory,
                "dataset_config.toml");

        var toml =
$"""[general]
shuffle_caption = false
caption_extension = ".txt"
keep_tokens = 1

[[datasets]]
resolution = {preset.Resolution}
batch_size = 1
enable_bucket = true
bucket_no_upscale = true
min_bucket_reso = 256
max_bucket_reso = {Math.Max(1024, preset.Resolution)}

  [[datasets.subsets]]
  image_dir = "{TomlPath(imageDirectory)}"
  caption_extension = ".txt"
  num_repeats = {preset.Repeats}
""";

        File.WriteAllText(
            path,
            toml,
            Encoding.UTF8);

        return path;
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '"')
            {
                if (quoted &&
                    i + 1 < line.Length &&
                    line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (ch == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString());
        return result;
    }

    private static string MakeUniqueName(
        string directory,
        string requested)
    {
        var baseName =
            Path.GetFileNameWithoutExtension(requested);

        var extension =
            Path.GetExtension(requested);

        var candidate = requested;
        var index = 2;

        while (File.Exists(
                   Path.Combine(
                       directory,
                       candidate)))
        {
            candidate =
                $"{baseName}-{index}{extension}";
            index++;
        }

        return candidate;
    }

    private static string TomlPath(string path) =>
        Path.GetFullPath(path)
            .Replace("\\", "/")
            .Replace("\"", "\\\"");

    private static string SanitizeFileName(
        string value)
    {
        foreach (var ch in
                 Path.GetInvalidFileNameChars())
        {
            value =
                value.Replace(
                    ch,
                    '-');
        }

        return value.Trim();
    }
}
