using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

// U7: wires FormEventReader → FormEventPlanner → FormEventExecutor behind two entry points, mirroring
// WebResourceService's orchestration shape (status spinner load → pure plan → execute).
//
// KTD12: a single pass can't serve both directions of the dependency-fault problem — a web resource about
// to be deleted must have its form references cleared FIRST (cleanup), while a form referencing a
// brand-new web resource must be registered AFTER that resource exists (registration). So the pipeline runs
// twice per push: CleanupOrphanedAsync before WebResourceService.SyncSolutionAsync, RegisterAsync after.
// Each call re-reads the snapshot rather than sharing state between the two — Phase 3 (registration) must
// see formxml as of after Phase 1's (cleanup's) own write landed, not a stale pre-cleanup snapshot (R11).
public class FormEventService(IAnsiConsole console)
{
    readonly FormEventReader _reader = new(console);
    readonly FormEventPlanner _planner = new(console);
    readonly FormEventExecutor _executor = new(console);

    // R14: cleanup pass — removes stale/orphaned handlers before web resources are created/updated/deleted,
    // so a pending web-resource delete never trips Dataverse's "referenced by N other components" fault.
    // cleanupOnly narrows the plan's writes to already-safe removals only (see FormEventExecutor.BuildFormXml)
    // — it never adds a handler/library reference that isn't already on the form.
    public Task<bool> CleanupOrphanedAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        bool force,
        bool dryRun,
        CancellationToken cancellationToken = default) =>
        SyncAsync(service, webresourceRoot, solutionName, force, dryRun, cleanupOnly: true, cancellationToken);

    // R10a: registration pass — runs strictly after web resources are pushed, so new/updated handlers can
    // only ever reference libraries that already exist in Dataverse.
    public Task<bool> RegisterAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        bool force,
        bool dryRun,
        CancellationToken cancellationToken = default) =>
        SyncAsync(service, webresourceRoot, solutionName, force, dryRun, cleanupOnly: false, cancellationToken);

    async Task<bool> SyncAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        bool force,
        bool dryRun,
        bool cleanupOnly,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webresourceRoot))
            throw new ArgumentException("webresourceRoot is required.", nameof(webresourceRoot));
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required.", nameof(solutionName));

        // Phase 1: Load snapshot (local annotations + current Dataverse form state)
        var snapshot = await console.Status().FlowlineSpinner().StartAsync("Lookup form event annotations...", _ =>
            _reader.LoadSnapshotAsync(service, webresourceRoot, solutionName, cancellationToken)).ConfigureAwait(false);

        // Phase 2: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot);

        if (plan.Forms.Count == 0)
        {
            // Both phases share this exact check; only the registration pass reports it, so a fully clean
            // push doesn't print the same "up to date" line twice (once per phase).
            if (!cleanupOnly)
                console.Skip("Form event handlers already up to date — skipping");
            return false;
        }

        // R18b: FormEventExecutor's dry-run preview never consults cleanupOnly — it prints and returns
        // before that point — so the plan (Reader+Planner are identical for both phases) would render the
        // exact same preview block twice. Only the registration pass (the fuller pass, run second by
        // PushCommand) surfaces it; the cleanup pass just signals "has pending changes" silently.
        if (dryRun && cleanupOnly)
            return true;

        // Phase 3: Execute the plan.
        await _executor.ExecuteAsync(service, snapshot, plan, force, dryRun, cleanupOnly, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
