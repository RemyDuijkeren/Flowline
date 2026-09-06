using Flowline.Core.Models;
using System.ServiceModel;
using FluentAssertions;
using Flowline.Core.Configure;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using NSubstitute;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class ComponentStateWriterTests
{
    static InventoryComponent Flow(bool enabled) =>
        new(ConfigurableComponentKind.Flow, "order_processing", Guid.NewGuid(), enabled);

    static InventoryComponent Step(bool enabled) =>
        new(ConfigurableComponentKind.PluginStep, "Contoso: Create of account", Guid.NewGuid(), enabled);

    static int State(Entity e) => e.GetAttributeValue<OptionSetValue>("statecode").Value;
    static int Status(Entity e) => e.GetAttributeValue<OptionSetValue>("statuscode").Value;

    // The state/status pairs are confirmed against Flowline's own workflow deactivation and against PACX's
    // workflow and plugin step commands. Getting one backwards silently inverts every declaration in a file,
    // which is why they are pinned here rather than left to the writer's shape.
    [Fact]
    public void BuildStateUpdate_FlowOn_ActivatesWithMatchingStatus()
    {
        var update = ComponentStateWriter.BuildStateUpdate(Flow(false), enabled: true)!;

        update.LogicalName.Should().Be("workflow");
        State(update).Should().Be(1);
        Status(update).Should().Be(2);
    }

    [Fact]
    public void BuildStateUpdate_FlowOff_ReturnsToDraft()
    {
        var update = ComponentStateWriter.BuildStateUpdate(Flow(true), enabled: false)!;

        State(update).Should().Be(0);
        Status(update).Should().Be(1);
    }

    [Fact]
    public void BuildStateUpdate_StepOn_UsesTheOppositePolarityToAFlow()
    {
        var update = ComponentStateWriter.BuildStateUpdate(Step(false), enabled: true)!;

        update.LogicalName.Should().Be("sdkmessageprocessingstep");
        State(update).Should().Be(0, "a step is enabled at statecode 0, the opposite of a workflow");
        Status(update).Should().Be(1);
    }

    [Fact]
    public void BuildStateUpdate_StepOff_Disables()
    {
        var update = ComponentStateWriter.BuildStateUpdate(Step(true), enabled: false)!;

        State(update).Should().Be(1);
        Status(update).Should().Be(2);
    }

    [Fact]
    public void BuildStateUpdate_OnTheSameDesiredState_StillProducesTheCorrectPair()
    {
        // Guards the inversion directly: turning a flow on and a step on must not produce the same statecode.
        State(ComponentStateWriter.BuildStateUpdate(Flow(false), true)!).Should()
            .NotBe(State(ComponentStateWriter.BuildStateUpdate(Step(false), true)!));
    }

    [Fact]
    public void BuildStateUpdate_ValueClass_HasNoStateToWrite()
    {
        var variable = new InventoryComponent(ConfigurableComponentKind.EnvironmentVariable, "cr123_Url", Guid.NewGuid(), null);

        ComponentStateWriter.BuildStateUpdate(variable, enabled: true).Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_AlreadyInDeclaredState_ReportsUnchangedAndWritesNothing()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();

        var outcome = await ComponentStateWriter.ApplyAsync(service, Flow(true), desiredEnabled: true, RunMode.Normal, CancellationToken.None);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Unchanged);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_StateDiffers_WritesAndReportsApplied()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();

        var outcome = await ComponentStateWriter.ApplyAsync(service, Flow(false), desiredEnabled: true, RunMode.Normal, CancellationToken.None);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Applied);
        await service.Received(1).UpdateAsync(Arg.Is<Entity>(e => State(e) == 1), Arg.Any<CancellationToken>());
    }

    // KTD8: Suspended is neither on nor off. A file declaring the flow on activates it, and the prior
    // Suspended state is carried on the outcome so a re-suspension by the platform stays visible.
    [Fact]
    public async Task ApplyAsync_SuspendedFlowDeclaredOn_ActivatesAndReportsThePriorState()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();

        var outcome = await ComponentStateWriter.ApplyAsync(
            service, Flow(false), desiredEnabled: true, RunMode.Normal, CancellationToken.None, currentlySuspended: true);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Applied);
        outcome.WasSuspended.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAsync_SuspendedFlowDeclaredOff_IsStillWritten()
    {
        // Enabled flattens Suspended to not-active, so without the explicit flag this would look unchanged
        // and a suspended flow would never be returned to a clean Draft.
        var service = Substitute.For<IOrganizationServiceAsync2>();

        var outcome = await ComponentStateWriter.ApplyAsync(
            service, Flow(false), desiredEnabled: false, RunMode.Normal, CancellationToken.None, currentlySuspended: true);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Applied);
    }

    [Fact]
    public async Task ApplyAsync_DataverseFault_ReportsFailedAndKeepsRunning()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040265) }, "boom"));

        var outcome = await ComponentStateWriter.ApplyAsync(service, Flow(false), true, RunMode.Normal, CancellationToken.None);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Failed);
        outcome.Detail.Should().Contain("order_processing");
    }

    [Fact]
    public void DescribeFault_DependencyBlocked_NamesTheRemedy()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = unchecked((int)0x80047002) }, "blocked");

        var message = ComponentStateWriter.DescribeFault(Flow(false), fault);

        message.Should().Contain("dependency");
        message.Should().Contain("re-run");
    }

    // R10a: a Dataverse fault on a value write commonly quotes the rejected value, so the fault text is
    // never echoed into a report.
    [Fact]
    public void DescribeFault_DoesNotEchoTheFaultMessage()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = 1 }, "the value 'hunter2' was rejected");

        ComponentStateWriter.DescribeFault(Flow(false), fault).Should().NotContain("hunter2");
    }
}
