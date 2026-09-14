using System.Net.Http.Json;
using System.Text.Json;

namespace PocketAI.Images;
public sealed class ImageGenerationClient : IDisposable
{
    private readonly HttpClient _http;
    public ImageGenerationClient(Uri baseUri) => _http = new HttpClient { BaseAddress=baseUri, Timeout=TimeSpan.FromMinutes(15) };
    public async Task<string> GenerateAsync(string prompt, int width, int height, string outputDirectory, CancellationToken ct=default)
    {
        using var response = await _http.PostAsJsonAsync("v1/images/generations", new { prompt, n=1, size=$"{width}x{height}", output_format="png" }, ct);
        var body=await response.Content.ReadAsStringAsync(ct); if(!response.IsSuccessStatusCode) throw new HttpRequestException($"Image server {(int)response.StatusCode}: {body}");
        using var doc=JsonDocument.Parse(body); var b64=doc.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString(); if(string.IsNullOrWhiteSpace(b64)) throw new InvalidDataException("Image server не вернул изображение.");
        Directory.CreateDirectory(outputDirectory); var path=Path.Combine(outputDirectory,$"PocketAI-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png"); await File.WriteAllBytesAsync(path,Convert.FromBase64String(b64),ct); return path;
    }
    public void Dispose()=>_http.Dispose();
}
