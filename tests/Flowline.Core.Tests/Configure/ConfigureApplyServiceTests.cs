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

public class ConfigureApplyServiceTests
{
    static IOrganizationServiceAsync2 Service()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new EntityCollection()));
        return service;
    }

    static InventoryComponent Flow(string name, bool enabled) =>
        new(ConfigurableComponentKind.CloudFlow, name, Guid.NewGuid(), enabled);

    static InventoryComponent SuspendedFlow(string name) =>
        new(ConfigurableComponentKind.CloudFlow, name, Guid.NewGuid(), false, Suspended: true);

    static InventoryComponent Variable(string name) =>
        new(ConfigurableComponentKind.EnvironmentVariable, name, Guid.NewGuid(), null);

    static InventoryComponent Reference(string name) =>
        new(ConfigurableComponentKind.ConnectionReference, name, Guid.NewGuid(), null);

    static SettingsDocument DocumentWith(string json) => SettingsFileReader.Parse(json);

    // AE4: the file lists the flow first, but the connection reference is applied first anyway. A flow
    // activated before its binding is set fails for a reason the file did not cause.
    [Fact]
    public async Task Apply_RunsValueTierBeforeStateTier_WhateverOrderTheFileUses()
    {
        var service = Service();
        var order = new List<string>();
        service.When(s => s.UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()))
            .Do(call => order.Add(call.Arg<Entity>().LogicalName));

        var document = DocumentWith("""
            {
              "CloudFlows": { "order_processing": true },
              "ConnectionReferences": [ { "LogicalName": "cr_dataverse", "ConnectionId": "abc123" } ]
            }
            """);

        var inventory = new SolutionInventory([Flow("order_processing", false), Reference("cr_dataverse")]);

        await new ConfigureApplyService().ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        order.Should().ContainInOrder("connectionreference", "workflow");
    }

    // R7 as it now stands: no cross-component dependency is inferred, so a failed binding does not suppress
    // the flow tier. Each component is attempted and reported on its own.
    [Fact]
    public async Task Apply_FailedConnectionReference_DoesNotSuppressTheFlowTier()
    {
        var service = Service();
        service.When(s => s.UpdateAsync(Arg.Is<Entity>(e => e.LogicalName == "connectionreference"), Arg.Any<CancellationToken>()))
            .Do(_ => throw new System.ServiceModel.FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = 1 }, "no"));

        var document = DocumentWith("""
            {
              "ConnectionReferences": [ { "LogicalName": "cr_dataverse", "ConnectionId": "abc123" } ],
              "CloudFlows": { "order_processing": true }
            }
            """);

        var inventory = new SolutionInventory([Reference("cr_dataverse"), Flow("order_processing", false)]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Failed.Should().Be(1);
        outcome.Applied.Should().Be(1, "the flow is still attempted");
    }

    [Fact]
    public async Task Apply_DeclaredComponentAbsentFromTheTarget_IsSkipped()
    {
        var document = DocumentWith("""{ "CloudFlows": { "missing_flow": true } }""");

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(Service(), document, new SolutionInventory([]), RunMode.Normal, CancellationToken.None);

        outcome.Skipped.Should().Be(1);
        outcome.ExitCode.Should().Be(ExitCode.Inconclusive);
        outcome.Components[0].Detail.Should().Contain("missing_flow");
    }

    // KTD11: the same all-skipped run under dry-run is still not a pass signal, or the preflight is useless
    // for catching a wrong file pointed at a wrong environment.
    [Fact]
    public async Task Apply_AllSkippedUnderDryRun_IsStillInconclusive()
    {
        var document = DocumentWith("""{ "CloudFlows": { "missing_flow": true } }""");

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(Service(), document, new SolutionInventory([]), RunMode.DryRun, CancellationToken.None);

        outcome.ExitCode.Should().Be(ExitCode.Inconclusive);
    }

    // AE7, the half that shipped wrong first: dry-run reported every declared component as a change, because
    // it short-circuited ahead of the comparison instead of running it. Caught against a real environment,
    // where a file pulled seconds earlier previewed as six changes. A preview that cannot tell a change from
    // a no-op is worse than none — it is the thing an operator reads before touching production.
    [Fact]
    public async Task Apply_DryRun_ComponentsAlreadyInTheirDeclaredState_AreUnchangedNotChanges()
    {
        var service = Service();
        var document = DocumentWith("""
            {
              "CloudFlows": { "order_processing": true },
              "ConnectionReferences": [ { "LogicalName": "cr_dataverse", "ConnectionId": "abc123" } ]
            }
            """);

        var inventory = new SolutionInventory(
        [
            Flow("order_processing", true),
            new InventoryComponent(ConfigurableComponentKind.ConnectionReference, "cr_dataverse", Guid.NewGuid(), null,
                CurrentValue: "abc123"),
        ]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.DryRun, CancellationToken.None);

        outcome.Unchanged.Should().Be(2);
        outcome.Applied.Should().Be(0);
    }

    [Fact]
    public async Task Apply_DryRun_ReportsOnlyTheComponentsThatWouldActuallyChange()
    {
        var document = DocumentWith("""
            {
              "CloudFlows": {
                "already_on": true,
                "currently_off": true
              }
            }
            """);

        var inventory = new SolutionInventory([Flow("already_on", true), Flow("currently_off", false)]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(Service(), document, inventory, RunMode.DryRun, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        outcome.Unchanged.Should().Be(1);
        outcome.Components.Single(c => c.Outcome == ComponentOutcomeKind.Applied).Name.Should().Be("currently_off");
    }

    // AE7: dry-run reports the change set a real run would apply and writes nothing.
    [Fact]
    public async Task Apply_DryRun_ReportsChangesAndIssuesNoWrite()
    {
        var service = Service();
        var document = DocumentWith("""{ "CloudFlows": { "order_processing": true } }""");
        var inventory = new SolutionInventory([Flow("order_processing", false)]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.DryRun, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().CreateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // R20: business rules, business process flows and actions are declarable, so a file that names one
    // governs it exactly as it governs a flow.
    [Theory]
    [InlineData("BusinessRules", ConfigurableComponentKind.BusinessRule, "contoso_ShowHideRit")]
    [InlineData("BusinessProcessFlows", ConfigurableComponentKind.BusinessProcessFlow, "Rijopdracht intake")]
    [InlineData("Actions", ConfigurableComponentKind.Action, "contoso_RecalculateTotals")]
    public async Task Apply_ADeclaredProcessClass_IsSwitchedLikeAnyOther(
        string section, ConfigurableComponentKind kind, string name)
    {
        var service = Service();
        var document = DocumentWith($$"""{ "{{section}}": { "{{name}}": false } }""");
        var inventory = new SolutionInventory([new InventoryComponent(kind, name, Guid.NewGuid(), true)]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        await service.Received(1).UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // One of them left out of the file is undeclared, same as any other class. It used to be filtered out
    // of that report because no section could hold it.
    [Fact]
    public async Task Apply_AnUndeclaredProcessClass_IsReportedLikeAnyOther()
    {
        var service = Service();
        var document = DocumentWith("""{ "CloudFlows": { "declared_flow": true } }""");
        var inventory = new SolutionInventory([
            Flow("declared_flow", true),
            new InventoryComponent(ConfigurableComponentKind.BusinessRule, "contoso_ShowHideRit", Guid.NewGuid(), true),
        ]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Undeclared.Should().ContainSingle().Which.Should().Contain("contoso_ShowHideRit");
    }

    // R20: a swap has to raise the new one before lowering the old, because some components cannot be
    // switched off while they are the last of their kind still on. Ordering it here is what makes the
    // swap declarable in one file.
    [Fact]
    public async Task Apply_RunsEveryActivationBeforeAnyDeactivation()
    {
        var service = Service();
        var order = new List<bool>();
        service.UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var e = call.Arg<Entity>();
                order.Add(e.GetAttributeValue<OptionSetValue>("statecode")?.Value == 1);
                return Task.CompletedTask;
            });

        var document = DocumentWith("""
            { "CloudFlows": { "turn_off": false }, "Workflows": { "turn_on": true } }
            """);
        var inventory = new SolutionInventory([
            Flow("turn_off", true),
            new InventoryComponent(ConfigurableComponentKind.Workflow, "turn_on", Guid.NewGuid(), false),
        ]);

        await new ConfigureApplyService().ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        // The activation is in the Workflows section, which is listed after CloudFlows; it still goes first.
        order.Should().Equal(true, false);
    }

    // AE2/R9: a component the file does not name is left alone and reported, never touched.
    [Fact]
    public async Task Apply_UndeclaredComponent_IsReportedAndNotTouched()
    {
        var service = Service();
        var document = DocumentWith("""{ "CloudFlows": { "declared_flow": true } }""");
        var inventory = new SolutionInventory([Flow("declared_flow", false), Flow("other_flow", false)]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Undeclared.Should().ContainSingle().Which.Should().Contain("other_flow");
        await service.Received(1).UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_AmbiguousName_IsSkippedNamingTheCount()
    {
        var document = DocumentWith("""{ "CloudFlows": { "shared": true } }""");
        var inventory = new SolutionInventory([Flow("shared", false), Flow("shared", false)]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(Service(), document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Skipped.Should().Be(1);
        outcome.Components[0].Detail.Should().Contain("2 components");
    }

    // pac solution create-settings emits an empty ConnectionId for a reference nobody has filled in.
    // Binding to nothing would clear a working binding.
    [Fact]
    public async Task Apply_ConnectionReferenceWithNoConnectionId_IsLeftAlone()
    {
        var service = Service();
        var document = DocumentWith("""
            { "ConnectionReferences": [ { "LogicalName": "cr_dataverse", "ConnectionId": "" } ] }
            """);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, new SolutionInventory([Reference("cr_dataverse")]), RunMode.Normal, CancellationToken.None);

        outcome.Components.Should().BeEmpty();
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ReadValues_ProjectsFromPassThroughWithoutRemovingTheSection()
    {
        var document = DocumentWith("""
            { "EnvironmentVariables": [ { "SchemaName": "cr123_Url", "Value": "https://x.invalid" } ] }
            """);

        var values = ConfigureApplyService.ReadValues(document, "EnvironmentVariables", "SchemaName", "Value");

        values.Should().ContainSingle().Which.Should().Be(new DeclaredValue("cr123_Url", "https://x.invalid"));
        document.PassThrough.Should().ContainKey("EnvironmentVariables",
            "PAC owns the section and a write must return it untouched");
    }

    [Fact]
    public async Task Apply_EnvironmentVariable_ResolvesAgainstTheInventory()
    {
        var service = Service();
        var document = DocumentWith("""
            { "EnvironmentVariables": [ { "SchemaName": "cr123_Url", "Value": "https://x.invalid" } ] }
            """);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, new SolutionInventory([Variable("cr123_Url")]), RunMode.Normal, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        await service.Received(1).CreateAsync(
            Arg.Is<Entity>(e => e.LogicalName == "environmentvariablevalue"), Arg.Any<CancellationToken>());
    }

    // R6/AE1 end to end. Idempotence was only ever proved one writer at a time, against hand-built
    // "already matches" inventories — which is how a caller that turned every connection reference into a
    // write got through the suite. This runs the real service over all four classes, then re-runs it against
    // the environment the first run produced, and demands that the second run touch nothing at all.
    [Fact]
    public async Task Apply_RunTwiceAcrossEveryClass_SecondRunWritesNothing()
    {
        const string connectionId = "shared-commondataser-863397d0";
        var document = DocumentWith($$"""
            {
              "EnvironmentVariables": [ { "SchemaName": "cr123_Url", "Value": "https://api.contoso.com" } ],
              "ConnectionReferences": [ { "LogicalName": "cr123_dataverse", "ConnectionId": "{{connectionId}}" } ],
              "CloudFlows": { "Nightly reconciliation": false },
              "PluginSteps": { "Contoso.Plugins.OnCreate: Create of account": false }
            }
            """);

        // First run: nothing is in its declared state yet.
        var first = Service();
        var before = new SolutionInventory(
        [
            Variable("cr123_Url"),
            Reference("cr123_dataverse"),
            Flow("Nightly reconciliation", true),
            new InventoryComponent(ConfigurableComponentKind.PluginStep,
                "Contoso.Plugins.OnCreate: Create of account", Guid.NewGuid(), true),
        ]);

        var firstOutcome = await new ConfigureApplyService()
            .ApplyAsync(first, document, before, RunMode.Normal, CancellationToken.None);

        firstOutcome.Applied.Should().Be(4);
        firstOutcome.Unchanged.Should().Be(0);

        // Second run against the environment the first one produced: the value row now exists and holds the
        // declared value, the reference is bound, and both state components are off.
        var second = Service();
        var valueRow = new EntityCollection();
        valueRow.Entities.Add(new Entity("environmentvariablevalue", Guid.NewGuid())
        {
            ["value"] = "https://api.contoso.com",
        });
        second.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(valueRow));

        var after = new SolutionInventory(
        [
            Variable("cr123_Url"),
            new InventoryComponent(ConfigurableComponentKind.ConnectionReference,
                "cr123_dataverse", Guid.NewGuid(), null, CurrentValue: connectionId),
            Flow("Nightly reconciliation", false),
            new InventoryComponent(ConfigurableComponentKind.PluginStep,
                "Contoso.Plugins.OnCreate: Create of account", Guid.NewGuid(), false),
        ]);

        var secondOutcome = await new ConfigureApplyService()
            .ApplyAsync(second, document, after, RunMode.Normal, CancellationToken.None);

        secondOutcome.Applied.Should().Be(0, "applying the same file twice must change nothing the second time");
        secondOutcome.Unchanged.Should().Be(4);
        secondOutcome.ExitCode.Should().Be(ExitCode.Success);
        await second.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await second.DidNotReceive().CreateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // The writer has always handled Suspended; nothing supplied it. The flag has to survive the whole way
    // from the inventory read to the writer, or a suspended flow declared off silently stays suspended.
    [Fact]
    public async Task Apply_SuspendedFlowDeclaredOff_IsMovedToDraft()
    {
        var service = Service();
        var document = DocumentWith("""
            { "CloudFlows": { "Nightly reconciliation": false } }
            """);

        var outcome = await new ConfigureApplyService().ApplyAsync(
            service, document, new SolutionInventory([SuspendedFlow("Nightly reconciliation")]),
            RunMode.Normal, CancellationToken.None);

        outcome.Applied.Should().Be(1, "Suspended is not the same as off, so this is a real change");
        await service.Received(1).UpdateAsync(
            Arg.Is<Entity>(e => e.LogicalName == "workflow"
                                && ((OptionSetValue)e["statecode"]).Value == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_SuspendedFlowDeclaredOn_ActivatesAndReportsThePriorState()
    {
        var service = Service();
        var document = DocumentWith("""
            { "CloudFlows": { "Nightly reconciliation": true } }
            """);

        var outcome = await new ConfigureApplyService().ApplyAsync(
            service, document, new SolutionInventory([SuspendedFlow("Nightly reconciliation")]),
            RunMode.Normal, CancellationToken.None);

        outcome.Components.Should().ContainSingle().Which.WasSuspended.Should().BeTrue(
            "the run has to say the flow had stopped itself, or a re-suspension looks like a new fault");
    }

    // A flow that is simply off stays the ordinary case: nothing to do, and no suspended claim on the report.
    [Fact]
    public async Task Apply_DraftFlowDeclaredOff_IsUnchangedAndNotReportedAsSuspended()
    {
        var service = Service();
        var document = DocumentWith("""
            { "CloudFlows": { "Nightly reconciliation": false } }
            """);

        var outcome = await new ConfigureApplyService().ApplyAsync(
            service, document, new SolutionInventory([Flow("Nightly reconciliation", false)]),
            RunMode.Normal, CancellationToken.None);

        outcome.Unchanged.Should().Be(1);
        outcome.Components.Single().WasSuspended.Should().BeFalse();
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // An interrupted run has already written to a live environment. What it managed to do is the one thing
    // the operator cannot recover from the exit code, so it survives the cancellation.
    [Fact]
    public async Task Apply_CancelledMidRun_ReturnsWhatItAlreadyDidAndSaysItStopped()
    {
        var service = Service();
        var calls = 0;
        service.UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? Task.CompletedTask : throw new OperationCanceledException());

        var document = DocumentWith("""
            {
              "CloudFlows": {
                "First": true,
                "Second": true
              }
            }
            """);

        var outcome = await new ConfigureApplyService().ApplyAsync(
            service, document,
            new SolutionInventory([Flow("First", false), Flow("Second", false)]),
            RunMode.Normal, CancellationToken.None);

        outcome.Cancelled.Should().BeTrue();
        outcome.Applied.Should().Be(1, "the first flow was written before the interruption");
        outcome.Components.Should().ContainSingle().Which.Name.Should().Be("First");
        outcome.ExitCode.Should().Be(ExitCode.Cancelled);
    }

    // Only a Dataverse fault is isolated to its own component, matching OrphanCleanupService. Anything else
    // is a bug or a broken client, and swallowing it would report a clean run over a broken one — so it
    // travels, and this pins that the cancellation handling above did not quietly widen into a catch-all.
    [Fact]
    public async Task Apply_NonDataverseException_IsNotSwallowed()
    {
        var service = Service();
        service.UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("client is broken"));

        var document = DocumentWith("""
            { "CloudFlows": { "Nightly reconciliation": true } }
            """);

        var act = async () => await new ConfigureApplyService().ApplyAsync(
            service, document, new SolutionInventory([Flow("Nightly reconciliation", false)]),
            RunMode.Normal, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // The Workflows section reaches the same writer and the same table as CloudFlows, but only if the apply
    // walks it. Leaving it out would silently ignore every classic workflow the file declares.
    [Fact]
    public async Task Apply_WorkflowsSection_IsAppliedLikeCloudFlows()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        var document = DocumentWith("""{ "Workflows": { "Escalate case": true } }""");
        var inventory = new SolutionInventory(
        [
            new InventoryComponent(ConfigurableComponentKind.Workflow, "Escalate case", Guid.NewGuid(), false),
        ]);

        var outcome = await new ConfigureApplyService().ApplyAsync(
            service, document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Components.Should().ContainSingle()
            .Which.Outcome.Should().Be(ComponentOutcomeKind.Applied);
        await service.Received(1).UpdateAsync(
            Arg.Is<Entity>(e => e.LogicalName == "workflow"), Arg.Any<CancellationToken>());
    }

    // A cloud flow named in the Workflows section addresses nothing, because Match is scoped by kind. That is
    // the point of two sections: a name in the wrong one is reported, not applied to whatever shares it.
    [Fact]
    public async Task Apply_CloudFlowDeclaredUnderWorkflows_IsSkippedRatherThanMatched()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        var document = DocumentWith("""{ "Workflows": { "Order Processing": true } }""");
        var inventory = new SolutionInventory([Flow("Order Processing", false)]);

        var outcome = await new ConfigureApplyService().ApplyAsync(
            service, document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Components.Should().ContainSingle()
            .Which.Outcome.Should().Be(ComponentOutcomeKind.Skipped);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }
}
