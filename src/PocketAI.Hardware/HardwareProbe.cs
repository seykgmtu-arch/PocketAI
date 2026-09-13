using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace PocketAI.Hardware;

public sealed class HardwareProbe
{
    public Task<HardwareProfile> ProbeAsync(CancellationToken cancellationToken = default)
        => Task.Run(Probe, cancellationToken);

    private static HardwareProfile Probe()
    {
        var cpuName = GetCpuName();
        var memory = GetMemoryStatus();
        var gpus = GetGpus();

        return new HardwareProfile(
            OperatingSystem: RuntimeInformation.OSDescription.Trim(),
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            CpuName: cpuName,
            LogicalProcessors: Environment.ProcessorCount,
            Avx2Supported: Avx2.IsSupported,
            TotalMemoryBytes: memory.TotalPhysical,
            AvailableMemoryBytes: memory.AvailablePhysical,
            Gpus: gpus);
    }

    private static string GetCpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch
        {
            // WMI can be disabled by policy. Fall back to architecture information.
        }

        return $"{RuntimeInformation.ProcessArchitecture} CPU";
    }

    private static IReadOnlyList<GpuInfo> GetGpus()
    {
        var result = new List<GpuInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                ulong? ram = null;
                try
                {
                    if (obj["AdapterRAM"] is not null)
                        ram = Convert.ToUInt64(obj["AdapterRAM"]);
                }
                catch
                {
                    // AdapterRAM from WMI is best-effort and can be absent/inaccurate.
                }

                result.Add(new GpuInfo(
                    name,
                    ram,
                    obj["DriverVersion"]?.ToString()?.Trim()));
            }
        }
        catch
        {
            // Hardware detection must never prevent local chat from starting.
        }

        return result;
    }

    private static MemoryStatus GetMemoryStatus()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
            return new MemoryStatus(0, 0);

        return new MemoryStatus(status.TotalPhys, status.AvailPhys);
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private readonly record struct MemoryStatus(ulong TotalPhysical, ulong AvailablePhysical);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);
}
