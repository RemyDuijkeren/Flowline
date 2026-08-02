using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class CreateEnvironmentResolverTests
{
    const string DevUrl = "https://contoso-dev.crm4.dynamics.com";

    static CreateEnvironmentResolver MakeResolver(ProfileResolutionService? profileResolutionService = null)
    {
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        profileResolutionService ??= new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions());
        return new CreateEnvironmentResolver(console, profileResolutionService, new SubprocessCapture(console));
    }

    static CreateEnvironmentResolver MakeResolverForUrl(string type, out ProfileResolutionService profiles)
    {
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var profile = new PacProfile { Name = "Contoso", Resource = DevUrl };
        profiles = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            FindBestProfileOverride = _ => new ProfileFound(profile),
            IsProfileActiveOverride = _ => true
        };
        return new CreateEnvironmentResolver(console, profiles, new SubprocessCapture(console))
        {
            GetEnvironmentInfoByUrlOverride = (_, _, _, _) => Task.FromResult<EnvironmentInfo?>(
                new EnvironmentInfo { DisplayName = $"Contoso {type}", EnvironmentUrl = DevUrl, Type = type })
        };
    }

    // --- KTD4/R8: the DEV-only whitelist predicate — pure, no I/O ---

    [Theory]
    [InlineData("Sandbox", true)]
    [InlineData("Developer", true)]
    [InlineData("sandbox", true)]
    [InlineData("developer", true)]
    [InlineData("SANDBOX", true)]
    [InlineData("Production", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Trial", false)]
    [InlineData("Unknown", false)]
    public void IsCreateEligibleEnvironmentType_ReturnsExpected(string? type, bool expected)
    {
        CreateEnvironmentResolver.IsCreateEligibleEnvironmentType(type).Should().Be(expected);
    }

    // --- Clone-source picker: Default/Teams hidden, every other type (incl. unknown) listed ---

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Sandbox", true)]
    [InlineData("Developer", true)]
    [InlineData("Trial", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Default", false)]
    [InlineData("default", false)]
    [InlineData("Teams", false)]
    [InlineData("TEAMS", false)]
    public void IsSelectableSourceType_ReturnsExpected(string? type, bool expected)
    {
        CreateEnvironmentResolver.IsSelectableSourceType(type).Should().Be(expected);
    }

    // --- Tenant with nothing but Default/Teams — errors instead of showing an unusable picker ---

    [Fact]
    public async Task ResolveSource_OnlyDefaultAndTeamsEnvironments_Throws()
    {
        var resolver = MakeResolver();
        resolver.IsInteractiveOverride = () => true;
        resolver.GetEnvironmentsOverride = _ => Task.FromResult(new List<EnvironmentInfo>
        {
            new() { DisplayName = "Personal Productivity", EnvironmentUrl = DevUrl, Type = "Default" },
            new() { DisplayName = "Contoso", EnvironmentUrl = DevUrl, Type = "Teams" }
        });

        var act = () => resolver.ResolveSourceAsync(sourceUrl: null, new FlowlineSettings(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain("Default and Teams");
    }

    // === ResolveCreateTargetAsync (init) — DEV-only ===

    // --- R13/AE2: no --dev, no TTY — errors naming the flag, never prompts ---

    [Fact]
    public async Task ResolveCreateTarget_NoDevUrl_NonInteractive_ThrowsNamingDevFlag_WithoutPrompting()
    {
        var resolver = MakeResolver();
        resolver.IsInteractiveOverride = () => false;
        // No SelectionPrompt/environment-list seam is configured — if the resolver tried to prompt
        // or fetch environments anyway, it would throw a different failure instead of this specific
        // FlowlineException, so *this* exception is itself proof it never reached the picker path.

        var act = () => resolver.ResolveCreateTargetAsync(devUrl: null, new FlowlineSettings(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain("--dev");
    }

    // --- R9/R13/AE7: --dev env with no matching pac auth profile, no TTY — errors naming `pac auth create` ---

    [Fact]
    public async Task ResolveCreateTarget_DevUrlWithNoMatchingProfile_ThrowsNamingPacAuthCreate()
    {
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var profileResolutionService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            FindBestProfileOverride = _ => new ProfileNotFound(DevUrl),
            IsInteractiveOverride = () => false
        };
        var resolver = new CreateEnvironmentResolver(console, profileResolutionService, new SubprocessCapture(console));

        var act = () => resolver.ResolveCreateTargetAsync(DevUrl, new FlowlineSettings(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain("pac auth create");
    }

    // --- R8/KTD4/AE3: a --dev Production-type env is refused for create ---

    [Fact]
    public async Task ResolveCreateTarget_DevUrlResolvesToProduction_ThrowsNamingDevOnlyRule()
    {
        var resolver = MakeResolverForUrl("Production", out _);

        var act = () => resolver.ResolveCreateTargetAsync(DevUrl, new FlowlineSettings(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain("Sandbox or Developer");
    }

    // --- R8/KTD4: a --dev Sandbox/Developer env proceeds and is returned ---

    [Fact]
    public async Task ResolveCreateTarget_DevUrlResolvesToSandbox_ReturnsEnvironment()
    {
        var resolver = MakeResolverForUrl("Sandbox", out _);

        var result = await resolver.ResolveCreateTargetAsync(DevUrl, new FlowlineSettings(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Type.Should().Be("Sandbox");
    }

    // === ResolveSourceAsync (clone) — any type allowed, no create guard ===

    // --- New contract: clone source may be Production (the default-model source of truth) — not refused ---

    [Fact]
    public async Task ResolveSource_UrlResolvesToProduction_ReturnsEnvironment()
    {
        var resolver = MakeResolverForUrl("Production", out _);

        var result = await resolver.ResolveSourceAsync(DevUrl, new FlowlineSettings(), CancellationToken.None);

        result.Type.Should().Be("Production");
    }

    // --- No source URL, no TTY — errors rather than prompting ---

    [Fact]
    public async Task ResolveSource_NoUrl_NonInteractive_Throws_WithoutPrompting()
    {
        var resolver = MakeResolver();
        resolver.IsInteractiveOverride = () => false;

        var act = () => resolver.ResolveSourceAsync(sourceUrl: null, new FlowlineSettings(), CancellationToken.None);

        await act.Should().ThrowAsync<FlowlineException>();
    }
}
