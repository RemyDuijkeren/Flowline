using System.Diagnostics;
using Flowline.Diagnostics;
using Flowline.Logging;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Flowline.Tests;

public class ActivityTraceEnricherTests
{
    static ActivityTraceEnricherTests()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Flowline.CLI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
    }

    [Fact]
    public void Enrich_WhenActivityIsActive_AddsTraceIdMatchingCurrentActivity()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new ActivityTraceEnricher())
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        using var activity = FlowlineActivitySource.Source.StartActivity("test-op");
        activity.Should().NotBeNull("ActivityListener must be registered before ActivitySource can produce non-null activities");

        logger.Information("test message");

        captured.Should().ContainSingle();
        captured[0].Properties.Should().ContainKey("TraceId");
        var traceId = (captured[0].Properties["TraceId"] as ScalarValue)?.Value as string;
        traceId.Should().Be(activity!.TraceId.ToString());
    }

    [Fact]
    public void Enrich_WhenNoActivityIsActive_DoesNotAddTraceIdProperty()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new ActivityTraceEnricher())
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        // No StartActivity call — Activity.Current is null in this execution context
        logger.Information("no activity");

        captured.Should().ContainSingle();
        captured[0].Properties.Should().NotContainKey("TraceId");
    }

    sealed class CapturingSink(List<LogEvent> captured) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => captured.Add(logEvent);
    }
}
