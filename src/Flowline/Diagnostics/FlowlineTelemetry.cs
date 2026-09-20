using Flowline.Core.Diagnostics;
using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Flowline.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
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
    // every command actually pays, not just a blocked one.
    //
    // Measured against the live resource. With one signal, a span sent with a 250 ms bound never
    // arrived and 700 ms or more always did. Adding the log pipeline changed that: at 1.5 s, six
    // identical runs delivered five, and a run abandoned after the server had already accepted it was
    // retried from the exporter's offline store, arriving twice. 3 s is what two signals need. The
    // way to get this back down is to stop racing process exit at all — spool to disk and let the
    // next run forward it — not to shorten the bound again.
    const int DefaultFlushBoundMs = 3000;

    static TracerProvider? s_provider;
    static IDisposable? s_logPipeline;
    static Activity? s_root;
    static int s_torndown;

    /// <summary>Starts the run's root span and, when consent allows it, the exporter behind it.</summary>
    /// <returns>Whether a provider was built, which is the same condition the first-run disclosure fires on.</returns>
    public static bool Start(string activityName, FlowlineScrubber scrubber) =>
        Start(activityName, scrubber, TelemetryConnectionString.Value, TelemetryConsent.IsEnabled());

    internal static bool Start(string activityName, FlowlineScrubber scrubber, string? connectionString, bool consented)
    {
        // The provider goes up before the root span: it registers its own ActivityListener when built,
        // and a listener only ever sees activities started after it.
        //
        // No salt, no export. Everything that leaves is supposed to be hashed, and a scrubber without
        // a salt cannot hash — it would send a precomputable digest while the disclosure promises
        // otherwise. Local logging still runs; it just carries the unsalted token instead.
        if (consented && !string.IsNullOrWhiteSpace(connectionString) && scrubber.HasSalt)
        {
            try
            {
                s_provider = Configure(Sdk.CreateTracerProviderBuilder(), scrubber, builder =>
                        builder.AddAzureMonitorTraceExporter(o => ApplyExporterOptions(o, connectionString)))
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
        try
        {
            s_root = FlowlineActivitySource.Source.StartActivity(activityName);
        }
        catch
        {
            // Guarded like everything else here: this runs before any command does, so an exception
            // escaping it would fail the launch outright rather than lose a span (D4).
            s_root = null;
        }

        return s_provider is not null;
    }

    /// <summary>
    /// Registers the scrubbing processor and then the exporter, in that order, for both the real
    /// provider and the test that proves the order.
    /// </summary>
    /// <remarks>
    /// The order is the whole guarantee: processors run in registration order, so an exporter added
    /// ahead of the scrubbing processor would send every tag as written. Nothing about that failure is
    /// visible at runtime, which is why the exporter is supplied by the caller rather than named here
    /// — it puts the test's exporter in exactly the slot the real one occupies.
    /// </remarks>
    internal static TracerProviderBuilder Configure(
        TracerProviderBuilder builder, FlowlineScrubber scrubber, Func<TracerProviderBuilder, TracerProviderBuilder> addExporter)
    {
        var configured = builder
            .AddSource(FlowlineActivitySource.Source.Name)
            .SetResourceBuilder(BuildResource(scrubber))
            .AddProcessor(new ScrubbingProcessor(scrubber));

        return addExporter(configured);
    }

    /// <summary>
    /// Registers the log-scrubbing processor and then the log exporter, in that order, for the same
    /// reason and with the same guarantee as <see cref="Configure"/>.
    /// </summary>
    internal static void ConfigureLogs(
        OpenTelemetryLoggerOptions options, FlowlineScrubber scrubber, Action<OpenTelemetryLoggerOptions> addExporter)
    {
        // The exporter reads FormattedMessage, so the message has to be rendered before it is scrubbed
        // — and ParseStateValues turns the structured properties into attributes the processor can
        // rewrite one by one rather than as one opaque blob.
        options.IncludeFormattedMessage = true;
        options.ParseStateValues = true;
        // Scopes would carry values nothing in this pipeline scrubs.
        options.IncludeScopes = false;

        options.SetResourceBuilder(BuildResource(scrubber));
        options.AddProcessor(new ScrubbingLogProcessor(scrubber));
        addExporter(options);
    }

    /// <summary>The resource every signal shares.</summary>
    /// <remarks>
    /// Built from empty on purpose. The default resource detectors put the machine's host name on
    /// every item, which arrives as cloud_RoleInstance — a plain machine identifier, and the one thing
    /// R13 says must only ever be the salted value.
    /// </remarks>
    internal static ResourceBuilder BuildResource(FlowlineScrubber scrubber) =>
        ResourceBuilder.CreateEmpty().AddService(
            serviceName: "flowline",
            serviceVersion: FlowlineActivitySource.Source.Version,
            serviceInstanceId: scrubber.MachineId);

    /// <summary>Applies the exporter settings both signals share.</summary>
    internal static void ApplyExporterOptions(AzureMonitorExporterOptions o, string connectionString)
    {
        // Before the first exporter is built, whichever signal builds first. The exporter's own
        // statsbeat sends usage metrics about the SDK on its own schedule and waits for them on the
        // way out — measured at about two of the four seconds a teardown took. Not persisted
        // anywhere, though any pac/git/dotnet subprocess started after this inherits it, which at
        // worst disables their statsbeat too.
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");

        o.ConnectionString = connectionString;
        // The exporter rate-limits to five traces a second by default, which arrived stamped as a 25%
        // sample. Flowline's volume does not need sampling, and a sampled dataset makes "which exit
        // code actually fires" harder to answer.
        o.TracesPerSecond = null;
        o.SamplingRatio = 1.0F;
        // These three are metric signals the plan does not send, and each one is work a short-lived
        // CLI process pays for on the way out.
        o.EnableLiveMetrics = false;
        o.EnableStandardMetrics = false;
        o.EnablePerformanceCounters = false;
    }

    /// <summary>
    /// Hands the logging pipeline's lifetime to the same bounded teardown the spans use, since the
    /// exported log records only leave when its provider is disposed.
    /// </summary>
    internal static void AttachLogPipeline(IDisposable loggerFactory) => s_logPipeline = loggerFactory;

    public static void RecordExit(int exitCode)
    {
        try { s_root?.SetTag("exit.code", exitCode); }
        catch { } // Intentional: recording an outcome must not become the command's outcome (D4).
    }

    /// <summary>Records the failure on the run's root span, from the one place that already rendered and scrubbed it.</summary>
    public static void RecordFailure(int exitCode, string? scrubbedException)
    {
        // Captured once. This runs on the main thread from the exception handler while Flush can null
        // the field from the SIGTERM handler on a thread-pool callback, and re-reading it would put a
        // NullReferenceException into the CLI's own error-handling path.
        var root = s_root;
        if (root is null) return;

        try
        {
            root.SetTag("exit.code", exitCode);
            root.SetStatus(ActivityStatusCode.Error);
            if (!string.IsNullOrEmpty(scrubbedException))
                root.SetTag("exception.detail", scrubbedException);
        }
        catch { } // Intentional: as above.
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
            // Captured and cleared before the root span is ended: a throw out of Dispose would
            // otherwise skip every statement below it, and because the latch above has already
            // flipped, no other exit hook would retry — the provider and its buffered spans would be
            // dropped silently.
            var provider = s_provider;
            s_provider = null;

            var logPipeline = s_logPipeline;
            s_logPipeline = null;

            var root = s_root;
            s_root = null;
            try { root?.Dispose(); }
            catch { } // Intentional: ending the span must not cost us the export below.

            if (provider is null && logPipeline is null) return;

            // ForceFlush returns as soon as the batch reaches the exporter, not when the transmission
            // settles — the HTTP send is what Dispose waits on, and its timeout is not ours to set. So
            // the bound is enforced here, on thread-pool (background) threads: an overrunning send is
            // abandoned rather than held onto, and never delays process exit.
            //
            // The two pipelines tear down side by side rather than one after the other. Run in
            // sequence, the spans consume the whole bound and the log records never leave at all.
            var teardown = new[]
            {
                Task.Run(() =>
                {
                    provider?.ForceFlush(boundMs);
                    provider?.Dispose();
                }),
                // Disposing the logger factory is what flushes the exported log records; the Serilog
                // logger behind it is not owned by that factory, so the local file is unaffected.
                Task.Run(() => logPipeline?.Dispose()),
            };
            Task.WaitAll(teardown, boundMs);
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
        s_logPipeline = null;
        s_root?.Dispose();
        s_root = null;
        s_provider = null;
        s_torndown = 0;
    }
}
