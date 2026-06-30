using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Core;

// Marker type: wraps Markup so VerboseFilterHook can identify verbose renderables by type.
// Markup is sealed in Spectre.Console 0.57.0 — composition required.
public sealed class VerboseMarkup(string message) : IRenderable
{
    private readonly IRenderable _markup = new Markup($"[dim]{Markup.Escape(message)}[/]");

    public Measurement Measure(RenderOptions options, int maxWidth) => _markup.Measure(options, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => _markup.Render(options, maxWidth);
}
