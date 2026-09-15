using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PocketAI.Images;

public sealed record ImageLoraSelection(
    string ApiPath,
    double Weight);

internal sealed record ImageLoraPayload(
    [property: JsonPropertyName("path")]
    string Path,
    [property: JsonPropertyName("multiplier")]
    double Multiplier);

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
    string HighResUpscaler = "Latent (bicubic antialiased)",
    double DenoisingStrength = 0.35,
    int HighResSteps = 12,
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
    string SizeMessage,
    bool UsedHighRes);

public static class ImageSizePolicy
{
    public const int MinimumDimension = 64;
    public const int MaximumDimension = 2048;
    public const int Alignment = 8;

    // У пользователя native-generation стабильно работает до 640.
    // Auto переводит всё, что выше, на двухпроходный high-res fix.
    public const int NativeSafeLargestDimension = 640;

    public static ImageSizeValidationResult Normalize(
        int requestedWidth,
        int requestedHeight)
    {
        var width =
            requestedWidth > 0
                ? requestedWidth
                : 512;

        var height =
            requestedHeight > 0
                ? requestedHeight
                : 512;

        width =
            Math.Max(
                MinimumDimension,
                width);

        height =
            Math.Max(
                MinimumDimension,
                height);

        var largest =
            Math.Max(
                width,
                height);

        if (largest > MaximumDimension)
        {
            var scale =
                MaximumDimension /
                (double)largest;

            width =
                Math.Max(
                    MinimumDimension,
                    (int)Math.Round(
                        width * scale));

            height =
                Math.Max(
                    MinimumDimension,
                    (int)Math.Round(
                        height * scale));
        }

        width = Align(width);
        height = Align(height);

        width =
            Math.Clamp(
                width,
                MinimumDimension,
                MaximumDimension);

        height =
            Math.Clamp(
                height,
                MinimumDimension,
                MaximumDimension);

        var adjusted =
            width != requestedWidth ||
            height != requestedHeight;

        var message =
            adjusted
                ? $"Размер нормализован: {requestedWidth}×{requestedHeight} → {width}×{height}. " +
                  $"Диапазон {MinimumDimension}..{MaximumDimension}, кратность {Alignment}."
                : $"Размер принят без изменения: {width}×{height}.";

        return new ImageSizeValidationResult(
            requestedWidth,
            requestedHeight,
            width,
            height,
            adjusted,
            message);
    }

    public static bool ShouldUseHighRes(
        ImageGenerationMode mode,
        int width,
        int height)
    {
        return mode switch
        {
            ImageGenerationMode.Native => false,
            ImageGenerationMode.HiRes => true,
            _ => Math.Max(width, height) >
                 NativeSafeLargestDimension
        };
    }

    public static (
        int BaseWidth,
        int BaseHeight)
        GetHighResBaseSize(
            int targetWidth,
            int targetHeight)
    {
        var largest =
            Math.Max(
                targetWidth,
                targetHeight);

        if (largest <=
            NativeSafeLargestDimension)
        {
            return (
                targetWidth,
                targetHeight);
        }

        var scale =
            NativeSafeLargestDimension /
            (double)largest;

        var width =
            Align(
                Math.Max(
                    MinimumDimension,
                    (int)Math.Round(
                        targetWidth * scale)));

        var height =
            Align(
                Math.Max(
                    MinimumDimension,
                    (int)Math.Round(
                        targetHeight * scale)));

        return (
            Math.Min(
                width,
                NativeSafeLargestDimension),
            Math.Min(
                height,
                NativeSafeLargestDimension));
    }

    private static int Align(int value)
    {
        var aligned =
            (int)Math.Round(
                value /
                (double)Alignment) *
            Alignment;

        return Math.Max(
            Alignment,
            aligned);
    }
}

public sealed class ImageGenerator : IDisposable
{
    private readonly HttpClient _http;

