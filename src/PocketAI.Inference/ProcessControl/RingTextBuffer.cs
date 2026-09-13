namespace PocketAI.Inference.ProcessControl;

internal sealed class RingTextBuffer
{
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private readonly int _capacity;

    public RingTextBuffer(int capacity = 80) => _capacity = Math.Max(10, capacity);

    public void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
                _lines.Dequeue();
        }
    }

    public string Snapshot()
    {
        lock (_gate)
            return string.Join(Environment.NewLine, _lines);
    }
}
