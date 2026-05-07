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
        await output.Progress()
            .StartAsync(async ctx =>
            {
                var createsTask = plan.Creates.Count > 0
                    ? ctx.AddTask("Creating web resources", maxValue: plan.Creates.Count)
                    : null;
                var updatesTask = plan.Updates.Count > 0
                    ? ctx.AddTask("Updating web resources", maxValue: plan.Updates.Count)
                    : null;
                var addsTask = plan.AddsToSolution.Count > 0
                    ? ctx.AddTask("Adding web resources to solution", maxValue: plan.AddsToSolution.Count)
                    : null;
                var removesTask = runMode != RunMode.Save && plan.RemovesFromSolution.Count > 0
                    ? ctx.AddTask("Removing web resources from solution", maxValue: plan.RemovesFromSolution.Count)
                    : null;
                var deletesTask = runMode != RunMode.Save && plan.Deletes.Count > 0
                    ? ctx.AddTask("Deleting web resources", maxValue: plan.Deletes.Count)
                    : null;
                var publishTask = publishAfterSync && plan.PublishCount > 0
                    ? ctx.AddTask("Publishing web resources", maxValue: plan.PublishCount)
                    : null;

                await _executor.ExecuteAsync(
                    service,
                    plan,
                    publishAfterSync,
                    runMode == RunMode.Save,
                    cancellationToken,
                    createsTask,
                    updatesTask,
                    addsTask,
                    removesTask,
                    deletesTask,
                    publishTask).ConfigureAwait(false);
            })
            .ConfigureAwait(false);
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
