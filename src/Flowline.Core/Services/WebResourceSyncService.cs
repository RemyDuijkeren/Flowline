using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class WebResourceSyncService(IFlowlineOutput output)
{
    readonly WebResourceSyncReader _reader = new();
    readonly WebResourceSyncPlanner _planner = new(output);
    readonly WebResourceSyncPlanExecutor _executor = new(output);

    public async Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        bool publishAfterSync = true,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webresourceRoot))
            throw new ArgumentException("webresourceRoot is required.", nameof(webresourceRoot));
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required.", nameof(solutionName));

        // Phase 1: Load snapshot (all Dataverse state in parallel)
        var snapshot = await _reader.LoadSnapshotAsync(service, webresourceRoot, solutionName, cancellationToken).ConfigureAwait(false);
        output.Info("[green]Snapshot loaded[/]");

        // Phase 2: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot);
        output.Info("[green]Web resource plan ready[/]");

        if (plan.TotalChanges == 0)
        {
            foreach (var a in plan.Skips.Values)
                output.Skip($"Web resource '{a.Name}' kept ({a.Reason})");

            output.Skip("Web resources already up to date — skipping");
            return;
        }

        // Dry-run: print preview and return without making any changes
        if (runMode == RunMode.DryRun)
        {
            WriteDryRunSummary(plan, publishAfterSync);
            return;
        }

        // Phase 3: Execute the plan
        await _executor.ExecuteAsync(service, plan, publishAfterSync, runMode == RunMode.Save, cancellationToken).ConfigureAwait(false);
    }

    void WriteDryRunSummary(WebResourceSyncPlan plan, bool publishAfterSync)
    {
        foreach (var a in plan.Creates.Values) output.Skip($"Web resource '{a.Name}' — would create");
        foreach (var a in plan.Updates.Values) output.Skip($"Web resource '{a.Name}' — would update");
        foreach (var a in plan.Deletes.Values) output.Skip($"Web resource '{a.Name}' — would delete");
        foreach (var a in plan.RemovesFromSolution.Values) output.Skip($"Web resource '{a.Name}' — would remove from solution");
        foreach (var a in plan.Skips.Values) output.Skip($"Web resource '{a.Name}' — kept ({a.Reason})");

        var publishCount = publishAfterSync ? plan.PublishCount : 0;
        if (publishCount > 0)
            output.Skip($"{publishCount} web resource(s) — would publish");

        output.Info($"[green]Dry run: {plan.Deletes.Count} delete(s), {plan.RemovesFromSolution.Count} remove(s), {plan.Creates.Count} create(s), {plan.Updates.Count} update(s), {plan.Skips.Count} skip(s). Run without --dry-run to apply.[/]");
    }
}
