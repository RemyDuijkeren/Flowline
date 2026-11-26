using System.Text.RegularExpressions;
using Flowline.Core.Environments;

namespace Flowline.Core.ConnectionReferences;

/// <summary>A role's binding for one reference: whether the role has a settings file, and the connection it declares.</summary>
/// <param name="HasFile">False means no evidence for this role.</param>
/// <param name="ConnectionId">Null when the file does not declare the reference or leaves it empty.</param>
public sealed record RoleBinding(bool HasFile, string? ConnectionId);

/// <summary>A component of the solution, other than a cloud flow, that depends on a reference.</summary>
public sealed record Dependent(Guid Id, int ComponentType);

/// <summary>One connection reference of the solution, with everything the classifier and the apply step need.</summary>
/// <param name="DevConnectionId">The connection the reference is bound to in DEV, or null when unbound.</param>
/// <param name="ComponentType">Read from the reference's <c>solutioncomponent</c> row, because it differs per environment.</param>
/// <param name="Bindings">One entry per role except DEV.</param>
/// <param name="FlowIds">Cloud flows of this solution that use the reference, each once.</param>
/// <param name="OtherDependents">Non-flow components of this solution that use it (canvas app, agent).</param>
/// <param name="UseUnknown">A check failed or a flow could not be read, so the uses cannot be trusted.</param>
public sealed record ConnectionReference(
    Guid Id,
    string LogicalName,
    string DisplayName,
    string ConnectorId,
    string? DevConnectionId,
    DateTime? CreatedOn,
    int ComponentType,
    IReadOnlyDictionary<EnvironmentRole, RoleBinding> Bindings,
    IReadOnlyList<Guid> FlowIds,
    IReadOnlyList<Dependent> OtherDependents,
    bool UseUnknown)
{
    public int UseCount => FlowIds.Count + OtherDependents.Count;
}

/// <summary>A cloud flow of the solution, as the apply step needs it to rewrite it.</summary>
/// <param name="StateCode">0 draft, 1 activated, 2 suspended.</param>
/// <param name="RowVersion">For the conditional definition update.</param>
/// <param name="Definition">The flow's definition text, null or empty when it could not be read.</param>
public sealed record CloudFlow(Guid Id, string Name, int StateCode, string? RowVersion, string? Definition);

/// <summary>References of one connector that hold one connection everywhere there is evidence.</summary>
/// <param name="Move">The members to fold into the keeper.</param>
/// <param name="FlowsToMove">Flows that use a member being moved, each once.</param>
/// <param name="MissingEvidence">Roles without a settings file.</param>
public sealed record SameConnectionGroup(
    string ConnectorId,
    ConnectionReference Keeper,
    IReadOnlyList<ConnectionReference> Move,
    IReadOnlyList<Guid> FlowsToMove,
    IReadOnlyList<EnvironmentRole> MissingEvidence);

/// <summary>References of one connector that differ somewhere, so a person decides. Each member carries its bindings.</summary>
public sealed record DifferentAccountGroup(string ConnectorId, IReadOnlyList<ConnectionReference> Members);

/// <summary>A group that would merge but is held back.</summary>
public sealed record NotMergedGroup(string ConnectorId, IReadOnlyList<ConnectionReference> Members, string Reason);

/// <summary>What the review decided.</summary>
/// <param name="GeneratedNames">References with a platform-generated display name that are neither unused nor about to be moved.</param>
public sealed record Classification(
    IReadOnlyList<SameConnectionGroup> SameConnection,
    IReadOnlyList<DifferentAccountGroup> DifferentAccount,
    IReadOnlyList<NotMergedGroup> NotMerged,
    IReadOnlyList<ConnectionReference> Unused,
    IReadOnlyList<ConnectionReference> GeneratedNames);

/// <summary>Pure: sorts a solution's connection references into groups, keepers, unused and generated names.</summary>
public static class ConnectionReferenceClassifier
{
    const int SuspendedState = 2;

