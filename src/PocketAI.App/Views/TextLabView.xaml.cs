using System;
using System.Windows.Controls;

namespace PocketAI.App.Views;

public partial class TextLabView :
    UserControl,
    IDisposable
{
    public TextLabView()
    {
        InitializeComponent();
    }

    public void Dispose()
    {
        // Future branch-specific runners are disposed here.
    }
}
