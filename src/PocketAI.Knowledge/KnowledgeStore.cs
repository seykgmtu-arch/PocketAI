using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace PocketAI.Knowledge;

public sealed record KnowledgeDocument(string Id, string Name, string SourcePath, DateTime ImportedAtUtc, IReadOnlyList<string> Chunks);
public sealed record KnowledgeHit(string DocumentName, string SourcePath, string Text, double Score);
public sealed record VectorChunk(string DocumentId, int ChunkIndex, float[] Vector);

public sealed class KnowledgeStore
{
    private const int ChunkSize = 900;
    private const int ChunkOverlap = 120;
    private readonly string _storeDirectory;
    private readonly string _indexPath;
    private readonly string _vectorPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private List<KnowledgeDocument>? _documents;
    private List<VectorChunk>? _vectors;

    public KnowledgeStore(string baseDirectory)
    {
        _storeDirectory = Path.Combine(baseDirectory, "knowledge");
        _indexPath = Path.Combine(_storeDirectory, "index.json");
        _vectorPath = Path.Combine(_storeDirectory, "vectors.json");
    }

    public async Task<int> GetDocumentCountAsync(CancellationToken ct = default) => (await GetDocumentsAsync(ct)).Count;
    public async Task<int> GetVectorCountAsync(CancellationToken ct = default) { await EnsureVectorsLoadedAsync(ct); return _vectors!.Count; }

