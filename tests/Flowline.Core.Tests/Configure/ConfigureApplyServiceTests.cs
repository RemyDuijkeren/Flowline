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
        new(ConfigurableComponentKind.Flow, name, Guid.NewGuid(), enabled);

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
              "Flows": [ { "Name": "order_processing", "Enabled": true } ],
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
              "Flows": [ { "Name": "order_processing", "Enabled": true } ]
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
        var document = DocumentWith("""{ "Flows": [ { "Name": "missing_flow", "Enabled": true } ] }""");

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
        var document = DocumentWith("""{ "Flows": [ { "Name": "missing_flow", "Enabled": true } ] }""");

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(Service(), document, new SolutionInventory([]), RunMode.DryRun, CancellationToken.None);

        outcome.ExitCode.Should().Be(ExitCode.Inconclusive);
    }

    // AE7: dry-run reports the change set a real run would apply and writes nothing.
    [Fact]
    public async Task Apply_DryRun_ReportsChangesAndIssuesNoWrite()
    {
        var service = Service();
        var document = DocumentWith("""{ "Flows": [ { "Name": "order_processing", "Enabled": true } ] }""");
        var inventory = new SolutionInventory([Flow("order_processing", false)]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.DryRun, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().CreateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // AE2/R9: a component the file does not name is left alone and reported, never touched.
    [Fact]
    public async Task Apply_UndeclaredComponent_IsReportedAndNotTouched()
    {
        var service = Service();
        var document = DocumentWith("""{ "Flows": [ { "Name": "declared_flow", "Enabled": true } ] }""");
        var inventory = new SolutionInventory([Flow("declared_flow", false), Flow("other_flow", false)]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Undeclared.Should().ContainSingle().Which.Should().Contain("other_flow");
        await service.Received(1).UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_AmbiguousName_IsSkippedNamingTheCount()
    {
        var document = DocumentWith("""{ "Flows": [ { "Name": "shared", "Enabled": true } ] }""");
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
}
