using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PocketAI.App.Infrastructure;
using PocketAI.App.Services;

namespace PocketAI.App.ViewModels;

public sealed class ImageTrainingViewModel :
    ObservableObject,
    IDisposable
{
    private readonly string _baseDirectory =
        AppContext.BaseDirectory;

    private readonly ImageTrainingDatasetService _datasets;
    private readonly ImageTrainingRunner _runner;

    private CancellationTokenSource? _cts;
    private ImageTrainingProject? _project;

    private string _projectName = "my-lora";
    private string _triggerWord = "pocketstyle";
    private string _baseModelPath = string.Empty;
    private string _defaultCaption =
        "pocketstyle, high quality, detailed";
    private string _negativePrompt =
        "low quality, blurry, distorted, watermark";
    private string _widthText = "1024";
    private string _heightText = "1024";

    private string _promptPlan =
"""pocketstyle, clean technical illustration of a marine diesel engine, white background
pocketstyle, engineering cutaway illustration of a ship propulsion system
pocketstyle, precise technical drawing of a marine propulsion shaft line
pocketstyle, educational engineering illustration, labeled components, clean composition""";

    private ImageTrainingPreset? _selectedPreset;
    private int _promptIndex;
    private string _statusText =
        "Создайте проект обучения.";
    private string _logText = string.Empty;
    private bool _isBusy;

    public ImageTrainingViewModel()
    {
        _datasets =
            new ImageTrainingDatasetService(
                _baseDirectory);

        _runner =
            new ImageTrainingRunner(
                _baseDirectory);

        Presets = new[]
        {
            new ImageTrainingPreset(
                "SDXL · RTX 3080 · balanced",
                ImageTrainingModelFamily.Sdxl,
                1024,
                16,
                16,
                10,
                10,
                1e-4,
                "AdamW8bit",
                "fp16"),

            new ImageTrainingPreset(
                "SDXL · RTX 3080 · low VRAM",
                ImageTrainingModelFamily.Sdxl,
                768,
                8,
                8,
                8,
                8,
                8e-5,
                "AdamW8bit",
                "fp16"),

            new ImageTrainingPreset(
                "SD 1.5 · RTX 3080",
                ImageTrainingModelFamily.Sd15,
                512,
                16,
                16,
                10,
                10,
                1e-4,
                "AdamW8bit",
                "fp16")
        };

        _selectedPreset =
            Presets[0];

        Items =
            new ObservableCollection<ImageTrainingItem>();

        Prompts =
            new ObservableCollection<ImageTrainingPrompt>();

        CreateProjectCommand =
            new RelayCommand(
                CreateProject);

        BrowseBaseModelCommand =
            new RelayCommand(
                BrowseBaseModel);

        BuildPromptPlanCommand =
            new RelayCommand(
                BuildPromptPlan);

        ExportPromptsCommand =
            new RelayCommand(
                ExportPrompts,
                () => _project is not null);

        ImportPromptsCommand =
            new RelayCommand(
                ImportPrompts,
                () => _project is not null);

        CopyNextPromptCommand =
            new RelayCommand(
                CopyNextPrompt,
                () => Prompts.Count > 0);

        ImportImagesCommand =
            new RelayCommand(
                ImportImages,
                () => _project is not null);

        OpenProjectFolderCommand =
            new RelayCommand(
                OpenProjectFolder,
                () => _project is not null);

        StartTrainingCommand =
            new AsyncRelayCommand(
                StartTrainingAsync,
                () =>
                    _project is not null &&
                    !IsBusy);

        StopTrainingCommand =
            new RelayCommand(
                StopTraining,
                () => IsBusy);
    }

    public IReadOnlyList<ImageTrainingPreset> Presets { get; }

    public ObservableCollection<ImageTrainingItem> Items { get; }

    public ObservableCollection<ImageTrainingPrompt> Prompts { get; }

    public RelayCommand CreateProjectCommand { get; }
    public RelayCommand BrowseBaseModelCommand { get; }
    public RelayCommand BuildPromptPlanCommand { get; }
    public RelayCommand ExportPromptsCommand { get; }
    public RelayCommand ImportPromptsCommand { get; }
    public RelayCommand CopyNextPromptCommand { get; }
    public RelayCommand ImportImagesCommand { get; }
    public RelayCommand OpenProjectFolderCommand { get; }
    public AsyncRelayCommand StartTrainingCommand { get; }
    public RelayCommand StopTrainingCommand { get; }

    public string ProjectName
    {
        get => _projectName;
        set => SetProperty(
            ref _projectName,
            value);
    }

    public string TriggerWord
    {
        get => _triggerWord;
        set => SetProperty(
            ref _triggerWord,
            value);
    }

    public string BaseModelPath
    {
        get => _baseModelPath;
        set => SetProperty(
            ref _baseModelPath,
            value);
    }

    public string DefaultCaption
    {
        get => _defaultCaption;
        set => SetProperty(
            ref _defaultCaption,
            value);
    }

    public string NegativePrompt
    {
        get => _negativePrompt;
        set => SetProperty(
            ref _negativePrompt,
            value);
    }

    public string WidthText
    {
        get => _widthText;
        set => SetProperty(
            ref _widthText,
            value);
    }

    public string HeightText
    {
        get => _heightText;
        set => SetProperty(
            ref _heightText,
            value);
    }

    public string PromptPlan
    {
        get => _promptPlan;
        set => SetProperty(
            ref _promptPlan,
            value);
    }

    public ImageTrainingPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(
                    ref _selectedPreset,
                    value) ||
                value is null)
            {
                return;
            }

            WidthText =
                value.Resolution.ToString(
                    CultureInfo.InvariantCulture);

            HeightText =
                value.Resolution.ToString(
                    CultureInfo.InvariantCulture);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(
            ref _statusText,
            value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(
            ref _logText,
            value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(
                    ref _isBusy,
                    value))
            {
                return;
            }

            StartTrainingCommand
                .RaiseCanExecuteChanged();

            StopTrainingCommand
                .RaiseCanExecuteChanged();
        }
    }

    public string RuntimeStatus =>
        _runner.IsInstalled
            ? "sd-scripts runtime: установлен"
            : "sd-scripts runtime: не установлен";

    public void Initialize()
    {
        OnPropertyChanged(
            nameof(RuntimeStatus));
    }

    private void CreateProject()
    {
        try
        {
            var preset =
                SelectedPreset ??
                Presets[0];

            _project =
                _datasets.CreateProject(
                    ProjectName,
                    TriggerWord,
                    preset.Family,
                    BaseModelPath);

            Items.Clear();
            Prompts.Clear();
            _promptIndex = 0;

            StatusText =
                "Проект создан: " +
                _project.ProjectDirectory;

            RaiseCommands();
        }
        catch (Exception ex)
        {
            StatusText =
                Friendly(ex);
        }
    }

    private void BrowseBaseModel()
    {
        var dialog =
            new OpenFileDialog
            {
                Title =
                    "Выберите базовую модель SD/SDXL",
                Filter =
                    "Training checkpoints|*.safetensors;*.ckpt|All files|*.*"
            };

        if (dialog.ShowDialog() == true)
        {
            BaseModelPath =
                dialog.FileName;

            if (_project is not null)
            {
                _project.BaseModelPath =
                    BaseModelPath;

                _datasets.SaveProject(
                    _project);
            }
        }
    }

    private void BuildPromptPlan()
    {
        var width =
            ParseInt(
                WidthText,
                SelectedPreset?.Resolution ??
                1024);

        var height =
            ParseInt(
                HeightText,
                SelectedPreset?.Resolution ??
                1024);

        var prompts =
            _datasets.BuildPromptPlan(
                PromptPlan,
                NegativePrompt,
                width,
                height);

        Prompts.Clear();

        foreach (var prompt in prompts)
        {
            Prompts.Add(prompt);
        }

        _promptIndex = 0;

        StatusText =
            $"Подготовлено промтов: {Prompts.Count}.";

        CopyNextPromptCommand
            .RaiseCanExecuteChanged();
    }

    private void ExportPrompts()
    {
        if (_project is null)
            return;

        if (Prompts.Count == 0)
        {
            BuildPromptPlan();
        }

        var path =
            _datasets.ExportPromptsCsv(
                _project,
                Prompts.ToArray());

        StatusText =
            "Экспортировано: " +
            path;
    }

    private void ImportPrompts()
    {
        if (_project is null)
            return;

        var dialog =
            new OpenFileDialog
            {
                Title =
                    "Импорт prompts.csv",
                Filter =
                    "CSV|*.csv|All files|*.*"
            };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var prompts =
                _datasets.ImportPromptsCsv(
                    dialog.FileName);

            Prompts.Clear();

            foreach (var prompt in prompts)
            {
                Prompts.Add(prompt);
            }

            _project.Prompts =
                prompts.ToList();

            _datasets.SaveProject(
                _project);

            _promptIndex = 0;

            StatusText =
                $"Импортировано промтов: {Prompts.Count}.";

            CopyNextPromptCommand
                .RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusText =
                Friendly(ex);
        }
    }

    private void CopyNextPrompt()
    {
        if (Prompts.Count == 0)
            return;

        if (_promptIndex >=
            Prompts.Count)
        {
            _promptIndex = 0;
        }

        var prompt =
            Prompts[_promptIndex];

        Clipboard.SetText(
            prompt.Prompt);

        _promptIndex++;

        StatusText =
            $"Промт {_promptIndex}/{Prompts.Count} скопирован. " +
            $"Ожидаем файл {prompt.FileName}.";
    }

    private void ImportImages()
    {
        if (_project is null)
            return;

        var dialog =
            new OpenFileDialog
            {
                Title =
                    "Импорт обучающих изображений",
                Filter =
                    "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files|*.*",
                Multiselect = true
            };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _project.Prompts =
                Prompts.ToList();

            var imported =
                _datasets.ImportImages(
                    _project,
                    dialog.FileNames,
                    EnsureTriggerWord(
                        DefaultCaption));

            foreach (var item in imported)
            {
                Items.Add(item);
            }

            StatusText =
                $"Импортировано изображений: {imported.Count}.";
        }
        catch (Exception ex)
        {
            StatusText =
                Friendly(ex);
        }
    }

    private async Task StartTrainingAsync()
    {
        if (_project is null)
            return;

        var preset =
            SelectedPreset ??
            Presets[0];

        if (Items.Count == 0)
        {
            StatusText =
                "Сначала импортируйте обучающие изображения.";
            return;
        }

        if (string.IsNullOrWhiteSpace(
                BaseModelPath))
        {
            StatusText =
                "Выберите базовую модель.";
            return;
        }

        _project.BaseModelPath =
            BaseModelPath;

        _project.Family =
            preset.Family;

        _datasets.SaveProject(
            _project);

        var datasetConfig =
            _datasets.BuildDatasetToml(
                _project,
                preset);

        _cts?.Dispose();
        _cts =
            new CancellationTokenSource();

        IsBusy = true;
        LogText = string.Empty;

        try
        {
            StatusText =
                "Запуск LoRA training…";

            var progress =
                new Progress<string>(
                    line =>
                    {
                        LogText =
                            AppendLog(
                                LogText,
                                line);

                        StatusText =
                            line;
                    });

            var result =
                await _runner.RunAsync(
                    new ImageTrainingRunOptions(
                        _project.ProjectDirectory,
                        _project.Name,
                        BaseModelPath,
                        preset,
                        datasetConfig),
                    progress,
                    _cts.Token);

            StatusText =
                "Обучение завершено. LoRA: " +
                result;
        }
        catch (OperationCanceledException)
        {
            StatusText =
                "Обучение остановлено.";
        }
        catch (Exception ex)
        {
            StatusText =
                Friendly(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void StopTraining()
    {
        _cts?.Cancel();
        _runner.Stop();
    }

    private void OpenProjectFolder()
    {
        if (_project is null)
            return;

        Process.Start(
            new ProcessStartInfo(
                "explorer.exe",
                _project.ProjectDirectory)
            {
                UseShellExecute = true
            });
    }

    private string EnsureTriggerWord(
        string caption)
    {
        caption =
            caption?.Trim() ??
            string.Empty;

        if (string.IsNullOrWhiteSpace(
                TriggerWord))
        {
            return caption;
        }

        if (caption.Contains(
                TriggerWord,
                StringComparison.OrdinalIgnoreCase))
        {
            return caption;
        }

        return string.IsNullOrWhiteSpace(
                caption)
            ? TriggerWord
            : TriggerWord + ", " + caption;
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

    private static string AppendLog(
        string current,
        string line)
    {
        var updated =
            string.IsNullOrWhiteSpace(
                current)
                ? line
                : current +
                  Environment.NewLine +
                  line;

        const int maxLength = 24000;

        return updated.Length <= maxLength
            ? updated
            : updated[^maxLength..];
    }

    private void RaiseCommands()
    {
        ExportPromptsCommand
            .RaiseCanExecuteChanged();

        ImportPromptsCommand
            .RaiseCanExecuteChanged();

        ImportImagesCommand
            .RaiseCanExecuteChanged();

        OpenProjectFolderCommand
            .RaiseCanExecuteChanged();

        StartTrainingCommand
            .RaiseCanExecuteChanged();
    }

    private static string Friendly(
        Exception exception)
    {
        var root =
            exception.GetBaseException();

        return string.IsNullOrWhiteSpace(
                root.Message)
            ? root.GetType().Name
            : root.Message;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _runner.Dispose();
    }
}
