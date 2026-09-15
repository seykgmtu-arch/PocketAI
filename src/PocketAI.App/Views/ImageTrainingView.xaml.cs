using System;
using System.Windows;
using System.Windows.Controls;
using PocketAI.App.ViewModels;

namespace PocketAI.App.Views;

public partial class ImageTrainingView :
    UserControl
{
    private ImageTrainingViewModel? _viewModel;
    private bool _windowHooked;
    private bool _disposed;

    public ImageTrainingView()
    {
        InitializeComponent();

        try
        {
            _viewModel =
                new ImageTrainingViewModel();

            DataContext =
                _viewModel;

            Loaded +=
                OnLoaded;
        }
        catch
        {
            // Re-throw synchronously so MainWindow crash guard can show the real error.
            throw;
        }
    }

    private void OnLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_disposed ||
            _viewModel is null)
        {
            return;
        }

        try
        {
            if (!_windowHooked &&
                Window.GetWindow(this)
                    is Window window)
            {
                window.Closed +=
                    OnWindowClosed;

                _windowHooked = true;
            }

            _viewModel.Initialize();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "ImageTrainingView initialization failed.",
                ex);
        }
    }

    private void OnWindowClosed(
        object? sender,
        EventArgs e)
    {
        DisposeTraining();
    }

    public void DisposeTraining()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loaded -= OnLoaded;

        if (Window.GetWindow(this)
                is Window window &&
            _windowHooked)
        {
            window.Closed -=
                OnWindowClosed;
        }

        _windowHooked = false;

        _viewModel?.Dispose();
        _viewModel = null;

        DataContext = null;
    }
}
