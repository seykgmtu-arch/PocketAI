using System.Diagnostics;
using System.Globalization;
using PocketAI.App.Infrastructure;
using PocketAI.Core.Configuration;
using PocketAI.Hardware;
using PocketAI.Images;

namespace PocketAI.App.ViewModels;

public sealed record ImageStylePreset(
    string Name,
    string PositiveSuffix,
    string NegativeSuffix);

public sealed class ImageGeneratorViewModel : ObservableObject, IDisposable
{
    private readonly string _baseDirectory = AppContext.BaseDirectory;
    private readonly PocketAiConfig _config;
    private readonly HardwareProbe _hardwareProbe = new();
    private readonly ImageEngineManager _engineManager;

    private HardwareProfile? _hardware;
    private CancellationTokenSource? _cts;

    private string _prompt = string.Empty;
    private string _negativePrompt =
        "blurry, low quality, distorted, deformed, watermark, text artifacts";
    private string _widthText;
    private string _heightText;
    private string _seedText = "-1";
    private string _stepsText = "24";
    private string _cfgScaleText = "7.0";
    private int _batchSize = 1;

    private ImageStylePreset? _selectedStyle;
    private ImageProfile? _selectedProfile;

    private string _statusText =
        "Локальный image backend ещё не запускался.";
    private string _backendText =
        "Определяем CPU/CUDA…";
    private string _lastImagePath = string.Empty;
    private bool _isBusy;
    private string? _errorText;

    public ImageGeneratorViewModel()
    {
        _config = ConfigLoader.Load(_baseDirectory);
        _engineManager = new ImageEngineManager(_baseDirectory);

        _widthText = _config.Images.Width.ToString(
            CultureInfo.InvariantCulture);
        _heightText = _config.Images.Height.ToString(
            CultureInfo.InvariantCulture);

        Styles = new[]
        {
            new ImageStylePreset(
                "Без стиля",
                string.Empty,
                string.Empty),
            new ImageStylePreset(
                "Фотореализм",
                "photorealistic, natural lighting, realistic materials, highly detailed",
                "illustration, cartoon, CGI look"),
            new ImageStylePreset(
                "Кинематографичный",
                "cinematic composition, dramatic lighting, film still, detailed atmosphere",
                "flat lighting, low detail"),
            new ImageStylePreset(
                "Цифровая иллюстрация",
                "professional digital illustration, clean composition, detailed",
                "photographic noise, compression artifacts"),
            new ImageStylePreset(
                "Карандашный рисунок",
                "detailed graphite pencil drawing, precise linework, shaded sketch",
                "photograph, glossy CGI"),
            new ImageStylePreset(
                "Техническая иллюстрация",
                "clean technical illustration, engineering visualization, precise geometry, clear details",
                "decorative clutter, illegible labels, distorted geometry")
        };

        Profiles = new[]
        {
            ImageProfile.Sd15Fast512,
            ImageProfile.Sdxl1024
        };

        BatchSizes = new[] { 1, 2, 4 };

        _selectedStyle = Styles[0];

        var currentLargest = Math.Max(
            _config.Images.Width,
            _config.Images.Height);

        _selectedProfile =
            currentLargest <= 640
                ? ImageProfile.Sd15Fast512
                : ImageProfile.Sdxl1024;

        GenerateCommand = new AsyncRelayCommand(
            GenerateAsync,
            () =>
                !IsBusy &&
                !string.IsNullOrWhiteSpace(Prompt));

        CheckServerCommand = new AsyncRelayCommand(
            CheckServerAsync,
            () => !IsBusy);

        StopCommand = new RelayCommand(
            Stop,
            () => IsBusy);

        OpenImagesFolderCommand = new RelayCommand(
            OpenImagesFolder);

        Set512Command = new RelayCommand(
            () => SetSize(512, 512));

        Set768Command = new RelayCommand(
            () => SetSize(768, 768));

        Set1024Command = new RelayCommand(
            () => SetSize(1024, 1024));

        SetLandscapeCommand = new RelayCommand(
            () => SetSize(1024, 576));

        SetPortraitCommand = new RelayCommand(
            () => SetSize(576, 1024));
    }

    public IReadOnlyList<ImageStylePreset> Styles { get; }
    public IReadOnlyList<ImageProfile> Profiles { get; }
    public IReadOnlyList<int> BatchSizes { get; }

