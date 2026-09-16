using System;
using System.Windows.Controls;

namespace PocketAI.App.Views;

public partial class AudioLabView :
    UserControl,
    IDisposable
{
    public AudioLabView()
    {
        InitializeComponent();
    }

    public void Dispose()
    {
        // Future branch-specific runners are disposed here.
    }
}
