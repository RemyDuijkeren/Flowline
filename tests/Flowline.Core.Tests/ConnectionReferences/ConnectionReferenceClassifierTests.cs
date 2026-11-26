using System.Text.Json;
using FluentAssertions;
using Flowline.Core.ConnectionReferences;
using Flowline.Core.Environments;
using Xunit;

namespace Flowline.Core.Tests.ConnectionReferences;

public class ConnectionReferenceClassifierTests
{
    const string Connector = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps";
    static readonly EnvironmentRole[] Roles = [EnvironmentRole.Test, EnvironmentRole.Uat, EnvironmentRole.Prod];

    static readonly Guid F1 = Guid.NewGuid(), F2 = Guid.NewGuid(), F3 = Guid.NewGuid(), F4 = Guid.NewGuid(), F5 = Guid.NewGuid(), F6 = Guid.NewGuid(), F7 = Guid.NewGuid();

    // Bindings: one connection per role (null = file exists, reference undeclared), or "-" for no file.
    static ConnectionReference Ref(
        string name, string? dev = "dev-conn", string? test = "c1", string? uat = "c1", string? prod = "c1",
        Guid[]? flows = null, int canvasApps = 0, DateTime? created = null, bool unknown = false, string connector = Connector)
    {
        RoleBinding B(string? v) => v == "-" ? new RoleBinding(false, null) : new RoleBinding(true, v);
        return new ConnectionReference(
            Guid.NewGuid(), name, name, connector, dev, created ?? new DateTime(2025, 1, 1), 10032,
            new Dictionary<EnvironmentRole, RoleBinding>
            {
                [EnvironmentRole.Test] = B(test), [EnvironmentRole.Uat] = B(uat), [EnvironmentRole.Prod] = B(prod),
            },
            flows ?? [], Enumerable.Range(0, canvasApps).Select(_ => new Dependent(Guid.NewGuid(), 300)).ToList(), unknown);
    }

    static CloudFlow Flow(Guid id, int state = 1) => new(id, "flow", state, "1", "{}");

    static Classification Classify(IEnumerable<ConnectionReference> refs, params CloudFlow[] flows) =>
        ConnectionReferenceClassifier.Classify(refs.ToList(), flows);

    // Same connection in DEV, every role file binds them alike.
    [Fact]
    public void SharedDevConnectionAndAlikeEverywhere_IsSameConnectionGroup()
    {
        var a = Ref("a", flows: [F1]);
        var b = Ref("b", flows: [F2]);

        var result = Classify([a, b], Flow(F1), Flow(F2));

        var group = result.SameConnection.Should().ContainSingle().Subject;
        group.Keeper.Should().Be(a);
        group.Move.Should().Equal(b);
        group.FlowsToMove.Should().Equal(F2);
        group.MissingEvidence.Should().BeEmpty();
        result.DifferentAccount.Should().BeEmpty();
    }

    // Declared in no role file counts as alike.
    [Fact]
    public void UndeclaredInEveryRoleFile_IsAlike()
    {
        var result = Classify([Ref("a", flows: [F1], test: null, uat: null, prod: null), Ref("b", flows: [F2], test: null, uat: null, prod: null)],
            Flow(F1), Flow(F2));

        result.SameConnection.Should().ContainSingle();
    }

    [Fact]
    public void RoleWithoutFile_IsMissingEvidence()
    {
        var result = Classify([Ref("a", flows: [F1], prod: "-"), Ref("b", flows: [F2], prod: "-")], Flow(F1), Flow(F2));

        result.SameConnection.Should().ContainSingle().Which.MissingEvidence.Should().Equal(EnvironmentRole.Prod);
    }

    // DEV alike, PROD differs.
    [Fact]
    public void DifferentBindingInOneRole_IsDifferentAccountGroupWithBindings()
    {
        var a = Ref("a", flows: [F1], prod: "p1");
        var b = Ref("b", flows: [F2], prod: "p2");

        var result = Classify([a, b], Flow(F1), Flow(F2));

        result.SameConnection.Should().BeEmpty();
        var group = result.DifferentAccount.Should().ContainSingle().Subject;
        group.Members.Should().BeEquivalentTo([a, b]);
        group.Members.Select(m => m.Bindings[EnvironmentRole.Prod].ConnectionId).Should().BeEquivalentTo(["p1", "p2"]);
    }

    // The file binds one member and leaves the other undeclared.
    [Fact]
    public void BoundInOneMemberUndeclaredInOther_Differs()
    {
        var result = Classify([Ref("keeper", flows: [F1, F2], uat: null), Ref("member", flows: [F3], uat: "u1")],
            Flow(F1), Flow(F2), Flow(F3));

        result.SameConnection.Should().BeEmpty();
        result.DifferentAccount.Should().ContainSingle();
    }

    [Fact]
    public void DifferentDevConnection_Differs()
    {
        var result = Classify([Ref("a", dev: "d1", flows: [F1]), Ref("b", dev: "d2", flows: [F2])], Flow(F1), Flow(F2));

        result.SameConnection.Should().BeEmpty();
        result.DifferentAccount.Should().ContainSingle();
    }

    [Fact]
    public void ConnectionIdsCompareCaseInsensitively()
    {
        var result = Classify([Ref("a", dev: "ABC", prod: "XyZ", flows: [F1]), Ref("b", dev: "abc", prod: "xYz", flows: [F2])],
            Flow(F1), Flow(F2));

        result.SameConnection.Should().ContainSingle();
    }

