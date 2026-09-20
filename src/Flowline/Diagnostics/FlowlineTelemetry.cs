using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Flowline.Logging;
using OpenTelemetry;
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
    // U1 measured this against a blackholed endpoint: the exporter's own worst case is about four
    // seconds, so without a bound of our own every command would pay it on exit.
    const int DefaultFlushBoundMs = 3000;

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
                s_provider = Sdk.CreateTracerProviderBuilder()
                    .AddSource(FlowlineActivitySource.Source.Name)
                    .AddProcessor(new ScrubbingProcessor(scrubber))
                    .AddAzureMonitorTraceExporter(o => o.ConnectionString = connectionString)
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
