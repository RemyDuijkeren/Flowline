using FluentAssertions;
using Flowline.Commands;
using Flowline.Core.Environments;

namespace Flowline.Tests;

public class DeployCommandTagHintTests
{
    [Fact]
    public void BuildDeployedTagHint_RoleAndCommit_GivesRunnableTagAndPush() =>
        DeployCommand.BuildDeployedTagHint(EnvironmentRole.Prod, "abc123", "ContosoSales", "1.2.4.0")
            .Should().Be("Mark it in git: git tag -f -a deployed/prod -m \"ContosoSales 1.2.4.0\" abc123 && git push -f origin refs/tags/deployed/prod");

    [Theory]
    [InlineData(null, "abc123")] // URL target: no role to name the tag after
    [InlineData(EnvironmentRole.Test, null)] // --path route: no commit known
    [InlineData(EnvironmentRole.Test, "")]
    public void BuildDeployedTagHint_MissingRoleOrCommit_GivesNoHint(EnvironmentRole? role, string? commitSha) =>
        DeployCommand.BuildDeployedTagHint(role, commitSha, "ContosoSales", "1.2.4.0").Should().BeNull();
}
