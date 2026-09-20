using Serilog.Core;
using Serilog.Events;

namespace Flowline.Logging;

/// <summary>Runs every log property through <see cref="FlowlineScrubber"/> before it reaches the file.</summary>
/// <remarks>
/// Replaces the separate URL and email enrichers: those each owned one rule, which meant the log file
/// and the telemetry could only apply the same five rules by keeping two implementations in step.
/// </remarks>
public sealed class ScrubEnricher(FlowlineScrubber scrubber) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (key, value) in logEvent.Properties.ToList())
        {
            // Not just strings. A value logged as an object renders through ToString() into the same
            // file, and WriteExceptionContext puts arbitrary ex.Data values through this path — so a
            // Uri or a record carrying an environment URL would otherwise reach the log unscrubbed,
            // in the file the disclosure calls a preview of what was sent.
            var rendered = value is ScalarValue { Value: string str } ? str : Render(value);
            if (rendered is null) continue;

            var scrubbed = scrubber.Scrub(rendered);
            if (scrubbed is null || scrubbed == rendered) continue;

            // A structured value only collapses to a string when scrubbing actually changed it, so an
            // ordinary structured log keeps its shape.
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, scrubbed));
        }
    }

    static string? Render(LogEventPropertyValue value)
    {
        try
        {
            if (value is ScalarValue scalar)
                return scalar.Value?.ToString();

            using var writer = new StringWriter();
            value.Render(writer);
            return writer.ToString();
        }
        catch
        {
            // A value whose own rendering throws is left alone: it never reaches the file either.
            return null;
        }
    }
}
