using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PocketAI.Inference.Api;

public sealed class EmbeddingApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    public EmbeddingApiClient(LlamaServerSession session)
    {
        _model = session.ModelAlias;
        _http = new HttpClient { BaseAddress = session.BaseUri, Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.ApiKey);
    }
    public async Task<float[]> CreateAsync(string input, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("v1/embeddings", new { input, model = _model, encoding_format = "float" }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Embedding server {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        return arr.EnumerateArray().Select(x => x.GetSingle()).ToArray();
    }
    public void Dispose() => _http.Dispose();
}
