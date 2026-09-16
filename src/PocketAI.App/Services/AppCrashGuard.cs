using System;
using System.Threading.Tasks;

namespace PocketAI.App.Services;

public static class AppCrashGuard
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed =
            true;

        TaskScheduler.UnobservedTaskException +=
            OnUnobservedTaskException;

        AppDomain.CurrentDomain.UnhandledException +=
            OnUnhandledException;
    }

    public static void Uninstall()
    {
        if (!_installed)
        {
            return;
        }

        TaskScheduler.UnobservedTaskException -=
            OnUnobservedTaskException;

        AppDomain.CurrentDomain.UnhandledException -=
            OnUnhandledException;

        _installed =
            false;
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        ModuleErrorService.WriteException(
            "background-task-error.log",
            e.Exception);

        // Prevent an unobserved Task exception from escalating later.
        e.SetObserved();
    }

    private static void OnUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ModuleErrorService.WriteException(
                "fatal-unhandled-error.log",
                ex);
        }
        else
        {
            ModuleErrorService.WriteText(
                "fatal-unhandled-error.log",
                e.ExceptionObject?.ToString() ??
                "Unknown fatal exception.");
        }
    }
}
