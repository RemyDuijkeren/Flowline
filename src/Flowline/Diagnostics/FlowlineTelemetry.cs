using Flowline.Core.Diagnostics;
using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Flowline.Logging;
using Flowline.Utils;
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
    // A backstop, not the expected cost. The teardown below waits for the export to genuinely finish,
    // which it can only do because the exporter is given a short network timeout and no retries
    // (ApplyExporterOptions): a healthy send settles in about two seconds and a dead endpoint gives up
    // after three, so this is never reached in practice. It exists so that no combination of proxy,
    // firewall and DNS can hold a command open indefinitely.
    //
    // Cutting the wait short instead is what produced duplicates: a send the server had already
    // accepted was abandoned before its response was read, left in the exporter's offline store, and
    // re-sent by a later run — observed arriving up to four times.
    const int FlushBackstopMs = 8000;

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

        // The exporter defaults to a 100-second network timeout and three exponential retries, which
        // is right for a service and wrong for a command someone is waiting on: it makes "wait until
        // the export is really done" a promise that can take minutes. One short attempt instead, so
        // the teardown below can wait for a real answer rather than abandoning the send. A failure is
        // written to the offline store and forwarded by a later run, which is what that store is for.
        o.Retry.NetworkTimeout = TimeSpan.FromSeconds(3);
        o.Retry.MaxRetries = 0;

        // A send the process abandoned on its way out is written here and forwarded by a later run,
        // which is what stops a run that overran its bound losing its telemetry outright. The default
        // location is a shared one under the system temp directory; keeping it beside the logs and the
        // salt means it belongs to this user, is discoverable, and goes away with the rest of
        // Flowline's state. A CI agent's ephemeral disk discards it, which is the right outcome there.
        try { o.StorageDirectory = Path.Combine(FlowlineStoragePaths.GetStorageRoot(), "telemetry-spool"); }
        catch { } // Intentional: an unresolvable storage root leaves the exporter's own default in place.
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

    /// <summary>Records what was actually typed, so a run is identifiable by more than its command name.</summary>
    /// <remarks>
    /// <c>flowline init --help</c> and a real <c>flowline init</c> produce the same span name, and the
    /// command pipeline that would otherwise log the arguments never runs for a help invocation. The
    /// value is already redacted for secrets; the tag is scrubbed on export like every other, which is
    /// what lets it be set before the solution and branch names are even known.
    /// </remarks>
    public static void RecordInvocation(string? argsRedacted)
    {
        if (string.IsNullOrWhiteSpace(argsRedacted)) return;

        try { s_root?.SetTag("args", argsRedacted); }
        catch { } // Intentional: recording an invocation must not become its outcome (D4).
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
    /// Ends the root span and waits for the export to finish, giving up only at
    /// <paramref name="boundMs"/>. Idempotent: the three exit hooks all call it and only the first
    /// does the work.
    /// </summary>
    public static void Flush(int boundMs = FlushBackstopMs)
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
            // settles — the HTTP send is what Dispose waits on. So the wait that matters is this one,
            // and it waits for the send to genuinely finish rather than giving up at a deadline: the
            // exporter is configured to make one short attempt, so "finished" arrives quickly whether
            // it worked or not. Abandoning it instead is what produced duplicate deliveries.
            //
            // Still on thread-pool (background) threads, and still capped, so no combination of proxy,
            // firewall and DNS can hold a command open indefinitely.
            //
            // The two pipelines tear down side by side rather than one after the other. Run in
            // sequence, the spans consume the whole budget and the log records never leave at all.
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
