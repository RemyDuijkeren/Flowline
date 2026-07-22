using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Bot's componenttype is env-specific (same shape CustomApiFamilyHandler documents for its own family)
// — a candidate can only be identified as a Bot by querying the "bot" table directly, so match and
// detect are the same batched async call. This handler owns its own query against "bot" only — a
// ConnectionReferenceHandler failure (or vice versa) can never affect this handler's detection.
public sealed class BotHandler(IAnsiConsole console) : IOrphanHandler
{
    public HandlerStatus Status => HandlerStatus.Active;

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        var idList = candidates.Select(c => c.ObjectId).Distinct().ToList();

        var rows = await DataverseFaultTolerance.TryQueryAsync(async () =>
        {
            // The 2000-id guard runs inside this query so an oversized batch degrades the same way any
            // other query fault does (warn + skip), rather than throwing uncaught.
            if (idList.Count > 2000)
                throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {idList.Count} IDs (max 2000). Solution has too many orphan candidates for Bot detection.");

            var idArray = idList.Select(id => (object)id).ToArray();
            var query = new QueryExpression("bot")
            {
                // schemaname is Bot's identity attribute — "name" is a separate, unrelated display
                // string. publishedon distinguishes Published/live (non-null) from never-published/draft
                // (null) for the Prio rule below — unlike componentstate, which tracks solution-layer
                // publish state and doesn't vary with the Copilot's own authoring lifecycle.
                ColumnSet = new ColumnSet("schemaname", "publishedon"),
                Criteria  = { Conditions = { new ConditionExpression("botid", ConditionOperator.In, idArray) } }
            };
            return await context.Service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        }, [], console, msg => $"Bot orphan detection failed ({msg}) — its candidates are skipped this run.");

        // A row existing in the table at all is enough evidence this candidate is a Bot — claimed
        // regardless of schemaname or local-declaration status.
        var claimedIds = rows.Select(r => r.Id).ToHashSet();

        // Bot has no GUID anywhere in local source — schemaname (bots/<schemaname>/bot.xml) is the only
        // local identity (see ComponentClassifier.ScanBotSchemaNames).
        var localSchemaNames = ComponentClassifier.ScanBotSchemaNames(context.DataverseSolutionSrcRoot);

        var componentTypeById = new Dictionary<Guid, int>();
        foreach (var candidate in candidates)
            componentTypeById[candidate.ObjectId] = candidate.ComponentType;

        var findings = new List<HandlerFinding>();
        foreach (var row in rows)
        {
            // No resolved schemaname means local-source verification never actually ran for this
            // candidate — not evidence of removal. Skip rather than default to "orphaned".
            var schemaName = row.GetAttributeValue<string>("schemaname");
            if (string.IsNullOrEmpty(schemaName)) continue;
            if (localSchemaNames.Contains(schemaName)) continue; // still declared locally — not orphaned

            // Published/live (publishedon set) -> Prio2; never-published/draft (publishedon null) ->
            // Prio3.
            var publishedOn = row.GetAttributeValue<DateTime?>("publishedon");
            var priority = publishedOn.HasValue ? OrphanPriority.Prio2 : OrphanPriority.Prio3;

            findings.Add(new HandlerFinding(
                ObjectId: row.Id,
                ComponentType: componentTypeById.GetValueOrDefault(row.Id),
                DisplayName: $"Bot '{schemaName}' ({row.Id})",
                // Bot can't be deleted automatically.
                Action: OrphanAction.Manual,
                Priority: priority,
                SequenceHint: 0,
                Timing: OrphanTiming.PreImportEligible,
                EntityName: "bot"));
        }

        return new HandlerDetectionResult(findings, claimedIds);
    }
}
