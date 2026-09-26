using System.Diagnostics;
using Flowline.Core.Diagnostics;
using Flowline.Diagnostics;
using Flowline.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using FluentAssertions;
using Xunit;

namespace Flowline.Tests.Diagnostics;

// Serial: these drive process-wide state (the root span and the teardown latch) that the exit hooks
// in Program.cs share.
[Collection(nameof(FlowlineTelemetryTests))]
[CollectionDefinition(nameof(FlowlineTelemetryTests), DisableParallelization = true)]
public class FlowlineTelemetryTests : IDisposable
{
    static readonly FlowlineScrubber Scrubber = new("test-salt"u8.ToArray());

    // Kept out of every test that is not specifically about the flush bound: building a provider
    // against the real connection string would send a span to the live resource from a test run.
    const string NoConnectionString = "";

    public void Dispose() => FlowlineTelemetry.ResetForTests();

    [Fact]
    public void WithConsentOff_NoProviderIsBuilt_ButTheRunStillHasARootSpan()
    {
        using var listener = RecordingListener();

        var built = FlowlineTelemetry.Start("deploy", Scrubber, "InstrumentationKey=irrelevant", consented: false);

        built.Should().BeFalse();
        Activity.Current.Should().NotBeNull("opting out must not cost the user the TraceId in their own log");
        Activity.Current!.DisplayName.Should().Be("deploy");
    }

    // The Azure Monitor exporter maps only Server and Consumer spans to requests; anything else lands in
    // dependencies, and the run never appears as an operation in Performance or Failures.
    [Fact]
    public void TheRootSpanIsAServerSpan_SoTheRunArrivesAsARequest()
    {
        using var listener = RecordingListener();

        FlowlineTelemetry.Start("deploy", Scrubber, NoConnectionString, consented: false);

        Activity.Current!.Kind.Should().Be(ActivityKind.Server);
        FlowlineTelemetry.Root.Should().BeSameAs(Activity.Current);
    }

    // The span source version is what App Insights shows as the application version, so it has to
    // carry the prerelease label to be filterable.
    [Fact]
    public void TheSpanSourceCarriesThePackageVersion() =>
        FlowlineActivitySource.Source.Version.Should().Be(FlowlineVersion.Display);

    [Fact]
    public void WithNoConnectionString_NoProviderIsBuilt_AndNothingThrows()
    {
        using var listener = RecordingListener();

        FlowlineTelemetry.Start("push", Scrubber, NoConnectionString, consented: true).Should().BeFalse();
        Activity.Current.Should().NotBeNull();
    }

    [Fact]
    public void WithAConnectionStringThatCannotBuildAProvider_TheCommandCarriesOnRegardless()
    {
        using var listener = RecordingListener();

        var built = FlowlineTelemetry.Start("pull", Scrubber, "this is not a connection string", consented: true);

        built.Should().BeFalse();
        Activity.Current.Should().NotBeNull();
    }

    [Fact]
    public void WithNoSalt_NoProviderIsBuilt_BecauseNothingCouldBeHashedBeforeItLeft()
    {
        using var listener = RecordingListener();

        var built = FlowlineTelemetry.Start("deploy", new FlowlineScrubber([]),
            "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://203.0.113.1/",
            consented: true);

        built.Should().BeFalse();
        Activity.Current.Should().NotBeNull("the local log still wants its TraceId");
    }

    [Fact]
    public void RecordFailure_PutsTheExitCodeAndTheScrubbedExceptionOnTheRootSpan()
    {
        using var listener = RecordingListener();
        FlowlineTelemetry.Start("deploy", Scrubber, NoConnectionString, consented: false);

        FlowlineTelemetry.RecordFailure(17, "System.InvalidOperationException", "import failed",
            "InvalidOperationException: import failed");

        var root = Activity.Current!;
        root.GetTagItem("exit.code").Should().Be(17);
        root.GetTagItem("exception.detail").Should().Be("InvalidOperationException: import failed");
        root.Status.Should().Be(ActivityStatusCode.Error);
    }

