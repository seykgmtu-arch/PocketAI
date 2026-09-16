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

                [TextTab] =
                    new ModuleHostController(
                        TextHost,
                        "Text LoRA",
                        "text-ui-error.log",
                        () =>
                            new TextLabView()),

                [AudioTab] =
                    new ModuleHostController(
                        AudioHost,
                        "Audio",
                        "audio-ui-error.log",
                        () =>
                            new AudioLabView()),

                [VideoTab] =
                    new ModuleHostController(
                        VideoHost,
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
