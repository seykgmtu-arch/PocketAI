using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PocketAI.App.ViewModels;

namespace PocketAI.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;

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
        _viewModel.Messages.CollectionChanged -= OnMessagesChanged;

        _viewModel.Dispose();
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