    // The exporter builds an Exceptions-table entry only from an event named "exception" carrying a
    // type and a non-empty message; a tag alone leaves that tab empty.
    [Fact]
    public void RecordFailure_AddsAnExceptionEvent_FromTheValuesItWasGiven()
    {
        using var listener = RecordingListener();
        FlowlineTelemetry.Start("deploy", Scrubber, NoConnectionString, consented: false);

        FlowlineTelemetry.RecordFailure(17, "System.InvalidOperationException", "import failed",
            "InvalidOperationException: import failed");

        var evt = Activity.Current!.Events.Should().ContainSingle().Subject;
        evt.Name.Should().Be("exception");
        var tags = evt.Tags.ToDictionary(t => t.Key, t => t.Value);
        tags["exception.type"].Should().Be("System.InvalidOperationException");
        tags["exception.message"].Should().Be("import failed");
        tags["exception.stacktrace"].Should().Be("InvalidOperationException: import failed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RecordFailure_WithNoMessage_UsesTheTypeSoTheExporterKeepsTheEvent(string? message)
    {
        using var listener = RecordingListener();
        FlowlineTelemetry.Start("deploy", Scrubber, NoConnectionString, consented: false);

        FlowlineTelemetry.RecordFailure(1, "System.Exception", message, "System.Exception");

        Activity.Current!.Events.Single().Tags.Should()
            .Contain(new KeyValuePair<string, object?>("exception.message", "System.Exception"));
    }

    // The guarantee the whole feature rests on, and the one nothing else would catch: processors run
    // in registration order, so an exporter added ahead of the scrubbing processor would send every
    // tag as written, at runtime, silently. The test exporter goes in the same slot the Azure one
    // occupies, so swapping those two lines in Start fails this.
    [Fact]
    public void TheProviderScrubsBeforeItExports()
    {
        var snapshots = new List<Dictionary<string, string?>>();
        var scrubber = new FlowlineScrubber("test-salt"u8.ToArray());
        scrubber.AddKnownValue("AcmeBankCustomizations");

        using var provider = FlowlineTelemetry
            .Configure(Sdk.CreateTracerProviderBuilder(), scrubber,
                b => b.AddProcessor(new SimpleActivityExportProcessor(new SnapshotExporter(snapshots))))
            .Build();

        using (var activity = FlowlineActivitySource.Source.StartActivity("deploy"))
        {
            activity?.SetTag("env.url", "https://acmebank.crm4.dynamics.com");
            activity?.SetTag("project.solutions", "AcmeBankCustomizations");
        }

        provider!.ForceFlush();

        var span = snapshots.Should().ContainSingle().Subject;
        span["env.url"].Should().NotContain("acmebank");
        span["project.solutions"].Should().Be(FlowlineScrubber.Hash("AcmeBankCustomizations", "test-salt"u8.ToArray()));
        span["machine.id"].Should().Be(scrubber.MachineId);
    }

    // Copies the tag values at the moment of export. An exporter that stored the Activity itself would
    // show the scrubbed values whatever the processor order was, because the processor mutates that
    // same object — which is exactly how this test could pass while the guarantee was broken.
    sealed class SnapshotExporter(List<Dictionary<string, string?>> snapshots) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                snapshots.Add(activity.TagObjects.ToDictionary(t => t.Key, t => t.Value as string));
            return ExportResult.Success;
        }
    }

    [Fact]
    public void RecordExit_PutsTheExitCodeOnTheRootSpanWithoutMarkingItFailed()
    {
        using var listener = RecordingListener();
        FlowlineTelemetry.Start("status", Scrubber, NoConnectionString, consented: false);

        FlowlineTelemetry.RecordExit(0);

        Activity.Current!.GetTagItem("exit.code").Should().Be(0);
        Activity.Current.Status.Should().Be(ActivityStatusCode.Unset);
        Activity.Current.Events.Should().BeEmpty("a cancelled run is not an exception");
    }

    [Fact]
    public void RecordInvocation_PutsWhatWasTypedOnTheRootSpan()
    {
        using var listener = RecordingListener();
        FlowlineTelemetry.Start("init", Scrubber, NoConnectionString, consented: false);

        FlowlineTelemetry.RecordInvocation("init --help");

        Activity.Current!.GetTagItem("args").Should().Be("init --help",
            "a help invocation is otherwise indistinguishable from the real command");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordInvocation_IgnoresAnEmptyCommandLine(string? args)
    {
        using var listener = RecordingListener();
        FlowlineTelemetry.Start("flowline", Scrubber, NoConnectionString, consented: false);

        FlowlineTelemetry.RecordInvocation(args);

        Activity.Current!.GetTagItem("args").Should().BeNull();
    }

    [Fact]
    public void RecordingAnInvocationWithNoRunStartedDoesNotThrow() =>
        FlowlineTelemetry.RecordInvocation("init --help");

    [Fact]
    public void RecordingAFailureWithNoRunStartedDoesNotThrow()
    {
        FlowlineTelemetry.RecordFailure(1, "System.Exception", "boom", "boom");
        FlowlineTelemetry.RecordExit(1);
    }

    [Fact]
    public void FlushWithNoProviderBuiltDoesNotThrow()
    {
        using var listener = RecordingListener();
        FlowlineTelemetry.Start("status", Scrubber, NoConnectionString, consented: false);

        FlowlineTelemetry.Flush();
    }

    [Fact]
    public void FlushIsIdempotent_AndEndsTheRootSpanOnce()
    {
        var ended = new List<Activity>();
        using var listener = RecordingListener(ended);
        FlowlineTelemetry.Start("deploy", Scrubber, NoConnectionString, consented: false);

        FlowlineTelemetry.Flush();
        FlowlineTelemetry.Flush();
        FlowlineTelemetry.Flush();

        ended.Should().ContainSingle();
        Activity.Current.Should().BeNull();
    }

    // R7/AE11: the guarantee that matters. The exporter's own teardown runs for seconds whatever the
    // endpoint does (see docs/solutions/azure-monitor-otlp-exporter-flush-behaviour.md), so the bound
    // has to be enforced here rather than by the timeout the SDK takes.
    [Fact]
    public void FlushAgainstAnEndpointThatNeverAnswersReturnsWithinItsBound()
    {
        using var listener = RecordingListener();
        // TEST-NET-3: routable, and nothing answers.
        FlowlineTelemetry.Start("deploy", Scrubber,
            "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://203.0.113.1/",
            consented: true).Should().BeTrue();

        var stopwatch = Stopwatch.StartNew();
        FlowlineTelemetry.Flush(boundMs: 500);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the bound passed in is what holds, not the default");
    }

    // The listener Program.cs registers unconditionally, which is what makes Activity.Current non-null
    // when no provider exists (D5/KTD7).
    static ActivityListener RecordingListener(List<Activity>? ended = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Flowline.CLI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => ended?.Add(a),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
