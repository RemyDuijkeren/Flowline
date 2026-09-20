using System.Runtime.InteropServices;
using Flowline.Logging;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Flowline.Tests.Logging;

public class FlowlineScrubberTests
{
    static readonly byte[] TestSalt = "test-salt"u8.ToArray();

    static FlowlineScrubber NewScrubber() => new(TestSalt);

    // Hashing primitives — these carry over from the URL and email enrichers this class replaced, and
    // the expectations are unchanged so the same input still produces the same hash as before.

    [Theory]
    [InlineData("contoso.com")]
    [InlineData("remy")]
    public void Hash_ReturnsDeterministicEightCharLowercaseHex(string value)
    {
        var hash = FlowlineScrubber.Hash(value, TestSalt);
        hash.Should().HaveLength(8);
        hash.Should().MatchRegex("^[0-9a-f]+$");
        FlowlineScrubber.Hash(value, TestSalt).Should().Be(hash);
    }

    [Fact]
    public void Hash_IsCaseInsensitive() =>
        FlowlineScrubber.Hash("Remy", TestSalt).Should().Be(FlowlineScrubber.Hash("remy", TestSalt));

    [Fact]
    public void Hash_DifferentValues_ProduceDifferentHashes() =>
        FlowlineScrubber.Hash("a.example.com", TestSalt)
            .Should().NotBe(FlowlineScrubber.Hash("b.example.com", TestSalt));

    [Fact]
    public void Hash_DifferentSalts_ProduceDifferentHashes() =>
        FlowlineScrubber.Hash("remy", TestSalt)
            .Should().NotBe(FlowlineScrubber.Hash("remy", "other-salt"u8.ToArray()));

    [Fact]
    public void HashUrl_ReturnsDeterministicEightCharLowercaseHex()
    {
        var hash = FlowlineScrubber.HashUrl("https://contoso.crm4.dynamics.com", TestSalt);
        hash.Should().HaveLength(8).And.MatchRegex("^[0-9a-f]+$");
        FlowlineScrubber.HashUrl("https://contoso.crm4.dynamics.com", TestSalt).Should().Be(hash);
    }

    // URLs

    [Fact]
    public void Scrub_ReplacesUrlWithItsHash()
    {
        var scrubbed = NewScrubber().Scrub("Connecting to https://contoso.crm4.dynamics.com/main.aspx now");

        scrubbed.Should().NotContain("contoso");
        scrubbed.Should().NotContain("https://");
        scrubbed.Should().Contain("Connecting to ").And.Contain(" now");
    }

    [Fact]
    public void Scrub_ReplacesEveryUrlInOneValue()
    {
        var scrubbed = NewScrubber().Scrub("from https://a.crm4.dynamics.com to https://b.crm4.dynamics.com");

        scrubbed.Should().NotContain("a.crm4").And.NotContain("b.crm4");
        scrubbed.Should().Contain(FlowlineScrubber.HashUrl("https://a.crm4.dynamics.com", TestSalt));
        scrubbed.Should().Contain(FlowlineScrubber.HashUrl("https://b.crm4.dynamics.com", TestSalt));
    }

    // Email addresses

    [Fact]
    public void Scrub_ReplacesEmailWithNonEmailShapedToken()
    {
        var scrubbed = NewScrubber().Scrub("Connected as remy@contoso.com to environment");

        scrubbed.Should().Be(
            $"Connected as usr_{FlowlineScrubber.Hash("remy", TestSalt)}.tnt_{FlowlineScrubber.Hash("contoso.com", TestSalt)} to environment");
        scrubbed.Should().NotContain("@");
    }

    [Fact]
    public void Scrub_ReplacesMultipleEmailsInOneValue()
    {
        var scrubbed = NewScrubber().Scrub("user=remy@contoso.com,admin=jane@fabrikam.com");

        scrubbed.Should().Contain($"usr_{FlowlineScrubber.Hash("remy", TestSalt)}.tnt_{FlowlineScrubber.Hash("contoso.com", TestSalt)}");
        scrubbed.Should().Contain($"usr_{FlowlineScrubber.Hash("jane", TestSalt)}.tnt_{FlowlineScrubber.Hash("fabrikam.com", TestSalt)}");
    }

