using Flowline.Logging;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Flowline.Tests;

public class EmailScrubEnricherTests
{
    static readonly byte[] TestSalt = "test-salt"u8.ToArray();

    // Hash

    [Theory]
    [InlineData("contoso.com")]
    [InlineData("remy")]
    public void Hash_ReturnsDeterministicEightCharLowercaseHex(string value)
    {
        var hash = EmailScrubEnricher.Hash(value, TestSalt);
        hash.Should().HaveLength(8);
        hash.Should().MatchRegex("^[0-9a-f]+$");
        EmailScrubEnricher.Hash(value, TestSalt).Should().Be(hash);
    }

    [Fact]
    public void Hash_IsCaseInsensitive()
    {
        EmailScrubEnricher.Hash("Remy", TestSalt).Should().Be(EmailScrubEnricher.Hash("remy", TestSalt));
    }

    [Fact]
    public void Hash_DifferentValues_ProduceDifferentHashes()
    {
        EmailScrubEnricher.Hash("a.example.com", TestSalt)
            .Should().NotBe(EmailScrubEnricher.Hash("b.example.com", TestSalt));
    }

    [Fact]
    public void Hash_DifferentSalts_ProduceDifferentHashes()
    {
        EmailScrubEnricher.Hash("remy", TestSalt)
            .Should().NotBe(EmailScrubEnricher.Hash("remy", "other-salt"u8.ToArray()));
    }

    // Enricher — structured property containing an email

    [Fact]
    public void Enrich_ReplacesEmailWithNonEmailShapedToken()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new EmailScrubEnricher(TestSalt))
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        logger.Information("login={UserEmail}", "remy@contoso.com");

        var props = captured.Single().Properties;
        props.Should().ContainKey("UserEmail");
        var value = (props["UserEmail"] as ScalarValue)?.Value as string;
        value.Should().Be($"usr_{EmailScrubEnricher.Hash("remy", TestSalt)}.tnt_{EmailScrubEnricher.Hash("contoso.com", TestSalt)}");
        value.Should().NotContain("@");
        value.Should().NotContain("remy");
    }

    // Enricher — email embedded in a longer string

    [Fact]
    public void Enrich_ReplacesEmailEmbeddedInString()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new EmailScrubEnricher(TestSalt))
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        logger.Information("{Message}", "Connected as remy@contoso.com to environment");

        var props = captured.Single().Properties;
        var value = (props["Message"] as ScalarValue)?.Value as string;
        value.Should().NotContain("@contoso.com");
        value.Should().Contain($"usr_{EmailScrubEnricher.Hash("remy", TestSalt)}.tnt_{EmailScrubEnricher.Hash("contoso.com", TestSalt)}");
        value.Should().Contain("Connected as ");
    }

    [Fact]
    public void Enrich_ReplacesMultipleEmailsInSingleProperty()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new EmailScrubEnricher(TestSalt))
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        logger.Information("{Message}", "user=remy@contoso.com,admin=jane@fabrikam.com");

        var value = (captured.Single().Properties["Message"] as ScalarValue)?.Value as string;
        value.Should().Contain($"usr_{EmailScrubEnricher.Hash("remy", TestSalt)}.tnt_{EmailScrubEnricher.Hash("contoso.com", TestSalt)}");
        value.Should().Contain($"usr_{EmailScrubEnricher.Hash("jane", TestSalt)}.tnt_{EmailScrubEnricher.Hash("fabrikam.com", TestSalt)}");
    }

    [Fact]
    public void Enrich_DoesNotModifyPropertyWithNoEmail()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new EmailScrubEnricher(TestSalt))
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        logger.Information("solution={SolutionName}", "ContosoCustomizations");

        var value = (captured.Single().Properties["SolutionName"] as ScalarValue)?.Value as string;
        value.Should().Be("ContosoCustomizations");
    }

    sealed class CapturingSink(List<LogEvent> captured) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => captured.Add(logEvent);
    }
}
