using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class WebResourceService(IAnsiConsole console)
{
    readonly WebResourceReader _reader = new(console);
    readonly WebResourcePlanner _planner = new(console);
    readonly WebResourceExecutor _executor = new(console);

    public async Task<bool> SyncSolutionAsync(
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
        var snapshot = await console.Status().FlowlineSpinner().StartAsync("Lookup web resources...", _ =>
            _reader.LoadSnapshotAsync(service, webresourceRoot, solutionName, cancellationToken)).ConfigureAwait(false);
        WriteSnapshotVerbose(snapshot);
        console.Ok($"Web resources found ({snapshot.DataverseResources.Count} Dataverse, {snapshot.LocalResources.Count} local)");

        // Phase 2: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot);
        WritePlanReport(plan, PlanReportMode.Verbose, publishAfterSync);
        console.Ok(plan.TotalChanges > 0
            ? $"Web resource plan ready: {plan.Creates.Count} creates, {plan.Updates.Count} updates, {plan.Deletes.Count} deletes"
            : "Web resource plan ready: no changes");

        if (plan.TotalChanges == 0)
        {
            foreach (var a in plan.Skips)
                console.Skip($"Web resource '{a.Name}' kept ({a.Reason})");

            console.Skip("Web resources already up to date — skipping");
            return false;
        }

        // Dry-run: print preview and return without making any changes
        if (runMode == RunMode.DryRun)
        {
            WritePlanReport(plan, PlanReportMode.DryRun, publishAfterSync);
            return true;
        }

        // Phase 3: Execute the plan
        await _executor.ExecuteAsync(service, plan, publishAfterSync, runMode == RunMode.NoDelete, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task DownloadWebResourcesAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await console.Status().FlowlineSpinner().StartAsync(
            $"Loading web resources for {solutionName}...",
            _ => _reader.LoadSnapshotAsync(service, webresourceRoot, solutionName, cancellationToken))
            .ConfigureAwait(false);

        if (snapshot.DataverseResources.Count == 0)
        {
            console.Skip("No web resources in solution — skipping");
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
            console.Verbose(name);
            count++;
        }

        console.Ok($"[bold]{count}[/] web resource(s) downloaded");
    }

    void WriteSnapshotVerbose(WebResourceSyncSnapshot snapshot)
    {
        console.Verbose(BuildResourceTree($"Dataverse ({snapshot.DataverseResources.Count})", snapshot.DataverseResources.Keys));
        console.Verbose(BuildResourceTree($"Local ({snapshot.LocalResources.Count})", snapshot.LocalResources.Keys));
    }

    static Tree BuildResourceTree(string label, IEnumerable<string> names)
    {
        var tree = new Tree($"[dim]{label}[/]") { Style = Style.Parse("dim") };

        foreach (var group in names
            .Select(n => n.Split('/'))
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folderNode = tree.AddNode($"[dim]{Markup.Escape(group.Key)}[/]");
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
            var node = parent.AddNode($"[dim]{Markup.Escape(folder.Key)}[/]");
            AddTreeChildren(node, folder.Select(p => p[1..]).ToList());
        }

        foreach (var file in paths
            .Where(p => p.Length == 1)
            .Select(p => p[0])
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            parent.AddNode($"[dim]{Markup.Escape(file)}[/]");
        }
    }

    enum PlanReportMode { Verbose, DryRun }

    void WritePlanReport(WebResourceSyncPlan plan, PlanReportMode mode, bool publishAfterSync = false)
    {
        Action<string> line = mode == PlanReportMode.Verbose
            ? msg => console.Verbose(msg)
            : console.Info;

        var publishCount = publishAfterSync ? plan.PublishCount : 0;
        var counts = JoinCounts(
            (plan.Deletes.Count, "delete(s)"), (plan.RemovesFromSolution.Count, "remove(s)"),
            (plan.Creates.Count, "create(s)"), (plan.Updates.Count, "update(s)"),
            (plan.AddsToSolution.Count, "add(s)"), (plan.Skips.Count, "skip(s)"),
            (publishCount, "publish(es)"));

        line($"  Summary: {counts}");

        WriteSection(line, "Creates", plan.Creates);
        WriteSection(line, "Updates", plan.Updates, withReason: true);
        WriteSection(line, "Add to solution", plan.AddsToSolution);
        WriteSection(line, "Deletes", plan.Deletes);
        WriteSection(line, "Remove from solution", plan.RemovesFromSolution);
        WriteSection(line, "Skips", plan.Skips, withReason: true);

        if (publishCount > 0)
            line($"  Publish ({publishCount})");

        if (mode != PlanReportMode.DryRun)
            return;

        console.Ok($"Dry run: {counts}. Run without --dry-run to apply.");
    }

    static string JoinCounts(params (int Count, string Label)[] parts)
    {
        var nonZero = parts.Where(p => p.Count > 0).Select(p => $"{p.Count} {p.Label}").ToList();
        return nonZero.Count > 0 ? string.Join(", ", nonZero) : "no changes";
    }

    static void WriteSection(Action<string> line, string label, List<WebResourcePlanAction> actions, bool withReason = false)
    {
        if (actions.Count == 0)
            return;

        line($"  {label} ({actions.Count})");
        foreach (var a in actions.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            line(withReason ? $"    - {a.Name} ({a.Reason})" : $"    - {a.Name}");
    }
}
