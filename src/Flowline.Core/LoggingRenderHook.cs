using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Core;

public sealed class LoggingRenderHook(ILogger<LoggingRenderHook> logger) : IRenderHook
{
    public IEnumerable<IRenderable> Process(RenderOptions options, IEnumerable<IRenderable> renderables)
    {
        foreach (var renderable in renderables)
        {
            yield return renderable;

            try
            {
                if (renderable is Markup or Tree or Panel or Table)
                {
                    var text = string.Concat(renderable.Render(options, int.MaxValue)
                        .Select(s => s.Text)).Trim();

                    var level = DetectLevel(text);
                    if (level.HasValue)
                        logger.Log(level.Value, "{Message}", text);
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Debug, ex, "LoggingRenderHook: render extraction failed");
            }
        }
    }

    private static LogLevel? DetectLevel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.StartsWith(FlowlineConsoleExtensions.OkPrefix) ||
            text.StartsWith(FlowlineConsoleExtensions.InfoPrefix) ||
            text.StartsWith(FlowlineConsoleExtensions.SkipPrefix) ||
            text.StartsWith(FlowlineConsoleExtensions.DonePrefix))
            return LogLevel.Information;
        if (text.StartsWith(FlowlineConsoleExtensions.WarningPrefix)) return LogLevel.Warning;
        if (text.StartsWith(FlowlineConsoleExtensions.ErrorPrefix)) return LogLevel.Error;
        return LogLevel.Debug;
    }
}
