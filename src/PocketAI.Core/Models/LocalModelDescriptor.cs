namespace PocketAI.Core.Models;

public sealed record LocalModelDescriptor(
    string Name,
    string RelativePath,
    long SizeBytes,
    DateTime LastWriteTimeUtc)
{
    public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{SizeBytes / 1024d / 1024d / 1024d:0.00} GB",
        >= 1024L * 1024 => $"{SizeBytes / 1024d / 1024d:0.0} MB",
        >= 1024L => $"{SizeBytes / 1024d:0.0} KB",
        _ => $"{SizeBytes} B"
    };

    public string Label => $"{Name} · {SizeText}";
}
