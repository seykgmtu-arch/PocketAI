using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PocketAI.App.Services;
using PocketAI.App.ViewModels;
using PocketAI.App.Views;

namespace PocketAI.App;

public partial class MainWindow :
    Window
{
    private readonly MainViewModel _viewModel;

    private readonly Dictionary<
        TabItem,
        ModuleHostController> _moduleHosts;

    public MainWindow(
        MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel =
            viewModel;

        DataContext =
            viewModel;

        // Heavy/optional modules are created in code and loaded lazily.
        // MainWindow.xaml intentionally stays unchanged.
        var (controlCenterTab, controlCenterHost) =
            CreateLazyModuleTab(
                "🛠 Control Center",
                "Control Center будет загружен при выборе вкладки.");

        var (localAgentTab, localAgentHost) =
            CreateLazyModuleTab(
                "🤖 Local Agent",
                "Local Agent / Workflows будет загружен при выборе вкладки.");

        var (textTab, textHost) =
            CreateLazyModuleTab(
                "📝 Text LoRA",
                "Text LoRA будет загружен при выборе вкладки.");

        var (audioTab, audioHost) =
            CreateLazyModuleTab(
                "🎵 Audio",
                "Audio будет загружен при выборе вкладки.");

        var (videoTab, videoHost) =
            CreateLazyModuleTab(
                "🎬 Video",
                "Video будет загружен при выборе вкладки.");

        // Keep Perchance as the last tab.
        var insertIndex =
            Math.Max(
                0,
                MainTabs.Items.Count - 1);

        MainTabs.Items.Insert(
            insertIndex++,
            controlCenterTab);

        MainTabs.Items.Insert(
            insertIndex++,
            localAgentTab);

        MainTabs.Items.Insert(
            insertIndex++,
            textTab);

        MainTabs.Items.Insert(
            insertIndex++,
            audioTab);

        MainTabs.Items.Insert(
            insertIndex,
            videoTab);

        _moduleHosts =
            new Dictionary<
                TabItem,
                ModuleHostController>
            {
                [TrainingTab] =
                    new ModuleHostController(
                        TrainingHost,
                        "Image Training",
                        "training-ui-error.log",
                        () =>
                            new ImageTrainingView(),
                        view =>
                            ((ImageTrainingView)view)
                            .DisposeTraining()),

                [controlCenterTab] =
                    new ModuleHostController(
                        controlCenterHost,
                        "Control Center",
                        "control-center-ui-error.log",
                        () =>
                            new ControlCenterView()),

                [localAgentTab] =
                    new ModuleHostController(
                        localAgentHost,
                        "Local Agent",
                        "local-agent-ui-error.log",
                        () =>
                            new LocalAgentView()),

                [textTab] =
                    new ModuleHostController(
                        textHost,
                        "Text LoRA",
                        "text-ui-error.log",
                        () =>
                            new TextLabView()),

                [audioTab] =
                    new ModuleHostController(
                        audioHost,
                        "Audio",
                        "audio-ui-error.log",
                        () =>
                            new AudioLabView()),

                [videoTab] =
                    new ModuleHostController(
                        videoHost,
                        "Video",
                        "video-ui-error.log",
                        () =>
                            new VideoLabView())
            };

        Loaded +=
            OnLoaded;

        Closing +=
            OnClosing;

        Dispatcher.UnhandledException +=
            OnDispatcherUnhandledException;

        _viewModel
            .Messages
            .CollectionChanged +=
            OnMessagesChanged;
    }

    private static (
        TabItem Tab,
        ContentControl Host)
        CreateLazyModuleTab(
            string header,
            string placeholder)
    {
        var host =
            new ContentControl
            {
                Content =
                    new TextBlock
                    {
                        Text =
                            placeholder,
                        Margin =
                            new Thickness(12),
                        TextWrapping =
                            TextWrapping.Wrap
                    }
            };

        var tab =
            new TabItem
            {
                Header =
                    header,
                Content =
                    host
            };

        return (
            tab,
            host);
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
        Dispatcher.UnhandledException -=
            OnDispatcherUnhandledException;

        _viewModel
            .Messages
            .CollectionChanged -=
            OnMessagesChanged;

        foreach (var controller in
                 _moduleHosts.Values)
        {
            try
            {
                controller.Dispose();
            }
            catch
            {
            }
        }

        _viewModel.Dispose();
    }

    private void MainTabs_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        // SelectionChanged also bubbles from controls inside tabs.
        if (!ReferenceEquals(
                e.OriginalSource,
                MainTabs))
        {
            return;
        }

        if (MainTabs.SelectedItem is not
            TabItem selectedTab)
        {
            return;
        }

        if (_moduleHosts.TryGetValue(
                selectedTab,
                out var controller))
        {
            controller.EnsureLoaded();
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainTabs.SelectedItem is not
            TabItem selectedTab)
        {
            return;
        }

        if (!_moduleHosts.TryGetValue(
                selectedTab,
                out var controller))
        {
            return;
        }

        try
        {
            controller.Fail(
                e.Exception);

            e.Handled =
                true;
        }
        catch (Exception fallbackError)
        {
            ModuleErrorService.WriteException(
                "module-guard-fallback-error.log",
                fallbackError);
        }
    }

    private void OnMessagesChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                if (ChatList.Items.Count >
                    0)
                {
                    ChatList.ScrollIntoView(
                        ChatList.Items[
                            ChatList.Items.Count -
                            1]);
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

        if (_viewModel
            .SendCommand
            .CanExecute(
                null))
        {
            e.Handled =
                true;

            _viewModel
                .SendCommand
                .Execute(
                    null);
        }
    }
}
