using Flowline.Core;
using Flowline.Core.Config;
using Flowline.Core.Dataverse;
using Flowline.Core.Environments;
using Flowline.Core.Models;
using Flowline.Core.Validation;
using FluentAssertions;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class EnvironmentServiceTests
{
    const string EnvUrl = "https://contoso-dev.crm4.dynamics.com";
    static readonly PacProfile s_profile = new() { Name = "Contoso", Resource = EnvUrl };

    // The lookup is the validator's probe, so no test here reaches pac. A null type means the probe
    // returns no environment at all.
    static (EnvironmentService Service, TestConsole Console) MakeService(string? environmentType, bool found = true)
    {
        var console = new TestConsole();
        var options = new FlowlineRuntimeOptions();
        var probes = new ValidationProbes
        {
            GetEnvironmentByProfileAsync = (_, url, _) => Task.FromResult<EnvironmentInfo?>(found
                ? new EnvironmentInfo { EnvironmentUrl = url, DisplayName = "Contoso", Type = environmentType }
                : null)
        };
        var validator = new FlowlineValidator(
            new ValidationCacheStore(Path.Combine(Path.GetTempPath(), $"flowline-validation-cache-{Guid.NewGuid()}.json")), probes);
        var profiles = new ProfileResolutionService(console, new DataverseConnector(console, new HttpClient()), options)
        {
            FindBestProfileOverride = _ => new ProfileFound(s_profile),
            IsProfileActiveOverride = _ => true
        };
        return (new EnvironmentService(console, profiles, validator, options), console);
    }

    // ── Check by URL ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckByUrl_RoleDev_ProductionType_Throws()
    {
        var (service, _) = MakeService("Production");

        var act = () => service.CheckByUrlAsync(EnvUrl, EnvironmentRole.Dev, CancellationToken.None, s_profile);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    // generate passes the skip so it can target Production.
    [Fact]
    public async Task CheckByUrl_SkipTypeGuard_RoleDev_ProductionType_DoesNotThrow()
    {
        var (service, _) = MakeService("Production");

        var (info, _) = await service.CheckByUrlAsync(EnvUrl, EnvironmentRole.Dev, CancellationToken.None, s_profile, skipTypeGuard: true);

        info.Type.Should().Be("Production");
    }

    // A "use this URL once" result carries no role, so there is no role to check the type against.
    [Fact]
    public async Task CheckByUrl_NoRole_ProductionType_DoesNotThrow()
    {
        var (service, _) = MakeService("Production");

        var act = () => service.CheckByUrlAsync(EnvUrl, null, CancellationToken.None, s_profile);

        await act.Should().NotThrowAsync();
    }

    // push and pull are DEV-only, so they refuse Production even without a role.
    [Fact]
    public async Task CheckByUrl_DevOnly_NoRole_ProductionType_Throws()
    {
        var (service, _) = MakeService("Production");

        var act = () => service.CheckByUrlAsync(EnvUrl, null, CancellationToken.None, s_profile, devOnly: true);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    [Fact]
    public async Task CheckByUrl_RoleProd_SandboxType_Throws()
    {
        var (service, _) = MakeService("Sandbox");

        var act = () => service.CheckByUrlAsync(EnvUrl, EnvironmentRole.Prod, CancellationToken.None, s_profile);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }

    // The refusal names the .flowline key to fix, because the role's URL came from there.
    [Theory]
    [InlineData(EnvironmentRole.Uat, "Production", "UatUrl")]
    [InlineData(EnvironmentRole.Prod, "Sandbox", "ProdUrl")]
    public async Task CheckByUrl_RoleTypeMismatch_NamesTheFlowlineKey(EnvironmentRole role, string type, string key)
    {
        var (service, _) = MakeService(type);

        var act = () => service.CheckByUrlAsync(EnvUrl, role, CancellationToken.None, s_profile);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain(key).And.Contain(".flowline");
    }

    [Fact]
    public async Task CheckByUrl_NotFound_ThrowsConnectionFailedNamingTheUrl()
    {
        var (service, _) = MakeService(null, found: false);

        var act = () => service.CheckByUrlAsync(EnvUrl, EnvironmentRole.Dev, CancellationToken.None, s_profile);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ConnectionFailed)
            .Which.Message.Should().Contain(EnvUrl);
    }

    [Fact]
    public async Task CheckByUrl_NoRole_NotFound_SaysEnvironmentOnce()
    {
        var (service, _) = MakeService(null, found: false);

        var act = () => service.CheckByUrlAsync(EnvUrl, null, CancellationToken.None, s_profile);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().StartWith("Environment not found").And.NotContain("Environment environment");
    }

    [Fact]
    public async Task CheckByUrl_NoRole_Found_PrintsEnvLineWithoutTheWordEnvironmentTwice()
    {
        var (service, console) = MakeService("Sandbox");

        await service.CheckByUrlAsync(EnvUrl, null, CancellationToken.None, s_profile);

        console.Output.Should().Contain("Env Contoso").And.NotContain("Environment env");
    }

    // ── Check by role ────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckByRole_UrlNotSaved_ThrowsConfigInvalid()
    {
        var (service, _) = MakeService("Sandbox");

        var act = () => service.CheckByRoleAsync(new ProjectConfig(), EnvironmentRole.Uat, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ConfigInvalid);
    }

    [Fact]
    public async Task CheckByRole_SavedValueIsNotAUrl_ThrowsValidationFailedNamingTheKey()
    {
        var (service, _) = MakeService("Sandbox");

        var act = () => service.CheckByRoleAsync(new ProjectConfig { UatUrl = "not-a-url" }, EnvironmentRole.Uat, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Where(e => e.ExitCode == ExitCode.ValidationFailed)
            .Which.Message.Should().Contain("UatUrl");
    }

    // ── Resolve a role or a URL ──────────────────────────────────────────────

    [Fact]
    public async Task Resolve_Role_AppliesTheTypeGuard()
    {
        var (service, _) = MakeService("Production");

        var act = () => service.ResolveAsync(new ProjectConfig { UatUrl = EnvUrl }, "uat", EnvironmentRole.Uat, CancellationToken.None);

        (await act.Should().ThrowAsync<FlowlineException>()).Where(e => e.ExitCode == ExitCode.ValidationFailed);
    }
}