    public AsyncRelayCommand GenerateCommand { get; }
    public AsyncRelayCommand CheckServerCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand OpenImagesFolderCommand { get; }

    public RelayCommand Set512Command { get; }
    public RelayCommand Set768Command { get; }
    public RelayCommand Set1024Command { get; }
    public RelayCommand SetLandscapeCommand { get; }
    public RelayCommand SetPortraitCommand { get; }

    public string Prompt
    {
        get => _prompt;
        set
        {
            if (SetProperty(ref _prompt, value))
                GenerateCommand.RaiseCanExecuteChanged();
        }
    }

    public string NegativePrompt
    {
        get => _negativePrompt;
        set => SetProperty(ref _negativePrompt, value);
    }

    public string WidthText
    {
        get => _widthText;
        set => SetProperty(ref _widthText, value);
    }

    public string HeightText
    {
        get => _heightText;
        set => SetProperty(ref _heightText, value);
    }

    public string SeedText
    {
        get => _seedText;
        set => SetProperty(ref _seedText, value);
    }

    public string StepsText
    {
        get => _stepsText;
        set => SetProperty(ref _stepsText, value);
    }

    public string CfgScaleText
    {
        get => _cfgScaleText;
        set => SetProperty(ref _cfgScaleText, value);
    }

    public int BatchSize
    {
        get => _batchSize;
        set => SetProperty(ref _batchSize, value);
    }

    public ImageStylePreset? SelectedStyle
    {
        get => _selectedStyle;
        set => SetProperty(ref _selectedStyle, value);
    }

    public ImageProfile? SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string BackendText
    {
        get => _backendText;
        private set => SetProperty(ref _backendText, value);
    }

    public string LastImagePath
    {
        get => _lastImagePath;
        private set
        {
            if (SetProperty(ref _lastImagePath, value))
                OnPropertyChanged(nameof(HasLastImage));
        }
    }

    public bool HasLastImage =>
        !string.IsNullOrWhiteSpace(LastImagePath) &&
        File.Exists(LastImagePath);

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError =>
        !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;

            GenerateCommand.RaiseCanExecuteChanged();
            CheckServerCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task InitializeAsync()
    {
        if (_hardware is not null)
            return;

        try
        {
            StatusText = "Определяем оборудование для image backend…";
            _hardware = await _hardwareProbe.ProbeAsync();

            var defaultProfile = _engineManager.ChooseDefaultProfile(
                _hardware,
                ParseInt(WidthText, _config.Images.Width),
                ParseInt(HeightText, _config.Images.Height));

            SelectedProfile = defaultProfile;

            BackendText =
                _hardware.HasNvidiaGpu
                    ? $"{_hardware.PrimaryGpuName} · CUDA auto, CPU fallback"
                    : $"{_hardware.PrimaryGpuName} · CPU/auto";

            StatusText =
                "Готово к генерации. sd-server.exe будет запущен при первом запросе.";
        }
        catch (Exception ex)
        {
            ErrorText = Friendly(ex);
            StatusText = "Не удалось определить image backend.";
        }
    }

