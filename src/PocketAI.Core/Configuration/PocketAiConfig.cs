namespace PocketAI.Core.Configuration;

public sealed class PocketAiConfig
{
    public string ModelPath { get; set; } = "models/chat/model.gguf";
    public RuntimeConfig Runtime { get; set; } = new();
    public EmbeddingConfig Embeddings { get; set; } = new();
    public KnowledgeConfig Knowledge { get; set; } = new();
    public WebConfig Web { get; set; } = new();
    public ImageConfig Images { get; set; } = new();
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

public sealed class EmbeddingConfig
{
    public bool Enabled { get; set; } = true;
    public string ModelPath { get; set; } = "models/embeddings/model.gguf";
    public int ContextSize { get; set; } = 2048;
}

public sealed class KnowledgeConfig
{
    public bool PreferVectorSearch { get; set; } = true;
    public int TopK { get; set; } = 4;
}

public sealed class WebConfig
{
    public bool Enabled { get; set; } = false;
    public int MaxResults { get; set; } = 5;
    public int MaxPagesToRead { get; set; } = 2;
    public int MaxCharactersPerPage { get; set; } = 8000;
}

public sealed class ImageConfig
{
    public bool Enabled { get; set; } = false;
    public string ServerUrl { get; set; } = "http://127.0.0.1:7860/";
    public string OutputDirectory { get; set; } = "outputs/images";
    public int Width { get; set; } = 768;
    public int Height { get; set; } = 768;
}
