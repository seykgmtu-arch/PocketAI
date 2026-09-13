using System.Text.Json;
using PocketAI.Core.Chat;
using PocketAI.Core.Configuration;
using PocketAI.Hardware;
using PocketAI.Inference;
using PocketAI.Inference.Api;

namespace PocketAI.App;

internal static class SelfTestRunner
{
    public static async Task<int> RunAsync(
        string baseDirectory,
        PocketAiConfig config,
        string outputPath,
        bool forceCpu,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, object?>
        {
            ["startedUtc"] = DateTime.UtcNow.ToString("O"),
            ["passed"] = false,
            ["forceCpu"] = forceCpu
        };

        try
        {
            var hardware = await new HardwareProbe().ProbeAsync(cancellationToken);
            if (forceCpu)
                hardware = hardware with { Gpus = Array.Empty<GpuInfo>() };

            result["cpu"] = hardware.CpuName;
            result["ramBytes"] = hardware.TotalMemoryBytes;
            result["gpus"] = hardware.Gpus.Select(g => g.Name).ToArray();

            using var server = new LlamaServerManager(baseDirectory, config);
            var session = await server.StartAsync(hardware, cancellationToken: cancellationToken);
            result["backend"] = session.Backend.ToString();
            result["port"] = session.Port;

            using var client = new LlamaApiClient(session, config);
            var text = string.Empty;
            var history = new[]
            {
                new ChatMessage("user", "Ответь одним словом: работает /no_think")
            };

            await client.StreamChatAsync(
                history,
                token =>
                {
                    text += token;
                    return Task.CompletedTask;
                },
                cancellationToken);

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Self-test chat completion returned empty content.");

            result["response"] = text.Trim();
            result["passed"] = true;
            result["finishedUtc"] = DateTime.UtcNow.ToString("O");
            await WriteResultAsync(outputPath, result, cancellationToken);
            return 0;
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
            result["finishedUtc"] = DateTime.UtcNow.ToString("O");
            await WriteResultAsync(outputPath, result, cancellationToken);
            return 1;
        }
    }

    private static Task WriteResultAsync(
        string path,
        Dictionary<string, object?> result,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        return File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }
}
