using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class WebResourceService(IAnsiConsole output, FlowlineRuntimeOptions opt)
{
    readonly WebResourceReader _reader = new();
    readonly WebResourcePlanner _planner = new(output, opt);
    readonly WebResourceExecutor _executor = new(output, opt);

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
        var snapshot = await output.Status()
            .StartAsync("Loading web resource snapshot...", _ => _reader.LoadSnapshotAsync(service, webresourceRoot, solutionName, cancellationToken))
            .ConfigureAwait(false);
        output.Info("[green]Snapshot loaded[/]");

        // Phase 2: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot);
        WritePlanVerbose(plan);
        output.Info("[green]Web resource plan ready[/]");

        if (plan.TotalChanges == 0)
        {
            foreach (var a in plan.Skips)
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

    void WritePlanVerbose(WebResourceSyncPlan plan)
    {
        if (!opt.IsVerbose)
            return;

        output.Verbose("Web resource plan", opt);
        output.Verbose($"  Summary: {plan.TotalDeletes} delete(s), {plan.TotalUpserts} upsert(s), {plan.AddsToSolution.Count} add-to-solution action(s)", opt);

        if (plan.Creates.Count > 0)
        {
            output.Verbose($"  Creates ({plan.Creates.Count})", opt);
            foreach (var a in plan.Creates.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt);
        }

        if (plan.Updates.Count > 0)
        {
            output.Verbose($"  Updates ({plan.Updates.Count})", opt);
            foreach (var a in plan.Updates.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt);
        }

        if (plan.AddsToSolution.Count > 0)
        {
            output.Verbose($"  Add to solution ({plan.AddsToSolution.Count})", opt);
            foreach (var a in plan.AddsToSolution.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt);
        }

        if (plan.Deletes.Count > 0)
        {
            output.Verbose($"  Deletes ({plan.Deletes.Count})", opt);
            foreach (var a in plan.Deletes.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt);
        }

        if (plan.RemovesFromSolution.Count > 0)
        {
            output.Verbose($"  Remove from solution ({plan.RemovesFromSolution.Count})", opt);
            foreach (var a in plan.RemovesFromSolution.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt);
        }

        if (plan.Skips.Count > 0)
        {
            output.Verbose($"  Skips ({plan.Skips.Count})", opt);
            foreach (var a in plan.Skips.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name} ({a.Reason})", opt);
        }
    }

    void WriteDryRunSummary(WebResourceSyncPlan plan, bool publishAfterSync)
    {
        foreach (var a in plan.Creates) output.Skip($"Web resource '{a.Name}' — would create");
        foreach (var a in plan.Updates) output.Skip($"Web resource '{a.Name}' — would update");
        foreach (var a in plan.AddsToSolution) output.Skip($"Web resource '{a.Name}' — would add to solution");
        foreach (var a in plan.Deletes) output.Skip($"Web resource '{a.Name}' — would delete");
        foreach (var a in plan.RemovesFromSolution) output.Skip($"Web resource '{a.Name}' — would remove from solution");
        foreach (var a in plan.Skips) output.Skip($"Web resource '{a.Name}' — kept ({a.Reason})");

        var publishCount = publishAfterSync ? plan.PublishCount : 0;
        if (publishCount > 0)
            output.Skip($"{publishCount} web resource(s) — would publish");

        output.Info($"[green]Dry run: {plan.Deletes.Count} delete(s), {plan.RemovesFromSolution.Count} remove(s), {plan.Creates.Count} create(s), {plan.Updates.Count} update(s), {plan.AddsToSolution.Count} add(s), {plan.Skips.Count} skip(s). Run without --dry-run to apply.[/]");
    }
}
