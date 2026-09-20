using System.Diagnostics;
using Flowline.Diagnostics;
using Flowline.Logging;
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

        FlowlineTelemetry.RecordFailure(17, "InvalidOperationException: import failed");

        var root = Activity.Current!;
        root.GetTagItem("exit.code").Should().Be(17);
        root.GetTagItem("exception.detail").Should().Be("InvalidOperationException: import failed");
        root.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public void RecordExit_PutsTheExitCodeOnTheRootSpanWithoutMarkingItFailed()
    {
        using var listener = RecordingListener();
        FlowlineTelemetry.Start("status", Scrubber, NoConnectionString, consented: false);

        FlowlineTelemetry.RecordExit(0);

        Activity.Current!.GetTagItem("exit.code").Should().Be(0);
        Activity.Current.Status.Should().Be(ActivityStatusCode.Unset);
    }

    [Fact]
    public void RecordingAFailureWithNoRunStartedDoesNotThrow()
    {
        FlowlineTelemetry.RecordFailure(1, "boom");
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

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
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
