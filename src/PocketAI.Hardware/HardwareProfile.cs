namespace PocketAI.Hardware;

public sealed record GpuInfo(string Name, ulong? AdapterRamBytes, string? DriverVersion);

public sealed record HardwareProfile(
    string OperatingSystem,
    string Architecture,
    string CpuName,
    int LogicalProcessors,
    bool Avx2Supported,
    ulong TotalMemoryBytes,
    ulong AvailableMemoryBytes,
    IReadOnlyList<GpuInfo> Gpus)
{
    public bool HasNvidiaGpu => Gpus.Any(g =>
        g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
        g.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
        g.Name.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
        g.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase));

    public string PrimaryGpuName => Gpus.FirstOrDefault()?.Name ?? "Не обнаружена";
}
