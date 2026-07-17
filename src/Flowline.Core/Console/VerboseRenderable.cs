using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Core.Console;

// Marker type: wraps a renderable so VerboseFilterHook can identify verbose renderables by type
// and LoggingRenderHook can still log their content unconditionally.
// Markup is sealed in Spectre.Console 0.57.0 — composition required for the string case.
public sealed class VerboseRenderable : IRenderable
{
    private readonly IRenderable _inner;
    private readonly bool _appendLineBreak;

    public VerboseRenderable(string message)
    {
        _inner = new Markup($"[dim]{Markup.Escape(message)}[/]");
        _appendLineBreak = true;
    }

    public VerboseRenderable(IRenderable renderable)
    {
        _inner = renderable;
        _appendLineBreak = false;
    }

    public Measurement Measure(RenderOptions options, int maxWidth) => _inner.Measure(options, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        foreach (var segment in _inner.Render(options, maxWidth))
            yield return segment;
        if (_appendLineBreak)
            yield return Segment.LineBreak;
    }
}
