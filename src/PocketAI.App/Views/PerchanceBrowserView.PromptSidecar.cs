using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace PocketAI.App.Views;

public partial class PerchanceBrowserView
{
    private bool _promptSidecarHookRequested;
    private CoreWebView2? _promptSidecarCore;
    private readonly List<CoreWebView2Frame> _promptFrames = new();

    private void PerchancePromptSidecar_OnLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_promptSidecarHookRequested)
            return;

        _promptSidecarHookRequested = true;

        Browser.CoreWebView2InitializationCompleted +=
            PromptSidecar_CoreWebView2InitializationCompleted;

        if (Browser.CoreWebView2 is not null)
        {
            AttachPromptSidecar(
                Browser.CoreWebView2);
        }
    }

    private void PromptSidecar_CoreWebView2InitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess ||
            Browser.CoreWebView2 is null)
        {
            return;
        }

        AttachPromptSidecar(
            Browser.CoreWebView2);
    }

    private void AttachPromptSidecar(
        CoreWebView2 core)
    {
        if (ReferenceEquals(
                _promptSidecarCore,
                core))
        {
            return;
        }

        if (_promptSidecarCore is not null)
        {
            _promptSidecarCore.DownloadStarting -=
                PromptSidecar_DownloadStarting;

            _promptSidecarCore.FrameCreated -=
                PromptSidecar_FrameCreated;
        }

        _promptSidecarCore =
            core;

        core.DownloadStarting +=
            PromptSidecar_DownloadStarting;

        core.FrameCreated +=
            PromptSidecar_FrameCreated;
    }

    private void PromptSidecar_FrameCreated(
        object? sender,
        CoreWebView2FrameCreatedEventArgs e)
    {
        var frame =
            e.Frame;

        _promptFrames.Add(
            frame);

        frame.Destroyed +=
            (_, _) =>
            {
                try
                {
                    _promptFrames.Remove(
                        frame);
                }
                catch
                {
                }
            };
    }

    private void PromptSidecarPaste_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                PromptSidecarBox.Text =
                    Clipboard.GetText().Trim();

                StatusText.Text =
                    "Prompt для TXT взят из буфера обмена.";
            }
            else
            {
                StatusText.Text =
                    "В буфере обмена нет текста.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "Не удалось прочитать буфер: " +
                ex.GetBaseException().Message;
        }
    }

    private void PromptSidecarClear_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        PromptSidecarBox.Clear();

        StatusText.Text =
            "Резервный prompt очищен.";
    }

    private void PromptSidecar_DownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        var operation =
            e.DownloadOperation;

        // Capture immediately, while the page still reflects the image being downloaded.
        var promptTask =
            CaptureCurrentPromptAsync();

        void OnStateChanged(
            object? stateSender,
            object stateArgs)
        {
            if (operation.State is
                CoreWebView2DownloadState.InProgress)
            {
                return;
            }

            operation.StateChanged -=
                OnStateChanged;

            if (operation.State !=
                CoreWebView2DownloadState.Completed)
            {
                return;
            }

            _ =
                SavePromptSidecarAfterDownloadAsync(
                    operation,
                    promptTask);
        }

        operation.StateChanged +=
            OnStateChanged;
    }

    private async Task SavePromptSidecarAfterDownloadAsync(
        CoreWebView2DownloadOperation operation,
        Task<PromptCaptureResult> promptTask)
    {
        try
        {
            var result =
                await promptTask;

            var prompt =
                result.Prompt.Trim();

            if (string.IsNullOrWhiteSpace(
                    prompt))
            {
                await Dispatcher.InvokeAsync(
                    () =>
                    {
                        StatusText.Text =
                            "Картинка сохранена, но prompt не найден. " +
                            "Перед Download вставьте его в поле «Prompt для TXT».";
                    });

                return;
            }

            var imagePath =
                operation.ResultFilePath;

            if (string.IsNullOrWhiteSpace(
                    imagePath))
            {
                return;
            }

            var txtPath =
                Path.ChangeExtension(
                    imagePath,
                    ".txt");

            await File.WriteAllTextAsync(
                txtPath,
                prompt +
                Environment.NewLine,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));

            await Dispatcher.InvokeAsync(
                () =>
                {
                    PromptSidecarBox.Text =
                        prompt;

                    StatusText.Text =
                        "Сохранено изображение + TXT prompt (" +
                        result.Source +
                        "): " +
                        Path.GetFileName(
                            imagePath);
                });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    StatusText.Text =
                        "Картинка сохранена; prompt.txt не создан: " +
                        ex.GetBaseException().Message;
                });
        }
    }

    private async Task<PromptCaptureResult> CaptureCurrentPromptAsync()
    {
        // 1. Automatic DOM probe: top document + WebView2 frames.
        var automatic =
            await ProbeAllDocumentsAsync();

        if (!string.IsNullOrWhiteSpace(
                automatic.Prompt))
        {
            return automatic;
        }

        // 2. Explicit fallback field in PocketAI.
        var manual =
            PromptSidecarBox.Text?.Trim() ??
            string.Empty;

        if (!string.IsNullOrWhiteSpace(
                manual))
        {
            return new PromptCaptureResult(
                manual,
                "поле PocketAI");
        }

        // 3. Clipboard fallback. This works especially well with
        // Training -> "Копировать следующий промт".
        try
        {
            if (Clipboard.ContainsText())
            {
                var clipboard =
                    Clipboard.GetText().Trim();

                if (clipboard.Length >= 3 &&
                    !Uri.TryCreate(
                        clipboard,
                        UriKind.Absolute,
                        out _))
                {
                    return new PromptCaptureResult(
                        clipboard,
                        "буфер обмена");
                }
            }
        }
        catch
        {
        }

        return PromptCaptureResult.Empty;
    }

    private async Task<PromptCaptureResult> ProbeAllDocumentsAsync()
    {
        var candidates =
            new List<PromptCandidate>();

        if (Browser.CoreWebView2 is not null)
        {
            var top =
                await TryProbeTopAsync();

            if (top is not null)
                candidates.Add(top);
        }

        var frames =
            _promptFrames.ToArray();

        foreach (var frame in frames)
        {
            var candidate =
                await TryProbeFrameAsync(
                    frame);

            if (candidate is not null)
                candidates.Add(candidate);
        }

        var best =
            candidates
                .Where(
                    item =>
                        !string.IsNullOrWhiteSpace(
                            item.Value))
                .OrderByDescending(
                    item => item.Score)
                .ThenByDescending(
                    item => item.Value.Length)
                .FirstOrDefault();

        return best is null
            ? PromptCaptureResult.Empty
            : new PromptCaptureResult(
                best.Value,
                best.Source);
    }

    private async Task<PromptCandidate?> TryProbeTopAsync()
    {
        try
        {
            if (Browser.CoreWebView2 is null)
                return null;

            var json =
                await Browser.CoreWebView2
                    .ExecuteScriptAsync(
                        PromptProbeScript);

            return ParseCandidate(
                json,
                "страница");
        }
        catch
        {
            return null;
        }
    }

    private async Task<PromptCandidate?> TryProbeFrameAsync(
        CoreWebView2Frame frame)
    {
        try
        {
            var json =
                await frame.ExecuteScriptAsync(
                    PromptProbeScript);

            return ParseCandidate(
                json,
                "frame");
        }
        catch
        {
            return null;
        }
    }

    private static PromptCandidate? ParseCandidate(
        string? json,
        string source)
    {
        if (string.IsNullOrWhiteSpace(
                json))
        {
            return null;
        }

        try
        {
            using var document =
                JsonDocument.Parse(
                    json);

            var root =
                document.RootElement;

            if (root.ValueKind !=
                JsonValueKind.Object)
            {
                return null;
            }

            var value =
                root.TryGetProperty(
                    "value",
                    out var valueElement)
                    ? valueElement.GetString()
                    : null;

            var score =
                root.TryGetProperty(
                    "score",
                    out var scoreElement) &&
                scoreElement.TryGetInt32(
                    out var parsed)
                    ? parsed
                    : 0;

            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return null;
            }

            return new PromptCandidate(
                value.Trim(),
                score,
                source);
        }
        catch
        {
            return null;
        }
    }

    private sealed record PromptCaptureResult(
        string Prompt,
        string Source)
    {
        public static PromptCaptureResult Empty =>
            new(
                string.Empty,
                "не найден");
    }

    private sealed record PromptCandidate(
        string Value,
        int Score,
        string Source);

    private const string PromptProbeScript = """
(() => {
  const candidates = [];

  function readValue(el) {
    if (!el) return '';

    if (typeof el.value === 'string') {
      return el.value.trim();
    }

    if (el.isContentEditable) {
      return (el.innerText || el.textContent || '').trim();
    }

    return '';
  }

  function add(el, bonus = 0) {
    if (!el) return;

    const value = readValue(el);
    if (!value || value.length < 3) return;

    const meta = [
      el.getAttribute?.('placeholder') || '',
      el.getAttribute?.('aria-label') || '',
      el.getAttribute?.('name') || '',
      el.getAttribute?.('id') || '',
      typeof el.className === 'string' ? el.className : ''
    ].join(' ').toLowerCase();

    let score = bonus;

    if (el === document.activeElement) score += 90;
    if (el.tagName === 'TEXTAREA') score += 35;
    if (el.isContentEditable) score += 25;

    if (/prompt|describe|description|what|draw|image|idea|scene|positive/.test(meta)) {
      score += 80;
    }

    if (/negative|exclude|avoid|seed|style|width|height/.test(meta)) {
      score -= 140;
    }

    if (value.length >= 20) score += 10;
    if (value.length >= 60) score += 10;
    if (value.length >= 4000) score -= 100;

    candidates.push({
      value,
      score
    });
  }

  add(document.activeElement, 110);

  document.querySelectorAll(
    'textarea, input[type="text"], input:not([type]), [contenteditable="true"]'
  ).forEach(el => add(el));

  candidates.sort((a, b) => {
    if (b.score !== a.score) return b.score - a.score;
    return b.value.length - a.value.length;
  });

  return candidates.length
    ? candidates[0]
    : { value: '', score: -9999 };
})()
""";
}
