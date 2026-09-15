using System.Net.Http.Json;
using System.Text.Json;

namespace PocketAI.Images;

public sealed record ImageLoraSelection(
    string Path,
    double Weight);

public sealed record ImageGenerationRequest(
    string Prompt,
    string NegativePrompt,
    int Width,
    int Height,
    long Seed = -1,
    int Steps = 24,
    double CfgScale = 7.0,
    int BatchSize = 1,
    ImageGenerationMode Mode = ImageGenerationMode.Auto,
    string? HighResUpscaler = "latent",
    double DenoisingStrength = 0.35,
    int HighResSteps = 12,
    bool EnableVaeTiling = true,
    IReadOnlyList<ImageLoraSelection>? Loras = null);

public sealed record ImageSizeValidationResult(
    int RequestedWidth,
    int RequestedHeight,
    int Width,
    int Height,
    bool WasAdjusted,
    string Message);

public sealed record ImageGenerationResult(
    IReadOnlyList<string> FilePaths,
    int Width,
    int Height,
    long Seed,
    bool SizeAdjusted,
    string SizeMessage);

public static class ImageSizePolicy
{
    public const int MinimumDimension = 64;
    public const int MaximumDimension = 2048;
    public const int Alignment = 8;

    public static ImageSizeValidationResult Normalize(int requestedWidth, int requestedHeight)
    {
        var width = requestedWidth > 0 ? requestedWidth : 512;
        var height = requestedHeight > 0 ? requestedHeight : 512;

        width = Math.Max(MinimumDimension, width);
        height = Math.Max(MinimumDimension, height);

        var largest = Math.Max(width, height);
        if (largest > MaximumDimension)
        {
            var scale = MaximumDimension / (double)largest;
            width = Math.Max(MinimumDimension, (int)Math.Round(width * scale));
            height = Math.Max(MinimumDimension, (int)Math.Round(height * scale));
        }

        width = Align(width);
        height = Align(height);

        width = Math.Clamp(width, MinimumDimension, MaximumDimension);
        height = Math.Clamp(height, MinimumDimension, MaximumDimension);

        var adjusted = width != requestedWidth || height != requestedHeight;
        var message = adjusted
            ? $"Размер нормализован backend: {requestedWidth}×{requestedHeight} → {width}×{height}. Допустимо {MinimumDimension}..{MaximumDimension}, кратность {Alignment}."
            : $"Размер принят backend без изменения: {width}×{height}.";

        return new ImageSizeValidationResult(
            requestedWidth,
            requestedHeight,
            width,
            height,
            adjusted,
            message);
    }

    public static bool ShouldUseHighRes(ImageGenerationRequest request)
    {
        if (request.Mode == ImageGenerationMode.Native)
            return false;

        if (request.Mode == ImageGenerationMode.HiRes)
            return true;

        return Math.Max(request.Width, request.Height) > 640;
    }

    public static (int BaseWidth, int BaseHeight) GetBaseSizeForHighRes(
        int width,
        int height)
    {
        var largest = Math.Max(width, height);
        if (largest <= 640)
            return (width, height);

        var scale = 640.0 / largest;
        var baseWidth = Align(Math.Max(MinimumDimension, (int)Math.Round(width * scale)));
        var baseHeight = Align(Math.Max(MinimumDimension, (int)Math.Round(height * scale)));

        return (baseWidth, baseHeight);
    }

    private static int Align(int value)
    {
        var aligned = (int)Math.Round(value / (double)Alignment) * Alignment;
        return Math.Max(Alignment, aligned);
    }
}

public sealed class ImageGenerator : IDisposable
{
    private readonly HttpClient _http;

    public ImageGenerator(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri)
            throw new ArgumentException("Image server URI должен быть абсолютным.", nameof(baseUri));

        _http = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        string outputDirectory,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Промпт изображения пуст.", nameof(request));

        var size = ImageSizePolicy.Normalize(request.Width, request.Height);
        var steps = Math.Clamp(request.Steps, 1, 150);
        var cfgScale = Math.Clamp(request.CfgScale, 0.0, 30.0);
        var batchSize = Math.Clamp(request.BatchSize, 1, 4);
        var seed = request.Seed < -1 ? -1 : request.Seed;

        var enableHr = ImageSizePolicy.ShouldUseHighRes(request);
        var (baseWidth, baseHeight) = enableHr
            ? ImageSizePolicy.GetBaseSizeForHighRes(size.Width, size.Height)
            : (size.Width, size.Height);

        var loraPayload = request.Loras?
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .Select(x => new
            {
                path = x.Path,
                scale = x.Weight
            })
            .ToArray();

        using var response = await _http.PostAsJsonAsync(
            "sdapi/v1/txt2img",
            new
            {
                prompt = request.Prompt.Trim(),
                negative_prompt = request.NegativePrompt?.Trim() ?? string.Empty,
                width = baseWidth,
                height = baseHeight,
                steps,
                cfg_scale = cfgScale,
                seed,
                batch_size = batchSize,

                enable_hr = enableHr,
                hr_upscaler = request.HighResUpscaler ?? "latent",
                hr_resize_x = size.Width,
                hr_resize_y = size.Height,
                hr_second_pass_steps = Math.Clamp(request.HighResSteps, 1, 100),
                denoising_strength = Math.Clamp(request.DenoisingStrength, 0.0, 1.0),

                vae_tiling = request.EnableVaeTiling,
                lora = loraPayload
            },
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Image server {(int)response.StatusCode}: {Trim(body, 1400)}");

        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Array ||
            images.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Image server вернул ответ без массива images.");
        }

        Directory.CreateDirectory(outputDirectory);

        var paths = new List<string>(images.GetArrayLength());
        var index = 0;

        foreach (var image in images.EnumerateArray())
        {
            var base64 = image.GetString();
            if (string.IsNullOrWhiteSpace(base64))
                continue;

            var commaIndex = base64.IndexOf(',');
            if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
                base64 = base64[(commaIndex + 1)..];

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("Image server вернул повреждённые base64-данные.", ex);
            }

            if (bytes.Length < 256)
                continue;

            var path = Path.Combine(
                outputDirectory,
                $"PocketAI-{DateTime.Now:yyyyMMdd-HHmmss}-{index + 1}-{Guid.NewGuid():N}.png");

            await File.WriteAllBytesAsync(path, bytes, ct);
            paths.Add(path);
            index++;
        }

        if (paths.Count == 0)
            throw new InvalidDataException("Image server не вернул ни одного непустого изображения.");

        return new ImageGenerationResult(
            paths,
            size.Width,
            size.Height,
            seed,
            size.WasAdjusted,
            size.Message);
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
