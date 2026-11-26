using System.ServiceModel;
using FluentAssertions;
using Flowline.Core.ConnectionReferences;
using Flowline.Core.Environments;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Xunit;

namespace Flowline.Core.Tests.ConnectionReferences;

public class ConnectionReferenceReviewTests
{
    const int CanvasAppType = 300;
    static readonly Guid RefA = Guid.NewGuid(), RefB = Guid.NewGuid(), RefC = Guid.NewGuid();
    static readonly Guid FlowX = Guid.NewGuid(), FlowY = Guid.NewGuid(), App = Guid.NewGuid(), Outside = Guid.NewGuid();

    static string FlowDefinition(params string[] logicalNames) =>
        "{\"properties\":{\"connectionReferences\":{" +
        string.Join(",", logicalNames.Select((n, i) => $"\"shared_x{i}\":{{\"connection\":{{\"connectionReferenceLogicalName\":\"{n}\"}}}}")) +
        "},\"definition\":{}}}";

    static Entity Component(Guid id, int type) =>
        new("solutioncomponent", Guid.NewGuid()) { ["objectid"] = id, ["componenttype"] = new OptionSetValue(type) };

    static Entity Reference(Guid id, string logical, string connection, DateTime created) =>
        new("connectionreference", id)
        {
            ["connectionreferencelogicalname"] = logical, ["connectionreferencedisplayname"] = logical + " display",
            ["connectorid"] = "/apis/shared_x", ["connectionid"] = connection, ["createdon"] = created,
        };

    static Entity Flow(Guid id, int state, string? clientData) =>
        new("workflow", id) { ["name"] = "flow " + id.ToString()[..4], ["statecode"] = new OptionSetValue(state), ["clientdata"] = clientData, RowVersion = "42" };

    static Entity Dep(Guid required, Guid dependent, int type) =>
        new("dependency", Guid.NewGuid())
        {
            ["requiredcomponentobjectid"] = required, ["dependentcomponentobjectid"] = dependent,
            ["dependentcomponenttype"] = new OptionSetValue(type),
        };

    static IOrganizationServiceAsync2 Service(
        IEnumerable<Entity> components, IEnumerable<Entity> refs, IEnumerable<Entity> flows, IEnumerable<Entity>? deps, QueryExpression[]? seen = null)
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<QueryExpression>();
                if (query.EntityName == "dependency" && deps is null)
                    throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault());
                return Task.FromResult(new EntityCollection(query.EntityName switch
                {
                    "solutioncomponent" => components.ToList(),
                    "connectionreference" => refs.ToList(),
                    "workflow" => flows.ToList(),
                    "dependency" => deps!.ToList(),
                    _ => [],
                }));
            });
        return service;
    }

    static readonly RoleEvidence[] NoFiles =
        [new(EnvironmentRole.Test, false, new Dictionary<string, string>()), new(EnvironmentRole.Uat, false, new Dictionary<string, string>()), new(EnvironmentRole.Prod, false, new Dictionary<string, string>())];

    static Task<ConnectionReferenceReview> Review(IOrganizationServiceAsync2 service, RoleEvidence[]? evidence = null) =>
        ConnectionReferenceReviewer.ReadAsync(service, "Contoso", evidence ?? NoFiles, CancellationToken.None);

    static readonly Entity[] Components =
    [
        Component(RefA, 10032), Component(RefB, 10032), Component(RefC, 10032),
        Component(FlowX, 29), Component(FlowY, 29), Component(App, CanvasAppType),
    ];

    static Entity[] References() =>
    [
        Reference(RefA, "new_a", "conn", new DateTime(2024, 1, 1)),
        Reference(RefB, "new_b", "conn", new DateTime(2025, 1, 1)),
        Reference(RefC, "new_c", "conn", new DateTime(2025, 6, 1)),
    ];

    [Fact]
    public async Task CarriesReferenceFlowAndUseFacts()
    {
        var evidence = new RoleEvidence[]
        {
            new(EnvironmentRole.Test, true, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["new_a"] = "t-conn" }),
            new(EnvironmentRole.Uat, false, new Dictionary<string, string>()),
            new(EnvironmentRole.Prod, true, new Dictionary<string, string>()),
        };
        var service = Service(Components, References(),
            [Flow(FlowX, 1, FlowDefinition("new_a", "new_b")), Flow(FlowY, 2, FlowDefinition("new_b"))],
            [Dep(RefA, FlowX, 29), Dep(RefB, FlowX, 29), Dep(RefB, FlowY, 29), Dep(RefC, App, CanvasAppType), Dep(RefC, Outside, CanvasAppType)]);

        var review = await Review(service, evidence);

        var a = review.References.Single(r => r.LogicalName == "new_a");
        a.Id.Should().Be(RefA);
        a.DisplayName.Should().Be("new_a display");
        a.DevConnectionId.Should().Be("conn");
        a.ComponentType.Should().Be(10032);
        a.CreatedOn.Should().Be(new DateTime(2024, 1, 1));
        a.FlowIds.Should().Equal(FlowX);
        a.Bindings[EnvironmentRole.Test].Should().Be(new RoleBinding(true, "t-conn"));
        a.Bindings[EnvironmentRole.Uat].Should().Be(new RoleBinding(false, null));
        a.Bindings[EnvironmentRole.Prod].Should().Be(new RoleBinding(true, null));

        review.References.Single(r => r.LogicalName == "new_b").FlowIds.Should().BeEquivalentTo([FlowX, FlowY]);

        // The canvas app is a component of the solution and counts; the dependent outside the solution does not.
        review.References.Single(r => r.LogicalName == "new_c").OtherDependents.Should().ContainSingle().Which.Id.Should().Be(App);

        var flowY = review.Flows.Single(f => f.Id == FlowY);
        flowY.StateCode.Should().Be(2);
        flowY.RowVersion.Should().Be("42");
        flowY.Definition.Should().Be(FlowDefinition("new_b"));
    }

    // Use comes from the flow's own definition even when the dependency table has not caught up.
    [Fact]
    public async Task FlowNamingAReference_CountsEvenWithoutADependencyRow()
    {
        var service = Service(Components, References(), [Flow(FlowX, 1, FlowDefinition("new_a"))], []);

        var review = await Review(service);

        review.References.Single(r => r.LogicalName == "new_a").FlowIds.Should().Equal(FlowX);
        review.Classification.Unused.Select(r => r.LogicalName).Should().BeEquivalentTo(["new_b", "new_c"]);
    }

    [Fact]
    public async Task DependencyQueryFault_MeansNothingIsUnused()
    {
        var service = Service(Components, References(), [Flow(FlowX, 1, FlowDefinition("new_a"))], deps: null);

        var review = await Review(service);

        review.References.Should().OnlyContain(r => r.UseUnknown);
        review.Classification.Unused.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    public async Task UnreadableFlowDefinition_MeansNothingIsUnused(string? definition)
    {
        var service = Service(Components, References(), [Flow(FlowX, 1, definition)], []);

        var review = await Review(service);

        review.Classification.Unused.Should().BeEmpty();
    }

    [Fact]
    public async Task OneDependencyQuery_ForAllReferences()
    {
        var service = Service(Components, References(), [], []);

        await Review(service);

        await service.Received(1).RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "dependency"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SolutionWithoutReferences_IsEmpty()
    {
        var service = Service([Component(FlowX, 29)], [], [Flow(FlowX, 1, FlowDefinition())], []);

        var review = await Review(service);

        review.References.Should().BeEmpty();
        review.HasFindings.Should().BeFalse();
    }
}
