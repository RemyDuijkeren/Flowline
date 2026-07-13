using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandFirstImportTests
{
    [Fact]
    public void BuildFirstImportPrompt_Managed_NamesIrreversibleModeSwitch()
    {
        var prompt = DeployCommand.BuildFirstImportPrompt("ContosoSales", "Production", includeManaged: true);

        prompt.Should().Contain("ContosoSales");
        prompt.Should().Contain("Production");
        prompt.Should().Contain("managed");
        prompt.Should().Contain("can't be changed later without uninstalling");
    }

    [Fact]
    public void BuildFirstImportPrompt_Unmanaged_NamesManualRemovalForModeSwitch()
    {
        var prompt = DeployCommand.BuildFirstImportPrompt("ContosoSales", "UAT", includeManaged: false);

        prompt.Should().Contain("ContosoSales");
        prompt.Should().Contain("UAT");
        prompt.Should().Contain("unmanaged");
        prompt.Should().Contain("removed manually first");
    }
}
