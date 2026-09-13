using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using PocketAI.App.Infrastructure;
using PocketAI.Core.Chat;
using PocketAI.Core.Configuration;
using PocketAI.Hardware;
using PocketAI.Inference;
using PocketAI.Inference.Api;

namespace PocketAI.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly PocketAiConfig _config;
    private readonly HardwareProbe _hardwareProbe;
    private readonly LlamaServerManager _serverManager;
    private LlamaApiClient? _apiClient;
    private CancellationTokenSource? _generationCts;

    private string _inputText = string.Empty;
    private string _statusText = "Подготовка…";
    private string _backendText = "—";
    private string _cpuText = "Определяем…";
    private string _gpuText = "Определяем…";
    private string _memoryText = "Определяем…";
    private bool _isInitializing = true;
    private bool _isGenerating;
    private bool _isReady;
    private string? _errorText;

    public MainViewModel(
        PocketAiConfig config,
        HardwareProbe hardwareProbe,
        LlamaServerManager serverManager)
    {
        _config = config;
        _hardwareProbe = hardwareProbe;
        _serverManager = serverManager;

        SendCommand = new AsyncRelayCommand(SendAsync, () => IsReady && !IsGenerating && !string.IsNullOrWhiteSpace(InputText));
        CancelCommand = new RelayCommand(CancelGeneration, () => IsGenerating);
    }

    public ObservableCollection<ChatBubbleViewModel> Messages { get; } = new();
    public AsyncRelayCommand SendCommand { get; }
    public RelayCommand CancelCommand { get; }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value))
                SendCommand.RaiseCanExecuteChanged();
        }
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

    public string CpuText
    {
        get => _cpuText;
        private set => SetProperty(ref _cpuText, value);
    }

    public string GpuText
    {
        get => _gpuText;
        private set => SetProperty(ref _gpuText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    public bool IsInitializing
    {
        get => _isInitializing;
        private set => SetProperty(ref _isInitializing, value);
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (!SetProperty(ref _isGenerating, value))
                return;
            SendCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsReady
    {
        get => _isReady;
        private set
        {
            if (!SetProperty(ref _isReady, value))
                return;
            SendCommand.RaiseCanExecuteChanged();
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public async Task InitializeAsync()
    {
        if (IsReady)
            return;

        IsInitializing = true;
        ErrorText = null;

        try
        {
            StatusText = "Определяем оборудование…";
            var hardware = await _hardwareProbe.ProbeAsync();

            CpuText = $"{hardware.CpuName} · {hardware.LogicalProcessors} потоков" +
                      (hardware.Avx2Supported ? " · AVX2" : string.Empty);
            GpuText = hardware.PrimaryGpuName;
            MemoryText = $"{ToGiB(hardware.TotalMemoryBytes):0.#} GB RAM · свободно {ToGiB(hardware.AvailableMemoryBytes):0.#} GB";

            var progress = new Progress<string>(text => StatusText = text);
            var session = await _serverManager.StartAsync(hardware, progress);

            _apiClient = new LlamaApiClient(session, _config);
            BackendText = session.Backend == BackendKind.Cuda ? "CUDA / GPU" : "CPU";
            StatusText = "Локальный AI готов";
            IsReady = true;

            Messages.Add(new ChatBubbleViewModel(
                "assistant",
                "Pocket AI готов. Все запросы в этом прототипе отправляются только локальному llama-server на 127.0.0.1."));
        }
        catch (Exception ex)
        {
            StatusText = "Не удалось запустить Pocket AI";
            ErrorText = BuildFriendlyError(ex);
        }
        finally
        {
            IsInitializing = false;
        }
    }

    public void Dispose()
    {
        CancelGeneration();
        _apiClient?.Dispose();
        _apiClient = null;
        _serverManager.Dispose();
    }

    private async Task SendAsync()
    {
        if (_apiClient is null || string.IsNullOrWhiteSpace(InputText))
            return;

        var userText = InputText.Trim();
        InputText = string.Empty;

        var userBubble = new ChatBubbleViewModel("user", userText);
        var assistantBubble = new ChatBubbleViewModel("assistant", string.Empty);
        Messages.Add(userBubble);
        Messages.Add(assistantBubble);

        var history = Messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) && !ReferenceEquals(m, assistantBubble))
            .Select(m => new ChatMessage(m.IsUser ? "user" : "assistant", m.Content))
            .TakeLast(20)
            .ToList();

        _generationCts?.Dispose();
        _generationCts = new CancellationTokenSource();
        IsGenerating = true;
        ErrorText = null;
        StatusText = "AI отвечает…";

        try
        {
            await _apiClient.StreamChatAsync(
                history,
                async token =>
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        assistantBubble.Content += token;
                    });
                },
                _generationCts.Token);

            if (string.IsNullOrWhiteSpace(assistantBubble.Content))
                assistantBubble.Content = "Модель не вернула текстовый ответ.";

            StatusText = "Локальный AI готов";
        }
        catch (OperationCanceledException)
        {
            if (string.IsNullOrWhiteSpace(assistantBubble.Content))
                Messages.Remove(assistantBubble);
            StatusText = "Генерация остановлена";
        }
        catch (Exception ex)
        {
            assistantBubble.Content = string.IsNullOrWhiteSpace(assistantBubble.Content)
                ? "Не удалось получить ответ."
                : assistantBubble.Content;
            ErrorText = BuildFriendlyError(ex);
            StatusText = "Ошибка генерации";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private void CancelGeneration() => _generationCts?.Cancel();

    private static double ToGiB(ulong bytes) => bytes / 1024d / 1024d / 1024d;

    private static string BuildFriendlyError(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (message.Length > 1600)
            message = message[..1600] + "…";

        return message;
    }
}
