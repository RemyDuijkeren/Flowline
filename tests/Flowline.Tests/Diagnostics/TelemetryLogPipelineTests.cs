using Flowline.Diagnostics;
using Flowline.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;

namespace Flowline.Tests.Diagnostics;

public class TelemetryLogPipelineTests
{
    static readonly byte[] TestSalt = "test-salt"u8.ToArray();

    // Builds the log pipeline exactly as Program.cs does, with the exporter in the slot the Azure one
    // occupies, and asserts on what that exporter received.
    static List<LogRecord> Capture(FlowlineScrubber scrubber, Action<ILogger> write)
    {
        var exported = new List<LogRecord>();
        var factory = LoggerFactory.Create(b =>
        {
            b.ClearProviders().SetMinimumLevel(LogLevel.Debug);
            b.AddOpenTelemetry(o => FlowlineTelemetry.ConfigureLogs(o, scrubber,
                options => options.AddInMemoryExporter(exported)));
        });

        write(factory.CreateLogger("Flowline.Tests"));
        factory.Dispose();
        return exported;
    }

    [Fact]
    public void ADebugLineReachesTheExporter()
    {
        var exported = Capture(new FlowlineScrubber(TestSalt), l => l.LogDebug("git: {Output}", ".git"));

        exported.Should().ContainSingle();
        exported[0].FormattedMessage.Should().Contain(".git");
    }

    [Fact]
    public void AnExportedLineHasItsPropertiesScrubbed()
    {
        var scrubber = new FlowlineScrubber(TestSalt);
        scrubber.AddKnownValue("AcmeBankCustomizations");
        // What the command registers before anything is logged.
        scrubber.AddKnownPath("/home/remy/Projects/AcmeBankNV");

        var exported = Capture(scrubber, l => l.LogInformation(
            "Invocation: root={ProjectRoot} solutions={Solution} env={Url}",
            "/home/remy/Projects/AcmeBankNV", "AcmeBankCustomizations", "https://acmebank.crm4.dynamics.com"));

        var record = exported.Should().ContainSingle().Subject;
        record.FormattedMessage.Should().NotContain("remy").And.NotContain("AcmeBank").And.NotContain("acmebank.crm4");
        var attributes = record.Attributes!.ToDictionary(a => a.Key, a => a.Value as string);
        attributes["ProjectRoot"].Should().NotContain("remy");
        attributes["Solution"].Should().Be(FlowlineScrubber.Hash("AcmeBankCustomizations", TestSalt));
    }

    [Fact]
    public void AnAttachedExceptionIsRenderedAndScrubbedRatherThanExportedRaw()
    {
        var exported = Capture(new FlowlineScrubber(TestSalt), l => l.LogError(
            new InvalidOperationException("import failed for https://acmebank.crm4.dynamics.com"),
            "Command failed"));

        var record = exported.Should().ContainSingle().Subject;
        record.Exception.Should().BeNull("the exporter would serialise the raw message and stack trace");
        var detail = record.Attributes!.Single(a => a.Key == "exception.detail").Value as string;
        detail.Should().Contain(nameof(InvalidOperationException)).And.NotContain("acmebank");
    }
}
