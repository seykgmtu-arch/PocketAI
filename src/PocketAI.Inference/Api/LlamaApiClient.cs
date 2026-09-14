using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PocketAI.Core.Chat;
using PocketAI.Core.Configuration;

namespace PocketAI.Inference.Api;

public sealed class LlamaApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly PocketAiConfig _config;
    private readonly string _modelAlias;

    public LlamaApiClient(LlamaServerSession session, PocketAiConfig config)
    {
        if (!IPAddressIsLoopback(session.BaseUri))
            throw new InvalidOperationException("Pocket AI разрешает inference API только на loopback-интерфейсе.");

        _config = config;
        _modelAlias = session.ModelAlias;

        _httpClient = new HttpClient
        {
            BaseAddress = session.BaseUri,
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session.ApiKey);
    }

    public async Task StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        Func<string, Task> onText,
        CancellationToken cancellationToken,
        string? knowledgeContext = null)
    {
        var systemContent = string.IsNullOrWhiteSpace(knowledgeContext)
            ? _config.SystemPrompt
            : _config.SystemPrompt + "\n\n" + knowledgeContext;

        var messages = new List<object>
        {
            new { role = "system", content = systemContent }
        };

        messages.AddRange(history.Select(m => (object)new
        {
            role = m.Role,
            content = m.Content
        }));

        var body = new
        {
            model = _modelAlias,
            messages,
            stream = true,
            temperature = _config.Temperature,
            max_tokens = _config.MaxOutputTokens
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(body)
        };

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"llama-server вернул {(int)response.StatusCode}: {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line[5..].TrimStart();
            if (payload == "[DONE]")
                break;

            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta))
                continue;

            // Deliberately ignore reasoning_content / hidden chain-of-thought fields.
            if (delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    await onText(text).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool IPAddressIsLoopback(Uri uri)
        => uri.IsLoopback &&
           (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase));
}
