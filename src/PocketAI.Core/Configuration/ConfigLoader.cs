using System.Text.Json;

namespace PocketAI.Core.Configuration;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static PocketAiConfig Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "pocketai.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Не найден pocketai.json рядом с PocketAI.exe.", path);

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<PocketAiConfig>(json, JsonOptions)
                     ?? throw new InvalidDataException("pocketai.json пуст или повреждён.");
        Validate(config);
        return config;
    }

    public static void Save(string baseDirectory, PocketAiConfig config)
    {
        Validate(config);
        var path = Path.Combine(baseDirectory, "pocketai.json");
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    public static string ResolvePath(string baseDirectory, string relativeOrAbsolutePath)
        => Path.IsPathRooted(relativeOrAbsolutePath)
            ? Path.GetFullPath(relativeOrAbsolutePath)
            : Path.GetFullPath(Path.Combine(baseDirectory, relativeOrAbsolutePath));

    private static void Validate(PocketAiConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ModelPath)) throw new InvalidDataException("modelPath не задан.");
        if (string.IsNullOrWhiteSpace(config.Runtime.CpuPath)) throw new InvalidDataException("runtime.cpuPath не задан.");
        if (config.ContextSize < 1024) throw new InvalidDataException("contextSize должен быть не меньше 1024.");
        if (config.MaxOutputTokens is < 32 or > 32768) throw new InvalidDataException("maxOutputTokens должен быть 32..32768.");
        if (config.Temperature is < 0 or > 2) throw new InvalidDataException("temperature должен быть 0..2.");
        if (config.Embeddings.ContextSize < 256) throw new InvalidDataException("embeddings.contextSize должен быть >= 256.");
        if (config.Knowledge.TopK is < 1 or > 20) throw new InvalidDataException("knowledge.topK должен быть 1..20.");
        if (config.Web.MaxResults is < 1 or > 20) throw new InvalidDataException("web.maxResults должен быть 1..20.");
        if (config.Web.MaxPagesToRead is < 0 or > 10) throw new InvalidDataException("web.maxPagesToRead должен быть 0..10.");
        if (config.Images.Width is < 64 or > 4096 || config.Images.Height is < 64 or > 4096)
            throw new InvalidDataException("Размер изображения должен быть 64..4096.");
        if (!Uri.TryCreate(config.Images.ServerUrl, UriKind.Absolute, out var imageUri) ||
            imageUri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("images.serverUrl должен быть HTTP/HTTPS URL.");
    }
}
