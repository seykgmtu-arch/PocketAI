namespace PocketAI.Inference;

public sealed record LlamaServerSession(
    Uri BaseUri,
    string ApiKey,
    BackendKind Backend,
    int Port,
    string ModelAlias);
