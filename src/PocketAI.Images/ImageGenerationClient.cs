using System.Net.Http.Json;
using System.Text.Json;

namespace PocketAI.Images;

public sealed class ImageGenerationClient : IDisposable
{
    private readonly HttpClient _http;

    public ImageGenerationClient(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri)
            throw new ArgumentException("Image server URI должен быть абсолютным.", nameof(baseUri));

        _http = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    public async Task<string> CheckAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/models", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Image server health check {(int)response.StatusCode}: {Trim(body, 700)}");
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0 &&
                data[0].TryGetProperty("id", out var id))
            {
                var model = id.GetString();
                if (!string.IsNullOrWhiteSpace(model))
                    return model;
            }
        }
        catch (JsonException)
        {
            // A successful HTTP response is enough to prove that the endpoint is alive.
        }

        return "stable-diffusion.cpp";
    }

    public async Task<string> GenerateAsync(
        string prompt,
        int width,
        int height,
        string outputDirectory,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Промпт изображения пуст.", nameof(prompt));

        if (width is < 64 or > 4096 || height is < 64 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(width), "Размер изображения должен быть 64..4096.");

        using var response = await _http.PostAsJsonAsync(
            "v1/images/generations",
            new
            {
                prompt,
                n = 1,
                size = $"{width}x{height}",
                output_format = "png"
            },
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Image server {(int)response.StatusCode}: {Trim(body, 1200)}");
        }

        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0 ||
            !data[0].TryGetProperty("b64_json", out var encoded))
        {
            throw new InvalidDataException(
                "Image server вернул ответ без data[0].b64_json.");
        }

        var base64 = encoded.GetString();

        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidDataException("Image server вернул пустое изображение.");

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(
                "Image server вернул повреждённые base64-данные.",
                ex);
        }

        if (bytes.Length < 256)
            throw new InvalidDataException("Image server вернул слишком маленький файл изображения.");

        Directory.CreateDirectory(outputDirectory);

        var path = Path.Combine(
            outputDirectory,
            $"PocketAI-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");

        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    private static string Trim(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(empty response)";

        text = text.Trim();
        return text.Length <= maxLength
            ? text
            : text[..maxLength] + "…";
    }

    public void Dispose() => _http.Dispose();
}
