using FluentAssertions;
using Flowline.Services;

namespace Flowline.Tests;

public class SolutionPackServiceTests
{
    [Fact]
    public void BuildZipFileName_VersionedUnmanagedName_WhenUnmanaged()
    {
        SolutionPackService.BuildZipFileName("Contoso", "1.2.0.0", managed: false)
            .Should().Be("Contoso_1_2_0_0_unmanaged.zip");
    }

    [Fact]
    public void BuildZipFileName_VersionedManagedName_WhenManaged()
    {
        SolutionPackService.BuildZipFileName("SpotlerConnector", "3.1.1.3", managed: true)
            .Should().Be("SpotlerConnector_3_1_1_3_managed.zip");
    }

    [Fact]
    public void ResolveZipPath_DefaultsToArtifactsNextToSolution_WhenNoOutputGiven()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");

        SolutionPackService.ResolveZipPath(root, output: null, "Contoso", "1.0.0.0", managed: false)
            .Should().Be(Path.Combine(root, "artifacts", "Contoso_1_0_0_0_unmanaged.zip"));
    }

    [Fact]
    public void ResolveZipPath_UsesOutputFolder_WhenGiven()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var output = Path.Combine(Path.GetTempPath(), "drop");

        SolutionPackService.ResolveZipPath(root, output, "Contoso", "1.0.0.0", managed: true)
            .Should().Be(Path.Combine(output, "Contoso_1_0_0_0_managed.zip"));
    }

    [Fact]
    public void ResolveZipPath_ResolvesRelativeOutputAgainstCurrentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");

        SolutionPackService.ResolveZipPath(root, "drop", "Contoso", "1.0.0.0", managed: false)
            .Should().Be(Path.Combine(Path.GetFullPath("drop"), "Contoso_1_0_0_0_unmanaged.zip"));
    }
}

public class SolutionPackServiceGuardMessageTests
{
    [Theory]
    [InlineData("pack")]
    [InlineData("deploy")]
    public void BuildDirtyTreeMessage_NamesTheCommand(string command)
    {
        SolutionPackService.BuildDirtyTreeMessage(["Solution/src/Other/Solution.xml"], command)
            .Should().Be($"Uncommitted changes in Solution/src/Other/Solution.xml — commit or stash first, then {command}.");
    }

    [Fact]
    public void BuildDirtyTreeMessage_ShowsThreePathsAndACount_WhenMoreChanged()
    {
        SolutionPackService.BuildDirtyTreeMessage(["a", "b", "c", "d", "e"], "pack")
            .Should().Be("Uncommitted changes in a, b, c (+2 more) — commit or stash first, then pack.");
    }

    [Fact]
    public void BuildDriftMessage_ForPack_SaysTheZipWouldHoldOlderCopies()
    {
        SolutionPackService.BuildDriftMessage("pack")
            .Should().Be("Local changes not in Dataverse — the zip would hold the older committed copies. Run 'push' then 'pull' to capture them, or use --force drift to skip.");
    }

    [Fact]
    public void BuildDriftMessage_ForDeploy_KeepsTheRevertWarning()
    {
        SolutionPackService.BuildDriftMessage("deploy")
            .Should().Be("Local changes not in Dataverse — deploy would revert them. Run 'push' then 'pull' to capture them, or use --force drift to skip.");
    }
}
