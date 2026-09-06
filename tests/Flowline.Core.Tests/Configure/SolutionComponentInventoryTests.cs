using FluentAssertions;
using Flowline.Core.Configure;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class SolutionComponentInventoryTests
{
    static Entity Workflow(int statecode)
    {
        var e = new Entity("workflow", Guid.NewGuid());
        e["statecode"] = new OptionSetValue(statecode);
        return e;
    }

    static Entity Step(int statecode)
    {
        var e = new Entity("sdkmessageprocessingstep", Guid.NewGuid());
        e["statecode"] = new OptionSetValue(statecode);
        return e;
    }

    // The two tables use opposite polarity and reading either as "1 means on" is the trap these helpers
    // exist to keep out of the writers. Confirmed against Flowline's own workflow deactivation, which sets
    // statecode 0 for Draft, and against PACX's plugin step disable, which sets statecode 1 for Disabled.
    [Theory]
    [InlineData(0, false)] // Draft
    [InlineData(1, true)]  // Activated
    [InlineData(2, false)] // Suspended — neither on nor off, treated as not-active so a declared-on file acts
    public void IsWorkflowActive_MapsStatecode(int statecode, bool expected)
    {
        SolutionComponentInventory.IsWorkflowActive(Workflow(statecode)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, true)]  // Enabled
    [InlineData(1, false)] // Disabled
    public void IsStepEnabled_MapsStatecode_WithOppositePolarityToWorkflow(int statecode, bool expected)
    {
        SolutionComponentInventory.IsStepEnabled(Step(statecode)).Should().Be(expected);
    }

    [Fact]
    public void StateHelpers_DisagreeOnTheSameStatecode()
    {
        // Guards the inversion explicitly: statecode 0 means off for a workflow and on for a step.
        SolutionComponentInventory.IsWorkflowActive(Workflow(0)).Should().BeFalse();
        SolutionComponentInventory.IsStepEnabled(Step(0)).Should().BeTrue();
    }

    [Fact]
    public void MissingStatecode_ReadsAsNotActiveAndNotEnabled()
    {
        SolutionComponentInventory.IsWorkflowActive(new Entity("workflow")).Should().BeFalse();
        SolutionComponentInventory.IsStepEnabled(new Entity("sdkmessageprocessingstep")).Should().BeFalse();
    }

    static SolutionInventory Inventory(params InventoryComponent[] components) => new(components);

    static InventoryComponent Flow(string name) =>
        new(ConfigurableComponentKind.Flow, name, Guid.NewGuid(), false);

    [Fact]
    public void Match_SingleName_ReturnsTheComponent()
    {
        var inventory = Inventory(Flow("order_processing"), Flow("welcome_email"));

        var match = inventory.Match(ConfigurableComponentKind.Flow, "order_processing");

        match.Component!.Name.Should().Be("order_processing");
        match.Ambiguous.Should().BeEmpty();
        match.NotFound.Should().BeFalse();
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var inventory = Inventory(Flow("order_processing"));

        inventory.Match(ConfigurableComponentKind.Flow, "ORDER_PROCESSING").Component.Should().NotBeNull();
    }

    [Fact]
    public void Match_AbsentName_IsNotFound()
    {
        var inventory = Inventory(Flow("order_processing"));

        var match = inventory.Match(ConfigurableComponentKind.Flow, "missing_flow");

        match.NotFound.Should().BeTrue();
        match.Component.Should().BeNull();
    }

    // KTD9: Dataverse enforces uniqueness on none of these name columns, so two matches happens in normal
    // use. Picking one arbitrarily could switch on the wrong flow in production.
    [Fact]
    public void Match_TwoComponentsWithTheSameName_ReturnsBothAndSelectsNeither()
    {
        var inventory = Inventory(Flow("shared_name"), Flow("shared_name"), Flow("other"));

        var match = inventory.Match(ConfigurableComponentKind.Flow, "shared_name");

        match.Component.Should().BeNull();
        match.Ambiguous.Should().HaveCount(2);
        match.NotFound.Should().BeFalse("ambiguous is a different outcome from absent");
    }

    [Fact]
    public void Match_SameNameInAnotherKind_DoesNotCollide()
    {
        var inventory = Inventory(
            Flow("shared_name"),
            new InventoryComponent(ConfigurableComponentKind.PluginStep, "shared_name", Guid.NewGuid(), true));

        inventory.Match(ConfigurableComponentKind.Flow, "shared_name").Component.Should().NotBeNull();
        inventory.Match(ConfigurableComponentKind.PluginStep, "shared_name").Component.Should().NotBeNull();
    }

    [Fact]
    public void OfKind_FiltersToOneClass()
    {
        var inventory = Inventory(
            Flow("a"),
            new InventoryComponent(ConfigurableComponentKind.EnvironmentVariable, "cr123_Url", Guid.NewGuid(), null));

        inventory.OfKind(ConfigurableComponentKind.EnvironmentVariable).Should().ContainSingle()
            .Which.Enabled.Should().BeNull("a value class carries no state");
    }
}