    private async Task CheckServerAsync()
    {
        await EnsureHardwareAsync();

        if (_hardware is null)
            return;

        IsBusy = true;
        ErrorText = null;

        ResetCancellation();

        try
        {
            var size = GetNormalizedSize();
            var profile = SelectedProfile ??
                          _engineManager.ChooseDefaultProfile(
                              _hardware,
                              size.Width,
                              size.Height);

            var session = await _engineManager.EnsureReadyAsync(
                GetServerUri(),
                _hardware,
                profile,
                new Progress<string>(x => StatusText = x),
                _cts!.Token);

            BackendText = session.Summary;
            StatusText = "Image server готов · " + size.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Проверка image backend остановлена.";
        }
        catch (Exception ex)
        {
            ErrorText = Friendly(ex);
            StatusText = "Image backend недоступен.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GenerateAsync()
    {
        await EnsureHardwareAsync();

        if (_hardware is null ||
            string.IsNullOrWhiteSpace(Prompt))
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;

        ResetCancellation();

        try
        {
            var size = GetNormalizedSize();

            WidthText = size.Width.ToString(
                CultureInfo.InvariantCulture);
            HeightText = size.Height.ToString(
                CultureInfo.InvariantCulture);

            _config.Images.Enabled = true;
            _config.Images.Width = size.Width;
            _config.Images.Height = size.Height;
            ConfigLoader.Save(_baseDirectory, _config);

            var profile = SelectedProfile ??
                          _engineManager.ChooseDefaultProfile(
                              _hardware,
                              size.Width,
                              size.Height);

            var session = await _engineManager.EnsureReadyAsync(
                GetServerUri(),
                _hardware,
                profile,
                new Progress<string>(x => StatusText = x),
                _cts!.Token);

            BackendText = session.Summary;

            var style = SelectedStyle ?? Styles[0];

            var finalPrompt = Combine(
                Prompt.Trim(),
                style.PositiveSuffix);

            var finalNegativePrompt = Combine(
                NegativePrompt.Trim(),
                style.NegativeSuffix);

            var request = new ImageGenerationRequest(
                finalPrompt,
                finalNegativePrompt,
                size.Width,
                size.Height,
                ParseLong(SeedText, -1),
                ParseInt(StepsText, 24),
                ParseDouble(CfgScaleText, 7.0),
                BatchSize);

            var outputDirectory = ConfigLoader.ResolvePath(
                _baseDirectory,
                _config.Images.OutputDirectory);

            StatusText =
                $"Генерация {size.Width}×{size.Height} · {session.Backend}…";

            var result = await _engineManager.GenerateAsync(
                GetServerUri(),
                request,
                outputDirectory,
                _cts.Token);

            LastImagePath = Path.GetFullPath(
                result.FilePaths[0]);

            StatusText =
                $"Готово · {result.Width}×{result.Height} · " +
                $"{result.FilePaths.Count} image(s). {result.SizeMessage}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Генерация остановлена.";
        }
        catch (Exception ex)
        {
            ErrorText = Friendly(ex);
            StatusText = "Ошибка генерации изображения.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ImageSizeValidationResult GetNormalizedSize()
    {
        var requestedWidth =
            ParseInt(WidthText, _config.Images.Width);

        var requestedHeight =
            ParseInt(HeightText, _config.Images.Height);

        return ImageSizePolicy.Normalize(
            requestedWidth,
            requestedHeight);
    }

    private Uri GetServerUri()
    {
        if (!Uri.TryCreate(
                _config.Images.ServerUrl,
                UriKind.Absolute,
                out var uri))
        {
            throw new InvalidDataException(
                "images.serverUrl имеет неверный формат.");
        }

        return uri;
    }

    private async Task EnsureHardwareAsync()
    {
        if (_hardware is null)
            await InitializeAsync();
    }

    private void ResetCancellation()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }

    private void Stop()
    {
        _cts?.Cancel();
        StatusText = "Останавливаем текущую операцию…";
    }

    private void OpenImagesFolder()
    {
        var path = ConfigLoader.ResolvePath(
            _baseDirectory,
            _config.Images.OutputDirectory);

        Directory.CreateDirectory(path);

        Process.Start(
            new ProcessStartInfo(
                "explorer.exe",
                path)
            {
                UseShellExecute = true
            });
    }

    private void SetSize(int width, int height)
    {
        WidthText = width.ToString(
            CultureInfo.InvariantCulture);
        HeightText = height.ToString(
            CultureInfo.InvariantCulture);

        StatusText =
            $"Размер выбран: {width}×{height}. " +
            "Перед генерацией backend выполнит финальную валидацию.";
    }

    private static string Combine(
        string first,
        string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(second))
            return first.Trim();

        return first.Trim() + ", " + second.Trim();
    }

    private static int ParseInt(
        string? value,
        int fallback)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }

    private static long ParseLong(
        string? value,
        long fallback)
    {
        return long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }

    private static double ParseDouble(
        string? value,
        double fallback)
    {
        var normalized = value?
            .Replace(',', '.')
            .Trim();

        return double.TryParse(
            normalized,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }

    private static string Friendly(Exception exception)
    {
        var root = exception.GetBaseException();

        return string.IsNullOrWhiteSpace(root.Message)
            ? root.GetType().Name
            : root.Message;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _engineManager.Dispose();
    }
}
