// =======================
// MainViewModel.ImageCuda.snippet.cs
// Вставить в текущий MainViewModel поверх уже рабочего image hotfix.
// =======================

// 1) Добавить using:
using System.Collections.ObjectModel;
using System.Globalization;
using PocketAI.Images;

// 2) Добавить поля:
private string _imageHardwareText = "Image hardware: detecting…";
private string _imageRuntimeText = "Image backend/model: not checked";
private ImageProfile? _selectedImageProfile;

// 3) Добавить свойства:
public ObservableCollection<ImageProfile> ImageProfiles { get; } = new()
{
    ImageProfile.Sd15Fast512,
    ImageProfile.Sdxl1024
};

public ImageProfile? SelectedImageProfile
{
    get => _selectedImageProfile;
    set
    {
        if (SetProperty(ref _selectedImageProfile, value) && value is not null)
        {
            _config.Images.Width = value.Width;
            _config.Images.Height = value.Height;
            ConfigLoader.Save(_baseDirectory, _config);
            OnPropertyChanged(nameof(ImageProfileText));
        }
    }
}

public string ImageProfileText =>
    SelectedImageProfile is null
        ? "Image profile: not selected"
        : $"{SelectedImageProfile.Name} · {_config.Images.Width}×{_config.Images.Height}";

public string ImageHardwareText
{
    get => _imageHardwareText;
    private set => SetProperty(ref _imageHardwareText, value);
}

public string ImageRuntimeText
{
    get => _imageRuntimeText;
    private set => SetProperty(ref _imageRuntimeText, value);
}

// 4) В конструкторе после _imageServerManager = new ImageServerManager(_baseDirectory);
// инициализировать профиль:
SelectedImageProfile = ImageProfile.Sdxl1024;

// 5) В InitializeAsync() после MemoryText=... добавить:
ImageHardwareText = BuildImageHardwareSummary();

// 6) Добавить методы:
private string BuildImageHardwareSummary()
{
    var fallback = $"{_hardware?.PrimaryGpuName ?? "GPU unknown"}";

    try
    {
        if (_hardware?.PrimaryGpuName?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true ||
            _hardware?.PrimaryGpuName?.Contains("RTX", StringComparison.OrdinalIgnoreCase) == true ||
            _hardware?.PrimaryGpuName?.Contains("GeForce", StringComparison.OrdinalIgnoreCase) == true)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);

            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(2000);

                if (!string.IsNullOrWhiteSpace(output))
                {
                    var first = output
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(first))
                    {
                        var parts = first.Split(',', 2, StringSplitOptions.TrimEntries);
                        if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib))
                        {
                            var gib = mib / 1024.0;
                            return $"{parts[0]} · {gib:0.#} GB VRAM · CUDA preferred";
                        }

                        return first + " · CUDA preferred";
                    }
                }
            }
        }
    }
    catch
    {
    }

    return fallback + " · image backend auto";
}

private bool PreferCudaForImages() =>
    _hardware?.PrimaryGpuName?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true ||
    _hardware?.PrimaryGpuName?.Contains("RTX", StringComparison.OrdinalIgnoreCase) == true ||
    _hardware?.PrimaryGpuName?.Contains("GeForce", StringComparison.OrdinalIgnoreCase) == true;

private async Task<string> EnsureImageServerWithProfileAsync(CancellationToken ct)
{
    if (!Uri.TryCreate(_config.Images.ServerUrl, UriKind.Absolute, out var serverUri))
        throw new InvalidDataException("images.serverUrl имеет неверный формат.");

    var profile = SelectedImageProfile ?? ImageProfile.Sdxl1024;
    _config.Images.Width = profile.Width;
    _config.Images.Height = profile.Height;
    ConfigLoader.Save(_baseDirectory, _config);

    var session = await _imageServerManager.EnsureStartedAsync(
        serverUri,
        preferCuda: PreferCudaForImages(),
        profile: profile,
        threads: Math.Max(1, _hardware?.LogicalProcessors ?? Environment.ProcessorCount),
        progress: new Progress<string>(text => ImageStatusText = text),
        ct: ct);

    ImageRuntimeText = $"{session.Backend} · {session.ModelLabel}" +
                       (session.ReusedExistingServer ? " · reused server" : " · local server");

    using var client = new ImageGenerationClient(serverUri);
    return await client.CheckAsync(ct);
}

// 7) В CheckImageServerAsync вместо старого вызова EnsureImageServerAsync использовать:
var model = await EnsureImageServerWithProfileAsync(_generationCts.Token);
ImageStatusText = $"Image server: готов · {model}";

// 8) В GenerateImageAsync вместо старого вызова EnsureImageServerAsync использовать:
var model = await EnsureImageServerWithProfileAsync(_generationCts.Token);
ImageStatusText = $"Генерация {_config.Images.Width}×{_config.Images.Height} · {model}…";

// 9) После успешной генерации обновлять статус:
ImageStatusText = $"Готово · {Path.GetFileName(path)} · {_config.Images.Width}×{_config.Images.Height}";
