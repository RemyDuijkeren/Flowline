using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class WebResourceService(IAnsiConsole output, FlowlineRuntimeOptions opt)
{
    readonly WebResourceReader _reader = new();
    readonly WebResourcePlanner _planner = new(output, opt.IsVerbose);
    readonly WebResourceExecutor _executor = new(output, opt.IsVerbose);

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
        var snapshot = await output.Status().StartAsync("Loading web resource snapshot...", _ =>
            _reader.LoadSnapshotAsync(service, webresourceRoot, solutionName, cancellationToken)).ConfigureAwait(false);
        WriteSnapshotVerbose(snapshot);
        output.Info("Snapshot web resources loaded");

        // Phase 2: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot);
        WritePlanVerbose(plan);
        output.Info("Web resource plan ready");

        if (plan.TotalChanges == 0)
        {
            foreach (var a in plan.Skips)
                output.Skip($"Web resource '{a.Name}' kept ({a.Reason})");

            output.Success("Web resources already up to date — skipping");
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

    public async Task DownloadWebResourcesAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await output.Status().StartAsync(
            $"Loading web resources for {solutionName}...",
            _ => _reader.LoadSnapshotAsync(service, webresourceRoot, solutionName, cancellationToken))
            .ConfigureAwait(false);

        if (snapshot.DataverseResources.Count == 0)
        {
            output.Skip("No web resources in solution — skipping");
            return;
        }

        var prefix = $"{snapshot.Solution.PublisherPrefix}_{solutionName}/";
        var count = 0;

        foreach (var (name, resource) in snapshot.DataverseResources)
        {
            if (string.IsNullOrEmpty(resource.Content)) continue;

            var relativePath = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? name[prefix.Length..]
                : name;

            var localPath = Path.Combine(webresourceRoot, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, Convert.FromBase64String(resource.Content), cancellationToken).ConfigureAwait(false);
            output.Verbose(name, opt.IsVerbose);
            count++;
        }

        output.Success($"[bold]{count}[/] web resource(s) downloaded");
    }

    void WriteSnapshotVerbose(WebResourceSyncSnapshot snapshot)
    {
        if (!opt.IsVerbose) return;

        output.Write(BuildResourceTree($"Dataverse ({snapshot.DataverseResources.Count})", snapshot.DataverseResources.Keys));
        output.Write(BuildResourceTree($"Local ({snapshot.LocalResources.Count})", snapshot.LocalResources.Keys));
    }

    static Tree BuildResourceTree(string label, IEnumerable<string> names)
    {
        var tree = new Tree(label) { Style = Style.Parse("dim") };

        foreach (var group in names
            .Select(n => n.Split('/'))
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folderNode = tree.AddNode($"{group.Key}");
            AddTreeChildren(folderNode, group.Select(p => p[1..]).Where(p => p.Length > 0).ToList());
        }

        return tree;
    }

    static void AddTreeChildren(TreeNode parent, List<string[]> paths)
    {
        foreach (var folder in paths
            .Where(p => p.Length > 1)
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var node = parent.AddNode($"{folder.Key}");
            AddTreeChildren(node, folder.Select(p => p[1..]).ToList());
        }

        foreach (var file in paths
            .Where(p => p.Length == 1)
            .Select(p => p[0])
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            parent.AddNode($"{file}");
        }
    }

    void WritePlanVerbose(WebResourceSyncPlan plan)
    {
        if (!opt.IsVerbose)
            return;

        output.Verbose("Web resource plan", opt.IsVerbose);
        output.Verbose($"  Summary: {plan.TotalDeletes} delete(s), {plan.TotalUpserts} upsert(s), {plan.AddsToSolution.Count} add-to-solution action(s)", opt.IsVerbose);

        if (plan.Creates.Count > 0)
        {
            output.Verbose($"  Creates ({plan.Creates.Count})", opt.IsVerbose);
            foreach (var a in plan.Creates.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt.IsVerbose);
        }

        if (plan.Updates.Count > 0)
        {
            output.Verbose($"  Updates ({plan.Updates.Count})", opt.IsVerbose);
            foreach (var a in plan.Updates.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt.IsVerbose);
        }

        if (plan.AddsToSolution.Count > 0)
        {
            output.Verbose($"  Add to solution ({plan.AddsToSolution.Count})", opt.IsVerbose);
            foreach (var a in plan.AddsToSolution.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt.IsVerbose);
        }

        if (plan.Deletes.Count > 0)
        {
            output.Verbose($"  Deletes ({plan.Deletes.Count})", opt.IsVerbose);
            foreach (var a in plan.Deletes.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt.IsVerbose);
        }

        if (plan.RemovesFromSolution.Count > 0)
        {
            output.Verbose($"  Remove from solution ({plan.RemovesFromSolution.Count})", opt.IsVerbose);
            foreach (var a in plan.RemovesFromSolution.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name}", opt.IsVerbose);
        }

        if (plan.Skips.Count > 0)
        {
            output.Verbose($"  Skips ({plan.Skips.Count})", opt.IsVerbose);
            foreach (var a in plan.Skips.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                output.Verbose($"    - {a.Name} ({a.Reason})", opt.IsVerbose);
        }
    }

    void WriteDryRunSummary(WebResourceSyncPlan plan, bool publishAfterSync)
    {
        foreach (var a in plan.Creates) output.Info($"Web resource '{a.Name}' — would create");
        foreach (var a in plan.Updates) output.Info($"Web resource '{a.Name}' — would update");
        foreach (var a in plan.AddsToSolution) output.Info($"Web resource '{a.Name}' — would add to solution");
        foreach (var a in plan.Deletes) output.Info($"Web resource '{a.Name}' — would delete");
        foreach (var a in plan.RemovesFromSolution) output.Info($"Web resource '{a.Name}' — would remove from solution");
        foreach (var a in plan.Skips) output.Info($"Web resource '{a.Name}' — kept ({a.Reason})");

        var publishCount = publishAfterSync ? plan.PublishCount : 0;
        if (publishCount > 0)
            output.Info($"{publishCount} web resource(s) — would publish");

        output.Success($"Dry run: {plan.Deletes.Count} delete(s), {plan.RemovesFromSolution.Count} remove(s), {plan.Creates.Count} create(s), {plan.Updates.Count} update(s), {plan.AddsToSolution.Count} add(s), {plan.Skips.Count} skip(s). Run without --dry-run to apply.");
    }
}
