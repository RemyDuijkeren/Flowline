using Flowline.Logging;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Flowline.Tests;

public class UrlScrubEnricherTests
{
    // HashUrl

    [Theory]
    [InlineData("https://contoso.crm4.dynamics.com/")]
    [InlineData("https://example.com")]
    public void HashUrl_ReturnsDeterministicEightCharLowercaseHex(string url)
    {
        var hash = UrlScrubEnricher.HashUrl(url);
        hash.Should().HaveLength(8);
        hash.Should().MatchRegex("^[0-9a-f]+$");
        UrlScrubEnricher.HashUrl(url).Should().Be(hash);
    }

    [Fact]
    public void HashUrl_DifferentUrls_ProduceDifferentHashes()
    {
        UrlScrubEnricher.HashUrl("https://a.example.com")
            .Should().NotBe(UrlScrubEnricher.HashUrl("https://b.example.com"));
    }

    // Enricher — structured property containing a URL

    [Fact]
    public void Enrich_ReplacesUrlInScalarStringProperty()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new UrlScrubEnricher())
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        logger.Information("target={EnvironmentUrl}", "https://contoso.crm.dynamics.com");

        var props = captured.Single().Properties;
        props.Should().ContainKey("EnvironmentUrl");
        var value = (props["EnvironmentUrl"] as ScalarValue)?.Value as string;
        value.Should().HaveLength(8).And.MatchRegex("^[0-9a-f]+$");
        value.Should().Be(UrlScrubEnricher.HashUrl("https://contoso.crm.dynamics.com"));
    }

    // Enricher — URL embedded in a longer string (LoggingRenderHook path)

    [Fact]
    public void Enrich_ReplacesUrlEmbeddedInString()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new UrlScrubEnricher())
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        logger.Information("{Message}", "✔ Prod env Contoso (https://contoso.crm.dynamics.com) exists");

        var props = captured.Single().Properties;
        var value = (props["Message"] as ScalarValue)?.Value as string;
        value.Should().NotContain("https://");
        value.Should().Contain(UrlScrubEnricher.HashUrl("https://contoso.crm.dynamics.com"));
        value.Should().Contain("✔ Prod env Contoso (");
    }

    [Fact]
    public void Enrich_ReplacesMultipleUrlsInSingleProperty()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new UrlScrubEnricher())
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        logger.Information("{Message}",
            "prod=https://contoso.crm.dynamics.com,uat=https://contoso-uat.crm.dynamics.com");

        var value = (captured.Single().Properties["Message"] as ScalarValue)?.Value as string;
        value.Should().NotContain("https://");
        value.Should().Contain(UrlScrubEnricher.HashUrl("https://contoso.crm.dynamics.com"));
        value.Should().Contain(UrlScrubEnricher.HashUrl("https://contoso-uat.crm.dynamics.com"));
    }

    [Fact]
    public void Enrich_DoesNotModifyPropertyWithNoUrl()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new UrlScrubEnricher())
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
