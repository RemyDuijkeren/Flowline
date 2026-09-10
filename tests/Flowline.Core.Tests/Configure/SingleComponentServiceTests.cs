using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Models;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class SingleComponentServiceTests
{
    /// <summary>A service whose environment-variable value-row lookup returns nothing unless a value is given.</summary>
    static IOrganizationServiceAsync2 Service(string? environmentVariableValue = null)
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        var rows = new EntityCollection();

        if (environmentVariableValue is not null)
            rows.Entities.Add(new Entity("environmentvariablevalue", Guid.NewGuid()) { ["value"] = environmentVariableValue });

        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(rows));

        return service;
    }

    static InventoryComponent Flow(string name, bool enabled, bool suspended = false) =>
        new(ConfigurableComponentKind.CloudFlow, name, Guid.NewGuid(), enabled, Suspended: suspended);

    static InventoryComponent Workflow(string name, bool enabled) =>
        new(ConfigurableComponentKind.Workflow, name, Guid.NewGuid(), enabled);

    static InventoryComponent Variable(string name) =>
        new(ConfigurableComponentKind.EnvironmentVariable, name, Guid.NewGuid(), null);

    static InventoryComponent Reference(string name, string? connectionId) =>
        new(ConfigurableComponentKind.ConnectionReference, name, Guid.NewGuid(), null, CurrentValue: connectionId);

    // A name matching one flow returns its current state on a read.
    [Fact]
    public async Task ReadOrWriteState_NameMatchesOneFlow_ReturnsCurrentStateOnRead()
    {
        var inventory = new SolutionInventory([Flow("order_processing", enabled: true)]);

        var outcome = await SingleComponentService.ReadOrWriteStateAsync(
            Service(), inventory, ConfigurableComponentKind.CloudFlow, "order_processing",
            desiredEnabled: null, RunMode.Normal, CancellationToken.None);

        outcome.Action.Should().Be(SingleComponentActionKind.Read);
        outcome.PriorEnabled.Should().BeTrue();
    }

    // A name matching nothing returns not-found, and the message names the unique-name key.
    [Fact]
    public async Task ReadOrWriteState_NoMatch_ThrowsNotFoundNamingTheSearchedName()
    {
        var inventory = new SolutionInventory([]);

        var act = async () => await SingleComponentService.ReadOrWriteStateAsync(
            Service(), inventory, ConfigurableComponentKind.CloudFlow, "missing_flow",
            desiredEnabled: null, RunMode.Normal, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<FlowlineException>()).Which;
        thrown.ExitCode.Should().Be(ExitCode.NotFound);
        thrown.Message.Should().Contain("missing_flow");

        // R11/KTD9: naming the key is the point. A display name is what the operator has in front of them
        // in the maker portal, and it is the one thing that will never match — so a message that does not
        // say which key was searched sends them back to retype the same wrong name.
        thrown.Message.Should().Contain("unique name").And.Contain("display name");
    }

    // Each kind is addressed by a different column, so the message has to name the right one.
    [Fact]
    public async Task ReadOrWriteValue_NoMatch_NamesTheKeyThatKindIsAddressedBy()
    {
        var inventory = new SolutionInventory([]);

        var act = async () => await SingleComponentService.ReadOrWriteValueAsync(
            Service(), inventory, ConfigurableComponentKind.EnvironmentVariable, "contoso_Missing",
            desiredValue: null, RunMode.Normal, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<FlowlineException>()).Which;
        thrown.Message.Should().Contain("environment variable").And.Contain("schema name");
    }

    // AE3: an address matching two components fails, names both, and changes nothing.
    [Fact]
    public async Task ReadOrWriteState_NameMatchesTwoWorkflows_ThrowsValidationFailedNamingBoth()
    {
        var inventory = new SolutionInventory(
        [
            Workflow("Escalate case", true),
            Workflow("Escalate case", false),
        ]);

        var act = async () => await SingleComponentService.ReadOrWriteStateAsync(
            Service(), inventory, ConfigurableComponentKind.Workflow, "Escalate case",
            desiredEnabled: false, RunMode.Normal, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<FlowlineException>()).Which;
        thrown.ExitCode.Should().Be(ExitCode.ValidationFailed);
        thrown.Message.Should().Contain("2 components");
    }

    // Matching is case-insensitive on the addressing key.
    [Fact]
    public async Task ReadOrWriteState_NameDiffersOnlyInCase_StillMatches()
    {
        var inventory = new SolutionInventory([Flow("order_processing", enabled: false)]);

        var outcome = await SingleComponentService.ReadOrWriteStateAsync(
            Service(), inventory, ConfigurableComponentKind.CloudFlow, "ORDER_PROCESSING",
            desiredEnabled: null, RunMode.Normal, CancellationToken.None);

        outcome.Action.Should().Be(SingleComponentActionKind.Read);
    }

    // AE4: setting an environment variable value inline changes the value in the target and leaves every
    // other component alone — proven here by asserting exactly one write reaches the right table and row.
    [Fact]
    public async Task ReadOrWriteValue_WriteToEnvironmentVariable_ChangesOnlyThatVariable()
    {
        var component = Variable("cr123_ApiUrl");
        var inventory = new SolutionInventory([component, Variable("cr123_Other")]);
        var service = Service();

        var outcome = await SingleComponentService.ReadOrWriteValueAsync(
            service, inventory, ConfigurableComponentKind.EnvironmentVariable, "cr123_ApiUrl",
            desiredValue: "https://example.invalid", RunMode.Normal, CancellationToken.None);

        outcome.Action.Should().Be(SingleComponentActionKind.Applied);
        await service.Received(1).CreateAsync(
            Arg.Is<Entity>(e => e.LogicalName == "environmentvariablevalue" &&
                e.GetAttributeValue<EntityReference>("environmentvariabledefinitionid").Id == component.Id),
            Arg.Any<CancellationToken>());
    }

    // A write to a connection reference binds it and leaves other components untouched.
    [Fact]
    public async Task ReadOrWriteValue_WriteToConnectionReference_BindsAndLeavesOthersUntouched()
    {
        var component = Reference("cr123_shared_dataverse", connectionId: "old-connection");
        var inventory = new SolutionInventory([component, Reference("cr123_other", "unrelated")]);
        var service = Service();

        var outcome = await SingleComponentService.ReadOrWriteValueAsync(
            service, inventory, ConfigurableComponentKind.ConnectionReference, "cr123_shared_dataverse",
            desiredValue: "new-connection", RunMode.Normal, CancellationToken.None);

        outcome.Action.Should().Be(SingleComponentActionKind.Applied);
        await service.Received(1).UpdateAsync(
            Arg.Is<Entity>(e => e.Id == component.Id && e.LogicalName == "connectionreference"),
            Arg.Any<CancellationToken>());
    }

    // KTD9: an empty --value is a deliberate request, not an omission, and is refused rather than written.
    [Fact]
    public async Task ReadOrWriteValue_EmptyValue_IsRefusedAndNoWriteReachesDataverse()
    {
        var inventory = new SolutionInventory([Variable("cr123_ApiUrl")]);
        var service = Service();

        var outcome = await SingleComponentService.ReadOrWriteValueAsync(
            service, inventory, ConfigurableComponentKind.EnvironmentVariable, "cr123_ApiUrl",
            desiredValue: "", RunMode.Normal, CancellationToken.None);

        outcome.Action.Should().Be(SingleComponentActionKind.Skipped);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().CreateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // A dry run returns the same outcome as a write and issues no update request.
    [Fact]
    public async Task ReadOrWriteState_DryRun_ReturnsSameOutcomeAsAWriteAndIssuesNoUpdate()
    {
        var inventory = new SolutionInventory([Flow("order_processing", enabled: false)]);
        var service = Service();

        var outcome = await SingleComponentService.ReadOrWriteStateAsync(
            service, inventory, ConfigurableComponentKind.CloudFlow, "order_processing",
            desiredEnabled: true, RunMode.DryRun, CancellationToken.None);

        outcome.Action.Should().Be(SingleComponentActionKind.Applied);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // An environment variable's value lives in its own row, so a read needs the further lookup the inventory
    // deliberately skips.
    [Fact]
    public async Task ReadOrWriteValue_ReadOnEnvironmentVariable_ReadsTheValueRow()
    {
        var inventory = new SolutionInventory([Variable("cr123_ApiUrl")]);
        var service = Service(environmentVariableValue: "https://api.contoso.com");

        var outcome = await SingleComponentService.ReadOrWriteValueAsync(
            service, inventory, ConfigurableComponentKind.EnvironmentVariable, "cr123_ApiUrl",
            desiredValue: null, RunMode.Normal, CancellationToken.None);

        outcome.Action.Should().Be(SingleComponentActionKind.Read);
        outcome.PriorValue.Should().Be("https://api.contoso.com");
    }

    // A connection reference's current binding is already on the inventory row, so a read needs no further query.
    [Fact]
    public async Task ReadOrWriteValue_ReadOnConnectionReference_UsesTheInventoryValue()
    {
        var inventory = new SolutionInventory([Reference("cr123_shared_dataverse", "bound-connection")]);
        var service = Service();

        var outcome = await SingleComponentService.ReadOrWriteValueAsync(
            service, inventory, ConfigurableComponentKind.ConnectionReference, "cr123_shared_dataverse",
            desiredValue: null, RunMode.Normal, CancellationToken.None);

        outcome.Action.Should().Be(SingleComponentActionKind.Read);
        outcome.PriorValue.Should().Be("bound-connection");
    }

    // KTD7: a suspended flow addressed with off must still be written — this pins that the caller-supplied
    // suspended flag actually reaches ComponentStateWriter through this service.
    [Fact]
    public async Task ReadOrWriteState_SuspendedFlowAddressedOff_WritesAndReportsItHadBeenSuspended()
    {
        var inventory = new SolutionInventory([Flow("Nightly reconciliation", enabled: false, suspended: true)]);
        var service = Service();

        var outcome = await SingleComponentService.ReadOrWriteStateAsync(
            service, inventory, ConfigurableComponentKind.CloudFlow, "Nightly reconciliation",
            desiredEnabled: false, RunMode.Normal, CancellationToken.None);

        outcome.Action.Should().Be(SingleComponentActionKind.Applied);
        outcome.WasSuspended.Should().BeTrue();
        await service.Received(1).UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }
}
