using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PocketAI.App.ViewModels;
using PocketAI.App.Views;

namespace PocketAI.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private ImageTrainingView? _trainingView;
    private bool _trainingLoadFailed;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;

        Dispatcher.UnhandledException += OnDispatcherUnhandledException;
        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
    }

    private async void OnLoaded(
        object sender,
        RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();

        PromptBox.Focus();
    }

    private void OnClosing(
        object? sender,
        CancelEventArgs e)
    {
        Dispatcher.UnhandledException -= OnDispatcherUnhandledException;
        _viewModel.Messages.CollectionChanged -= OnMessagesChanged;

        try
        {
            _trainingView?.DisposeTraining();
        }
        catch
        {
        }

        _viewModel.Dispose();
    }

    private void TrainingTab_OnSelected(
        object sender,
        RoutedEventArgs e)
    {
        if (_trainingView is not null ||
            _trainingLoadFailed)
        {
            return;
        }

        try
        {
            var view =
                new ImageTrainingView();

            _trainingView =
                view;

            TrainingHost.Content =
                view;
        }
        catch (Exception ex)
        {
            ShowTrainingError(
                ex);
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        if (!TrainingTab.IsSelected)
        {
            return;
        }

        try
        {
            ShowTrainingError(
                e.Exception);

            e.Handled = true;
        }
        catch
        {
            // If even the fallback UI cannot be built, let WPF handle the original error.
        }
    }

    private void ShowTrainingError(
        Exception exception)
    {
        _trainingLoadFailed = true;

        try
        {
            _trainingView?.DisposeTraining();
        }
        catch
        {
        }

        _trainingView = null;

        var root =
            exception.GetBaseException();

        var message =
            root.GetType().FullName +
            Environment.NewLine +
            root.Message;

        TryWriteTrainingErrorLog(
            exception);

        TrainingHost.Content =
            new Border
            {
                Background =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            58,
                            29,
                            29)),
                BorderBrush =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            127,
                            29,
                            29)),
                BorderThickness =
                    new Thickness(1),
                CornerRadius =
                    new CornerRadius(10),
                Padding =
                    new Thickness(14),
                Child =
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text =
                                    "Training UI не удалось открыть. PocketAI продолжает работать.",
                                FontWeight =
                                    FontWeights.SemiBold,
                                Foreground =
                                    new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(
                                            254,
                                            202,
                                            202)),
                                TextWrapping =
                                    TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text =
                                    message,
                                Margin =
                                    new Thickness(
                                        0,
                                        10,
                                        0,
                                        0),
                                Foreground =
                                    new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(
                                            254,
                                            226,
                                            226)),
                                TextWrapping =
                                    TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text =
                                    "Подробности: logs\\training-ui-error.log",
                                Margin =
                                    new Thickness(
                                        0,
                                        10,
                                        0,
                                        0),
                                Foreground =
                                    new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(
                                            148,
                                            163,
                                            184))
                            }
                        }
                    }
            };
    }

    private static void TryWriteTrainingErrorLog(
        Exception exception)
    {
        try
        {
            var directory =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "logs");

            Directory.CreateDirectory(
                directory);

            var path =
                Path.Combine(
                    directory,
                    "training-ui-error.log");

            File.AppendAllText(
                path,
                $"[{DateTime.Now:O}]{Environment.NewLine}" +
                exception +
                Environment.NewLine +
                new string('-', 72) +
                Environment.NewLine);
        }
        catch
        {
        }
    }

    private void OnMessagesChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ChatList.Items.Count > 0)
            {
                ChatList.ScrollIntoView(
                    ChatList.Items[
                        ChatList.Items.Count - 1]);
            }
        });
    }

    private void PromptBox_OnPreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            Keyboard.Modifiers.HasFlag(
                ModifierKeys.Shift))
        {
            return;
        }

        if (_viewModel.SendCommand.CanExecute(null))
        {
            e.Handled = true;

            _viewModel.SendCommand.Execute(null);
        }
    }
}
