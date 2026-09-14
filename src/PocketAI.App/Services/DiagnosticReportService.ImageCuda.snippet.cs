// =======================
// DiagnosticReportService.ImageCuda.snippet.cs
// Встроить в текущий DiagnosticReportService.
// =======================

// 1) В report.json полезно добавить поля:
imagePreferredBackend = imageServerUsesLoopback ? "auto (CUDA if NVIDIA available)" : "external",
imageProfilesSupported = new[] { "SD 1.5 Fast 512", "SDXL 1024" },

// 2) В config.sanitized.json полезно добавить:
imageDefaultProfile = "auto",
imageRecommendedForCurrentMachine = "SDXL 1024 (RTX 3080 12 GB)",
imageServerRuntimeSearchOrder = new[]
{
    "runtime/image/cuda/sd-server.exe",
    "runtime/image/sd-server.exe",
    "runtime/image/cpu/sd-server.exe"
},

// 3) Если в MainViewModel позже будут передаваться runtime stats, можно добавить:
imageLastBackend = "[optional runtime value]",
imageLastModel = "[optional runtime value]",
imageLastProfile = "[optional runtime value]",
imageHardwareSummary = "[optional runtime value]",

// 4) privacy-заметка:
note = "Prompt texts, generated images and absolute model paths are omitted."
