using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

// U7: wires FormEventReader → FormEventPlanner → FormEventExecutor behind one entry point, mirroring
// WebResourceService's orchestration shape (status spinner load → pure plan → execute).
public class FormEventService(IAnsiConsole console)
{
    readonly FormEventReader _reader = new(console);
    readonly FormEventPlanner _planner = new(console);
    readonly FormEventExecutor _executor = new(console);

    public async Task<bool> SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        bool force,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
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
            console.Skip("Form event handlers already up to date — skipping");
            return false;
        }

        // Dry-run: web resources referenced by a new Library entry may not actually exist yet in this mode
        // (R10a), and the whole-push --dry-run contract must not write real form registrations either — so
        // report the plan's shape and return without calling the executor.
        if (runMode == RunMode.DryRun)
        {
            console.Ok($"Dry run: {plan.DistinctFormCount} form(s) with pending handler/library changes. Run without --dry-run to apply.");
            return true;
        }

        // Phase 3: Execute the plan
        // dryRun/cleanupOnly wiring from RunMode/KTD12 phasing is U7's job — false/false here preserves
        // today's behavior (the RunMode.DryRun branch above already short-circuits before reaching this call).
        await _executor.ExecuteAsync(service, snapshot, plan, force, dryRun: false, cleanupOnly: false, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
