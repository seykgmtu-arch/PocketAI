using System;
using System.Windows;
using System.Windows.Controls;

namespace PocketAI.App.Services;

public sealed class ModuleHostController :
    IDisposable
{
    private readonly ContentControl _host;
    private readonly string _moduleTitle;
    private readonly string _logFileName;
    private readonly Func<FrameworkElement> _factory;
    private readonly Action<FrameworkElement>? _dispose;

    private FrameworkElement? _view;
    private bool _loadFailed;

    public ModuleHostController(
        ContentControl host,
        string moduleTitle,
        string logFileName,
        Func<FrameworkElement> factory,
        Action<FrameworkElement>? dispose = null)
    {
        _host =
            host;

        _moduleTitle =
            moduleTitle;

        _logFileName =
            logFileName;

        _factory =
            factory;

        _dispose =
            dispose;
    }

    public bool IsLoaded =>
        _view is not null;

    public bool LoadFailed =>
        _loadFailed;

    public void EnsureLoaded()
    {
        if (_view is not null ||
            _loadFailed)
        {
            return;
        }

        try
        {
            var view =
                _factory();

            _view =
                view;

            _host.Content =
                view;
        }
        catch (Exception ex)
        {
            Fail(
                ex);
        }
    }

    public void Fail(
        Exception exception)
    {
        _loadFailed =
            true;

        DisposeView();

        ModuleErrorService.WriteException(
            _logFileName,
            exception);

        _host.Content =
            ModuleErrorService.BuildErrorPanel(
                _moduleTitle,
                _logFileName,
                exception);
    }

    public void Reset()
    {
        DisposeView();

        _loadFailed =
            false;

        _host.Content =
            new TextBlock
            {
                Text =
                    _moduleTitle +
                    " будет загружен при выборе вкладки.",
                Margin =
                    new Thickness(12),
                TextWrapping =
                    TextWrapping.Wrap
            };
    }

    private void DisposeView()
    {
        if (_view is null)
        {
            return;
        }

        try
        {
            if (_dispose is not null)
            {
                _dispose(
                    _view);
            }
            else if (_view is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            ModuleErrorService.WriteException(
                _logFileName,
                ex);
        }

        _view =
            null;
    }

    public void Dispose()
    {
        DisposeView();
    }
}
