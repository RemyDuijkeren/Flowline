using System.ServiceModel;
using System.Text.Json.Nodes;
using FluentAssertions;
using Flowline.Core.Configure;
using Flowline.Core.ConnectionReferences;
using Flowline.Core.Environments;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Xunit;

namespace Flowline.Core.Tests.ConnectionReferences;

public sealed class ConnectionReferenceCleanupTests : IDisposable
{
    const int RowVersionMismatch = -2147088254;
    const int RefType = 10032;

    readonly string _folder = Directory.CreateTempSubdirectory("flowline-cleanup-").FullName;
    readonly List<string> _log = [];
    readonly List<UpdateRequest> _updates = [];
    readonly List<Entity> _plainUpdates = [];
    readonly Dictionary<Guid, int> _failUpdate = [];                       // flow id -> fault code
    readonly Dictionary<Guid, List<Entity>> _dependents = [];              // reference id -> dependents
    readonly Dictionary<Guid, List<string>> _memberships = [];             // reference id -> solutions
    readonly HashSet<Guid> _deleteFaultsWithDependency = [];
    readonly string _settingsFile;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    static readonly Guid IdA = Guid.NewGuid(), IdB = Guid.NewGuid(), IdC = Guid.NewGuid();
    static readonly Guid F1 = Guid.NewGuid(), F2 = Guid.NewGuid(), F3 = Guid.NewGuid(), F4 = Guid.NewGuid();

    static ConnectionReference Ref(Guid id, string logical, params Guid[] flows) =>
        new(id, logical, logical + " display", "/apis/shared_x", "conn", new DateTime(2024, 1, 1), RefType,
            new Dictionary<EnvironmentRole, RoleBinding>(), flows, [], false);

    static readonly ConnectionReference A = Ref(IdA, "new_a", F1);
    static readonly ConnectionReference B = Ref(IdB, "new_b", F1, F2, F3, F4);

    static string Definition(params string[] names) =>
        "{\"properties\":{\"connectionReferences\":{" +
        string.Join(",", names.Select((n, i) => $"\"shared_x{(i == 0 ? "" : "_" + i)}\":{{\"connection\":{{\"connectionReferenceLogicalName\":\"{n}\",\"name\":\"abc\"}}}}")) +
        "},\"definition\":{\"actions\":{}}}}";

    static CloudFlow Flow(Guid id, int state, string? definition = null) =>
        new(id, "flow " + id.ToString()[..4], state, "7", definition ?? Definition("new_b"));

    static ConnectionReferenceReview Review(IEnumerable<ConnectionReference> refs, params CloudFlow[] flows) =>
        new(refs.ToList(), flows, new Classification([], [], [], [], []));

    static readonly SameConnectionGroup Group = new("/apis/shared_x", A, [B], [F1, F2, F3, F4], []);
    static readonly CloudFlow[] FourFlows = [Flow(F1, 1), Flow(F2, 1), Flow(F3, 1), Flow(F4, 0)];

    public ConnectionReferenceCleanupTests()
    {
        _settingsFile = Path.Combine(_folder, SettingsFileLocator.SharedFileName);
        File.WriteAllText(_settingsFile,
            "{\n  \"ConnectionReferences\": [\n    { \"LogicalName\": \"new_a\", \"ConnectionId\": \"x\" },\n    { \"LogicalName\": \"new_b\", \"ConnectionId\": \"x\" }\n  ]\n}");
    }

    bool SettingsHasB() => File.ReadAllText(_settingsFile).Contains("new_b");

    IOrganizationServiceAsync2 Service()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();

