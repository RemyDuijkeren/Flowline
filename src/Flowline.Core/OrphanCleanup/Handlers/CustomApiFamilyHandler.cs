using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// The CustomApi/CustomApiRequestParameter/CustomApiResponseProperty family's componenttype is
// env-specific — a candidate can only be identified as CustomApi-family by querying the backing tables
// directly, so match and detect are the same batched async call.
//
// Each of the three tables is queried and caught independently — a failure on one table (e.g.
// customapiresponseproperty) must not blank out detection for the other two (see
// docs/solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md).
public sealed class CustomApiFamilyHandler(IAnsiConsole console) : IOrphanHandler
{
    public HandlerStatus Status => HandlerStatus.Active;

    // Children before parent — the request-parameter and response-property child records execute first
    // (SequenceHint 0), the customapi parent last (1).
    const int ChildSequenceHint  = 0;
    const int ParentSequenceHint = 1;

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        var idList = candidates.Select(c => c.ObjectId).Distinct().ToList();

        // Each table is queried and caught independently — a business fault (no matching rows) resolves
        // quietly to "no candidates claimed for this table", same as an infrastructure fault (which
        // additionally warns). Either way a candidate this table can't resolve a name for is skipped, not
        // reported as orphaned.
        //
        // RowIds carries every id found, independent of the name filter below — a null/empty name is
        // still evidence this candidate belongs to this table, so ClaimedIds includes it even though
        // Names does not.
        async Task<(Dictionary<Guid, string> Names, HashSet<Guid> RowIds)> ResolveNamesAsync(string entityLogicalName, string idAttribute)
        {
            try
            {
                // The 2000-id guard runs inside each table's own try so an oversized batch degrades
                // per-table (warn + skip) rather than throwing uncaught for all three at once.
                if (idList.Count > 2000)
                    throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {idList.Count} IDs (max 2000). Solution has too many orphan candidates for CustomApi-family detection.");

                var idArray = idList.Select(id => (object)id).ToArray();
                var query = new QueryExpression(entityLogicalName)
                {
                    ColumnSet = new ColumnSet("name"),
                    Criteria  = { Conditions = { new ConditionExpression(idAttribute, ConditionOperator.In, idArray) } }
                };
                var entities = await context.Service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
                var names = entities
                    .Where(e => !string.IsNullOrEmpty(e.GetAttributeValue<string>("name")))
                    .ToDictionary(e => e.Id, e => e.GetAttributeValue<string>("name")!);
                var rowIds = entities.Select(e => e.Id).ToHashSet();
                return (names, rowIds);
            }
            catch (FaultException<OrganizationServiceFault>)
            {
                return ([], []);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                console.Warning($"CustomApi-family orphan detection failed for '{entityLogicalName}' ({Markup.Escape(ex.Message)}) — its candidates are skipped this run.");
                return ([], []);
            }
        }

        var caTask    = ResolveNamesAsync("customapi",                 "customapiid");
        var paramTask = ResolveNamesAsync("customapirequestparameter", "customapirequestparameterid");
        var propTask  = ResolveNamesAsync("customapiresponseproperty", "customapiresponsepropertyid");
        await Task.WhenAll(caTask, paramTask, propTask).ConfigureAwait(false);

        // CustomApi has no GUID in local source — uniquename is the only local identity, so a recreated
        // CustomApi (same uniquename, new customapiid) must not be reported.
        var localNames = ComponentClassifier.ScanCustomApiNames(context.DataverseSolutionSrcRoot);

        var componentTypeById = new Dictionary<Guid, int>();
        foreach (var candidate in candidates)
            componentTypeById[candidate.ObjectId] = candidate.ComponentType;

        // A candidate is claimed once its table lookup found a matching row, even if AddFindings then
        // suppresses it. Union across all three tables since a candidate id only ever appears in one.
        var claimedIds = caTask.Result.RowIds
            .Concat(paramTask.Result.RowIds)
            .Concat(propTask.Result.RowIds)
            .ToHashSet();

        var findings = new List<HandlerFinding>();
        AddFindings(findings, caTask.Result.Names,    "customapi",                 "CustomApi",                 localNames.ApiUniqueNames,        ParentSequenceHint, componentTypeById);
        AddFindings(findings, paramTask.Result.Names, "customapirequestparameter", "CustomApiRequestParameter", localNames.RequestParameterNames, ChildSequenceHint,  componentTypeById);
        AddFindings(findings, propTask.Result.Names,  "customapiresponseproperty", "CustomApiResponseProperty", localNames.ResponsePropertyNames, ChildSequenceHint,  componentTypeById);

        return new HandlerDetectionResult(findings, claimedIds);
    }

    static void AddFindings(
        List<HandlerFinding> findings,
        Dictionary<Guid, string> resolvedNames,
        string entityName,
        string displayLabel,
        IReadOnlySet<string> localNames,
        int sequenceHint,
        Dictionary<Guid, int> componentTypeById)
    {
        foreach (var (id, name) in resolvedNames)
        {
            if (localNames.Contains(name)) continue; // still declared locally — not orphaned

            findings.Add(new HandlerFinding(
                ObjectId: id,
                ComponentType: componentTypeById.GetValueOrDefault(id),
                DisplayName: $"{displayLabel} '{name}' ({id})",
                // RemoveFromSolution-vs-Delete cross-solution resolution is a centralized orchestrator
                // concern, not this handler's — it always starts from Delete.
                Action: OrphanAction.Delete,
                // Prio2 always — the CustomApi record itself is the live callable surface and stays
                // invocable until deleted.
                Priority: OrphanPriority.Prio2,
                SequenceHint: sequenceHint,
                Timing: OrphanTiming.PreImportEligible,
                EntityName: entityName));
        }
    }
}
