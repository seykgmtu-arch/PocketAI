using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PocketAI.App.Services;

public sealed record GuardedProcessRequest(
    string ModuleKey,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record GuardedProcessResult(
    int ExitCode,
    bool WasCancelled);

public sealed class GuardedProcessRunner :
    IDisposable
{
    private readonly object _sync =
        new();

    private Process? _process;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return
                    _process is not null &&
                    !_process.HasExited;
            }
        }
    }

    public async Task<GuardedProcessResult> RunAsync(
        GuardedProcessRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(
                request.ModuleKey))
        {
            throw new ArgumentException(
                "ModuleKey is required.");
        }

        if (!File.Exists(
                request.FileName))
        {
            throw new FileNotFoundException(
                "Executable not found.",
                request.FileName);
        }

        if (!Directory.Exists(
                request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                request.WorkingDirectory);
        }

        var logName =
            request.ModuleKey +
            "-process.log";

        var psi =
            new ProcessStartInfo
            {
                FileName =
                    request.FileName,
                WorkingDirectory =
                    request.WorkingDirectory,
                UseShellExecute =
                    false,
                CreateNoWindow =
                    true,
                RedirectStandardOutput =
                    true,
                RedirectStandardError =
                    true
            };

        foreach (var argument in request.Arguments)
        {
            psi.ArgumentList.Add(
                argument);
        }

        var process =
            new Process
            {
                StartInfo =
                    psi,
                EnableRaisingEvents =
                    true
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

                ModuleErrorService.WriteText(
                    logName,
                    "[OUT] " +
                    e.Data);
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
                    "[ERR] " +
                    e.Data);

                ModuleErrorService.WriteText(
                    logName,
                    "[ERR] " +
                    e.Data);
            };

        lock (_sync)
        {
            if (_process is not null &&
                !_process.HasExited)
            {
                throw new InvalidOperationException(
                    "A child process is already running.");
            }

            _process =
                process;
        }

        try
        {
            ModuleErrorService.WriteText(
                logName,
                "START " +
                request.FileName);

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Failed to start child process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var registration =
                cancellationToken.Register(
                    () =>
                    {
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
                    });

            await process.WaitForExitAsync(
                CancellationToken.None);

            var cancelled =
                cancellationToken.IsCancellationRequested;

            ModuleErrorService.WriteText(
                logName,
                "EXIT " +
                process.ExitCode);

            return new GuardedProcessResult(
                process.ExitCode,
                cancelled);
        }
        catch (Exception ex)
        {
            ModuleErrorService.WriteException(
                logName,
                ex);

            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(
                        _process,
                        process))
                {
                    _process =
                        null;
                }
            }

            process.Dispose();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            try
            {
                if (_process is not null &&
                    !_process.HasExited)
                {
                    _process.Kill(
                        entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
