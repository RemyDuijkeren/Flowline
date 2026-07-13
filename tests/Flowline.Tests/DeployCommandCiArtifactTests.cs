using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandCiArtifactTests
{
    private const string PackagePath = "solutions/Contoso/artifacts/Contoso_unmanaged.zip";
    private const string SolutionName = "Contoso";
    private const string Version = "1.2.3.45";

    [Fact]
    public void BuildAzureDevOpsArtifactUploadLine_ReturnsExpectedVsoFormat()
    {
        var line = DeployCommand.BuildAzureDevOpsArtifactUploadLine(PackagePath, SolutionName, Version);

        line.Should().Be($"##vso[artifact.upload artifactname={SolutionName}-{Version}]{PackagePath}");
    }

    [Fact]
    public void BuildAzureDevOpsArtifactUploadLine_QualifiesArtifactNameByVersion_WithoutTouchingTheFilePath()
    {
        var line = DeployCommand.BuildAzureDevOpsArtifactUploadLine(PackagePath, SolutionName, Version);

        line.Should().Contain($"artifactname={SolutionName}-{Version}]");
        line.Should().EndWith(PackagePath);
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
