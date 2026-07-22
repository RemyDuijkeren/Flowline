using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup;

// Shared batched-query detection for handlers whose componenttype is env-specific — a candidate can
// only be identified by querying its backing table directly, so match and detect are the same batched
// async call (see BotHandler/ConnectionReferenceHandler doc comments for why each owns its own query
// rather than sharing one across tables).
public static class EntityDetectionHelper
{
    public static async Task<HandlerDetectionResult> DetectByTableAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        IAnsiConsole console,
        CancellationToken ct,
        string entityLogicalName,
        string idAttribute,
        string keyAttribute,
        ColumnSet columnSet,
        IReadOnlySet<string> localKeys,
        string label,
        Func<Entity, OrphanPriority> priority)
    {
        if (candidates.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        var idList = candidates.Select(c => c.ObjectId).Distinct().ToList();

        var rows = await DataverseFaultTolerance.TryQueryAsync(async () =>
        {
            // The 2000-id guard runs inside this query so an oversized batch degrades the same way any
            // other query fault does (warn + skip), rather than throwing uncaught.
            if (idList.Count > 2000)
                throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {idList.Count} IDs (max 2000). Solution has too many orphan candidates for {label} detection.");

            var idArray = idList.Select(id => (object)id).ToArray();
            var query = new QueryExpression(entityLogicalName)
            {
                ColumnSet = columnSet,
                Criteria  = { Conditions = { new ConditionExpression(idAttribute, ConditionOperator.In, idArray) } }
            };
            return await context.Service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        }, [], console, msg => $"{label} orphan detection failed ({msg}) — its candidates are skipped this run.");

        // A row existing in the table at all is enough evidence this candidate is a match — claimed
        // regardless of key or local-declaration status.
        var claimedIds = rows.Select(r => r.Id).ToHashSet();

        var componentTypeById = new Dictionary<Guid, int>();
        foreach (var candidate in candidates)
            componentTypeById[candidate.ObjectId] = candidate.ComponentType;

        var findings = new List<HandlerFinding>();
        foreach (var row in rows)
        {
            // No resolved key means local-source verification never actually ran for this candidate —
            // not evidence of removal. Skip rather than default to "orphaned".
            var key = row.GetAttributeValue<string>(keyAttribute);
            if (string.IsNullOrEmpty(key)) continue;
            if (localKeys.Contains(key)) continue; // still declared locally — not orphaned

            findings.Add(new HandlerFinding(
                ObjectId: row.Id,
                ComponentType: componentTypeById.GetValueOrDefault(row.Id),
                DisplayName: $"{label} '{key}' ({row.Id})",
                // Neither Bot nor ConnectionReference can be deleted automatically.
                Action: OrphanAction.Manual,
                Priority: priority(row),
                SequenceHint: 0,
                Timing: OrphanTiming.PreImportEligible,
                EntityName: entityLogicalName));
        }

        return new HandlerDetectionResult(findings, claimedIds);
    }
}
