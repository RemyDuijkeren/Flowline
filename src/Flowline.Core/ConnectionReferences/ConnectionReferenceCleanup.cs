using System.ServiceModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowline.Core.Configure;
using Flowline.Core.Dataverse;
using Flowline.Core.Solutions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.ConnectionReferences;

/// <summary>A display name change for one reference.</summary>
public sealed record Rename(ConnectionReference Reference, string DisplayName);

/// <summary>What the user confirmed. Different-account groups are merged into the member the classifier's keeper rule picks.</summary>
public sealed record CleanupChoices(
    IReadOnlyList<SameConnectionGroup> MergeSame,
    IReadOnlyList<DifferentAccountGroup> MergeDifferent,
    IReadOnlyList<ConnectionReference> RemoveUnused,
    IReadOnlyList<Rename> Renames)
{
    /// <summary>Each reference that folds into another, with its keeper.</summary>
    public IReadOnlyList<(ConnectionReference From, ConnectionReference Keeper)> Moves() =>
        MergeSame.SelectMany(g => g.Move.Select(m => (m, g.Keeper)))
            .Concat(MergeDifferent.SelectMany(g =>
            {
                var keeper = ConnectionReferenceClassifier.PickKeeper(g.Members);
                return g.Members.Where(m => m != keeper).Select(m => (m, keeper));
            }))
            .ToList();
}

public enum CleanupStatus { Done, Failed, NotAttempted }

/// <param name="Kind">flow, settings, reference or rename.</param>
/// <param name="Detail">The failure reason, or what a done step did that the reader should know.</param>
public sealed record CleanupOutcome(string Kind, string Name, CleanupStatus Status, string? Detail = null);

/// <summary>
/// Applies the confirmed cleanup to DEV in a fixed order and stops at the first failure: re-point flows,
/// edit the settings files, delete or remove the references, rename. Wherever it stops, a later review
/// finds the remaining work: a reference whose flows moved shows up as unused.
/// </summary>
public static class ConnectionReferenceCleanup
{
    // OrganizationServiceFault.ErrorCode for a row version mismatch under ConcurrencyBehavior.IfRowVersionMatches.
    const int RowVersionMismatch = -2147088254;

    public static async Task<IReadOnlyList<CleanupOutcome>> ApplyAsync(
        IOrganizationServiceAsync2 service,
        string solutionUniqueName,
        string settingsFolder,
        ConnectionReferenceReview review,
        CleanupChoices choices,
        CancellationToken ct)
    {
        var moves = choices.Moves();
        var keeperOf = moves.ToDictionary(m => m.From.LogicalName, m => m.Keeper.LogicalName, StringComparer.OrdinalIgnoreCase);
        var removable = moves.Select(m => m.From).Concat(choices.RemoveUnused).DistinctBy(r => r.Id).ToList();

        var steps = new List<(string Kind, string Name, Func<Task<CleanupOutcome>> Run)>();

        foreach (var flowId in moves.SelectMany(m => m.From.FlowIds).Distinct())
        {
            var flow = review.Flows.FirstOrDefault(f => f.Id == flowId);
            steps.Add(("flow", flow?.Name ?? flowId.ToString(), () => RepointFlowAsync(service, flow, keeperOf, ct)));
        }

        if (removable.Count > 0)
            steps.Add(("settings", "settings files", () => Task.FromResult(RemoveSettingsEntries(settingsFolder, removable))));

        // A dependent that is one of this solution's own components means the reference is still in use here,
        // for example a flow a maker saved after the review: that stops the cleanup instead of detaching it.
        var ownComponents = review.Flows.Select(f => f.Id)
            .Concat(review.References.SelectMany(r => r.OtherDependents.Select(d => d.Id)))
            .ToHashSet();
        foreach (var reference in removable)
            steps.Add(("reference", reference.LogicalName, () => DeleteOrRemoveAsync(service, solutionUniqueName, reference, ownComponents, ct)));

        foreach (var rename in choices.Renames)
            steps.Add(("rename", rename.Reference.LogicalName, () => RenameAsync(service, rename, ct)));

        var outcomes = new List<CleanupOutcome>();
        var stopped = false;
        foreach (var (kind, name, run) in steps)
        {
            if (stopped) { outcomes.Add(new CleanupOutcome(kind, name, CleanupStatus.NotAttempted)); continue; }
            CleanupOutcome outcome;
            try
            {
                outcome = await run().ConfigureAwait(false);
            }
            // Anything a step did not turn into an outcome (a timeout, a dropped connection, Ctrl+C) still ends
            // as a failed step, so the caller reports what landed instead of losing the list.
            catch (Exception ex)
            {
                outcome = new CleanupOutcome(kind, name, CleanupStatus.Failed,
                    ex is OperationCanceledException && ct.IsCancellationRequested ? "Cancelled." : ex.Message);
            }
            outcomes.Add(outcome);
            stopped = outcome.Status == CleanupStatus.Failed;
        }

        return outcomes;
    }

