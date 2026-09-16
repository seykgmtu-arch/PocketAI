using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using PocketAI.App.Services;

namespace PocketAI.App.Views;

public partial class ControlCenterView :
    UserControl,
    INotifyPropertyChanged,
    IDisposable
{
    private readonly ControlCenterService _service =
        new();

    private CancellationTokenSource? _refreshCts;
    private ControlCenterScanResult? _lastScan;

    private string _hardwareText =
        "Определяем…";

    private string _diskText =
        "Определяем…";

    private string _rootText =
        "PocketAI root: —";

    private string _versionLockText =
        "Version lock: проверяем…";

    private string _lastActionText =
        "Готов к проверке.";

    private bool _isBusy;

    public ControlCenterView()
    {
        InitializeComponent();

        DataContext =
            this;

        Loaded +=
            OnLoaded;
    }

    public ObservableCollection<
        ControlCenterModuleStatus>
        Modules { get; } =
            new();

    public string HardwareText
    {
        get =>
            _hardwareText;

        private set =>
            SetField(
                ref _hardwareText,
                value);
    }

    public string DiskText
    {
        get =>
            _diskText;

        private set =>
            SetField(
                ref _diskText,
                value);
    }

    public string RootText
    {
        get =>
            _rootText;

        private set =>
            SetField(
                ref _rootText,
                value);
    }

    public string VersionLockText
    {
        get =>
            _versionLockText;

        private set =>
            SetField(
                ref _versionLockText,
                value);
    }

    public string LastActionText
    {
        get =>
            _lastActionText;

        private set =>
            SetField(
                ref _lastActionText,
                value);
    }

    public Visibility BusyVisibility =>
        _isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;

    public event PropertyChangedEventHandler?
        PropertyChanged;

    private async void OnLoaded(
        object sender,
        RoutedEventArgs e)
    {
        Loaded -=
            OnLoaded;

        await RefreshSafeAsync();
    }

    private async void Refresh_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshSafeAsync();
    }

    private async Task RefreshSafeAsync()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(
            true);

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();

        _refreshCts =
            new CancellationTokenSource();

        try
        {
            LastActionText =
                "Проверяем состояние PocketAI…";

            var scan =
                await _service.ScanAsync(
                    _refreshCts.Token);

            _lastScan =
                scan;

            Modules.Clear();

            foreach (var module in
                     scan.Modules)
            {
                Modules.Add(
                    module);
            }

            HardwareText =
                scan.HardwareSummary;

            DiskText =
                scan.DiskSummary;

            RootText =
                "PocketAI root: " +
                scan.RootDirectory;

            VersionLockText =
                scan.VersionLockSummary;

            LastActionText =
                $"Проверка завершена: {scan.CreatedAt:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            LastActionText =
                "Проверка отменена.";
        }
        catch (Exception ex)
        {
            HandleError(
                "refresh",
                ex);
        }
        finally
        {
            SetBusy(
                false);
        }
    }

    private async void Snapshot_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        await ExecuteSafeAsync(
            async () =>
            {
                var scan =
                    await EnsureScanAsync();

                var path =
                    await _service
                        .CreateConfigurationSnapshotAsync(
                            scan);

                LastActionText =
                    "Снимок создан: " +
                    path;
            });
    }

    private async void Report_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        await ExecuteSafeAsync(
            async () =>
            {
                var scan =
                    await EnsureScanAsync();

                var path =
                    await _service
                        .CreateControlCenterReportAsync(
                            scan);

                LastActionText =
                    "ZIP создан: " +
                    path;
            });
    }

    private async void StopWorkers_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        var answer =
            MessageBox.Show(
                "Остановить процессы PocketAI runtime: " +
                "llama-server, sd-server, Python/FFmpeg workers " +
                "с командной строкой внутри каталога PocketAI?\n\n" +
                "Сам PocketAI.exe закрыт не будет.",
                "PocketAI Control Center",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

        if (answer !=
            MessageBoxResult.Yes)
        {
            return;
        }

        await ExecuteSafeAsync(
            async () =>
            {
                var count =
                    await _service
                        .StopPocketAiWorkersAsync();

                LastActionText =
                    $"Остановлено AI workers: {count}";

                await RefreshSafeAsync();
            });
    }

    private void OpenLogs_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        ExecuteSafe(
            _service.OpenLogsFolder);
    }

    private void OpenSnapshots_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        ExecuteSafe(
            _service.OpenSnapshotsFolder);
    }

    private void OpenVersionLock_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        ExecuteSafe(
            _service.OpenVersionLockFolder);
    }

    private void OpenDiagnostics_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        ExecuteSafe(
            _service.OpenDiagnosticsFolder);
    }

    private async Task<
        ControlCenterScanResult>
        EnsureScanAsync()
    {
        if (_lastScan is not null)
        {
            return _lastScan;
        }

        var scan =
            await _service.ScanAsync();

        _lastScan =
            scan;

        return scan;
    }

    private async Task ExecuteSafeAsync(
        Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(
            true);

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            HandleError(
                "action",
                ex);
        }
        finally
        {
            SetBusy(
                false);
        }
    }

    private void ExecuteSafe(
        Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            HandleError(
                "action",
                ex);
        }
    }

    private void HandleError(
        string stage,
        Exception exception)
    {
        ModuleErrorService.WriteException(
            "control-center-ui-error.log",
            new InvalidOperationException(
                $"Control Center {stage} failed.",
                exception));

        LastActionText =
            "Ошибка: " +
            exception.Message;
    }

    private void SetBusy(
        bool value)
    {
        if (_isBusy ==
            value)
        {
            return;
        }

        _isBusy =
            value;

        OnPropertyChanged(
            nameof(
                BusyVisibility));
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName]
        string? propertyName = null)
    {
        if (EqualityComparer<T>
            .Default
            .Equals(
                field,
                value))
        {
            return false;
        }

        field =
            value;

        OnPropertyChanged(
            propertyName);

        return true;
    }

    private void OnPropertyChanged(
        [CallerMemberName]
        string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(
                propertyName));
    }

    public void Dispose()
    {
        Loaded -=
            OnLoaded;

        try
        {
            _refreshCts?.Cancel();
        }
        catch
        {
        }

        _refreshCts?.Dispose();
        _refreshCts = null;
    }
}
