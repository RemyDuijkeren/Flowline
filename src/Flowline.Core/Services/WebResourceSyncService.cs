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
        string? patchSolutionName = null,
        bool publishAfterSync = true,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webresourceRoot))
            throw new ArgumentException("webresourceRoot is required.", nameof(webresourceRoot));
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required.", nameof(solutionName));

        var snapshot = await _reader.LoadSnapshotAsync(service, webresourceRoot, solutionName, patchSolutionName, cancellationToken).ConfigureAwait(false);
        var plan = _planner.Plan(snapshot, runMode);
        output.Info("[green]Web resource plan ready[/]");

        if (plan.TotalChanges == 0)
        {
            foreach (var a in plan.Skips.Values)
                output.Skip($"Web resource '{a.Name}' kept ({a.Reason})");

            output.Skip("Web resources already up to date — skipping");
            return;
        }

        if (runMode == RunMode.DryRun)
        {
            WriteDryRunSummary(plan, publishAfterSync);
            return;
        }

        await _executor.ExecuteAsync(service, plan, publishAfterSync, cancellationToken).ConfigureAwait(false);
    }

    void WriteDryRunSummary(WebResourceSyncPlan plan, bool publishAfterSync)
    {
        foreach (var a in plan.Creates.Values) output.Skip($"Web resource '{a.Name}' — would create");
        foreach (var a in plan.Updates.Values) output.Skip($"Web resource '{a.Name}' — would update");
        foreach (var a in plan.UpdatesAndAddsToPatch.Values) output.Skip($"Web resource '{a.Name}' — would update and add to patch");
        foreach (var a in plan.Deletes.Values) output.Skip($"Web resource '{a.Name}' — would delete");
        foreach (var a in plan.RemovesFromSolution.Values) output.Skip($"Web resource '{a.Name}' — would remove from solution");
        foreach (var a in plan.Skips.Values) output.Skip($"Web resource '{a.Name}' — kept ({a.Reason})");

        var publishCount = publishAfterSync ? plan.PublishCount : 0;
        if (publishCount > 0)
            output.Skip($"{publishCount} web resource(s) — would publish");

        output.Info($"[green]Dry run: {plan.Deletes.Count} delete(s), {plan.RemovesFromSolution.Count} remove(s), {plan.Creates.Count} create(s), {plan.Updates.Count + plan.UpdatesAndAddsToPatch.Count} update(s), {plan.Skips.Count} skip(s). Run without --dry-run to apply.[/]");
    }
}
