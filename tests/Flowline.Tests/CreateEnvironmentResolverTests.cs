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

    // --- R13/AE2: no --dev, no TTY — errors naming the flag, never prompts ---

    [Fact]
    public async Task ResolveAsync_NoDevUrl_NonInteractive_ThrowsNamingDevFlag_WithoutPrompting()
    {
        var resolver = MakeResolver();
        resolver.IsInteractiveOverride = () => false;
        // No SelectionPrompt/environment-list seam is configured — if the resolver tried to prompt
        // or fetch environments anyway, it would throw NullReferenceException-ish/different failures
        // instead of this specific FlowlineException, so a *this* exception is itself proof it never
        // reached the picker path.

        var act = () => resolver.ResolveAsync(devUrl: null, new FlowlineSettings(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain("--dev");
    }

    // --- R9/R13/AE7: --dev env with no matching pac auth profile, no TTY — errors naming `pac auth create` ---

    [Fact]
    public async Task ResolveAsync_DevUrlWithNoMatchingProfile_ThrowsNamingPacAuthCreate()
    {
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var profileResolutionService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            FindBestProfileOverride = _ => new ProfileNotFound(DevUrl),
            IsInteractiveOverride = () => false
        };
        var resolver = new CreateEnvironmentResolver(console, profileResolutionService, new SubprocessCapture(console));

        var act = () => resolver.ResolveAsync(DevUrl, new FlowlineSettings(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain("pac auth create");
    }

    // --- R8/KTD4/AE3: a --dev Production-type env is refused ---

    [Fact]
    public async Task ResolveAsync_DevUrlResolvesToProduction_ThrowsNamingDevOnlyRule()
    {
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var profile = new PacProfile { Name = "Contoso", Resource = DevUrl };
        var profileResolutionService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            FindBestProfileOverride = _ => new ProfileFound(profile),
            IsProfileActiveOverride = _ => true
        };
        var resolver = new CreateEnvironmentResolver(console, profileResolutionService, new SubprocessCapture(console))
        {
            GetEnvironmentInfoByUrlOverride = (_, _, _, _) => Task.FromResult<EnvironmentInfo?>(
                new EnvironmentInfo { DisplayName = "Contoso Prod", EnvironmentUrl = DevUrl, Type = "Production" })
        };

        var act = () => resolver.ResolveAsync(DevUrl, new FlowlineSettings(), CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain("Sandbox or Developer");
    }

    // --- R8/KTD4: a --dev Sandbox/Developer env proceeds and is returned ---

    [Fact]
    public async Task ResolveAsync_DevUrlResolvesToSandbox_ReturnsEnvironment()
    {
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        var profile = new PacProfile { Name = "Contoso", Resource = DevUrl };
        var profileResolutionService = new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            FindBestProfileOverride = _ => new ProfileFound(profile),
            IsProfileActiveOverride = _ => true
        };
        var expected = new EnvironmentInfo { DisplayName = "Contoso Dev", EnvironmentUrl = DevUrl, Type = "Sandbox" };
        var resolver = new CreateEnvironmentResolver(console, profileResolutionService, new SubprocessCapture(console))
        {
            GetEnvironmentInfoByUrlOverride = (_, _, _, _) => Task.FromResult<EnvironmentInfo?>(expected)
        };

        var result = await resolver.ResolveAsync(DevUrl, new FlowlineSettings(), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }
}
