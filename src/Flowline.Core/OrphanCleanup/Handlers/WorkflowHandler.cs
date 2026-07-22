using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Handles Workflow (29) detection and per-instance Prio. TryDeactivateWorkflowAsync's
// deactivate-before-delete mechanic stays in the orchestrator's execution step — it's an execution-time
// action, not a classification decision. This handler only reads the live statecode to decide Prio:
// Activated -> Prio2 (still silently running deleted logic), Deactivated -> Prio3 (default/safe to
// clean up). It does not execute, delete, or deactivate anything.
//
// The live query is caught — a failed query degrades to an empty byId map, which the loop below already
// treats identically to "record already gone" (Prio3 default, bare id).
public sealed class WorkflowHandler(IAnsiConsole console) : IOrphanHandler
{
    const int WorkflowComponentType = 29;

    // Workflow statecode option set: 0 = Draft/Deactivated, 1 = Activated (matches
    // OrphanCleanupService.TryDeactivateWorkflowAsync, which deactivates by setting statecode to 0).
    const int ActivatedStateCode = 1;

    public HandlerStatus Status => HandlerStatus.Active;

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        var workflowIds = candidates
            .Where(c => c.ComponentType == WorkflowComponentType)
            .Select(c => c.ObjectId)
            .Distinct()
            .ToList();

        if (workflowIds.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        // Every componenttype-29 candidate is claimed — this handler always emits a finding for each
        // one (Prio3-default when unresolved), so ClaimedIds equals the full workflowIds set.
        var claimedIds = workflowIds.ToHashSet();

        var query = new QueryExpression("workflow")
        {
            ColumnSet = new ColumnSet("name", "statecode"),
            Criteria  = { Conditions = { new ConditionExpression("workflowid", ConditionOperator.In, workflowIds.Select(id => (object)id).ToArray()) } }
        };

        var entities = await DataverseFaultTolerance.TryQueryAsync(
            () => context.Service.RetrieveAllAsync(query, ct),
            [], console, msg => $"Workflow orphan detection failed ({msg}) — defaulting to Prio3 this run.");
        var byId = entities.ToDictionary(e => e.Id);

        var findings = new List<HandlerFinding>(workflowIds.Count);
        foreach (var id in workflowIds)
        {
            // Unresolved (record already gone by the time this handler queries it) defaults to Prio3 —
            // we can't confirm it's still live, so it's not treated as risky.
            string? name = null;
            var priority = OrphanPriority.Prio3;

            if (byId.TryGetValue(id, out var entity))
            {
                name = entity.GetAttributeValue<string>("name");
                var stateCode = entity.GetAttributeValue<OptionSetValue>("statecode")?.Value;
                priority = stateCode == ActivatedStateCode ? OrphanPriority.Prio2 : OrphanPriority.Prio3;
            }

            var displayName = name != null ? $"Workflow '{name}' ({id})" : $"Workflow {id}";
            findings.Add(new HandlerFinding(
                id,
                WorkflowComponentType,
                displayName,
                OrphanAction.Delete,
                priority,
                SequenceHint: 0,
                OrphanTiming.PreImportEligible));
        }

        return new HandlerDetectionResult(findings, claimedIds);
    }
}
