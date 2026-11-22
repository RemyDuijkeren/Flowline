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
