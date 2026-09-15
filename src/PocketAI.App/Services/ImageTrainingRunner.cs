using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PocketAI.App.Services;

public sealed class ImageTrainingRunner :
    IDisposable
{
    private readonly string _baseDirectory;
    private Process? _process;

    public ImageTrainingRunner(
        string baseDirectory)
    {
        _baseDirectory =
            Path.GetFullPath(
                baseDirectory);
    }

    public bool IsRunning =>
        _process is { HasExited: false };

    public string TrainingRoot =>
        Path.Combine(
            _baseDirectory,
            "runtime",
            "image",
            "training",
            "sd-scripts");

    public string PythonPath =>
        Path.Combine(
            TrainingRoot,
            "venv",
            "Scripts",
            "python.exe");

    public bool IsInstalled =>
        File.Exists(PythonPath) &&
        File.Exists(
            Path.Combine(
                TrainingRoot,
                "train_network.py")) &&
        File.Exists(
            Path.Combine(
                TrainingRoot,
                "sdxl_train_network.py"));

    public async Task<string> RunAsync(
        ImageTrainingRunOptions options,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException(
                "Training process уже запущен.");
        }

        if (!IsInstalled)
        {
            throw new FileNotFoundException(
                "Runtime обучения не установлен. " +
                "Запустите tools\\setup-image-training.ps1.");
        }

        var baseModelExtension =
            Path.GetExtension(
                options.BaseModelPath);

        if (!baseModelExtension.Equals(
                ".safetensors",
                StringComparison.OrdinalIgnoreCase) &&
            !baseModelExtension.Equals(
                ".ckpt",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Для LoRA training через sd-scripts используйте исходный checkpoint " +
                ".safetensors или .ckpt. Квантованный .gguf предназначен для inference, " +
                "а не для этого режима обучения.");
        }

        if (!File.Exists(
                options.BaseModelPath))
        {
            throw new FileNotFoundException(
                "Базовая training-модель не найдена.",
                options.BaseModelPath);
        }

        if (!File.Exists(
                options.DatasetConfigPath))
        {
            throw new FileNotFoundException(
                "dataset_config.toml не найден.",
                options.DatasetConfigPath);
        }

        var outputDirectory =
            Path.Combine(
                options.ProjectDirectory,
                "output");

        Directory.CreateDirectory(
            outputDirectory);

        var trainingScript =
            options.Preset.Family ==
            ImageTrainingModelFamily.Sdxl
                ? "sdxl_train_network.py"
                : "train_network.py";

        var psi =
            new ProcessStartInfo
            {
                FileName = PythonPath,
                WorkingDirectory =
                    TrainingRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

        foreach (var arg in new[]
        {
            "-m",
            "accelerate.commands.launch",
            "--num_processes", "1",
            "--num_machines", "1",
            "--mixed_precision",
            options.Preset.MixedPrecision,
            "--num_cpu_threads_per_process", "1",
            trainingScript,

            "--pretrained_model_name_or_path",
            options.BaseModelPath,

            "--dataset_config",
            options.DatasetConfigPath,

            "--output_dir",
            outputDirectory,

            "--output_name",
            options.ProjectName,

            "--network_module",
            "networks.lora",

            "--network_dim",
            options.Preset.Rank.ToString(
                CultureInfo.InvariantCulture),

            "--network_alpha",
            options.Preset.Alpha.ToString(
                CultureInfo.InvariantCulture),

            "--learning_rate",
            options.Preset.LearningRate.ToString(
                CultureInfo.InvariantCulture),

            "--optimizer_type",
            options.Preset.Optimizer,

            "--mixed_precision",
            options.Preset.MixedPrecision,

            "--save_model_as",
            "safetensors",

            "--max_train_epochs",
            options.Preset.Epochs.ToString(
                CultureInfo.InvariantCulture),

            "--save_every_n_epochs",
            "1",

            "--seed",
            "42",

            "--max_data_loader_n_workers",
            "2",

            "--gradient_checkpointing",
            "--cache_latents",
            "--network_train_unet_only"
        })
        {
            psi.ArgumentList.Add(arg);
        }

        if (options.Preset.Family ==
            ImageTrainingModelFamily.Sdxl)
        {
            psi.ArgumentList.Add(
                "--cache_text_encoder_outputs");
        }

        var logPath =
            Path.Combine(
                options.ProjectDirectory,
                "logs",
                $"training-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        await using var log =
            new StreamWriter(
                logPath,
                append: false);

        var process =
            new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

        process.OutputDataReceived +=
            (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(
                        e.Data))
                {
                    return;
                }

                progress?.Report(
                    e.Data);

                lock (log)
                {
                    log.WriteLine(
                        e.Data);
                    log.Flush();
                }
            };

        process.ErrorDataReceived +=
            (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(
                        e.Data))
                {
                    return;
                }

                progress?.Report(
                    e.Data);

                lock (log)
                {
                    log.WriteLine(
                        "[ERR] " +
                        e.Data);
                    log.Flush();
                }
            };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Не удалось запустить training process.");
        }

        _process = process;

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration =
            ct.Register(
                Stop);

        await process.WaitForExitAsync(
            CancellationToken.None);

        var exitCode =
            process.ExitCode;

        _process = null;
        process.Dispose();

        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                ct);
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Обучение завершилось с кодом {exitCode}. " +
                $"Смотрите {logPath}");
        }

        var lora =
            Directory
                .EnumerateFiles(
                    outputDirectory,
                    "*.safetensors",
                    SearchOption.TopDirectoryOnly)
                .OrderByDescending(
                    File.GetLastWriteTimeUtc)
                .FirstOrDefault();

        if (lora is null)
        {
            throw new FileNotFoundException(
                "Обучение завершено, но .safetensors не найден.");
        }

        var loraDirectory =
            Path.Combine(
                _baseDirectory,
                "models",
                "images",
                "loras");

        Directory.CreateDirectory(
            loraDirectory);

        var destination =
            Path.Combine(
                loraDirectory,
                Path.GetFileName(
                    lora));

        File.Copy(
            lora,
            destination,
            overwrite: true);

        progress?.Report(
            "LoRA установлена: " +
            destination);

        return destination;
    }

    public void Stop()
    {
        var process =
            _process;

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
        _process = null;
    }
}