    // ponytail: the logical name alone is the signal. The platform writes <prefix>_shared<connector>_<5 hex>
    // for every reference it generates, whatever the display name ended up as, and hand-made references
    // have no such name. A hand-made name that happens to fit would be flagged; offering a rename is harmless.
    static readonly Regex GeneratedLogicalName =
        new(@"^[^_]+_shared[a-z0-9]+_[0-9a-f]{5}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool HasGeneratedName(ConnectionReference reference) =>
        GeneratedLogicalName.IsMatch(reference.LogicalName);

    public static Classification Classify(IReadOnlyList<ConnectionReference> references, IReadOnlyList<CloudFlow> flows)
    {
        var flowState = flows.ToDictionary(f => f.Id, f => f.StateCode);

        var unused = references.Where(IsUnused).ToList();
        var same = new List<SameConnectionGroup>();
        var different = new List<DifferentAccountGroup>();
        var notMerged = new List<NotMergedGroup>();

        foreach (var connector in references.Except(unused).GroupBy(r => r.ConnectorId, StringComparer.OrdinalIgnoreCase))
        {
            if (connector.Count() < 2) continue;

            var matching = connector.GroupBy(Signature).ToList();
            var groups = matching.Where(g => g.Count() >= 2).ToList();
            var rest = matching.Where(g => g.Count() < 2).SelectMany(g => g).ToList();

            foreach (var group in groups)
            {
                var members = group.ToList();
                var reason = NotMergedReason(members, flowState);
                if (reason is not null)
                {
                    notMerged.Add(new NotMergedGroup(connector.Key, members, reason));
                    continue;
                }

                var keeper = PickKeeper(members);
                var move = members.Where(m => m != keeper).ToList();
                same.Add(new SameConnectionGroup(
                    connector.Key, keeper, move,
                    move.SelectMany(m => m.FlowIds).Distinct().ToList(),
                    members[0].Bindings.Where(b => !b.Value.HasFile).Select(b => b.Key).OrderBy(r => r).ToList()));
            }

            // A lone reference that differs from a merged group's members is still a different-account case.
            if (rest.Count >= 2 || (rest.Count == 1 && groups.Count > 0))
            {
                // The same holds as for a same-connection group: a person may choose to merge these, so a
                // group that could not be merged safely is held back with its reason.
                var reason = rest.Count >= 2 ? NotMergedReason(rest, flowState) : null;
                if (reason is not null) notMerged.Add(new NotMergedGroup(connector.Key, rest, reason));
                else different.Add(new DifferentAccountGroup(connector.Key, rest));
            }
        }

        var moved = same.SelectMany(g => g.Move).ToHashSet();
        var generated = references.Where(r => HasGeneratedName(r) && !moved.Contains(r) && !unused.Contains(r)).ToList();

        return new Classification(same, different, notMerged, unused, generated);
    }

    // "Can't tell means in use": an unreadable flow or a failed check never yields unused.
    static bool IsUnused(ConnectionReference r) => !r.UseUnknown && r.UseCount == 0;

    // Two references match when DEV holds the same connection and every role file that exists binds both to
    // one connection or declares neither. Roles without a file are left out: they give no evidence either way.
    // An unbound DEV reference matches nothing.
    static string Signature(ConnectionReference r)
    {
        if (string.IsNullOrWhiteSpace(r.DevConnectionId)) return "unbound:" + r.Id;

        var roles = r.Bindings.Where(b => b.Value.HasFile).OrderBy(b => b.Key)
            .Select(b => $"{b.Key}={b.Value.ConnectionId?.ToLowerInvariant() ?? ""}");
        return r.DevConnectionId.ToLowerInvariant() + "|" + string.Join(";", roles);
    }

    static string? NotMergedReason(List<ConnectionReference> members, Dictionary<Guid, int> flowState)
    {
        if (members.Any(m => m.UseUnknown))
            return "A use of one of these references could not be checked.";

        if (members.Count(m => m.OtherDependents.Count > 0) > 1)
            return "More than one is used by a canvas app or agent, which cannot be re-pointed.";

        if (members.SelectMany(m => m.FlowIds).Any(id => flowState.TryGetValue(id, out var s) && s == SuspendedState))
            return "A suspended flow uses one of them.";

        return null;
    }

    // A member used by a non-flow component must stay, because only flows are re-pointed. Otherwise the most
    // used, then the oldest; the logical name only makes ties deterministic.
    public static ConnectionReference PickKeeper(IEnumerable<ConnectionReference> members) =>
        members
            .OrderByDescending(m => m.OtherDependents.Count > 0)
            .ThenByDescending(m => m.UseCount)
            .ThenBy(m => m.CreatedOn ?? DateTime.MaxValue)
            .ThenBy(m => m.LogicalName, StringComparer.OrdinalIgnoreCase)
            .First();
}
