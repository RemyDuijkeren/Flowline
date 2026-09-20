using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Flowline.Logging;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Flowline.Diagnostics;

/// <summary>
/// Owns the run's root span, the export provider, and the one bounded teardown that every exit path
/// calls.
/// </summary>
/// <remarks>
/// Static because the three exit hooks in <c>Program.cs</c> — after <c>RunAsync</c>, the
/// <c>ProcessExit</c> handler, and the SIGTERM registration — have nothing else in common to hang
/// this off, and the CLI is one run per process.
///
/// <para>Nothing here is allowed to fail a command (D4), so every entry point swallows.</para>
/// </remarks>
public static class FlowlineTelemetry
{
    // The exporter's own teardown runs for about three seconds whatever happens, so this bound is what
    // every command actually pays, not just a blocked one. Measured against the live resource: a span
    // sent with a 250 ms bound never arrived, and one sent with 700 ms or more always did. 1.5 s is the
    // margin over the smallest value observed to work, and it is the number to revisit if the cost of
    // a command matters more than the odd lost span.
    const int DefaultFlushBoundMs = 1500;

    static TracerProvider? s_provider;
    static Activity? s_root;
    static int s_torndown;

    /// <summary>Starts the run's root span and, when consent allows it, the exporter behind it.</summary>
    /// <returns>Whether a provider was built, which is the same condition the first-run disclosure fires on.</returns>
    public static bool Start(string activityName, FlowlineScrubber scrubber) =>
        Start(activityName, scrubber, TelemetryConnectionString.Value, TelemetryConsent.IsEnabled());

    internal static bool Start(string activityName, FlowlineScrubber scrubber, string? connectionString, bool consented)
    {
        // The provider registers its own ActivityListener when it is built, and a listener only sees
        // activities started after it. So the provider goes up first, or the run's own root span is
        // the one span that never gets exported.
        if (consented && !string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                // The exporter's own statsbeat sends usage metrics about the SDK on its own schedule,
                // and waits for them on the way out — measured at about two of the four seconds a
                // teardown took. Set in-process only; it does not touch the user's environment.
                Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");

                s_provider = Sdk.CreateTracerProviderBuilder()
                    .AddSource(FlowlineActivitySource.Source.Name)
                    // Built from empty on purpose. The default resource detectors put the machine's
                    // host name on every item, which arrives as cloud_RoleInstance — a plain machine
                    // identifier, and the one thing R13 says must only ever be the salted value.
                    .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService(
                        serviceName: "flowline",
                        serviceVersion: FlowlineActivitySource.Source.Version,
                        serviceInstanceId: scrubber.MachineId))
                    .AddProcessor(new ScrubbingProcessor(scrubber))
                    .AddAzureMonitorTraceExporter(o =>
                    {
                        o.ConnectionString = connectionString;
                        // The exporter rate-limits to five traces a second by default, which arrived
                        // stamped as a 25% sample. Flowline's volume does not need sampling, and a
                        // sampled dataset makes "which exit code actually fires" harder to answer.
                        o.TracesPerSecond = null;
                        o.SamplingRatio = 1.0F;
                        // Traces only. These three are metric signals the plan does not send, and each
                        // one is work a short-lived CLI process pays for on the way out.
                        o.EnableLiveMetrics = false;
                        o.EnableStandardMetrics = false;
                        o.EnablePerformanceCounters = false;
                    })
                    .Build();
            }
            catch
            {
                // A provider that will not build leaves the command running normally, without one (D4).
                s_provider = null;
            }
        }

        // Started either way, and deliberately outside the consent check (D5/KTD7): the root span is
        // what gives the local log file its TraceId, so opting out must not cost a user correlation in
        // their own logs. The ActivityListener in Program.cs is what records it when no provider exists.
        s_root = FlowlineActivitySource.Source.StartActivity(activityName);

        return s_provider is not null;
    }

    public static void RecordExit(int exitCode) => s_root?.SetTag("exit.code", exitCode);

    /// <summary>Records the failure on the run's root span, from the one place that already rendered and scrubbed it.</summary>
    public static void RecordFailure(int exitCode, string? scrubbedException)
    {
        if (s_root is null) return;

        s_root.SetTag("exit.code", exitCode);
        s_root.SetStatus(ActivityStatusCode.Error);
        if (!string.IsNullOrEmpty(scrubbedException))
            s_root.SetTag("exception.detail", scrubbedException);
    }

    /// <summary>
    /// Ends the root span and settles the export, never taking longer than <paramref name="boundMs"/>.
    /// Idempotent: the three exit hooks all call it and only the first does the work.
    /// </summary>
    public static void Flush(int boundMs = DefaultFlushBoundMs)
    {
        if (Interlocked.Exchange(ref s_torndown, 1) != 0) return;

        try
        {
            s_root?.Dispose();
            s_root = null;

            var provider = s_provider;
            s_provider = null;
            if (provider is null) return;

            // ForceFlush returns as soon as the batch reaches the exporter, not when the transmission
            // settles — the HTTP send is what Dispose waits on, and its timeout is not ours to set. So
            // the bound is enforced here, around both, on a thread-pool (background) thread: an
            // overrunning send is abandoned rather than held onto, and never delays process exit.
            var teardown = Task.Run(() =>
            {
                provider.ForceFlush(boundMs);
                provider.Dispose();
            });
            teardown.Wait(boundMs);
        }
        catch
        {
            // A blocked endpoint, a refusing proxy or a provider that fails to tear down must not change
            // the command's exit code or its output (R7).
        }
    }

    // Test seam: the exit hooks are process-wide, so a test that exercised Flush would otherwise leave
    // the next one with a torn-down state.
    internal static void ResetForTests()
    {
        s_root?.Dispose();
        s_root = null;
        s_provider = null;
        s_torndown = 0;
    }
}