        service.ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            switch (call.Arg<OrganizationRequest>())
            {
                case UpdateRequest update:
                    _log.Add($"flow-update:{update.Target.Id}:settingsHasB={SettingsHasB()}");
                    if (_failUpdate.TryGetValue(update.Target.Id, out var code))
                        throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = code }, "refused");
                    _updates.Add(update);
                    return Task.FromResult<OrganizationResponse>(new UpdateResponse());
                case RetrieveDependenciesForDeleteRequest deps:
                    _log.Add($"deps:{deps.ObjectId}:type={deps.ComponentType}");
                    var rows = _dependents.GetValueOrDefault(deps.ObjectId) ?? [];
                    return Task.FromResult<OrganizationResponse>(new RetrieveDependenciesForDeleteResponse
                    {
                        Results = new ParameterCollection { { "EntityCollection", new EntityCollection(rows) } },
                    });
                case var other when other.RequestName == "RemoveSolutionComponent":
                    _log.Add($"remove:{other["ComponentId"]}:type={other["ComponentType"]}:solution={other["SolutionUniqueName"]}");
                    return Task.FromResult(new OrganizationResponse());
                default:
                    throw new InvalidOperationException("unexpected request " + call.Arg<OrganizationRequest>().RequestName);
            }
        });

        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var query = call.Arg<QueryExpression>();
            query.EntityName.Should().Be("solutioncomponent");
            var ids = ((IEnumerable<object>)query.Criteria.Conditions.Single(c => c.AttributeName == "objectid").Values).Cast<Guid>();
            var rows = ids.SelectMany(id => _memberships.GetValueOrDefault(id) ?? [], (id, sln) =>
                new Entity("solutioncomponent", Guid.NewGuid())
                {
                    ["objectid"] = id,
                    ["sol.uniquename"] = new AliasedValue("solution", "uniquename", sln),
                }).ToList();
            return Task.FromResult(new EntityCollection(rows));
        });

        service.DeleteAsync("connectionreference", Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.ArgAt<Guid>(1);
            _log.Add($"delete:{id}:settingsHasB={SettingsHasB()}");
            if (_deleteFaultsWithDependency.Contains(id))
                throw new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = unchecked((int)0x80047002) }, "dependency");
            return Task.CompletedTask;
        });

        service.UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var entity = call.Arg<Entity>();
            _log.Add($"rename:{entity.Id}");
            _plainUpdates.Add(entity);
            return Task.CompletedTask;
        });

        return service;
    }

    Task<IReadOnlyList<CleanupOutcome>> Apply(
        IOrganizationServiceAsync2 service, ConnectionReferenceReview review, CleanupChoices choices) =>
        ConnectionReferenceCleanup.ApplyAsync(service, "Contoso", _folder, review, choices, CancellationToken.None);

    static CleanupChoices MergeGroup(params Rename[] renames) => new([Group], [], [], renames);

    static Entity Dependent(Guid id, int type) =>
        new("dependency", Guid.NewGuid()) { ["dependentcomponentobjectid"] = id, ["dependentcomponenttype"] = new OptionSetValue(type) };

    [Fact]
    public async Task Merge_repoints_each_flow_once_then_settings_then_delete_then_rename()
    {
        var service = Service();
        var outcomes = await Apply(service, Review([A, B], FourFlows), MergeGroup(new Rename(A, "Dataverse (shared)")));

        outcomes.Should().OnlyContain(o => o.Status == CleanupStatus.Done);
        _updates.Should().HaveCount(4);
        _updates.Select(u => u.Target.Id).Should().Equal(F1, F2, F3, F4);
        _updates.Should().OnlyContain(u => u.ConcurrencyBehavior == ConcurrencyBehavior.IfRowVersionMatches && u.Target.RowVersion == "7");
        _updates.Should().OnlyContain(u => u.Target.Attributes.Keys.SequenceEqual(new[] { "clientdata" }));
        _updates.Select(u => (string)u.Target["clientdata"]).Should().OnlyContain(d => d.Contains("new_a") && !d.Contains("new_b"));

        // Flows first (settings still hold B), then settings edit (gone at delete time), then delete, then rename.
        _log.Should().Equal(
            $"flow-update:{F1}:settingsHasB=True", $"flow-update:{F2}:settingsHasB=True",
            $"flow-update:{F3}:settingsHasB=True", $"flow-update:{F4}:settingsHasB=True",
            $"deps:{IdB}:type={RefType}", $"delete:{IdB}:settingsHasB=False", $"rename:{IdA}");
        File.ReadAllText(_settingsFile).Should().Contain("new_a");
    }

    [Fact]
    public async Task Third_rewrite_refused_stops_with_first_two_done_and_nothing_after()
    {
        _failUpdate[F3] = 12345;
        var outcomes = await Apply(Service(), Review([A, B], FourFlows), MergeGroup(new Rename(A, "Shared")));

        outcomes.Take(2).Should().OnlyContain(o => o.Status == CleanupStatus.Done);
        outcomes[2].Name.Should().Be("flow " + F3.ToString()[..4]);
        outcomes[2].Status.Should().Be(CleanupStatus.Failed);
        outcomes.Skip(3).Should().OnlyContain(o => o.Status == CleanupStatus.NotAttempted);
        outcomes.Should().Contain(o => o.Kind == "settings").And.Contain(o => o.Kind == "rename");
        SettingsHasB().Should().BeTrue();
        _log.Should().NotContain(l => l.StartsWith("delete") || l.StartsWith("rename") || l.StartsWith("deps"));
    }

    [Fact]
    public async Task Row_version_mismatch_stops_and_says_the_flow_changed_since_the_review()
    {
        _failUpdate[F1] = RowVersionMismatch;
        var outcomes = await Apply(Service(), Review([A, B], FourFlows), MergeGroup());

        outcomes[0].Status.Should().Be(CleanupStatus.Failed);
        outcomes[0].Name.Should().Be("flow " + F1.ToString()[..4]);
        outcomes[0].Detail.Should().Contain("changed since the review");
        outcomes.Skip(1).Should().OnlyContain(o => o.Status == CleanupStatus.NotAttempted);
    }

    [Fact]
    public async Task Reference_with_a_dependent_in_another_solution_is_removed_not_deleted()
    {
        _dependents[IdB] = [Dependent(Guid.NewGuid(), 29)];
        var outcomes = await Apply(Service(), Review([A, B], FourFlows), MergeGroup());

        outcomes.Should().OnlyContain(o => o.Status == CleanupStatus.Done);
        _log.Should().NotContain(l => l.StartsWith("delete"));
        _log.Should().Contain($"remove:{IdB}:type={RefType}:solution=Contoso");
        outcomes.Single(o => o.Name == "new_b").Detail.Should().Contain("1 component");
    }

    [Fact]
    public async Task Reference_held_by_another_solution_without_dependents_is_removed_not_deleted()
    {
        _memberships[IdB] = ["Contoso", "Default", "Other"];
        await Apply(Service(), Review([A, B], FourFlows), MergeGroup());

        _log.Should().NotContain(l => l.StartsWith("delete"));
        _log.Should().Contain($"remove:{IdB}:type={RefType}:solution=Contoso");
    }

    [Fact]
    public async Task Membership_in_Default_and_this_solution_alone_still_deletes()
    {
        _memberships[IdB] = ["Contoso", "Default"];
        await Apply(Service(), Review([A, B], FourFlows), MergeGroup());

        _log.Should().Contain(l => l.StartsWith("delete"));
        _log.Should().NotContain(l => l.StartsWith("remove"));
    }

    [Fact]
    public async Task Delete_that_faults_with_a_dependency_error_falls_back_to_removal()
    {
        _deleteFaultsWithDependency.Add(IdB);
        var outcomes = await Apply(Service(), Review([A, B], FourFlows), MergeGroup());

        outcomes.Should().OnlyContain(o => o.Status == CleanupStatus.Done);
        _log.Should().Contain($"remove:{IdB}:type={RefType}:solution=Contoso");
    }

    [Fact]
    public async Task Settings_failure_after_the_flows_stops_before_any_delete()
    {
        File.WriteAllText(_settingsFile, "{ this is not json");
        var outcomes = await Apply(Service(), Review([A, B], FourFlows), MergeGroup(new Rename(A, "Shared")));

        _updates.Should().HaveCount(4);
        outcomes.Single(o => o.Kind == "settings").Status.Should().Be(CleanupStatus.Failed);
        outcomes.Single(o => o.Name == "new_b").Status.Should().Be(CleanupStatus.NotAttempted);
        outcomes.Single(o => o.Kind == "rename").Status.Should().Be(CleanupStatus.NotAttempted);
        _log.Should().NotContain(l => l.StartsWith("delete") || l.StartsWith("deps") || l.StartsWith("rename"));
    }

    [Fact]
    public async Task Unused_reference_is_removed_with_its_settings_entry_and_no_flow_is_touched()
    {
        var service = Service();
        var outcomes = await Apply(service, Review([A, B]), new CleanupChoices([], [], [B], []));

        outcomes.Should().OnlyContain(o => o.Status == CleanupStatus.Done);
        _updates.Should().BeEmpty();
        _log.Should().Equal($"deps:{IdB}:type={RefType}", $"delete:{IdB}:settingsHasB=False");
    }

    [Fact]
    public async Task Different_account_merge_keeps_the_member_the_classifier_rule_picks()
    {
        // B is used more, so B is the keeper and A is moved; only A's flow is rewritten.
        var a = Ref(IdA, "new_a", F1);
        var b = Ref(IdB, "new_b", F2, F3);
        var flows = new[] { Flow(F1, 1, Definition("new_a")), Flow(F2, 1, Definition("new_b")), Flow(F3, 1, Definition("new_b")) };
        await Apply(Service(), Review([a, b], flows), new CleanupChoices([], [new DifferentAccountGroup("/apis/shared_x", [a, b])], [], []));

        _updates.Should().ContainSingle().Which.Target.Id.Should().Be(F1);
        ((string)_updates[0].Target["clientdata"]).Should().Contain("new_b").And.NotContain("new_a");
        _log.Should().Contain($"deps:{IdA}:type={RefType}");
    }

    [Fact]
    public async Task Rename_writes_only_the_display_name()
    {
        await Apply(Service(), Review([A]), new CleanupChoices([], [], [], [new Rename(A, "Dataverse (shared)")]));

        var written = _plainUpdates.Should().ContainSingle().Subject;
        written.LogicalName.Should().Be("connectionreference");
        written.Id.Should().Be(IdA);
        written.Attributes.Keys.Should().Equal("connectionreferencedisplayname");
        written["connectionreferencedisplayname"].Should().Be("Dataverse (shared)");
    }

    [Fact]
    public async Task Rewritten_definition_differs_only_in_the_moved_references_fields()
    {
        var original = File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "ConnectionReferences", "Fixtures", "flow-multi-entry.clientdata.json"));
        var a = Ref(IdA, "flsp_spikeA", F1);
        var b = Ref(IdB, "flsp_spikeB", F1);
        var group = new SameConnectionGroup("/apis/shared_x", a, [b], [F1], []);
        await Apply(Service(), Review([a, b], new CloudFlow(F1, "multi", 1, "7", original)), new CleanupChoices([group], [], [], []));

        var expected = JsonNode.Parse(original)!;
        foreach (var key in new[] { "shared_commondataserviceforapps_1", "shared_commondataserviceforapps-1" })
        {
            var connection = expected["properties"]!["connectionReferences"]![key]!["connection"]!.AsObject();
            connection["connectionReferenceLogicalName"] = "flsp_spikeA";
            connection.Remove("name");
        }

        var actual = JsonNode.Parse((string)_updates.Single().Target["clientdata"])!;
        JsonNode.DeepEquals(actual, expected).Should().BeTrue();
        JsonNode.DeepEquals(actual, JsonNode.Parse(original)).Should().BeFalse();
    }

    [Fact]
    public async Task Reference_still_used_by_a_flow_of_this_solution_stops_instead_of_being_detached()
    {
        _dependents[IdB] = [Dependent(F2, 29)];
        var outcomes = await Apply(Service(), Review([A, B], FourFlows), MergeGroup());

        var b = outcomes.Single(o => o.Name == "new_b");
        b.Status.Should().Be(CleanupStatus.Failed);
        b.Detail.Should().Contain("still uses it");
        _log.Should().NotContain(l => l.StartsWith("delete") || l.StartsWith("remove"));
    }

    [Fact]
    public async Task A_step_that_throws_a_non_fault_exception_ends_as_a_failed_step_and_keeps_the_list()
    {
        var service = Service();
        service.ExecuteAsync(Arg.Is<OrganizationRequest>(r => r is RetrieveDependenciesForDeleteRequest), Arg.Any<CancellationToken>())
            .Returns<Task<OrganizationResponse>>(_ => throw new TimeoutException("Dataverse took too long"));

        var outcomes = await Apply(service, Review([A, B], FourFlows), MergeGroup(new Rename(A, "Dataverse")));

        outcomes.Where(o => o.Kind == "flow").Should().OnlyContain(o => o.Status == CleanupStatus.Done);
        var b = outcomes.Single(o => o.Name == "new_b");
        b.Status.Should().Be(CleanupStatus.Failed);
        b.Detail.Should().Be("Dataverse took too long");
        outcomes.Single(o => o.Kind == "rename").Status.Should().Be(CleanupStatus.NotAttempted);
    }

    [Fact]
    public async Task Flow_whose_definition_does_not_name_the_moved_reference_is_not_written()
    {
        var flows = new[] { Flow(F1, 1, Definition("new_a")), Flow(F2, 1), Flow(F3, 1), Flow(F4, 0) };
        var outcomes = await Apply(Service(), Review([A, B], flows), MergeGroup());

        _updates.Select(u => u.Target.Id).Should().NotContain(F1).And.HaveCount(3);
        outcomes.Single(o => o.Name == flows[0].Name).Status.Should().Be(CleanupStatus.Done);
    }
}
