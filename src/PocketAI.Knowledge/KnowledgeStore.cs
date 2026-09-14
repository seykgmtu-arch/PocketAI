using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PocketAI.Knowledge;

public sealed record KnowledgeDocument(
    string Id,
    string Name,
    string SourcePath,
    DateTime ImportedAtUtc,
    IReadOnlyList<string> Chunks);

public sealed record KnowledgeHit(
    string DocumentName,
    string SourcePath,
    string Text,
    double Score);

public sealed class KnowledgeStore
{
    private const int ChunkSize = 1600;
    private const int ChunkOverlap = 220;

    private readonly string _storeDirectory;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private List<KnowledgeDocument>? _documents;

    public KnowledgeStore(string baseDirectory)
    {
        _storeDirectory = Path.Combine(baseDirectory, "knowledge");
        _indexPath = Path.Combine(_storeDirectory, "index.json");
    }

    public async Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default)
    {
        return (await GetDocumentsAsync(cancellationToken).ConfigureAwait(false)).Count;
    }

    public async Task<IReadOnlyList<KnowledgeDocument>> GetDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadIfNeededUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return _documents!.ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task ImportAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        var pathList = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (pathList.Length == 0)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadIfNeededUnsafeAsync(cancellationToken).ConfigureAwait(false);

            var updated = new List<KnowledgeDocument>(_documents!);
            foreach (var path in pathList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var text = await ReadDocumentTextAsync(path, cancellationToken)
                    .ConfigureAwait(false);

                var chunks = SplitIntoChunks(text);
                if (chunks.Count == 0)
                    continue;

                var document = new KnowledgeDocument(
                    CreateStableId(path),
                    Path.GetFileName(path),
                    path,
                    DateTime.UtcNow,
                    chunks);

                var existingIndex = updated.FindIndex(item =>
                    item.Id.Equals(document.Id, StringComparison.OrdinalIgnoreCase));

                if (existingIndex >= 0)
                    updated[existingIndex] = document;
                else
                    updated.Add(document);
            }

            await SaveUnsafeAsync(updated, cancellationToken).ConfigureAwait(false);
            _documents = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var empty = new List<KnowledgeDocument>();
            Directory.CreateDirectory(_storeDirectory);
            await SaveUnsafeAsync(empty, cancellationToken).ConfigureAwait(false);
            _documents = empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
        string query,
        int maxResults = 4,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return Array.Empty<KnowledgeHit>();

        var documents = await GetDocumentsAsync(cancellationToken).ConfigureAwait(false);

        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
            return Array.Empty<KnowledgeHit>();

        var hits = new List<KnowledgeHit>();

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var chunk in document.Chunks)
            {
                var chunkTerms = Tokenize(chunk);
                if (chunkTerms.Count == 0)
                    continue;

                var overlap = queryTerms.Count(term => chunkTerms.Contains(term));
                if (overlap == 0)
                    continue;

                var score = overlap /
                            Math.Sqrt((double)queryTerms.Count * chunkTerms.Count);

                if (chunk.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    score += 0.25;

                hits.Add(new KnowledgeHit(
                    document.Name,
                    document.SourcePath,
                    chunk,
                    score));
            }
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .Take(maxResults)
            .ToArray();
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_documents is not null)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadIfNeededUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadIfNeededUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_documents is not null)
            return;

        if (!File.Exists(_indexPath))
        {
            _documents = new List<KnowledgeDocument>();
            return;
        }

        await using var stream = File.OpenRead(_indexPath);
        _documents = await JsonSerializer.DeserializeAsync<List<KnowledgeDocument>>(
                         stream,
                         _jsonOptions,
                         cancellationToken)
                     .ConfigureAwait(false)
                     ?? new List<KnowledgeDocument>();
    }

    private async Task SaveUnsafeAsync(List<KnowledgeDocument> documents, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storeDirectory);

        var tempPath = _indexPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    documents,
                    _jsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(tempPath, _indexPath, overwrite: true);
    }

    private static async Task<string> ReadDocumentTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл базы знаний не найден.", path);

        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".txt" or ".md" or ".csv" or ".json" or ".log" or ".cs" =>
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),

            ".docx" => await Task.Run(
                () => ReadDocx(path),
                cancellationToken).ConfigureAwait(false),

            _ => throw new NotSupportedException(
                $"Формат {extension} пока не поддерживается. " +
                "Используйте TXT, MD, CSV, JSON, LOG, CS или DOCX.")
        };
    }

    private static string ReadDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml")
                    ?? throw new InvalidDataException("DOCX не содержит word/document.xml.");

        using var stream = entry.Open();
        var document = XDocument.Load(stream);

        var paragraphs = document
            .Descendants()
            .Where(element => element.Name.LocalName == "p")
            .Select(paragraph => string.Concat(
                paragraph
                    .Descendants()
                    .Where(element => element.Name.LocalName == "t")
                    .Select(element => element.Value)))
            .Where(text => !string.IsNullOrWhiteSpace(text));

        return string.Join(Environment.NewLine, paragraphs);
    }

    private static IReadOnlyList<string> SplitIntoChunks(string text)
    {
        var normalized = Regex.Replace(text ?? string.Empty, @"[ \t]+", " ")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (normalized.Length == 0)
            return Array.Empty<string>();

        var chunks = new List<string>();
        var start = 0;

        while (start < normalized.Length)
        {
            var remaining = normalized.Length - start;
            var length = Math.Min(ChunkSize, remaining);
            var end = start + length;

            if (end < normalized.Length)
            {
                var preferredBreak = normalized.LastIndexOfAny(
                    ['\n', '.', '!', '?', ';'],
                    end - 1,
                    Math.Min(length, 420));

                if (preferredBreak > start + 500)
                    end = preferredBreak + 1;
            }

            var chunk = normalized[start..end].Trim();
            if (chunk.Length > 0)
                chunks.Add(chunk);

            if (end >= normalized.Length)
                break;

            start = Math.Max(start + 1, end - ChunkOverlap);
        }

        return chunks;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(
                     text.ToLowerInvariant(),
                     @"[\p{L}\p{N}][\p{L}\p{N}_-]{2,}"))
        {
            terms.Add(match.Value);
        }

        return terms;
    }

    private static string CreateStableId(string path)
    {
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));

        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }
}