    // Two match each other, the third differs.
    [Fact]
    public void TwoMatchOneDiffers_IsSameConnectionPairPlusDifferentAccountGroup()
    {
        var a = Ref("a", flows: [F1]);
        var b = Ref("b", flows: [F2]);
        var c = Ref("c", flows: [F3], prod: "other");

        var result = Classify([a, b, c], Flow(F1), Flow(F2), Flow(F3));

        result.SameConnection.Should().ContainSingle().Which.Move.Concat([result.SameConnection[0].Keeper])
            .Should().BeEquivalentTo([a, b]);
        result.DifferentAccount.Should().ContainSingle().Which.Members.Should().Equal(c);
    }

    [Fact]
    public void DifferentConnectors_NeverGroup()
    {
        var result = Classify([Ref("a", flows: [F1]), Ref("b", flows: [F2], connector: "/apis/shared_office365")], Flow(F1), Flow(F2));

        result.SameConnection.Should().BeEmpty();
        result.DifferentAccount.Should().BeEmpty();
    }

    // Five uses against two.
    [Fact]
    public void Keeper_IsTheMostUsed()
    {
        var two = Ref("two", flows: [F1, F2], created: new DateTime(2020, 1, 1));
        var five = Ref("five", flows: [F3, F4, F5, F6, F7], created: new DateTime(2025, 1, 1));

        var group = Classify([two, five], Flow(F1), Flow(F2), Flow(F3), Flow(F4), Flow(F5), Flow(F6), Flow(F7))
            .SameConnection.Single();

        group.Keeper.Should().Be(five);
        group.FlowsToMove.Should().BeEquivalentTo([F1, F2]);
    }

    [Fact]
    public void Keeper_OnTieIsTheOlder()
    {
        var newer = Ref("newer", flows: [F1], created: new DateTime(2025, 5, 1));
        var older = Ref("older", flows: [F2], created: new DateTime(2024, 5, 1));

        Classify([newer, older], Flow(F1), Flow(F2)).SameConnection.Single().Keeper.Should().Be(older);
    }

    [Fact]
    public void MemberUsedByCanvasApp_BecomesKeeperEvenWithFewerUses()
    {
        var flowy = Ref("flowy", flows: [F1, F2, F3]);
        var app = Ref("app", canvasApps: 1);

        var group = Classify([flowy, app], Flow(F1), Flow(F2), Flow(F3)).SameConnection.Single();

        group.Keeper.Should().Be(app);
        group.Move.Should().Equal(flowy);
    }

    [Fact]
    public void TwoMembersUsedByNonFlowComponents_NotMerged()
    {
        var result = Classify([Ref("a", canvasApps: 1), Ref("b", canvasApps: 1)]);

        result.SameConnection.Should().BeEmpty();
        result.NotMerged.Should().ContainSingle().Which.Reason.Should().Contain("canvas app or agent");
    }

    [Fact]
    public void SuspendedFlowUser_NotMerged()
    {
        var result = Classify([Ref("a", flows: [F1]), Ref("b", flows: [F2])], Flow(F1), Flow(F2, state: 2));

        result.SameConnection.Should().BeEmpty();
        result.NotMerged.Should().ContainSingle().Which.Reason.Should().Contain("suspended");
    }

    [Fact]
    public void MemberWithUnknownUses_NotMerged()
    {
        var result = Classify([Ref("a", flows: [F1]), Ref("b", flows: [F2], unknown: true)], Flow(F1), Flow(F2));

        result.SameConnection.Should().BeEmpty();
        result.NotMerged.Should().ContainSingle();
    }

    [Fact]
    public void ReferenceNothingUses_IsUnused_AndLeavesGroups()
    {
        var used1 = Ref("used1", flows: [F1]);
        var used2 = Ref("used2", flows: [F2]);
        var idle = Ref("idle");

        var result = Classify([used1, used2, idle], Flow(F1), Flow(F2));

        result.Unused.Should().Equal(idle);
        result.SameConnection.Single().Move.Concat([result.SameConnection[0].Keeper]).Should().BeEquivalentTo([used1, used2]);
    }

    // A failed check or unreadable flow means "in use", never unused.
    [Fact]
    public void UnknownUses_AreNeverUnused()
    {
        Classify([Ref("idle", unknown: true)]).Unused.Should().BeEmpty();
    }

    [Fact]
    public void UsedOnlyByNonFlowComponent_IsNotUnused()
    {
        Classify([Ref("app", canvasApps: 1)]).Unused.Should().BeEmpty();
    }

    // Names fixture: every generated sample is flagged, every hand-made one is not.
    [Fact]
    public void GeneratedNamesFromFixture_AreFlagged_HandmadeAreNot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ConnectionReferences", "Fixtures", "connection-reference-names.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        ConnectionReference Make(JsonElement e) => Ref(e.GetProperty("logical").GetString()!) with { DisplayName = e.GetProperty("display").GetString()! };

        foreach (var e in doc.RootElement.GetProperty("generated").EnumerateArray())
            ConnectionReferenceClassifier.HasGeneratedName(Make(e)).Should().BeTrue(e.GetProperty("logical").GetString());
        foreach (var e in doc.RootElement.GetProperty("handmade").EnumerateArray())
            ConnectionReferenceClassifier.HasGeneratedName(Make(e)).Should().BeFalse(e.GetProperty("logical").GetString());
    }

    [Fact]
    public void GeneratedNames_ListsOnlySurvivors()
    {
        var keeper = Ref("msdyn_sharedcommondataserviceforapps_aaaaa", flows: [F1, F2]);
        var moved = Ref("msdyn_sharedcommondataserviceforapps_bbbbb", flows: [F3]);
        var idle = Ref("msdyn_sharedcommondataserviceforapps_ccccc", dev: "other");

        var result = Classify([keeper, moved, idle], Flow(F1), Flow(F2), Flow(F3));

        result.GeneratedNames.Should().Equal(keeper);
    }
}
