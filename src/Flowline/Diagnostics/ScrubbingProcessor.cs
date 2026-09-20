using System.Diagnostics;
using Flowline.Logging;
using OpenTelemetry;

namespace Flowline.Diagnostics;

/// <summary>
/// Rewrites every string tag value through <see cref="FlowlineScrubber"/> on the way out, and stamps
/// the per-machine dimension.
/// </summary>
/// <remarks>
/// No tag name is withheld (R4/D3): an early-phase failure report missing the one tag that explains it
/// costs more than the tag costs to send. The protection is that the value is non-identifying by the
/// time the exporter sees it, which is the same protection the log file relies on.
/// </remarks>
public sealed class ScrubbingProcessor(FlowlineScrubber scrubber) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        try
        {
            foreach (var (key, value) in data.TagObjects.ToList())
                if (value is string str)
                    data.SetTag(key, scrubber.Scrub(str));

            data.SetTag("machine.id", scrubber.MachineId);
        }
        catch
        {
            // Telemetry never fails a command (D4). A span that could not be rewritten is dropped rather
            // than exported unscrubbed.
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
