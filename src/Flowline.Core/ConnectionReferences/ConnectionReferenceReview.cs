using Flowline.Core.Configure;
using Flowline.Core.Dataverse;
using Flowline.Core.Environments;
using Flowline.Core.OrphanCleanup;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.ConnectionReferences;

/// <summary>Everything read from DEV and the settings files, with the classification of it.</summary>
public sealed record ConnectionReferenceReview(
    IReadOnlyList<ConnectionReference> References,
    IReadOnlyList<CloudFlow> Flows,
    Classification Classification)
{
    /// <summary>Whether there is anything to advise on or act on.</summary>
    public bool HasFindings =>
        Classification.SameConnection.Count + Classification.DifferentAccount.Count +
        Classification.NotMerged.Count + Classification.Unused.Count + Classification.GeneratedNames.Count > 0;
}

/// <summary>Reads a solution's connection references, their uses and the settings evidence from DEV, then classifies them.</summary>
public static class ConnectionReferenceReviewer
{
    const int CloudFlowCategory = 5;

    public static async Task<ConnectionReferenceReview> ReadAsync(
        IOrganizationServiceAsync2 service,
        string solutionUniqueName,
        IReadOnlyList<RoleEvidence> evidence,
        CancellationToken ct)
    {
        var components = await ReadComponentsAsync(service, solutionUniqueName, ct).ConfigureAwait(false);
        var componentIds = components.Select(c => c.ObjectId).ToHashSet();

        var rows = await ReadReferenceRowsAsync(service, components.Select(c => c.ObjectId).ToList(), ct).ConfigureAwait(false);
        var flows = await ReadFlowsAsync(
            service, components.Where(c => c.ComponentType == SolutionComponentInventory.WorkflowComponentType)
                .Select(c => c.ObjectId).ToList(), ct).ConfigureAwait(false);
        var dependents = await ReadDependentsAsync(service, rows.Select(r => r.Id).ToList(), ct).ConfigureAwait(false);

        // One flow that cannot be read might name any reference, so no reference can be called unused.
        var flowNames = flows.ToDictionary(f => f.Id, f => FlowConnectionMap.ReferencedLogicalNames(f.Definition));
        var useUnknown = dependents is null || flowNames.Values.Any(n => n is null);
        var flowIds = flows.Select(f => f.Id).ToHashSet();

        var references = rows.Select(r =>
        {
            var fromMap = flows.Where(f => flowNames[f.Id]?.Contains(r.LogicalName) == true).Select(f => f.Id);
            var fromDependency = dependents?.Where(d => d.Required == r.Id && flowIds.Contains(d.Dependent)).Select(d => d.Dependent) ?? [];
            var other = dependents?
                .Where(d => d.Required == r.Id && !flowIds.Contains(d.Dependent) && componentIds.Contains(d.Dependent)
                            && d.ComponentType != SolutionComponentInventory.WorkflowComponentType)
                .Select(d => new Dependent(d.Dependent, d.ComponentType)).DistinctBy(d => d.Id).ToList() ?? [];

            return new ConnectionReference(
                r.Id, r.LogicalName, r.DisplayName, r.ConnectorId, r.ConnectionId, r.CreatedOn,
                components.FirstOrDefault(c => c.ObjectId == r.Id).ComponentType,
                BindingsFor(evidence, r.LogicalName),
                fromMap.Concat(fromDependency).Distinct().ToList(), other, useUnknown);
        }).ToList();

        return new ConnectionReferenceReview(references, flows, ConnectionReferenceClassifier.Classify(references, flows));
    }

    static IReadOnlyDictionary<EnvironmentRole, RoleBinding> BindingsFor(IReadOnlyList<RoleEvidence> evidence, string logicalName) =>
        evidence.ToDictionary(
            e => e.Role,
            e => new RoleBinding(e.HasFile, e.ConnectionIds.TryGetValue(logicalName, out var id) ? id : null));

    static async Task<List<(Guid ObjectId, int ComponentType)>> ReadComponentsAsync(
        IOrganizationServiceAsync2 service, string solutionUniqueName, CancellationToken ct)
    {
        var query = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid", "componenttype") };
        var solutionLink = query.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return entities
            .Select(e => (ObjectId: e.GetAttributeValue<Guid>("objectid"), Type: e.GetAttributeValue<OptionSetValue>("componenttype")?.Value))
            .Where(c => c.ObjectId != Guid.Empty && c.Type is not null)
            .Select(c => (c.ObjectId, c.Type!.Value))
            .ToList();
    }

