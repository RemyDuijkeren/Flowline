using System.Diagnostics;
using Flowline.Diagnostics;
using Flowline.Logging;
using FluentAssertions;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Flowline.Tests.Diagnostics;

public class ScrubbingProcessorTests
{
    static readonly byte[] TestSalt = "test-salt"u8.ToArray();

    // Asserts on what the exporter received, not on the activity object: the processor's job is only
    // done if the rewritten value is the one that leaves.
    static List<Activity> Export(FlowlineScrubber scrubber, Action<Activity?> build, [System.Runtime.CompilerServices.CallerMemberName] string sourceName = "")
    {
        var exported = new List<Activity>();
        using var source = new ActivitySource($"Flowline.Tests.{sourceName}");
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .AddProcessor(new ScrubbingProcessor(scrubber))
            .AddInMemoryExporter(exported)
            .Build();

        using (var activity = source.StartActivity("command"))
            build(activity);

        provider!.ForceFlush();
        return exported;
    }

    [Fact]
    public void AnAbsoluteProjectPathTagExportsWithTheUserSegmentHashed()
    {
        var exported = Export(new FlowlineScrubber(TestSalt),
            a => a?.SetTag("project.root", "/home/remy/Projects/AcmeBank"));

        var value = (string?)exported.Single().GetTagItem("project.root");
        value.Should().NotContain("remy");
        value.Should().StartWith("/home/").And.EndWith("/Projects/AcmeBank");
    }

    [Fact]
    public void SolutionAndBranchTagsExportHashed()
    {
        var scrubber = new FlowlineScrubber(TestSalt);
        scrubber.AddKnownValue("AcmeBankCustomizations");
        scrubber.AddKnownValue("feature/acme-invoice-sync");

        var exported = Export(scrubber, a =>
        {
            a?.SetTag("project.solutions", "AcmeBankCustomizations");
            a?.SetTag("git.branch", "feature/acme-invoice-sync");
        });

        var activity = exported.Single();
        ((string?)activity.GetTagItem("project.solutions")).Should().Be(FlowlineScrubber.Hash("AcmeBankCustomizations", TestSalt));
        ((string?)activity.GetTagItem("git.branch")).Should().Be(FlowlineScrubber.Hash("feature/acme-invoice-sync", TestSalt));
    }

    // AE9: no tag name is withheld — a key no rule anticipated still exports, with its value scrubbed.
    [Fact]
    public void ATagNoRuleAnticipatedStillExportsWithItsValueScrubbed()
    {
        var exported = Export(new FlowlineScrubber(TestSalt),
            a => a?.SetTag("some.future.tag", "reached https://contoso.crm4.dynamics.com"));

        var value = (string?)exported.Single().GetTagItem("some.future.tag");
        value.Should().NotBeNull().And.NotContain("contoso");
        value.Should().StartWith("reached ");
    }

    [Fact]
    public void ATagValueWithNothingToHideExportsUnchanged()
    {
        var exported = Export(new FlowlineScrubber(TestSalt), a => a?.SetTag("os.arch", "X64"));

        ((string?)exported.Single().GetTagItem("os.arch")).Should().Be("X64");
    }

    [Fact]
    public void NullAndNonStringTagValuesAreLeftAlone()
    {
        var exported = Export(new FlowlineScrubber(TestSalt), a =>
        {
            a?.SetTag("ci", true);
            a?.SetTag("ci.platform", null);
            a?.SetTag("empty", "");
        });

        var activity = exported.Single();
        activity.GetTagItem("ci").Should().Be(true);
        activity.GetTagItem("ci.platform").Should().BeNull();
        activity.GetTagItem("empty").Should().Be("");
    }

    [Fact]
    public void EveryExportedSpanCarriesTheMachineDimension()
    {
        var scrubber = new FlowlineScrubber(TestSalt);

        var exported = Export(scrubber, a => a?.SetTag("k", "v"));

        ((string?)exported.Single().GetTagItem("machine.id")).Should().Be(scrubber.MachineId);
    }

    // AE5 / AE6 / KTD3: the exception is rendered and scrubbed once, and the rendered text carries the
    // type, the message and the trace for the whole inner chain.
    [Fact]
    public void ARenderedExceptionExportsWithItsTypeMessageAndTraceIntactAndTheUrlUnreadable()
    {
        var scrubber = new FlowlineScrubber(TestSalt);
        var rendered = scrubber.Scrub(ThrownException().ToString());

        var exported = Export(scrubber, a => a?.SetTag("exception.detail", rendered));

        var value = (string?)exported.Single().GetTagItem("exception.detail");
        value.Should().Contain(nameof(InvalidOperationException));
        value.Should().Contain("Import failed for");
        value.Should().Contain(nameof(ThrownException));
        value.Should().NotContain("contoso.crm4.dynamics.com");
    }

    [Fact]
    public void AnInnerExceptionIsRenderedAndScrubbedToo()
    {
        var scrubber = new FlowlineScrubber(TestSalt);

        var rendered = scrubber.Scrub(ThrownException().ToString())!;

        rendered.Should().Contain(nameof(HttpRequestException));
        rendered.Should().Contain("could not reach");
        rendered.Should().NotContain("fabrikam.crm4.dynamics.com");
    }

    // AE10 / R11: the log file and the telemetry are only comparable if one input hashes the same way
    // in both. Same scrubber, same salt, one rendered string.
    [Fact]
    public void TheLogPropertyAndTheExportedTagCarryIdenticalScrubbedText()
    {
        var scrubber = new FlowlineScrubber(TestSalt);
        var rendered = scrubber.Scrub(ThrownException().ToString());

        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new ScrubEnricher(scrubber))
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();
        logger.Error("Command failed: {ExceptionDetail}", rendered);

        var exported = Export(scrubber, a => a?.SetTag("exception.detail", rendered));

        var logged = (captured.Single().Properties["ExceptionDetail"] as ScalarValue)?.Value as string;
        logged.Should().Be((string?)exported.Single().GetTagItem("exception.detail"));
    }

    static Exception ThrownException()
    {
        try
        {
            try
            {
                throw new HttpRequestException("could not reach https://fabrikam.crm4.dynamics.com/api");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("Import failed for https://contoso.crm4.dynamics.com", inner);
            }
        }
        catch (Exception e)
        {
            return e;
        }
    }

    sealed class CapturingSink(List<LogEvent> captured) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => captured.Add(logEvent);
    }
}
