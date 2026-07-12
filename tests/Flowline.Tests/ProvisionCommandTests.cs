using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class ProvisionCommandTests
{
    private static SolutionInfo Unmanaged(string uniqueName) =>
        new() { SolutionUniqueName = uniqueName, IsManaged = false };

    private static SolutionInfo Managed(string uniqueName) =>
        new() { SolutionUniqueName = uniqueName, IsManaged = true };

    [Fact]
    public void FindProblematicSolutions_ReturnsEmpty_WhenTargetUnmanagedExistsAsUnmanagedInProd()
    {
        var target = new[] { Unmanaged("SharedLib") };
        var prod   = new[] { Unmanaged("SharedLib") };

        ProvisionCommand.FindProblematicSolutions(target, prod).Should().BeEmpty();
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsManagedInProd_WhenTargetUnmanagedIsManagedInProd()
    {
        var target = new[] { Unmanaged("MySolution") };
        var prod   = new[] { Managed("MySolution") };

        var result = ProvisionCommand.FindProblematicSolutions(target, prod);

        result.Should().HaveCount(1);
        result[0].Target.SolutionUniqueName.Should().Be("MySolution");
        result[0].Reason.Should().Be("managed in prod");
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsAbsentFromProd_WhenTargetUnmanagedMissingInProd()
    {
        var target = new[] { Unmanaged("WorkInProgress") };
        var prod   = Array.Empty<SolutionInfo>();

        var result = ProvisionCommand.FindProblematicSolutions(target, prod);

        result.Should().HaveCount(1);
        result[0].Target.SolutionUniqueName.Should().Be("WorkInProgress");
        result[0].Reason.Should().Be("absent from prod");
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsEmpty_WhenTargetHasNoUnmanagedSolutions()
    {
        var target = new[] { Managed("ManagedSolution") };
        var prod   = Array.Empty<SolutionInfo>();

        ProvisionCommand.FindProblematicSolutions(target, prod).Should().BeEmpty();
    }

    [Fact]
    public void FindProblematicSolutions_ReturnsEmpty_WhenTargetIsEmpty()
    {
        ProvisionCommand.FindProblematicSolutions(
            Array.Empty<SolutionInfo>(),
            Array.Empty<SolutionInfo>()).Should().BeEmpty();
    }

    [Fact]
    public void FindProblematicSolutions_IsCaseInsensitive_OnUniqueName()
    {
        var target = new[] { Unmanaged("mysolution") };
        var prod   = new[] { Unmanaged("MySolution") };

        ProvisionCommand.FindProblematicSolutions(target, prod).Should().BeEmpty();
    }

    [Fact]
    public void FindProblematicSolutions_ExcludesSolutionsWithNullUniqueName()
    {
        var target = new[] { new SolutionInfo { SolutionUniqueName = null, IsManaged = false } };
        var prod   = Array.Empty<SolutionInfo>();

        ProvisionCommand.FindProblematicSolutions(target, prod).Should().BeEmpty();
    }

    [Fact]
    public void ValidateForce_UnrecognizedValue_ThrowsNamingConfigAndAll()
    {
        var settings = new ProvisionCommand.Settings { Force = ["dirty"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "provision");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("config") && e.Message.Contains("all"));
    }

    [Fact]
    public void ValidateForce_Config_DoesNotThrow()
    {
        var settings = new ProvisionCommand.Settings { Force = ["config"] };

        var act = () => FlowlineSettings.ValidateForce(settings.Force, FlowlineSettings.ConfigOnlyValidSpecifiers, "provision");

        act.Should().NotThrow();
    }
}