    public async Task<IReadOnlyList<KnowledgeDocument>> GetDocumentsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await LoadDocumentsUnsafeAsync(ct); return _documents!.ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task ImportAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (list.Length == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            await LoadDocumentsUnsafeAsync(ct);
            var updated = new List<KnowledgeDocument>(_documents!);
            foreach (var path in list)
            {
                ct.ThrowIfCancellationRequested();
                var text = await ReadDocumentTextAsync(path, ct);
                var chunks = SplitIntoChunks(text);
                if (chunks.Count == 0) continue;
                var doc = new KnowledgeDocument(CreateStableId(path), Path.GetFileName(path), path, DateTime.UtcNow, chunks);
                var i = updated.FindIndex(x => x.Id.Equals(doc.Id, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) updated[i] = doc; else updated.Add(doc);
            }
            await SaveDocumentsUnsafeAsync(updated, ct);
            _documents = updated;
            _vectors = new List<VectorChunk>();
            await SaveVectorsUnsafeAsync(_vectors, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _documents = new List<KnowledgeDocument>(); _vectors = new List<VectorChunk>();
            await SaveDocumentsUnsafeAsync(_documents, ct); await SaveVectorsUnsafeAsync(_vectors, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task BuildVectorIndexAsync(Func<string, CancellationToken, Task<float[]>> embed, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var docs = await GetDocumentsAsync(ct);
        var result = new List<VectorChunk>();
        var total = docs.Sum(d => d.Chunks.Count); var done = 0;
        foreach (var doc in docs)
        {
            for (var i = 0; i < doc.Chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var vector = await embed(doc.Chunks[i], ct);
                if (vector.Length > 0) result.Add(new VectorChunk(doc.Id, i, vector));
                done++; progress?.Report($"Embeddings: {done}/{total}");
            }
        }
        await _gate.WaitAsync(ct);
        try { _vectors = result; await SaveVectorsUnsafeAsync(result, ct); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchVectorAsync(string query, float[] queryVector, int maxResults = 4, CancellationToken ct = default)
    {
        if (queryVector.Length == 0) return await SearchAsync(query, maxResults, ct);
        var docs = await GetDocumentsAsync(ct); await EnsureVectorsLoadedAsync(ct);
        var byId = docs.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var hits = new List<KnowledgeHit>();
        foreach (var item in _vectors!)
        {
            if (!byId.TryGetValue(item.DocumentId, out var doc) || item.ChunkIndex < 0 || item.ChunkIndex >= doc.Chunks.Count) continue;
            if (item.Vector.Length != queryVector.Length) continue;
            var score = Cosine(queryVector, item.Vector);
            hits.Add(new KnowledgeHit(doc.Name, doc.SourcePath, doc.Chunks[item.ChunkIndex], score));
        }
        return hits.OrderByDescending(h => h.Score).Take(maxResults).ToArray();
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int maxResults = 4, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<KnowledgeHit>();
        var docs = await GetDocumentsAsync(ct); var terms = Tokenize(query); var hits = new List<KnowledgeHit>();
        foreach (var doc in docs)
        foreach (var chunk in doc.Chunks)
        {
            var ctokens = Tokenize(chunk); var overlap = terms.Count(t => ctokens.Contains(t)); if (overlap == 0) continue;
            var score = overlap / Math.Sqrt((double)Math.Max(1, terms.Count) * Math.Max(1, ctokens.Count));
            if (chunk.Contains(query, StringComparison.CurrentCultureIgnoreCase)) score += .25;
            hits.Add(new KnowledgeHit(doc.Name, doc.SourcePath, chunk, score));
        }
        return hits.OrderByDescending(h => h.Score).Take(maxResults).ToArray();
    }

    private async Task LoadDocumentsUnsafeAsync(CancellationToken ct)
    {
        if (_documents is not null) return;
        if (!File.Exists(_indexPath)) { _documents = new(); return; }
        await using var s = File.OpenRead(_indexPath);
        _documents = await JsonSerializer.DeserializeAsync<List<KnowledgeDocument>>(s, _jsonOptions, ct) ?? new();
    }

    private async Task EnsureVectorsLoadedAsync(CancellationToken ct)
    {
        if (_vectors is not null) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_vectors is not null) return;
            if (!File.Exists(_vectorPath)) { _vectors = new(); return; }
            await using var s = File.OpenRead(_vectorPath);
            _vectors = await JsonSerializer.DeserializeAsync<List<VectorChunk>>(s, _jsonOptions, ct) ?? new();
        }
        finally { _gate.Release(); }
    }

    private async Task SaveDocumentsUnsafeAsync(List<KnowledgeDocument> docs, CancellationToken ct)
    {
        Directory.CreateDirectory(_storeDirectory); var tmp = _indexPath + ".tmp";
        await using (var s = File.Create(tmp)) await JsonSerializer.SerializeAsync(s, docs, _jsonOptions, ct);
        File.Move(tmp, _indexPath, true);
    }
    private async Task SaveVectorsUnsafeAsync(List<VectorChunk> vectors, CancellationToken ct)
    {
        Directory.CreateDirectory(_storeDirectory); var tmp = _vectorPath + ".tmp";
        await using (var s = File.Create(tmp)) await JsonSerializer.SerializeAsync(s, vectors, _jsonOptions, ct);
        File.Move(tmp, _vectorPath, true);
    }

    private static async Task<string> ReadDocumentTextAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл базы знаний не найден.", path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".csv" or ".json" or ".log" or ".cs" => await File.ReadAllTextAsync(path, ct),
            ".docx" => await Task.Run(() => ReadDocx(path), ct),
            ".pdf" => await Task.Run(() => ReadPdf(path), ct),
            var ext => throw new NotSupportedException($"Формат {ext} пока не поддерживается.")
        };
    }

    private static string ReadPdf(string path)
    {
        var sb = new StringBuilder(); using var pdf = PdfDocument.Open(path);
        foreach (var page in pdf.GetPages()) { sb.AppendLine(page.Text); sb.AppendLine(); }
        return sb.ToString();
    }

    private static string ReadDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("DOCX не содержит word/document.xml.");
        using var stream = entry.Open(); var doc = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Join("\n", doc.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))));
    }

    private static List<string> SplitIntoChunks(string text)
    {
        text = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim(); var chunks = new List<string>();
        for (var start = 0; start < text.Length; start += ChunkSize - ChunkOverlap)
        { var len = Math.Min(ChunkSize, text.Length - start); var c = text.Substring(start, len).Trim(); if (c.Length > 40) chunks.Add(c); if (start + len >= text.Length) break; }
        return chunks;
    }
    private static HashSet<string> Tokenize(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value).ToHashSet();
    private static string CreateStableId(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()))).ToLowerInvariant()[..24];
    private static double Cosine(float[] a, float[] b)
    { double dot=0, aa=0, bb=0; for(int i=0;i<a.Length;i++){dot+=a[i]*b[i];aa+=a[i]*a[i];bb+=b[i]*b[i];} return aa==0||bb==0?0:dot/(Math.Sqrt(aa)*Math.Sqrt(bb)); }
}
