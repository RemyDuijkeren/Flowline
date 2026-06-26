namespace Flowline.Utils;

public sealed class SubprocessBuffer
{
    const int MaxLines = 50;
    readonly Queue<string> _lines = new();

    public void Append(string line)
    {
        if (_lines.Count >= MaxLines)
            _lines.Dequeue();
        _lines.Enqueue(line);
    }

    public IReadOnlyList<string> Lines => _lines.ToArray();
}
