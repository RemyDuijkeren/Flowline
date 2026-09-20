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
            if (value is not ScalarValue { Value: string str }) continue;

            var scrubbed = scrubber.Scrub(str);
            if (!ReferenceEquals(scrubbed, str) && scrubbed != str)
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, scrubbed));
        }
    }
}