    static async Task<CleanupOutcome> RepointFlowAsync(
        IOrganizationServiceAsync2 service, CloudFlow? flow, Dictionary<string, string> keeperOf, CancellationToken ct)
    {
        if (flow is null)
            return new CleanupOutcome("flow", "unknown flow", CleanupStatus.Failed, "The flow is not in the review.");

        var rewritten = Rewrite(flow.Definition, keeperOf);
        if (rewritten is null)
            return new CleanupOutcome("flow", flow.Name, CleanupStatus.Failed, "The flow's definition could not be read.");
        // Listed through the dependency table only, the definition names no moved reference: nothing to write.
        if (rewritten == flow.Definition)
            return new CleanupOutcome("flow", flow.Name, CleanupStatus.Done, "already on the keeper");
        if (flow.RowVersion is null)
            return new CleanupOutcome("flow", flow.Name, CleanupStatus.Failed, "The review holds no row version for the flow, so it cannot be updated safely.");

        try
        {
            var target = new Entity("workflow", flow.Id) { ["clientdata"] = rewritten, RowVersion = flow.RowVersion };
            await service.ExecuteAsync(
                new UpdateRequest { Target = target, ConcurrencyBehavior = ConcurrencyBehavior.IfRowVersionMatches }, ct).ConfigureAwait(false);
            return new CleanupOutcome("flow", flow.Name, CleanupStatus.Done);
        }
        catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail?.ErrorCode == RowVersionMismatch)
        {
            return new CleanupOutcome("flow", flow.Name, CleanupStatus.Failed,
                "The flow changed since the review (someone saved it). It was left as it was.");
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            return new CleanupOutcome("flow", flow.Name, CleanupStatus.Failed, $"Dataverse refused the update: {ex.Message}. The flow was left as it was.");
        }
    }

    // Only the entries naming a moved reference change: the keeper's logical name goes in and the stored
    // connection name goes out. Map keys, actions and triggers stay as they were. Null when unreadable.
    static string? Rewrite(string? definition, Dictionary<string, string> keeperOf)
    {
        if (string.IsNullOrWhiteSpace(definition)) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(definition); }
        catch (JsonException) { return null; }
        if (root?["properties"]?["connectionReferences"] is not JsonObject map) return null;

        var changed = false;
        foreach (var entry in map)
        {
            if (entry.Value?["connection"] is not JsonObject connection) continue;
            var name = (connection["connectionReferenceLogicalName"] as JsonValue)?.GetValue<string>();
            if (name is null || !keeperOf.TryGetValue(name, out var keeper)) continue;
            connection["connectionReferenceLogicalName"] = keeper;
            connection.Remove("name");
            changed = true;
        }

        if (!changed) return definition;
        return root.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    static CleanupOutcome RemoveSettingsEntries(string settingsFolder, List<ConnectionReference> references)
    {
        try
        {
            var changed = SettingsEntryRemover.Remove(settingsFolder, references.Select(r => r.LogicalName));
            return new CleanupOutcome("settings", "settings files", CleanupStatus.Done, $"{changed.Count} {(changed.Count == 1 ? "file" : "files")} changed");
        }
        catch (FlowlineException ex)
        {
            return new CleanupOutcome("settings", "settings files", CleanupStatus.Failed, ex.Message);
        }
    }

    static async Task<CleanupOutcome> DeleteOrRemoveAsync(
        IOrganizationServiceAsync2 service, string solutionUniqueName, ConnectionReference reference,
        IReadOnlySet<Guid> ownComponents, CancellationToken ct)
    {
        try
        {
            var dependents = await DependentsAsync(service, reference, ct).ConfigureAwait(false);
            if (dependents.Any(d => ownComponents.Contains(d.GetAttributeValue<Guid>("dependentcomponentobjectid"))))
                return new CleanupOutcome("reference", reference.LogicalName, CleanupStatus.Failed,
                    "A component of this solution still uses it, so it was left in place.");
            var otherSolutions = await OtherSolutionsAsync(service, solutionUniqueName, reference.Id, ct).ConfigureAwait(false);

            var keepReasons = new List<string>();
            if (dependents.Count > 0)
                keepReasons.Add($"still used by {dependents.Count} {(dependents.Count == 1 ? "component" : "components")} outside this solution");
            if (otherSolutions.Count > 0)
                keepReasons.Add($"also in {(otherSolutions.Count == 1 ? "solution" : "solutions")} {string.Join(", ", otherSolutions)}");

            if (keepReasons.Count == 0)
            {
                try
                {
                    await service.DeleteAsync("connectionreference", reference.Id, ct).ConfigureAwait(false);
                    return new CleanupOutcome("reference", reference.LogicalName, CleanupStatus.Done, "deleted");
                }
                catch (FaultException<OrganizationServiceFault> ex) when (ComponentStateWriter.IsDependencyFault(ex))
                {
                    keepReasons.Add("Dataverse reported a dependency on delete");
                }
            }

            await SolutionComponentRemoval.RemoveAsync(service, solutionUniqueName, reference.Id, reference.ComponentType, ct).ConfigureAwait(false);
            return new CleanupOutcome("reference", reference.LogicalName, CleanupStatus.Done,
                "removed from this solution only: " + string.Join("; ", keepReasons));
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            return new CleanupOutcome("reference", reference.LogicalName, CleanupStatus.Failed, ex.Message);
        }
    }

    // A fault here is "can't tell", which stops the cleanup before any delete (it surfaces as a failed outcome).
    static async Task<IReadOnlyList<Entity>> DependentsAsync(IOrganizationServiceAsync2 service, ConnectionReference reference, CancellationToken ct)
    {
        var response = (RetrieveDependenciesForDeleteResponse)await service.ExecuteAsync(
            new RetrieveDependenciesForDeleteRequest { ComponentType = reference.ComponentType, ObjectId = reference.Id }, ct).ConfigureAwait(false);
        return response.EntityCollection.Entities;
    }

    // Default holds every component, so it never counts as another solution.
    static async Task<List<string>> OtherSolutionsAsync(IOrganizationServiceAsync2 service, string solutionUniqueName, Guid id, CancellationToken ct)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            Criteria = { Conditions = { new ConditionExpression("objectid", ConditionOperator.Equal, id) } },
            LinkEntities = { new LinkEntity("solutioncomponent", "solution", "solutionid", "solutionid", JoinOperator.Inner)
                { Columns = new ColumnSet("uniquename"), EntityAlias = "sol" } },
        };
        var rows = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return rows.Select(r => r.GetAttributeValue<AliasedValue>("sol.uniquename")?.Value as string)
            .Where(s => !string.IsNullOrEmpty(s)
                        && !string.Equals(s, solutionUniqueName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase))
            .Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task<CleanupOutcome> RenameAsync(IOrganizationServiceAsync2 service, Rename rename, CancellationToken ct)
    {
        try
        {
            await service.UpdateAsync(new Entity("connectionreference", rename.Reference.Id)
                { ["connectionreferencedisplayname"] = rename.DisplayName }, ct).ConfigureAwait(false);
            return new CleanupOutcome("rename", rename.Reference.LogicalName, CleanupStatus.Done);
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            return new CleanupOutcome("rename", rename.Reference.LogicalName, CleanupStatus.Failed, ex.Message);
        }
    }
}
