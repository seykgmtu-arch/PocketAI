namespace PocketAI.Core.Models;

public sealed class LocalModelCatalog
{
    private readonly string _baseDirectory;

    public LocalModelCatalog(string baseDirectory)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
    }

    public IReadOnlyList<LocalModelDescriptor> ScanChatModels()
    {
        var modelsDirectory = Path.Combine(_baseDirectory, "models", "chat");
        if (!Directory.Exists(modelsDirectory))
            return Array.Empty<LocalModelDescriptor>();

        return Directory
            .EnumerateFiles(modelsDirectory, "*.gguf", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                var relativePath = Path.GetRelativePath(_baseDirectory, path)
                    .Replace('\\', '/');

                return new LocalModelDescriptor(
                    Path.GetFileNameWithoutExtension(path),
                    relativePath,
                    info.Length,
                    info.LastWriteTimeUtc);
            })
            .OrderBy(model => model.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public LocalModelDescriptor? FindConfiguredModel(
        IEnumerable<LocalModelDescriptor> models,
        string configuredPath)
    {
        var normalized = configuredPath.Replace('\\', '/');

        return models.FirstOrDefault(model =>
            model.RelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }
}
