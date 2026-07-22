using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// ConnectionReference's componenttype is env-specific, same shape as CustomApi/Bot — a candidate can
// only be identified as a ConnectionReference by querying the "connectionreference" table directly, so
// match and detect are the same batched async call. This handler owns its own query and try/catch
// against "connectionreference" only — a BotHandler failure (or vice versa) can never affect this
// handler's detection.
public sealed class ConnectionReferenceHandler(IAnsiConsole console) : IOrphanHandler
{
    public HandlerStatus Status => HandlerStatus.Active;

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        var idList = candidates.Select(c => c.ObjectId).Distinct().ToList();

        List<Entity> rows;
        try
        {
            // The 2000-id guard runs inside this try so an oversized batch degrades the same way any
            // other query fault does (warn + skip), rather than throwing uncaught.
            if (idList.Count > 2000)
                throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {idList.Count} IDs (max 2000). Solution has too many orphan candidates for ConnectionReference detection.");

            var idArray = idList.Select(id => (object)id).ToArray();
            var query = new QueryExpression("connectionreference")
            {
                ColumnSet = new ColumnSet("connectionreferencelogicalname"),
                Criteria  = { Conditions = { new ConditionExpression("connectionreferenceid", ConditionOperator.In, idArray) } }
            };
            rows = await context.Service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        }
        // A business fault (no matching rows) is not evidence of deletion — resolves quietly to "no
        // candidates claimed", unlike an infrastructure fault, which additionally warns.
        catch (FaultException<OrganizationServiceFault>)
        {
            return new HandlerDetectionResult([], new HashSet<Guid>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.Warning($"ConnectionReference orphan detection failed ({Markup.Escape(ex.Message)}) — its candidates are skipped this run.");
            return new HandlerDetectionResult([], new HashSet<Guid>());
        }

        // A row existing in the table at all is enough evidence this candidate is a ConnectionReference —
        // claimed regardless of logical-name or local-declaration status.
        var claimedIds = rows.Select(r => r.Id).ToHashSet();

        // ConnectionReference has no dedicated folder like Bot's bots/<schemaname>/bot.xml — it's
        // declared inline in Other/Customizations.xml's <connectionreferences> section (see
        // ComponentClassifier.ScanConnectionReferenceLogicalNames).
        var localLogicalNames = ComponentClassifier.ScanConnectionReferenceLogicalNames(context.DataverseSolutionSrcRoot);

        var componentTypeById = new Dictionary<Guid, int>();
        foreach (var candidate in candidates)
            componentTypeById[candidate.ObjectId] = candidate.ComponentType;

        var findings = new List<HandlerFinding>();
        foreach (var row in rows)
        {
            // No resolved connectionreferencelogicalname means local-source verification never actually
            // ran for this candidate — not evidence of removal. Skip rather than default to "orphaned".
            var logicalName = row.GetAttributeValue<string>("connectionreferencelogicalname");
            if (string.IsNullOrEmpty(logicalName)) continue;
            if (localLogicalNames.Contains(logicalName)) continue; // still declared locally — not orphaned

            findings.Add(new HandlerFinding(
                ObjectId: row.Id,
                ComponentType: componentTypeById.GetValueOrDefault(row.Id),
                DisplayName: $"ConnectionReference '{logicalName}' ({row.Id})",
                // ConnectionReference can't be deleted automatically.
                Action: OrphanAction.Manual,
                // Prio2 always — a live connection reference remains usable by anything still holding
                // its logical name.
                Priority: OrphanPriority.Prio2,
                SequenceHint: 0,
                Timing: OrphanTiming.PreImportEligible,
                EntityName: "connectionreference"));
        }

        return new HandlerDetectionResult(findings, claimedIds);
    }
}
