namespace PocketAI.Core.Configuration;

public sealed class PocketAiConfig
{
    public string ModelPath { get; set; } = "models/chat/model.gguf";
    public RuntimeConfig Runtime { get; set; } = new();
    public int ContextSize { get; set; } = 8192;
    public int MaxOutputTokens { get; set; } = 1024;
    public double Temperature { get; set; } = 0.6;
    public string SystemPrompt { get; set; } =
        "Ты Pocket AI — локальный приватный помощник. Отвечай по существу и на языке пользователя.";
}

public sealed class RuntimeConfig
{
    public string CpuPath { get; set; } = "runtime/llama/cpu/llama-server.exe";
    public string CudaPath { get; set; } = "runtime/llama/cuda/llama-server.exe";
}
