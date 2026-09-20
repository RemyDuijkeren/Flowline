using Flowline.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Flowline.Diagnostics;

/// <summary>
/// Rewrites every part of a log record through <see cref="FlowlineScrubber"/> on the way out.
/// </summary>
/// <remarks>
/// The log file's own scrubbing is a Serilog enricher, and this pipeline never sees a Serilog event —
/// it receives the original <c>ILogger</c> call. So the rules have to run again here, over the same
/// component and the same salt, or the exported copy of a line would carry what the local copy hides.
/// </remarks>
public sealed class ScrubbingLogProcessor(FlowlineScrubber scrubber) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        try
        {
            data.FormattedMessage = scrubber.Scrub(data.FormattedMessage);
            data.Body = scrubber.Scrub(data.Body);

            if (data.Attributes is { } attributes)
            {
                var scrubbed = new List<KeyValuePair<string, object?>>(attributes.Count);
                foreach (var (key, value) in attributes)
                    scrubbed.Add(new(key, value is string s ? scrubber.Scrub(s) : value));
                data.Attributes = scrubbed;
            }

            // The raw object behind the attributes. Exporters read Attributes, but leaving the source
            // in place keeps an unscrubbed copy of every value attached to the record.
            data.State = null;

            // An exception attached to a log call carries its message and stack trace, and the
            // exporter serialises both. Rendered and scrubbed into an attribute instead, matching what
            // the command's own failure path does (KTD3).
            if (data.Exception is { } exception)
            {
                var rendered = scrubber.Scrub(exception.ToString());
                data.Exception = null;
                data.Attributes = [.. data.Attributes ?? [], new("exception.detail", rendered)];
            }
        }
        catch
        {
            // Telemetry never fails a command (D4). A record that could not be rewritten is emptied
            // rather than exported as it stands.
            data.FormattedMessage = "<scrub-failed>";
            data.Body = "<scrub-failed>";
            data.Attributes = [];
            data.State = null;
            data.Exception = null;
        }
    }
}
