using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// U5: migrates the CustomApi/CustomApiRequestParameter/CustomApiResponseProperty entity-detected family
// (see docs/plans/2026-07-08-001-refactor-orphan-cleanup-handler-architecture-plan.md, KTD2/KTD4/KTD5/
// KTD6/KTD8) into its own handler. Unlike the componenttype-gated handlers, this family's componenttype
// is env-specific — a candidate can only be identified as CustomApi-family by querying the backing
// tables directly, so match and detect are the same batched async call (see the Planning Contract's HTD
// note on entity-detected dispatch).
//
// KTD4: each of the three tables is queried and caught independently — a failure on one table (e.g.
// customapiresponseproperty) must not blank out detection for the other two. This is the structural fix
// for part 9's bug #1 (docs/solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-pipeline.md),
// applied here from the start rather than inherited as a shared try/catch.
public sealed class CustomApiFamilyHandler(IAnsiConsole console) : IOrphanHandler
{
    public HandlerStatus Status => HandlerStatus.Active;

    // KTD1: children before parent, matching the deletion order OrphanCleanupService's old
    // CustomApiEntityOrder array previously encoded (removed during U9's orchestrator rewrite, now
    // expressed as a per-family SequenceHint) — the request-parameter and response-property child
    // records execute first (SequenceHint 0), the customapi parent last (1).
    const int ChildSequenceHint  = 0;
    const int ParentSequenceHint = 1;

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        var idList = candidates.Select(c => c.ObjectId).Distinct().ToList();

        // Each table is queried and caught independently (KTD4) — a business fault (the table genuinely
        // has no matching rows, e.g. not provisioned in this org edition) is distinguished from an
        // infrastructure fault (network/auth/throttle): the former resolves quietly to "no candidates
        // claimed for this table" (KTD5/KTD6 — not evidence any of them were deleted), the latter warns
        // and does the same. Either way, a candidate this table can't resolve a name for is simply never
        // added to `resolved` below, so it's skipped rather than reported as orphaned (KTD5).
        //
        // RowIds carries every id the query actually found a row for, independent of the name filter
        // below — a row with a null/empty name is still evidence this candidate belongs to this table
        // (KTD5 skip, not "not claimed"), so ClaimedIds must include it even though Names (and so
        // AddFindings) does not. Each async local call computes and returns its own tuple entirely
        // within its own async context, so reading .Result after Task.WhenAll below is race-free — no
        // shared mutable state between the three concurrent calls.
        async Task<(Dictionary<Guid, string> Names, HashSet<Guid> RowIds)> ResolveNamesAsync(string entityLogicalName, string idAttribute)
        {
            try
            {
                // Code-review fault-isolation fix: the 2000-id guard now runs inside each table's own
                // try, per KTD4 (independent isolation) — an oversized batch degrades the same way any
                // other query fault for this table does (warn + skip), rather than throwing uncaught for
                // all three tables before any of their tries start.
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

        // CustomApi source has no GUID anywhere — uniquename is the only local identity (see
        // ComponentClassifier.ScanCustomApiNames) — so a recreated CustomApi (same uniquename, new
        // customapiid) must not be reported, even though its objectid differs from before.
        var localNames = ComponentClassifier.ScanCustomApiNames(context.PackageSrcRoot);

        var componentTypeById = new Dictionary<Guid, int>();
        foreach (var candidate in candidates)
            componentTypeById[candidate.ObjectId] = candidate.ComponentType;

        // A candidate is claimed once its table lookup found a matching row — regardless of whether the
        // row's name was null (KTD5 skip) or the local-source suppression below (AddFindings) then
        // keeps it out of Findings. Union across all three tables since a single candidate id only ever
        // appears in one.
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
                // Auto per KTD8; RemoveFromSolution-vs-Delete cross-solution resolution is a centralized
                // orchestrator concern (U9), not this handler's — it always starts from Delete.
                Action: OrphanAction.Delete,
                // Prio2 always (KTD8) — the CustomApi record itself is the live callable surface and
                // stays invocable until deleted.
                Priority: OrphanPriority.Prio2,
                SequenceHint: sequenceHint,
                Timing: OrphanTiming.PreImportEligible,
                EntityName: entityName));
        }
    }
}
