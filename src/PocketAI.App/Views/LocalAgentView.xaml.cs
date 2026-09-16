using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PocketAI.App.Services;

namespace PocketAI.App.Views;

public partial class LocalAgentView :
    UserControl,
    INotifyPropertyChanged,
    IDisposable
{
    private readonly LocalWorkflowService _service =
        new();

    private CancellationTokenSource? _runCts;
    private LocalWorkflowPlan? _plan;

    private string _workspacePath = "";

    private string _instruction =
        "проанализируй папку, найди дубликаты и собери текстовые файлы";

    private string _statusText =
        "Выберите рабочую папку и сформулируйте задачу.";

    private string _logText = "";

    private bool _isBusy;

    public LocalAgentView()
    {
        InitializeComponent();

        DataContext =
            this;
    }

    public ObservableCollection<LocalWorkflowStep>
        Steps { get; } =
            new();

    public string WorkspacePath
    {
        get => _workspacePath;
        set => SetField(
            ref _workspacePath,
            value);
    }

    public string Instruction
    {
        get => _instruction;
        set => SetField(
            ref _instruction,
            value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(
            ref _statusText,
            value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetField(
            ref _logText,
            value);
    }

    public Visibility BusyVisibility =>
        _isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;

    public event PropertyChangedEventHandler?
        PropertyChanged;

    private void ChooseWorkspace_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var dialog =
                new OpenFolderDialog
                {
                    Title =
                        "Выберите рабочую папку Local Agent",

                    Multiselect =
                        false
                };

            if (dialog.ShowDialog() ==
                true)
            {
                WorkspacePath =
                    dialog.FolderName;

                _plan =
                    null;

                Steps.Clear();

                StatusText =
                    "Папка выбрана. Нажмите «Составить план».";
            }
        }
        catch (Exception ex)
        {
            HandleError(
                ex);
        }
    }

    private void BuildPlan_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _plan =
                _service.BuildPlan(
                    WorkspacePath,
                    Instruction);

            Steps.Clear();

            foreach (var step in
                     _plan.Steps)
            {
                Steps.Add(
                    step);
            }

            StatusText =
                $"План готов: {Steps.Count} шаг(ов). " +
                "Просмотрите его и нажмите «Выполнить».";

            AppendLog(
                "PLAN CREATED");

            foreach (var step in
                     Steps)
            {
                AppendLog(
                    "  - " +
                    step.Title +
                    ": " +
                    step.Description);
            }
        }
        catch (Exception ex)
        {
            HandleError(
                ex);
        }
    }

    private async void Execute_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (_plan is null)
        {
            BuildPlan_OnClick(
                sender,
                e);

            if (_plan is null)
            {
                return;
            }
        }

        var answer =
            MessageBox.Show(
                "Local Agent выполнит показанный план.\n\n" +
                "v1 не удаляет и не перемещает исходные файлы. " +
                "Новые результаты будут созданы только внутри:\n\n" +
                _plan.OutputDirectory +
                "\n\nПродолжить?",
                "PocketAI Local Agent",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

        if (answer !=
            MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(
            true);

        _runCts?.Cancel();
        _runCts?.Dispose();

        _runCts =
            new CancellationTokenSource();

        try
        {
            LogText =
                "";

            StatusText =
                "Workflow выполняется…";

            var textProgress =
                new Progress<string>(
                    AppendLog);

            var stepProgress =
                new Progress<LocalWorkflowStep>(
                    updated =>
                    {
                        var index =
                            FindStepIndex(
                                updated.Id);

                        if (index >=
                            0)
                        {
                            Steps[index] =
                                updated;
                        }
                    });

            var result =
                await _service.ExecuteAsync(
                    _plan,
                    textProgress,
                    stepProgress,
                    _runCts.Token);

            if (result.WasCancelled)
            {
                StatusText =
                    $"Workflow остановлен. Выполнено шагов: {result.CompletedSteps}. " +
                    $"Результаты: {result.RunDirectory}";
            }
            else
            {
                StatusText =
                    $"Workflow завершён. Выполнено шагов: {result.CompletedSteps}. " +
                    $"Результаты: {result.RunDirectory}";
            }
        }
        catch (Exception ex)
        {
            HandleError(
                ex);
        }
        finally
        {
            SetBusy(
                false);
        }
    }

    private void Stop_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            _runCts?.Cancel();

            StatusText =
                "Запрошена остановка workflow…";
        }
        catch (Exception ex)
        {
            HandleError(
                ex);
        }
    }

    private void OpenOutput_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(
                    WorkspacePath) ||
                !Directory.Exists(
                    WorkspacePath))
            {
                throw new DirectoryNotFoundException(
                    "Сначала выберите рабочую папку.");
            }

            var output =
                _service.GetOutputRoot(
                    WorkspacePath);

            Directory.CreateDirectory(
                output);

            Process.Start(
                new ProcessStartInfo
                {
                    FileName =
                        "explorer.exe",

                    Arguments =
                        "\"" +
                        output +
                        "\"",

                    UseShellExecute =
                        true
                });
        }
        catch (Exception ex)
        {
            HandleError(
                ex);
        }
    }

    private void Example_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        Instruction =
            "выполни полный анализ файлов, найди дубликаты, " +
            "собери текстовые файлы, разложи копии по расширениям " +
            "и создай zip архив";

        StatusText =
            "Пример задачи вставлен. Выберите папку и составьте план.";
    }

    private int FindStepIndex(
        string id)
    {
        for (var i = 0;
             i < Steps.Count;
             i++)
        {
            if (string.Equals(
                    Steps[i].Id,
                    id,
                    StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void AppendLog(
        string line)
    {
        if (string.IsNullOrWhiteSpace(
                line))
        {
            return;
        }

        LogText =
            string.IsNullOrWhiteSpace(
                LogText)
                ? line
                : LogText +
                  Environment.NewLine +
                  line;
    }

    private void HandleError(
        Exception exception)
    {
        ModuleErrorService.WriteException(
            "local-agent-ui-error.log",
            exception);

        AppendLog(
            "[ERROR] " +
            exception.Message);

        StatusText =
            "Ошибка Local Agent: " +
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
        try
        {
            _runCts?.Cancel();
        }
        catch
        {
        }

        _runCts?.Dispose();
        _runCts = null;
    }
}
