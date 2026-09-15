using System;
using System.IO;
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
        }

        _promptSidecarCore =
            core;

        core.DownloadStarting +=
            PromptSidecar_DownloadStarting;
    }

    private void PromptSidecar_DownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs e)
    {
        var operation =
            e.DownloadOperation;

        // Start prompt detection immediately. The existing PocketAI download
        // handler may still change ResultFilePath; at completion we read the
        // final path from DownloadOperation.ResultFilePath.
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
        Task<string> promptTask)
    {
        try
        {
            var prompt =
                (await promptTask)
                .Trim();

            if (string.IsNullOrWhiteSpace(
                    prompt))
            {
                await Dispatcher.InvokeAsync(
                    () =>
                    {
                        StatusText.Text =
                            "Картинка сохранена, но prompt автоматически не найден.";
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
                    StatusText.Text =
                        "Сохранено изображение + prompt.txt: " +
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

    private async Task<string> CaptureCurrentPromptAsync()
    {
        try
        {
            if (Browser.CoreWebView2 is null)
                return string.Empty;

            var json =
                await Browser.CoreWebView2
                    .ExecuteScriptAsync(
                        PromptProbeScript);

            if (string.IsNullOrWhiteSpace(
                    json))
            {
                return string.Empty;
            }

            return
                JsonSerializer.Deserialize<string>(
                    json) ??
                string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private const string PromptProbeScript = """
(() => {
  const candidates = [];

  function readValue(el) {
    if (!el) return '';
    if (typeof el.value === 'string') return el.value.trim();
    if (el.isContentEditable) return (el.innerText || el.textContent || '').trim();
    return '';
  }

  function add(el, bonus = 0) {
    const value = readValue(el);
    if (!value || value.length < 3) return;

    const meta = [
      el.getAttribute?.('placeholder') || '',
      el.getAttribute?.('aria-label') || '',
      el.getAttribute?.('name') || '',
      el.getAttribute?.('id') || '',
      el.className || ''
    ].join(' ').toLowerCase();

    let score = bonus;

    if (el === el.ownerDocument.activeElement) score += 80;
    if (el.tagName === 'TEXTAREA') score += 30;
    if (/prompt|describe|description|what|draw|image|idea|scene/.test(meta)) score += 60;
    if (/negative|exclude|avoid/.test(meta)) score -= 120;
    if (value.length >= 20) score += 10;
    if (value.length >= 60) score += 10;

    candidates.push({ value, score });
  }

  function scanDocument(doc) {
    try {
      add(doc.activeElement, 100);

      doc.querySelectorAll(
        'textarea, input[type="text"], input:not([type]), [contenteditable="true"]'
      ).forEach(el => add(el));

      doc.querySelectorAll('iframe').forEach(frame => {
        try {
          if (frame.contentDocument) scanDocument(frame.contentDocument);
        } catch {}
      });
    } catch {}
  }

  scanDocument(document);

  candidates.sort((a, b) => b.score - a.score);

  return candidates.length ? candidates[0].value : '';
})()
""";
}
