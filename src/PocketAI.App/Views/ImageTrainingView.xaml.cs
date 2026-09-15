using System;
using System.Windows;
using System.Windows.Controls;
using PocketAI.App.ViewModels;

namespace PocketAI.App.Views;

public partial class ImageTrainingView :
    UserControl
{
    private readonly ImageTrainingViewModel _viewModel;
    private bool _windowHooked;

    public ImageTrainingView()
    {
        InitializeComponent();

        _viewModel =
            new ImageTrainingViewModel();

        DataContext =
            _viewModel;

        Loaded +=
            OnLoaded;
    }

    private void OnLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (!_windowHooked &&
            Window.GetWindow(this)
                is Window window)
        {
            window.Closed +=
                (_, _) =>
                    _viewModel.Dispose();

            _windowHooked = true;
        }

        _viewModel.Initialize();
    }
}
