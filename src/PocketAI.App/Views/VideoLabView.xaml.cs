using System;
using System.Windows.Controls;

namespace PocketAI.App.Views;

public partial class VideoLabView :
    UserControl,
    IDisposable
{
    public VideoLabView()
    {
        InitializeComponent();
    }

    public void Dispose()
    {
        // Future branch-specific runners are disposed here.
    }
}