    sealed record ReferenceRow(Guid Id, string LogicalName, string DisplayName, string ConnectorId, string? ConnectionId, DateTime? CreatedOn);

    static async Task<List<ReferenceRow>> ReadReferenceRowsAsync(
        IOrganizationServiceAsync2 service, List<Guid> allComponentIds, CancellationToken ct)
    {
        if (allComponentIds.Count == 0) return [];
        // No componenttype filter is possible: the value is environment-specific, so the whole id set is
        // offered to the connectionreference table and the intersection is what comes back.
        EntityNameLookup.EnsureInLimit(allComponentIds.Count, "component IDs", "Split the solution into smaller ones.");

        var query = new QueryExpression("connectionreference")
        {
            ColumnSet = new ColumnSet(
                "connectionreferenceid", "connectionreferencelogicalname", "connectionreferencedisplayname",
                "connectorid", "connectionid", "createdon"),
            NoLock = true,
        };
        query.Criteria.AddCondition("connectionreferenceid", ConditionOperator.In, allComponentIds.Cast<object>().ToArray());

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return entities
            .Select(e => new ReferenceRow(
                e.Id,
                e.GetAttributeValue<string>("connectionreferencelogicalname") ?? string.Empty,
                e.GetAttributeValue<string>("connectionreferencedisplayname") ?? string.Empty,
                e.GetAttributeValue<string>("connectorid") ?? string.Empty,
                e.GetAttributeValue<string>("connectionid"),
                e.GetAttributeValue<DateTime?>("createdon")))
            .Where(r => r.LogicalName.Length > 0)
            .ToList();
    }

    static async Task<List<CloudFlow>> ReadFlowsAsync(IOrganizationServiceAsync2 service, List<Guid> workflowIds, CancellationToken ct)
    {
        if (workflowIds.Count == 0) return [];
        EntityNameLookup.EnsureInLimit(workflowIds.Count, "workflow IDs", "Split the solution into smaller ones.");

        var query = new QueryExpression("workflow")
        {
            ColumnSet = new ColumnSet("workflowid", "name", "statecode", "versionnumber", "clientdata"),
            NoLock = true,
        };
        query.Criteria.AddCondition("workflowid", ConditionOperator.In, workflowIds.Cast<object>().ToArray());
        query.Criteria.AddCondition("category", ConditionOperator.Equal, CloudFlowCategory);

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return entities
            .Select(e => new CloudFlow(
                e.Id,
                e.GetAttributeValue<string>("name") ?? string.Empty,
                e.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0,
                e.RowVersion ?? e.GetAttributeValue<long?>("versionnumber")?.ToString(),
                e.GetAttributeValue<string>("clientdata")))
            .ToList();
    }

    // One query on the dependency table for every reference, not a RetrieveDependenciesForDelete per
    // reference: it is far faster, and the table is a superset, which errs toward "in use". A fault returns
    // null, meaning "can't tell", the opposite of the web resource checker, which moves on.
    static async Task<List<(Guid Required, Guid Dependent, int ComponentType)>?> ReadDependentsAsync(
        IOrganizationServiceAsync2 service, List<Guid> referenceIds, CancellationToken ct)
    {
        if (referenceIds.Count == 0) return [];

        try
        {
            EntityNameLookup.EnsureInLimit(referenceIds.Count, "connection reference IDs", "Split the solution into smaller ones.");
            var query = new QueryExpression("dependency")
            {
                ColumnSet = new ColumnSet("requiredcomponentobjectid", "dependentcomponentobjectid", "dependentcomponenttype"),
                NoLock = true,
            };
            query.Criteria.AddCondition("requiredcomponentobjectid", ConditionOperator.In, referenceIds.Cast<object>().ToArray());

            var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
            return entities
                .Select(e => (
                    e.GetAttributeValue<Guid>("requiredcomponentobjectid"),
                    e.GetAttributeValue<Guid>("dependentcomponentobjectid"),
                    e.GetAttributeValue<OptionSetValue>("dependentcomponenttype")?.Value ?? 0))
                .ToList();
        }
        // A client-side timeout surfaces as OperationCanceledException without the caller's token firing;
        // that is still "check could not run", not cancellation.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
