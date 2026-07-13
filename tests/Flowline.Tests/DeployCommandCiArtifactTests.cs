using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandCiArtifactTests
{
    private const string PackagePath = "solutions/Contoso/artifacts/Contoso_unmanaged.zip";
    private const string SolutionName = "Contoso";

    [Fact]
    public void BuildAzureDevOpsArtifactUploadLine_ReturnsExpectedVsoFormat()
    {
        var line = DeployCommand.BuildAzureDevOpsArtifactUploadLine(PackagePath, SolutionName);

        line.Should().Be($"##vso[artifact.upload artifactname={SolutionName}]{PackagePath}");
    }

    [Fact]
    public void BuildGitHubActionsOutputLine_ReturnsExpectedOutputFormat()
    {
        var line = DeployCommand.BuildGitHubActionsOutputLine(PackagePath, SolutionName);

        line.Should().Be($"artifact-path-{SolutionName}={PackagePath}");
    }

    [Fact]
    public void BuildGitHubActionsOutputLine_QualifiesKeyBySolutionName_ToAvoidClobberingSiblingOutputs()
    {
        var first = DeployCommand.BuildGitHubActionsOutputLine(PackagePath, "SolutionA");
        var second = DeployCommand.BuildGitHubActionsOutputLine(PackagePath, "SolutionB");

        first.Should().NotBe(second);
        first.Should().StartWith("artifact-path-SolutionA=");
        second.Should().StartWith("artifact-path-SolutionB=");
    }
}
