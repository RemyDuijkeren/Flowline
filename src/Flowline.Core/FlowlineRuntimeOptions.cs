namespace Flowline.Core;

public sealed class FlowlineRuntimeOptions
{
    public bool IsVerbose { get; set; }
    public bool Force { get; set; }
    public string? CommandName { get; set; }
    public VerboseOutputBuffer VerboseOutput { get; } = new();

    public sealed class VerboseOutputBuffer
    {
        const int MaxLines = 50;
        readonly Queue<string> _lines = new();

        public void Append(string markup)
        {
            if (_lines.Count >= MaxLines)
                _lines.Dequeue();
            _lines.Enqueue(markup);
        }

        public IReadOnlyList<string> Lines => _lines.ToArray();

        public void Clear() => _lines.Clear();
    }
}
