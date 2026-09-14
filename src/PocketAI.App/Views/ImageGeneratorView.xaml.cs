using System.Windows;
using System.Windows.Controls;
using PocketAI.App.ViewModels;

namespace PocketAI.App.Views;

public partial class ImageGeneratorView : UserControl
{
    private readonly ImageGeneratorViewModel _viewModel;
    private bool _windowHooked;

    public ImageGeneratorView()
    {
        InitializeComponent();

        _viewModel = new ImageGeneratorViewModel();
        DataContext = _viewModel;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (!_windowHooked &&
            Window.GetWindow(this) is Window window)
        {
            window.Closed += (_, _) =>
                _viewModel.Dispose();

            _windowHooked = true;
        }

        await _viewModel.InitializeAsync();
    }
}
