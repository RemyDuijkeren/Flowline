using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Services;
using FluentAssertions;
using Spectre.Console.Testing;

namespace Flowline.Tests.Services;

public class ProfileResolutionServiceTests
{
    static readonly string EnvironmentUrl = "https://automatevalue-dev.crm4.dynamics.com";

    static ProfileResolutionService MakeService(
        out TestConsole console,
        ProfileResolutionResult resolvedResult)
    {
        console = new TestConsole();
        var opt = new FlowlineRuntimeOptions();
        var connector = new DataverseConnector(console, opt);
        var svc = new ProfileResolutionService(console, connector, opt);
        svc.FindBestProfileOverride = _ => resolvedResult;
        return svc;
    }

    static PacProfile MakeProfile(string? name = "MyProfile", string? kind = "DATAVERSE",
        string? resource = "https://automatevalue-dev.crm4.dynamics.com")
        => new() { Name = name, Kind = kind, Resource = resource };

    // ── ProfileFound ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ProfileFound_ReturnsProfile()
    {
        var profile = MakeProfile();
        var svc = MakeService(out _, new ProfileFound(profile));

        var result = await svc.ResolveAsync(EnvironmentUrl);

        result.Should().BeSameAs(profile);
    }

    [Fact]
    public async Task ProfileFound_EmitsStatusLine()
    {
        var profile = MakeProfile(name: "MyProfile", kind: "DATAVERSE");
        var svc = MakeService(out var console, new ProfileFound(profile));

        await svc.ResolveAsync(EnvironmentUrl);

        console.Output.Should().Contain("Using PAC profile 'MyProfile' (DATAVERSE)");
    }

    [Fact]
    public async Task ProfileFound_NameEmpty_EmitsUnnamedStatusLine()
    {
        var profile = MakeProfile(name: "", kind: "UNIVERSAL", resource: EnvironmentUrl);
        var svc = MakeService(out var console, new ProfileFound(profile));

        await svc.ResolveAsync(EnvironmentUrl);

        // TestConsole may word-wrap long lines; assert the key parts appear in output
        console.Output.Should().Contain("Using PAC profile (unnamed, UNIVERSAL)");
        console.Output.Should().Contain(EnvironmentUrl);
    }

    [Fact]
    public async Task ProfileFound_NameNull_EmitsUnnamedStatusLine()
    {
        var profile = MakeProfile(name: null, kind: "DATAVERSE", resource: EnvironmentUrl);
        var svc = MakeService(out var console, new ProfileFound(profile));

        await svc.ResolveAsync(EnvironmentUrl);

        // TestConsole may word-wrap long lines; assert the key parts appear in output
        console.Output.Should().Contain("Using PAC profile (unnamed, DATAVERSE)");
        console.Output.Should().Contain(EnvironmentUrl);
    }

    // ── ProfileAmbiguous — non-interactive ───────────────────────────────────

    [Fact]
    public async Task ProfileAmbiguous_NonInteractive_ThrowsFlowlineException()
    {
        var candidates = new List<PacProfile>
        {
            MakeProfile(name: "Alpha", kind: "DATAVERSE"),
            MakeProfile(name: "Beta", kind: "UNIVERSAL")
        };
        // CI env var makes ConsoleHelper.IsInteractive return false
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var svc = MakeService(out _, new ProfileAmbiguous(candidates));
            var act = () => svc.ResolveAsync(EnvironmentUrl);

            await act.Should().ThrowAsync<FlowlineException>()
                .Where(ex => ex.ExitCode == ExitCode.NotAuthenticated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI", null);
        }
    }

    [Fact]
    public async Task ProfileAmbiguous_NonInteractive_ExceptionMessageContainsCandidates()
    {
        var candidates = new List<PacProfile>
        {
            MakeProfile(name: "Alpha", kind: "DATAVERSE"),
            MakeProfile(name: "Beta", kind: "UNIVERSAL")
        };
        Environment.SetEnvironmentVariable("CI", "true");
        try
        {
            var svc = MakeService(out _, new ProfileAmbiguous(candidates));

            var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

            ex.Message.Should().Contain("Alpha");
            ex.Message.Should().Contain("Beta");
            ex.Message.Should().Contain("pac auth select");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI", null);
        }
    }

    // ── ProfileNotFound ──────────────────────────────────────────────────────

    [Fact]
    public async Task ProfileNotFound_ThrowsFlowlineException()
    {
        var svc = MakeService(out _, new ProfileNotFound(EnvironmentUrl));

        var act = () => svc.ResolveAsync(EnvironmentUrl);

        await act.Should().ThrowAsync<FlowlineException>()
            .Where(ex => ex.ExitCode == ExitCode.NotAuthenticated);
    }

    [Fact]
    public async Task ProfileNotFound_ExceptionMessageContainsPacAuthCreate()
    {
        var svc = MakeService(out _, new ProfileNotFound(EnvironmentUrl));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.Message.Should().Contain("pac auth create");
        ex.Message.Should().Contain(EnvironmentUrl.TrimEnd('/'));
    }

    [Fact]
    public async Task ProfileNotFound_ExceptionMessageContainsNameSuggestion()
    {
        // automatevalue-dev.crm4.dynamics.com → first segment = "automatevalue-dev"
        // split on "-" → ["automatevalue", "dev"] → TitleCase → ["Automatevalue", "Dev"] → "Automatevalue-Dev"
        var svc = MakeService(out _, new ProfileNotFound(EnvironmentUrl));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() => svc.ResolveAsync(EnvironmentUrl));

        ex.Message.Should().Contain("Automatevalue-Dev");
    }

    // ── BuildNameSuggestion ──────────────────────────────────────────────────

    [Theory]
    [InlineData("https://automatevalue-dev.crm4.dynamics.com", "Automatevalue-Dev")]
    [InlineData("https://myorg.crm.dynamics.com", "Myorg")]
    [InlineData("https://contoso.crm4.dynamics.com", "Contoso")]
    [InlineData("https://my-org-test.crm4.dynamics.com", "My-Org-Test")]
    public void BuildNameSuggestion_VariousUrls_ReturnsExpected(string url, string expected)
    {
        ProfileResolutionService.BuildNameSuggestion(url).Should().Be(expected);
    }

    [Fact]
    public void BuildNameSuggestion_UrlWithPort_UsesHostOnly()
    {
        // Only host segment first part used — port should not affect the result
        ProfileResolutionService.BuildNameSuggestion("https://myorg.crm4.dynamics.com:443/api/data")
            .Should().Be("Myorg");
    }
}