    // Filesystem paths (AE7)

    [Theory]
    [InlineData("/home/remy/Projects/AcmeBank/Solution", "/home/", "remy")]
    [InlineData("/Users/remy/Projects/AcmeBank/Solution", "/Users/", "remy")]
    [InlineData(@"C:\Users\remy\Projects\AcmeBank\Solution", @"C:\Users\", "remy")]
    public void Scrub_HashesTheUserSegmentAndKeepsTheLayout(string path, string prefix, string user)
    {
        var scrubbed = NewScrubber().Scrub(path);

        scrubbed.Should().StartWith(prefix + FlowlineScrubber.Hash(user, TestSalt));
        scrubbed.Should().NotContain(user);
        scrubbed.Should().Contain("Projects");
        scrubbed.Should().EndWith("Solution");
    }

    [Fact]
    public void Scrub_HashesTheUserSegmentOfAPathEmbeddedInText()
    {
        var scrubbed = NewScrubber().Scrub("Reading /home/remy/.local/share/Flowline/telemetry-salt failed");

        scrubbed.Should().NotContain("remy");
        scrubbed.Should().Contain("/.local/share/Flowline/telemetry-salt failed");
    }

    // Known values: solution name, branch name, project folder (AE8)

    [Fact]
    public void Scrub_HashesRegisteredSolutionAndBranchNames()
    {
        var scrubber = NewScrubber();
        scrubber.AddKnownValue("AcmeBankCustomizations");
        scrubber.AddKnownValue("feature/acme-invoice-sync");

        var scrubbed = scrubber.Scrub("solution=AcmeBankCustomizations branch=feature/acme-invoice-sync");

        scrubbed.Should().NotContain("AcmeBank").And.NotContain("invoice-sync");
        scrubbed.Should().Contain(FlowlineScrubber.Hash("AcmeBankCustomizations", TestSalt));
        scrubbed.Should().Contain(FlowlineScrubber.Hash("feature/acme-invoice-sync", TestSalt));
    }

    [Fact]
    public void Scrub_WhenOneKnownValueIsAPrefixOfAnother_LeavesNoRemainderInPlaintext()
    {
        // Registration order is solution, branch, folder, and a solution name really can be a prefix of
        // the folder holding it. Replacing the shorter one first would destroy the match the longer one
        // needed and leave "Customizations" readable.
        var scrubber = NewScrubber();
        scrubber.AddKnownValue("AcmeBank");
        scrubber.AddKnownValue("AcmeBankCustomizations");

        var scrubbed = scrubber.Scrub("solution=AcmeBankCustomizations root=/x/AcmeBank done")!;

        scrubbed.Should().NotContain("AcmeBank").And.NotContain("Customizations");
        scrubbed.Should().Contain(FlowlineScrubber.Hash("AcmeBankCustomizations", TestSalt));
        scrubbed.Should().Contain(FlowlineScrubber.Hash("AcmeBank", TestSalt));
        scrubbed.Should().EndWith(" done");
    }

    [Fact]
    public void Scrub_HandlesTheRenderedExceptionChainItIsActuallyFed()
    {
        // Scrub is handed ex.ToString() for the whole inner chain, so it has to cope with a long value
        // without hanging. The rules carry a match timeout; a value that trips it is replaced wholesale
        // rather than escaping unscrubbed.
        var scrubber = NewScrubber();
        var long_ = string.Concat(Enumerable.Repeat("at Flowline.Some.Frame() in /home/remy/x.cs:line 9\n", 5_000));

        var scrubbed = scrubber.Scrub(long_)!;

        scrubbed.Should().NotContain("/home/remy/");
    }

    [Fact]
    public void AddKnownValue_IgnoresValuesTooShortToIdentifyAnyone()
    {
        var scrubber = NewScrubber();
        scrubber.AddKnownValue("app");
        scrubber.AddKnownValue(null);
        scrubber.AddKnownValue("  ");

        scrubber.Scrub("the app directory").Should().Be("the app directory");
    }

    [Fact]
    public void AddKnownValue_IsIdempotent()
    {
        var scrubber = NewScrubber();
        scrubber.AddKnownValue("AcmeBank");
        scrubber.AddKnownValue("acmebank");

        scrubber.Scrub("AcmeBank").Should().Be(FlowlineScrubber.Hash("AcmeBank", TestSalt));
    }

    // KTD6: a value logged before config load is scrubbed for URLs, emails and paths, and passes
    // through unchanged for the two rules that only arrive once the command knows them.

    [Fact]
    public void Scrub_BeforeKnownValuesAreRegistered_StillScrubsTheOtherThreeRules()
    {
        var scrubber = NewScrubber();

        var scrubbed = scrubber.Scrub("https://contoso.crm4.dynamics.com remy@contoso.com /home/remy/x AcmeBankCustomizations");

        scrubbed.Should().NotContain("contoso.crm4").And.NotContain("@").And.NotContain("/home/remy");
        scrubbed.Should().Contain("AcmeBankCustomizations");
    }

    // Values with nothing to hide, and values that cannot be handled

    [Fact]
    public void Scrub_LeavesAValueWithNoMatchingShapeByteIdentical()
    {
        const string value = "Imported solution, 14 components, 3.2s";
        NewScrubber().Scrub(value).Should().BeSameAs(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Scrub_HandlesNullAndEmpty(string? value) => NewScrubber().Scrub(value).Should().Be(value);

    [Fact]
    public void Scrub_NeverLeaksTheOperatingSystemUserOrMachineName()
    {
        var scrubber = NewScrubber();
        var raw = $"/home/{Environment.UserName}/work on {Environment.MachineName} as {Environment.UserName}@contoso.com";
        scrubber.AddKnownValue(Environment.MachineName);

        var scrubbed = scrubber.Scrub(raw)!;

        scrubbed.Should().NotContainEquivalentOf(Environment.UserName);
        scrubbed.Should().NotContainEquivalentOf(Environment.MachineName);
    }

    // The per-machine dimension (KTD11/R13)

    [Fact]
    public void MachineId_IsStableForOneSaltAndDiffersAcrossSalts()
    {
        new FlowlineScrubber(TestSalt).MachineId.Should().Be(new FlowlineScrubber(TestSalt).MachineId);
        new FlowlineScrubber(TestSalt).MachineId.Should().NotBe(new FlowlineScrubber("other"u8.ToArray()).MachineId);
    }

    [Fact]
    public void MachineId_IsNotDerivedFromAnyMachineOrUserIdentity()
    {
        var id = new FlowlineScrubber(TestSalt).MachineId;

        id.Should().MatchRegex("^[0-9a-f]{16}$");
        id.Should().NotContainEquivalentOf(Environment.MachineName);
        id.Should().NotContainEquivalentOf(Environment.UserName);
    }

    // The log path, end to end through Serilog

    [Fact]
    public void ScrubEnricher_ScrubsEveryStringPropertyOnTheWayToTheSink()
    {
        var captured = new List<LogEvent>();
        var scrubber = NewScrubber();
        scrubber.AddKnownValue("AcmeBankCustomizations");
        var logger = new LoggerConfiguration()
            .Enrich.With(new ScrubEnricher(scrubber))
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        logger.Information("env={Url} user={Email} solution={Solution} count={Count}",
            "https://contoso.crm4.dynamics.com", "remy@contoso.com", "AcmeBankCustomizations", 14);

        var props = captured.Single().Properties;
        Value(props, "Url").Should().NotContain("contoso");
        Value(props, "Email").Should().NotContain("@");
        Value(props, "Solution").Should().Be(FlowlineScrubber.Hash("AcmeBankCustomizations", TestSalt));
        props["Count"].ToString().Should().Be("14");
    }

    [Fact]
    public void ScrubEnricher_LeavesAPropertyWithNothingToHideAlone()
    {
        var captured = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Enrich.With(new ScrubEnricher(NewScrubber()))
            .WriteTo.Sink(new CapturingSink(captured))
            .CreateLogger();

        logger.Information("solution={SolutionName}", "ContosoCustomizations");

        Value(captured.Single().Properties, "SolutionName").Should().Be("ContosoCustomizations");
    }

    static string? Value(IReadOnlyDictionary<string, LogEventPropertyValue> props, string key) =>
        (props[key] as ScalarValue)?.Value as string;

    sealed class CapturingSink(List<LogEvent> captured) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => captured.Add(logEvent);
    }
}
