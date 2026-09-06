using Flowline.Core.Models;
using System.ServiceModel;
using FluentAssertions;
using Flowline.Core.Configure;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class ComponentValueWriterTests
{
    static InventoryComponent Variable(Guid? id = null) =>
        new(ConfigurableComponentKind.EnvironmentVariable, "cr123_ApiUrl", id ?? Guid.NewGuid(), null);

    static InventoryComponent Reference(string? currentConnectionId = null) =>
        new(ConfigurableComponentKind.ConnectionReference, "cr123_shared_dataverse", Guid.NewGuid(), null,
            CurrentValue: currentConnectionId);

    static IOrganizationServiceAsync2 ServiceReturning(params Entity[] valueRows)
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new EntityCollection(valueRows.ToList()) { EntityName = "environmentvariablevalue" }));
        return service;
    }

    static Entity ValueRow(string value)
    {
        var e = new Entity("environmentvariablevalue", Guid.NewGuid());
        e["value"] = value;
        return e;
    }

    // An environment variable is two tables. The definition holds the default and belongs to the solution;
    // the value row holds this environment's override. A first apply has no value row to update.
    [Fact]
    public async Task EnvironmentVariable_NoValueRow_CreatesOneAndNeverTouchesTheDefinition()
    {
        var service = ServiceReturning();
        var component = Variable();

        var outcome = await ComponentValueWriter.ApplyEnvironmentVariableAsync(
            service, component, "https://example.invalid", RunMode.Normal, CancellationToken.None);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Applied);
        await service.Received(1).CreateAsync(
            Arg.Is<Entity>(e => e.LogicalName == "environmentvariablevalue"), Arg.Any<CancellationToken>());
        await service.DidNotReceive().UpdateAsync(
            Arg.Is<Entity>(e => e.LogicalName == "environmentvariabledefinition"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnvironmentVariable_CreatedRow_PointsBackAtItsDefinition()
    {
        var component = Variable();
        var service = ServiceReturning();

        await ComponentValueWriter.ApplyEnvironmentVariableAsync(service, component, "v", RunMode.Normal, CancellationToken.None);

        await service.Received(1).CreateAsync(
            Arg.Is<Entity>(e =>
                e.GetAttributeValue<EntityReference>("environmentvariabledefinitionid").Id == component.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnvironmentVariable_ValueRowExists_IsUpdatedNotDuplicated()
    {
        var service = ServiceReturning(ValueRow("old"));

        var outcome = await ComponentValueWriter.ApplyEnvironmentVariableAsync(
            service, Variable(), "new", RunMode.Normal, CancellationToken.None);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Applied);
        await service.Received(1).UpdateAsync(
            Arg.Is<Entity>(e => e.LogicalName == "environmentvariablevalue"), Arg.Any<CancellationToken>());
        await service.DidNotReceive().CreateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnvironmentVariable_SameValue_ReportsUnchangedAndWritesNothing()
    {
        var service = ServiceReturning(ValueRow("same"));

        var outcome = await ComponentValueWriter.ApplyEnvironmentVariableAsync(
            service, Variable(), "same", RunMode.Normal, CancellationToken.None);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Unchanged);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().CreateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // The value row is located by its definition lookup, never by name. Confirmed against a live environment:
    // environmentvariablevalue.schemaname holds a GUID rather than the definition's schema name, so a
    // name-based match would find nothing and create a duplicate row on every run.
    [Fact]
    public async Task EnvironmentVariable_ValueRowIsFoundByDefinitionLookup()
    {
        var component = Variable();
        var service = ServiceReturning(ValueRow("x"));

        await ComponentValueWriter.ApplyEnvironmentVariableAsync(service, component, "y", RunMode.Normal, CancellationToken.None);

        await service.Received().RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q =>
                q.EntityName == "environmentvariablevalue" &&
                q.Criteria.Conditions.Any(c =>
                    c.AttributeName == "environmentvariabledefinitionid" &&
                    c.Values.Contains(component.Id))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnvironmentVariable_Fault_ReportsFailedWithoutEchoingTheValue()
    {
        var service = ServiceReturning();
        service.CreateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns<Task<Guid>>(_ => throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = 1 }, "value 'hunter2' rejected"));

        var outcome = await ComponentValueWriter.ApplyEnvironmentVariableAsync(
            service, Variable(), "hunter2", RunMode.Normal, CancellationToken.None);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Failed);
        outcome.Detail.Should().NotContain("hunter2");
        outcome.Detail.Should().Contain("cr123_ApiUrl");
    }

    [Fact]
    public async Task ConnectionReference_SameConnection_ReportsUnchanged()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        var id = Guid.NewGuid().ToString();

        var outcome = await ComponentValueWriter.ApplyConnectionReferenceAsync(
            service, Reference(currentConnectionId: id), id, RunMode.Normal, CancellationToken.None);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Unchanged);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectionReference_DifferentConnection_IsBound()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();

        var outcome = await ComponentValueWriter.ApplyConnectionReferenceAsync(
            service, Reference(currentConnectionId: "old-connection"), "new-connection", RunMode.Normal, CancellationToken.None);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Applied);
        await service.Received(1).UpdateAsync(
            Arg.Is<Entity>(e => e.LogicalName == "connectionreference"), Arg.Any<CancellationToken>());
    }

    // The likeliest real failure on the CI path: a connection reference binds a connection owned by a
    // specific principal, and an unattended identity often owns none of them. An agent only ever sees this
    // message, so it has to carry the remedy.
    [Fact]
    public void ConnectionReference_BindingFault_NamesTheConnectionAndTheSharingFix()
    {
        var fault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040217) }, "not found");

        var message = ComponentValueWriter.DescribeBindingFault(Reference(), fault);

        message.Should().Contain("cr123_shared_dataverse");
        message.Should().Contain("shared with");
    }
}