    public ImageGenerator(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "Image server URI должен быть абсолютным.",
                nameof(baseUri));
        }

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
        if (string.IsNullOrWhiteSpace(
                request.Prompt))
        {
            throw new ArgumentException(
                "Промпт изображения пуст.",
                nameof(request));
        }

        var size =
            ImageSizePolicy.Normalize(
                request.Width,
                request.Height);

        var steps =
            Math.Clamp(
                request.Steps,
                1,
                150);

        var cfgScale =
            Math.Clamp(
                request.CfgScale,
                0.0,
                30.0);

        var batchSize =
            Math.Clamp(
                request.BatchSize,
                1,
                4);

        var seed =
            request.Seed < -1
                ? -1
                : request.Seed;

        var enableHighRes =
            ImageSizePolicy.ShouldUseHighRes(
                request.Mode,
                size.Width,
                size.Height);

        var (
            requestWidth,
            requestHeight) =
            enableHighRes
                ? ImageSizePolicy
                    .GetHighResBaseSize(
                        size.Width,
                        size.Height)
                : (
                    size.Width,
                    size.Height);

        // IMPORTANT:
        // stable-diffusion.cpp refreshes its internal LoRA cache when
        // GET /sdapi/v1/loras is called. PocketAI may discover a LoRA
        // directly from disk before the server has refreshed that cache.
        // Resolve against the server immediately before txt2img so the
        // structured lora.path value is guaranteed to be accepted.
        var loras =
            await PrepareLorasAsync(
                request.Loras,
                ct);

        using var response =
            await _http.PostAsJsonAsync(
                "sdapi/v1/txt2img",
                new
                {
                    prompt =
                        request.Prompt.Trim(),

                    negative_prompt =
                        request.NegativePrompt?
                            .Trim() ??
                        string.Empty,

                    width = requestWidth,
                    height = requestHeight,
                    steps,
                    cfg_scale = cfgScale,
                    seed,
                    batch_size = batchSize,

                    lora = loras,

                    enable_hr = enableHighRes,

                    hr_upscaler =
                        request.HighResUpscaler,

                    hr_resize_x =
                        enableHighRes
                            ? size.Width
                            : 0,

                    hr_resize_y =
                        enableHighRes
                            ? size.Height
                            : 0,

                    hr_steps =
                        enableHighRes
                            ? Math.Clamp(
                                request.HighResSteps,
                                1,
                                100)
                            : 0,

                    denoising_strength =
                        Math.Clamp(
                            request.DenoisingStrength,
                            0.0,
                            1.0)
                },
                ct);

        var body =
            await response.Content
                .ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Image server {(int)response.StatusCode}: " +
                $"{Trim(body, 1800)}");
        }

        using var document =
            JsonDocument.Parse(body);

        if (!document.RootElement
                .TryGetProperty(
                    "images",
                    out var images) ||
            images.ValueKind !=
                JsonValueKind.Array ||
            images.GetArrayLength() == 0)
        {
            throw new InvalidDataException(
                "Image server вернул ответ без массива images.");
        }

        Directory.CreateDirectory(
            outputDirectory);

        var paths =
            new List<string>(
                images.GetArrayLength());

        var index = 0;

        foreach (var image in
                 images.EnumerateArray())
        {
            var base64 =
                image.GetString();

            if (string.IsNullOrWhiteSpace(
                    base64))
            {
                continue;
            }

            var commaIndex =
                base64.IndexOf(',');

            if (base64.StartsWith(
                    "data:",
                    StringComparison.OrdinalIgnoreCase) &&
                commaIndex >= 0)
            {
                base64 =
                    base64[
                        (commaIndex + 1)..];
            }

            byte[] bytes;

            try
            {
                bytes =
                    Convert.FromBase64String(
                        base64);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    "Image server вернул повреждённые base64-данные.",
                    ex);
            }

            if (bytes.Length < 256)
                continue;

            var path =
                Path.Combine(
                    outputDirectory,
                    $"PocketAI-{DateTime.Now:yyyyMMdd-HHmmss}-" +
                    $"{index + 1}-{Guid.NewGuid():N}.png");

            await File.WriteAllBytesAsync(
                path,
                bytes,
                ct);

            paths.Add(path);
            index++;
        }

        if (paths.Count == 0)
        {
            throw new InvalidDataException(
                "Image server не вернул ни одного непустого изображения.");
        }

        return new ImageGenerationResult(
            paths,
            size.Width,
            size.Height,
            seed,
            size.WasAdjusted,
            size.Message,
            enableHighRes);
    }

    private async Task<ImageLoraPayload[]?> PrepareLorasAsync(
        IReadOnlyList<ImageLoraSelection>? requestedLoras,
        CancellationToken ct)
    {
        var requested =
            requestedLoras?
                .Where(
                    x =>
                        !string.IsNullOrWhiteSpace(
                            x.ApiPath))
                .ToArray();

        if (requested is null ||
            requested.Length == 0)
        {
            return null;
        }

        // This endpoint refreshes stable-diffusion.cpp's LoRA cache.
        using var discoveryResponse =
            await _http.GetAsync(
                "sdapi/v1/loras",
                ct);

        var discoveryBody =
            await discoveryResponse.Content
                .ReadAsStringAsync(ct);

        if (!discoveryResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Image server LoRA discovery {(int)discoveryResponse.StatusCode}: " +
                $"{Trim(discoveryBody, 1400)}");
        }

        using var document =
            JsonDocument.Parse(
                discoveryBody);

        if (document.RootElement.ValueKind !=
            JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Image server вернул неверный формат /sdapi/v1/loras.");
        }

        var serverPaths =
            new List<string>();

        foreach (var item in
                 document.RootElement
                     .EnumerateArray())
        {
            if (!item.TryGetProperty(
                    "path",
                    out var pathElement) ||
                pathElement.ValueKind !=
                    JsonValueKind.String)
            {
                continue;
            }

            var path =
                pathElement.GetString();

            if (!string.IsNullOrWhiteSpace(
                    path))
            {
                serverPaths.Add(
                    path);
            }
        }

        if (serverPaths.Count == 0)
        {
            throw new InvalidOperationException(
                "LoRA выбрана в PocketAI, но sd-server не видит ни одной LoRA. " +
                "Проверьте models\\images\\loras и запуск сервера с --lora-model-dir.");
        }

        var result =
            new List<ImageLoraPayload>(
                requested.Length);

        foreach (var requestedLora in requested)
        {
            var requestedPath =
                NormalizeApiPath(
                    requestedLora.ApiPath);

            var resolved =
                serverPaths.FirstOrDefault(
                    serverPath =>
                        string.Equals(
                            NormalizeApiPath(
                                serverPath),
                            requestedPath,
                            StringComparison.OrdinalIgnoreCase));

            // Fallback only for a single unambiguous filename match.
            if (resolved is null)
            {
                var requestedName =
                    GetApiFileName(
                        requestedPath);

                var filenameMatches =
                    serverPaths
                        .Where(
                            serverPath =>
                                string.Equals(
                                    GetApiFileName(
                                        NormalizeApiPath(
                                            serverPath)),
                                    requestedName,
                                    StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                if (filenameMatches.Length == 1)
                {
                    resolved =
                        filenameMatches[0];
                }
            }

            if (resolved is null)
            {
                var visible =
                    string.Join(
                        ", ",
                        serverPaths.Take(8));

                throw new InvalidOperationException(
                    $"LoRA «{requestedLora.ApiPath}» не найдена в кэше sd-server. " +
                    $"Сервер видит: {visible}");
            }

            result.Add(
                new ImageLoraPayload(
                    resolved,
                    Math.Clamp(
                        requestedLora.Weight,
                        -4.0,
                        4.0)));
        }

        return result.ToArray();
    }

    private static string NormalizeApiPath(
        string path)
    {
        return path
            .Trim()
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static string GetApiFileName(
        string path)
    {
        var normalized =
            NormalizeApiPath(
                path);

        var slash =
            normalized.LastIndexOf('/');

        return slash >= 0
            ? normalized[(slash + 1)..]
            : normalized;
    }

    private static string Trim(
        string text,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(
                text))
        {
            return "(empty response)";
        }

        text = text.Trim();

        return text.Length <= maxLength
            ? text
            : text[..maxLength] + "…";
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
