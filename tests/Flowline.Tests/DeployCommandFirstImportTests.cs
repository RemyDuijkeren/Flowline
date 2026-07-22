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

    [Fact]
    public void BuildFirstImportDryRunNote_Managed_NamesThatRealDeployWillConfirm()
    {
        var note = DeployCommand.BuildFirstImportDryRunNote("ContosoSales", "Production", includeManaged: true);

        note.Should().Contain("ContosoSales");
        note.Should().Contain("Production");
        note.Should().Contain("managed");
        note.Should().Contain("real deploy will ask you to confirm");
    }

    [Fact]
    public void BuildFirstImportDryRunNote_Unmanaged_NamesThatRealDeployWillConfirm()
    {
        var note = DeployCommand.BuildFirstImportDryRunNote("ContosoSales", "UAT", includeManaged: false);

        note.Should().Contain("ContosoSales");
        note.Should().Contain("UAT");
        note.Should().Contain("unmanaged");
        note.Should().Contain("real deploy will ask you to confirm");
    }
}
