using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Core;

public sealed class VerboseFilterHook(FlowlineRuntimeOptions options) : IRenderHook
{
    public IEnumerable<IRenderable> Process(RenderOptions renderOptions, IEnumerable<IRenderable> renderables)
    {
        foreach (var renderable in renderables)
        {
            if (renderable is VerboseMarkup && !options.IsVerbose)
                continue;
            yield return renderable;
        }
    }
}
