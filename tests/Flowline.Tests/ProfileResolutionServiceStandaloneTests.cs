using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Services;
using FluentAssertions;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class ProfileResolutionServiceStandaloneTests
{
    const string DevUrl = "https://contoso-dev.crm4.dynamics.com";

    static ProfileResolutionService MakeService(PacProfile? currentProfile)
    {
        var console = new TestConsole();
        var connector = new DataverseConnector(console, new HttpClient());
        return new ProfileResolutionService(console, connector, new FlowlineRuntimeOptions())
        {
            GetCurrentResourceSpecificProfileOverride = () => currentProfile
        };
    }

    [Fact]
    public void ResolveStandaloneEnvironmentUrl_WithExplicitDevUrl_ShouldPreferItOverActiveProfile()
    {
        var service = MakeService(new PacProfile { Name = "Other", Resource = "https://other.crm4.dynamics.com" });

        var result = service.ResolveStandaloneEnvironmentUrl($"  {DevUrl}  ");

        result.Should().Be(DevUrl);
    }

    [Fact]
    public void ResolveStandaloneEnvironmentUrl_WithoutDevUrl_ShouldFallBackToActiveProfileResource()
    {
        var service = MakeService(new PacProfile { Name = "Contoso", Resource = DevUrl });

        var result = service.ResolveStandaloneEnvironmentUrl(null);

        result.Should().Be(DevUrl);
    }

    [Fact]
    public void ResolveStandaloneEnvironmentUrl_WithoutDevUrlAndNoResourceSpecificProfile_ShouldThrow()
    {
        var service = MakeService(null);

        var act = () => service.ResolveStandaloneEnvironmentUrl(null);

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.ValidationFailed);
    }
}
