using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Core;

public sealed class VerboseFilterHook(bool isVerbose) : IRenderHook
{
    public IEnumerable<IRenderable> Process(RenderOptions options, IEnumerable<IRenderable> renderables)
    {
        foreach (var renderable in renderables)
        {
            if (renderable is VerboseMarkup && !isVerbose)
                continue;
            yield return renderable;
        }
    }
}
