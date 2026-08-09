using FluentAssertions;
using Flowline.Commands;
using Flowline.Core.Deploy;
using Flowline.Core.Services;
using Flowline.Services;

namespace Flowline.Tests;

public class DeployCommandPostDeployTests
{
    // Mirrors the Program.cs registration order, which is the pre-import run order.
    static IEnumerable<IPostDeployService> RegisteredServices() =>
    [
        new MissingComponentCheckService(null!),
        new SolutionCheckService(null!, null!),
        new BackupService(null!, null!)
    ];

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

        active.Should().HaveCount(3);
        active[0].Should().BeOfType<MissingComponentCheckService>();
        active[1].Should().BeOfType<SolutionCheckService>();
        active[2].Should().BeOfType<BackupService>();
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
        active.Should().HaveCount(2);
    }

    [Fact]
    public void ResolveActiveServices_ComponentGateSkipped_StillOrdersTheRemainderCorrectly()
    {
        var settings = new DeployCommand.Settings { SkipComponentCheck = true, NoBackup = true };

        var active = DeployCommand.ResolveActiveServices(RegisteredServices(), settings);

        active.Should().ContainSingle().Which.Should().BeOfType<SolutionCheckService>();
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
}
