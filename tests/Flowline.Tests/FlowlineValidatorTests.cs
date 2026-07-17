using FluentAssertions;
using Flowline.Core.Models;
using Flowline.Validation;

namespace Flowline.Tests;

public class FlowlineValidatorTests
{
    [Theory]
    [InlineData(".NET 10 dnx (One-shot)", true)]
    [InlineData("dnx", true)]
    [InlineData("DNX", true)]
    [InlineData("Dotnet Tool (.NET)", false)]
    [InlineData("MSI Installer (.NET Framework)", false)]
    [InlineData("Unknown", false)]
    public void IsSlowDnxInstall_DetectsDnxInstallTypeOnly(string installType, bool expected) =>
        FlowlineValidator.IsSlowDnxInstall(installType).Should().Be(expected);

    static readonly string EnvironmentUrl = "https://contoso.crm4.dynamics.com";

    static FlowlineValidator MakeValidator(out ValidationCacheStore store, ValidationProbes probes)
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"flowline-validation-cache-{Guid.NewGuid()}.json");
        store = new ValidationCacheStore(tempFile);
        return new FlowlineValidator(store, probes);
    }

    [Fact]
    public async Task GetEnvironmentInfoByUrlAsync_ProfileOverload_ForwardsGivenProfileToProbe()
    {
        var profile = new PacProfile { Name = "MyProfile", Resource = EnvironmentUrl };
        PacProfile? capturedProfile = null;
        var probes = new ValidationProbes
        {
            GetEnvironmentByProfileAsync = (p, url, _, _) =>
            {
                capturedProfile = p;
                return Task.FromResult<EnvironmentInfo?>(new EnvironmentInfo { EnvironmentUrl = url, Type = "Sandbox" });
            }
        };
        var validator = MakeValidator(out _, probes);

        var result = await validator.GetEnvironmentInfoByUrlAsync(EnvironmentUrl, profile, new FlowlineSettings(), CancellationToken.None);

        result.Should().NotBeNull();
        capturedProfile.Should().BeSameAs(profile);
    }

    [Fact]
    public async Task GetEnvironmentInfoByUrlAsync_ProfileOverload_FreshCacheHit_SkipsBothProbes()
    {
        var profile = new PacProfile { Name = "MyProfile", Resource = EnvironmentUrl };
        var unprofiledCalls = 0;
        var profiledCalls = 0;
        var probes = new ValidationProbes
        {
            GetEnvironmentAsync = (url, _, _) => { unprofiledCalls++; return Task.FromResult<EnvironmentInfo?>(new EnvironmentInfo { EnvironmentUrl = url, Type = "Sandbox" }); },
            GetEnvironmentByProfileAsync = (_, url, _, _) => { profiledCalls++; return Task.FromResult<EnvironmentInfo?>(new EnvironmentInfo { EnvironmentUrl = url, Type = "Sandbox" }); }
        };
        var validator = MakeValidator(out _, probes);

        // First call populates the cache via the profiled probe.
        await validator.GetEnvironmentInfoByUrlAsync(EnvironmentUrl, profile, new FlowlineSettings(), CancellationToken.None);
        // Second call, same URL, within TTL — should hit cache, not invoke either probe again.
        await validator.GetEnvironmentInfoByUrlAsync(EnvironmentUrl, profile, new FlowlineSettings(), CancellationToken.None);

        profiledCalls.Should().Be(1);
        unprofiledCalls.Should().Be(0);
    }

    [Fact]
    public async Task GetEnvironmentInfoByUrlAsync_UnprofiledOverload_StillUsesUnprofiledProbe()
    {
        var profiledCalls = 0;
        var unprofiledCalls = 0;
        var probes = new ValidationProbes
        {
            GetEnvironmentAsync = (url, _, _) => { unprofiledCalls++; return Task.FromResult<EnvironmentInfo?>(new EnvironmentInfo { EnvironmentUrl = url, Type = "Production" }); },
            GetEnvironmentByProfileAsync = (_, url, _, _) => { profiledCalls++; return Task.FromResult<EnvironmentInfo?>(new EnvironmentInfo { EnvironmentUrl = url, Type = "Production" }); }
        };
        var validator = MakeValidator(out _, probes);

        var result = await validator.GetEnvironmentInfoByUrlAsync(EnvironmentUrl, new FlowlineSettings(), CancellationToken.None);

        result.Should().NotBeNull();
        unprofiledCalls.Should().Be(1);
        profiledCalls.Should().Be(0);
    }
}
