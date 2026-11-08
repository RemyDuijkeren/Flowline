using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandPendingUpgradeTests
{
    const string Url = "https://contoso-test.crm4.dynamics.com";

    [Fact]
    public void HoldingSolutionName_AppendsPlatformSuffix()
    {
        DeployCommand.HoldingSolutionName("ContosoSales").Should().Be("ContosoSales_Upgrade");
    }

    [Fact]
    public void BuildPendingUpgradeMessage_NoHoldingSolution_ReturnsNull()
    {
        DeployCommand.BuildPendingUpgradeMessage("ContosoSales", null, "1.2.4.0", "Test", Url).Should().BeNull();
    }

    [Fact]
    public void BuildPendingUpgradeMessage_SameVersion_NamesBothRemediesByCause()
    {
        var message = DeployCommand.BuildPendingUpgradeMessage("ContosoSales", "1.2.4.0", "1.2.4.0", "Test", Url);

        message.Should().Contain("An earlier deploy staged 'ContosoSales' v1.2.4.0 in Test");
        message.Should().Contain("Solution history");
        message.Should().Contain($"'pac solution upgrade --solution-name ContosoSales --environment {Url}'");
        message.Should().Contain($"'pac solution delete --solution-name ContosoSales_Upgrade --environment {Url}'");
    }

    // The same version can be spelled differently (a leading zero here); the comparison is numeric, not textual.
    [Fact]
    public void BuildPendingUpgradeMessage_SameVersionDifferentSpelling_IsSameVersion()
    {
        DeployCommand.BuildPendingUpgradeMessage("ContosoSales", "1.2.4.0", "1.2.04.0", "Test", Url)
            .Should().Contain("An earlier deploy staged");
    }

    [Fact]
    public void BuildPendingUpgradeMessage_DifferentVersion_NamesBothVersionsAndDataLoss()
    {
        var message = DeployCommand.BuildPendingUpgradeMessage("ContosoSales", "1.2.3.0", "1.2.4.0", "Test", Url);

        message.Should().Contain("pending upgrade to v1.2.3.0 in Test");
        message.Should().Contain("this deploy is v1.2.4.0");
        message.Should().Contain("pac solution upgrade --solution-name ContosoSales");
        message.Should().Contain("pac solution delete --solution-name ContosoSales_Upgrade");
        message.Should().Contain("you lose data");
    }
}
