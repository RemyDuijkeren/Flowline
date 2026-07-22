using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.FormEvents;

// Wires FormEventReader → FormEventPlanner → FormEventExecutor behind two entry points, mirroring
// WebResourceService's orchestration shape (status spinner load → pure plan → execute).
//
// A single pass can't serve both directions of the dependency-fault problem — a web resource about to
// be deleted must have its form references cleared FIRST (cleanup), while a form referencing a
// brand-new web resource must be registered AFTER that resource exists (registration). So the pipeline
// runs twice per push: CleanupOrphanedAsync before WebResourceService.SyncSolutionAsync, RegisterAsync
// after. Each call re-reads the snapshot rather than sharing state — registration must see formxml as
// of after cleanup's own write landed, not a stale pre-cleanup snapshot.
public class FormEventService(IAnsiConsole console)
{
    readonly FormEventReader _reader = new(console);
    readonly FormEventPlanner _planner = new(console);
    readonly FormEventExecutor _executor = new(console);

    // Cleanup pass — removes stale/orphaned handlers before web resources are created/updated/deleted, so
    // a pending web-resource delete never trips Dataverse's "referenced by N other components" fault.
    // cleanupOnly narrows the plan's writes to already-safe removals only (see FormEventExecutor.BuildFormXml)
    // — it never adds a handler/library reference that isn't already on the form.
    public Task<bool> CleanupOrphanedAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        bool force,
        bool dryRun,
        bool publishAfterSync = true,
        string? formEventCachePath = null,
        CancellationToken cancellationToken = default) =>
        SyncAsync(service, webresourceRoot, solutionName, force, dryRun, cleanupOnly: true, publishAfterSync, formEventCachePath, cancellationToken);

    // Registration pass — runs strictly after web resources are pushed, so new/updated handlers can only
    // ever reference libraries that already exist in Dataverse.
    public Task<bool> RegisterAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        bool force,
        bool dryRun,
        bool publishAfterSync = true,
        string? formEventCachePath = null,
        CancellationToken cancellationToken = default) =>
        SyncAsync(service, webresourceRoot, solutionName, force, dryRun, cleanupOnly: false, publishAfterSync, formEventCachePath, cancellationToken);

    async Task<bool> SyncAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        bool force,
        bool dryRun,
        bool cleanupOnly,
        bool publishAfterSync,
        string? formEventCachePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webresourceRoot))
            throw new ArgumentException("webresourceRoot is required.", nameof(webresourceRoot));
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required.", nameof(solutionName));

        // Phase 1: Load snapshot (local annotations + current Dataverse form state). Cleanup and
        // registration both re-scan the same local JS files, so cleanup's pass stays silent — only the
        // registration (second, fuller) pass surfaces reader/planner-level warnings. Cleanup can write a
        // new formxml to Dataverse before registration runs (see class doc comment), so each pass re-reads
        // rather than sharing one snapshot — the label below distinguishes the two reads (mirrors
        // FormEventExecutor's cleanupOnly ? "Cleaning forms" : "Updating forms") so they don't read as an
        // accidental duplicate.
        var spinnerLabel = cleanupOnly ? "Checking form events..." : "Registering form events...";
        var snapshot = await console.Status().FlowlineSpinner().StartAsync(spinnerLabel, _ =>
            _reader.LoadSnapshotAsync(service, webresourceRoot, solutionName, formEventCachePath, suppressWarnings: cleanupOnly, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Phase 2: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot, suppressWarnings: cleanupOnly);

        if (plan.Forms.Count == 0)
        {
            // Both phases share this exact check; only the registration pass reports it, so a fully clean
            // push doesn't print the same "up-to-date" line twice (once per phase).
            if (!cleanupOnly)
                console.Skip("Form event handlers already up to date — skipping");
            return false;
        }

        // FormEventExecutor's dry-run preview never consults cleanupOnly — it prints and returns before
        // that point — so the plan (identical for both phases) would render the exact same preview twice.
        // Only the registration pass surfaces it; the cleanup pass just signals "has pending changes"
        // silently.
        if (dryRun && cleanupOnly)
            return true;

        // Phase 3: Execute the plan.
        await _executor.ExecuteAsync(service, snapshot, plan, force, dryRun, cleanupOnly, publishAfterSync, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
