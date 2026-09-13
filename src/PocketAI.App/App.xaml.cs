using System.Windows;
using PocketAI.App.ViewModels;
using PocketAI.Core.Configuration;
using PocketAI.Hardware;
using PocketAI.Inference;

namespace PocketAI.App;

public partial class App : Application
{
    private LlamaServerManager? _serverManager;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            var config = ConfigLoader.Load(baseDirectory);

            if (e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                var outputPath = GetArgValue(e.Args, "--self-test-output")
                    ?? Path.Combine(baseDirectory, "smoke-test-pocketai.json");
                var forceCpu = e.Args.Any(a => a.Equals("--force-cpu", StringComparison.OrdinalIgnoreCase));

                var exitCode = await SelfTestRunner.RunAsync(
                    baseDirectory,
                    config,
                    outputPath,
                    forceCpu);
                Shutdown(exitCode);
                return;
            }

            var hardwareProbe = new HardwareProbe();
            _serverManager = new LlamaServerManager(baseDirectory, config);

            var viewModel = new MainViewModel(config, hardwareProbe, _serverManager);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Pocket AI — ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Closing the Windows Job Object also terminates llama-server if it is still alive.
        _serverManager?.Dispose();
        base.OnExit(e);
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
