using FluentAssertions;
using Flowline.Commands;
using Flowline.Core.Deploy;
using Flowline.Core.OrphanCleanup;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Spectre.Console;

namespace Flowline.Tests;

public class DeployCommandPostDeployTests
{
    // Builds the real DI registration from Program.cs (PostDeployServiceRegistration) rather than a
    // hand-written mirror list — a mirror can silently drift from the actual registration (it already
    // had, missing OrphanCleanupService). This resolves exactly what Program.cs wires up.
    static List<IPostDeployService> RegisteredServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IAnsiConsole>());
        services.AddSingleton<SubprocessCapture>();
        PostDeployServiceRegistration.RegisterPostDeployServices(services);

        return services.BuildServiceProvider().GetServices<IPostDeployService>().ToList();
    }

    [Theory]
    [InlineData(0, false)]     // no failures
    [InlineData(1, true)]      // single service reports a failure
    [InlineData(5, true)]      // multiple services' failures summed by the caller
    public void ShouldReportPartialSuccess_ReturnsExpected(int cleanupFailures, bool expected) =>
        DeployCommand.ShouldReportPartialSuccess(cleanupFailures).Should().Be(expected);

    [Theory]
    [InlineData(0, false)]     // no Critical findings — deploy proceeds
    [InlineData(1, true)]      // single Critical finding aborts the gate
    public void ShouldAbort_ReturnsExpected(int criticalCount, bool expected) =>
        SolutionCheckService.ShouldAbort(criticalCount).Should().Be(expected);

    // R13: the missing-component gate has to run before the solution checker and the backup, and the
    // only thing that makes that true is registration order surviving this method.
    [Fact]
    public void ResolveActiveServices_NoSkips_ReturnsEveryServiceInRegistrationOrder()
    {
        var active = DeployCommand.ResolveActiveServices(RegisteredServices(), new DeployCommand.Settings());

        active.Should().HaveCount(4);
        active[0].Should().BeOfType<MissingComponentCheckService>();
        active[1].Should().BeOfType<SolutionCheckService>();
        active[2].Should().BeOfType<BackupService>();
        active[3].Should().BeOfType<OrphanCleanupService>();
    }

    [Fact]
    public void ResolveActiveServices_SkipComponentCheck_OmitsOnlyThatGate()
    {
        var settings = new DeployCommand.Settings { SkipComponentCheck = true };

        var active = DeployCommand.ResolveActiveServices(RegisteredServices(), settings);

        active.Should().NotContain(s => s is MissingComponentCheckService);
        active.Should().Contain(s => s is SolutionCheckService);
        active.Should().Contain(s => s is BackupService);
    }

    [Theory]
    [InlineData(true, false)]   // --skip-solution-check leaves the component gate running
    [InlineData(false, true)]   // --no-backup leaves the component gate running
    public void ResolveActiveServices_OtherSkips_LeaveTheComponentGateRunning(bool skipSolutionCheck, bool noBackup)
    {
        var settings = new DeployCommand.Settings { SkipSolutionCheck = skipSolutionCheck, NoBackup = noBackup };

        var active = DeployCommand.ResolveActiveServices(RegisteredServices(), settings);

        active[0].Should().BeOfType<MissingComponentCheckService>();
        active.Should().HaveCount(3);
    }

    [Fact]
    public void ResolveActiveServices_ComponentGateSkipped_StillOrdersTheRemainderCorrectly()
    {
        var settings = new DeployCommand.Settings { SkipComponentCheck = true, NoBackup = true };

        var active = DeployCommand.ResolveActiveServices(RegisteredServices(), settings);

        active.Should().HaveCount(2);
        active[0].Should().BeOfType<SolutionCheckService>();
        active[1].Should().BeOfType<OrphanCleanupService>();
    }

    // FIX A: skipping the gate means "no current verdict" — a report an earlier blocked run left for
    // this target must not survive a run that never re-checked it.
    [Fact]
    public void ClearComponentCheckReportIfSkipped_Skipped_RemovesExistingReportForThatTarget()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flowline-deploy-clear-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var packagePath = Path.Combine(dir, "MySolution_1_0_0_0.zip");
            const string targetUrl = "https://example.crm.dynamics.com";
            var reportPath = MissingComponentReport.GetReportPath(packagePath, targetUrl);
            File.WriteAllText(reportPath, "stale report from an earlier blocked run");

            DeployCommand.ClearComponentCheckReportIfSkipped(true, packagePath, targetUrl);

            File.Exists(reportPath).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ClearComponentCheckReportIfSkipped_NotSkipped_LeavesExistingReportInPlace()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"flowline-deploy-clear-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var packagePath = Path.Combine(dir, "MySolution_1_0_0_0.zip");
            const string targetUrl = "https://example.crm.dynamics.com";
            var reportPath = MissingComponentReport.GetReportPath(packagePath, targetUrl);
            File.WriteAllText(reportPath, "stale report from an earlier blocked run");

            DeployCommand.ClearComponentCheckReportIfSkipped(false, packagePath, targetUrl);

            File.Exists(reportPath).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SkipComponentCheck_DefaultsToFalse() =>
        new DeployCommand.Settings().SkipComponentCheck.Should().BeFalse();

    [Fact]
    public void SkipComponentCheck_SetIndependentlyOfTheOtherSkipFlags()
    {
        var settings = new DeployCommand.Settings { SkipComponentCheck = true };

        settings.SkipComponentCheck.Should().BeTrue();
        settings.SkipSolutionCheck.Should().BeFalse();
        settings.NoBackup.Should().BeFalse();
    }

    // U4/anti-inert gate: resolving through the REAL registration (not a hand-mirrored list) must supply
    // a git-backed lookup. An unwired adapter (missing registration, or a stub left in place of the real
    // factory) would otherwise silently degrade every orphan-cleanup run to Undetermined verdicts —
    // this test fails loudly instead.
    [Fact]
    public void RegisterPostDeployServices_ResolvesGitBackedProvenanceLookup()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IAnsiConsole>());
        services.AddSingleton<SubprocessCapture>();
        PostDeployServiceRegistration.RegisterPostDeployServices(services);

        var lookup = services.BuildServiceProvider().GetRequiredService<IComponentProvenanceLookup>();

        lookup.Should().BeOfType<GitComponentProvenanceLookup>();
    }
}
