using System.Security.Cryptography;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;

namespace Flowline.Core.Plugins;

public class PluginService(IAnsiConsole console)
{
    const string FlowlineMarker = "[flowline]";

    readonly PluginReader _reader = new();
    readonly PluginPlanner _planner = new(console);
    readonly PluginExecutor _executor = new(console);
    readonly SolutionReader _solutionReader = new();
    readonly PluginAssemblyReader _assemblyReader = new(console);

    // Analyze() is the single choke point for every Validate* throw in PluginTypeMetadataScanner —
    // all plain InvalidOperationException by convention. Rewrapped here so a bad [CustomApi]/[Step]
    // attribute on the pushed assembly renders as a clean Error: line instead of a raw stack trace.
    PluginAssemblyMetadata AnalyzeAssembly(string dllPath)
    {
        try
        {
            return console.Status().FlowlineSpinner().Start("Analyzing plugin assembly...", _ => _assemblyReader.Analyze(dllPath));
        }
        catch (InvalidOperationException ex)
        {
            throw new FlowlineException(ExitCode.ValidationFailed, ex.Message, ex);
        }
    }

    public async Task<bool> SyncAssemblyOnlyAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            throw new ArgumentException("dllPath is required and cannot be empty.", nameof(dllPath));

        var metadata = AnalyzeAssembly(dllPath);
        return await SyncAssemblyOnlyAsync(service, metadata, solutionName, runMode, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> SyncAssemblyOnlyAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        await console.Status().FlowlineSpinner()
                    .StartAsync($"Looking up solution [bold]{solutionName}[/]...",
                        _ => _solutionReader.GetSupportedSolutionInfoAsync(service, solutionName, cancellationToken))
                    .ConfigureAwait(false);
        console.Info("Solution found and supported");

        var query = new QueryExpression("pluginassembly")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("pluginassemblyid", "name", "version", "publickeytoken", "culture", "description", "packageid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, metadata.Name) } }
        };
        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var existing = result.Entities.FirstOrDefault();

        if (existing == null)
            throw new InvalidOperationException($"Assembly '{metadata.Name}' not found in Dataverse — run push without --scope assemblyonly to register it first.");

        if (existing.GetAttributeValue<EntityReference>("packageid") != null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Assembly '{metadata.Name}' is already registered in Dataverse as part of a plugin " +
                "package — push the .nupkg package instead of the raw assembly. Automated migration is not supported.");

        var identityChanges = DetectIdentityChanges(existing, metadata);
        if (identityChanges != null)
            throw new InvalidOperationException($"Assembly '{metadata.Name}' identity changed ({string.Join(", ", identityChanges)}) — cannot update assembly-only. Run push without --scope assemblyonly to delete and recreate registrations.");

        var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
        if (storedHash == metadata.Hash)
        {
            console.Skip("Assembly already up to date — skipping");
            return false;
        }

        if (runMode == RunMode.DryRun)
        {
            console.Info($"  [yellow]~[/] Assembly [bold]{metadata.Name}[/] ({metadata.Version}) — would update content");
            console.Ok("Dry run: 1 update. Run without --dry-run to apply.");
            return true;
        }

        await RunWithProgressAsync("Updating plugin assembly", 1, async task =>
        {
            await UpdateAssemblyContentAsync(service, existing, metadata, cancellationToken).ConfigureAwait(false);
            task.Increment(1);
        }).ConfigureAwait(false);
        console.Ok($"Assembly [bold]{metadata.Name}[/] ({metadata.Version}) updated");
        return true;
    }

    /// <param name="pushedAssemblyNames">
    /// Every assembly name this <c>flowline push</c> owns, across all its plugin projects — nothing in
    /// this set counts as an orphan. <c>null</c> means "only the assembly being pushed here", the
    /// single-project shape. See <see cref="ExcludePushedAssemblies"/> for why this can't be one name.
    /// </param>
    public async Task<bool> SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        bool forceDeleteOrphans = false,
        bool forceRecreateAssembly = false,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? pushedAssemblyNames = null)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            throw new ArgumentException("dllPath is required and cannot be empty.", nameof(dllPath));

        var metadata = AnalyzeAssembly(dllPath);
        return await SyncSolutionAsync(service, metadata, solutionName, runMode, forceDeleteOrphans, forceRecreateAssembly, cancellationToken, pushedAssemblyNames).ConfigureAwait(false);
    }

    internal async Task<bool> SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        bool forceDeleteOrphans = false,
        bool forceRecreateAssembly = false,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? pushedAssemblyNames = null)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        // Phase 0: Check if solution exists and is supported
        await console.Status().FlowlineSpinner()
                    .StartAsync($"Looking up solution [bold]{solutionName}[/]...",
                        _ => _solutionReader.GetSupportedSolutionInfoAsync(service, solutionName, cancellationToken))
                    .ConfigureAwait(false);
        console.Info("Solution found");

        // Phase 1: Get or register assembly
        var (assembly, needsUpdate, cascadeDeleteCount) = await console.Status().FlowlineSpinner()
            .StartAsync("Lookup or add assembly", _ => GetOrRegisterAssemblyAsync(service, metadata, solutionName, runMode, forceRecreateAssembly, cancellationToken))
            .ConfigureAwait(false);
        console.Info(needsUpdate
            ? $"Assembly [bold]{metadata.Name}[/] ({metadata.Version}) found but needs content update"
            : $"Assembly [bold]{metadata.Name}[/] ({metadata.Version}) found");

        var blockedAssemblyIds = await WarnOrphanAssembliesAsync(service, metadata.Name, pushedAssemblyNames, solutionName, forceDeleteOrphans, runMode, cancellationToken).ConfigureAwait(false);
        await WarnOrphanStepsAsync(service, metadata.Name, pushedAssemblyNames, blockedAssemblyIds, solutionName, forceDeleteOrphans, runMode, cancellationToken).ConfigureAwait(false);

        // Phase 2: Load snapshot (all Dataverse state in parallel)
        var snapshot = await console.Status().FlowlineSpinner()
            .StartAsync("Lookup plugin registrations...", _ => _reader.LoadSnapshotAsync(service, assembly.Id, metadata, solutionName, cancellationToken))
            .ConfigureAwait(false);
        WriteSnapshotVerbose(snapshot);
        console.Info("Plugin registrations found");

        // Phase 3: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot, metadata, assembly, solutionName, forceDeleteOrphans: forceDeleteOrphans);
        console.Info(plan.TotalChanges > 0
            ? $"Registration plan ready: {plan.TotalChanges} changes ({plan.TotalUpserts} upserts, {plan.TotalDeletes} deletes)"
            : "Registration plan ready: no changes required");

        foreach (var warning in plan.Warnings)
            console.Warning(warning);

        if (needsUpdate && snapshot.ComponentSolutionMembership.TryGetValue(assembly.Id, out var assemblyMembership))
        {
            var otherSolutions = assemblyMembership
                .Where(s => !string.Equals(s, solutionName, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (otherSolutions.Count > 0)
                console.Warning($"Updating assembly [bold]{metadata.Name}[/] ({metadata.Version}) which also exists in other solutions: {string.Join(", ", otherSolutions)}.");
        }

        WritePlanTree(metadata, needsUpdate, plan, runMode, cascadeDeleteCount);

        // Pre-flight: UQ1_PluginType constraint is on (friendlyname, solutionId) — friendlyname must
        // be unique org-wide. Check before executing so the failure is clear, not a raw SQL error.
        var friendlyNamesToCreate = plan.PluginTypes.Upserts
            .Where(u => u.IsCreate)
            .Select(u => u.Entity.GetAttributeValue<string>("friendlyname"))
            .OfType<string>()
            .ToArray();
        if (friendlyNamesToCreate.Length > 0)
            await CheckFriendlyNameCollisionsAsync(service, assembly.Id, friendlyNamesToCreate, cancellationToken).ConfigureAwait(false);

        if (runMode == RunMode.DryRun)
            return true;

        if (!needsUpdate && plan.TotalChanges == 0)
        {
            console.Skip("Plugins already up to date — skipping");
            return false;
        }

        // Phase 4: Execute the deletes first — must precede assembly update and upserts
        if (runMode == RunMode.NoDelete || plan.TotalDeletes == 0)
        {
            await _executor.ExecuteDeletesAsync(service, plan, solutionName, runMode == RunMode.NoDelete, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RunWithProgressAsync("Deleting stale plugin components", plan.TotalDeletes,
                task => _executor.ExecuteDeletesAsync(service, plan, solutionName, false, cancellationToken, task)).ConfigureAwait(false);
        }
        if (plan.TotalDeletes > 0) console.Ok($"{plan.TotalDeletes} stale component(s) deleted");

        // Phase 5: Update assembly content — must happen before new plugin types are registered
        if (needsUpdate)
        {
            await RunWithProgressAsync("Updating plugin assembly", 1, async task =>
            {
                await UpdateAssemblyContentAsync(service, assembly, metadata, cancellationToken).ConfigureAwait(false);
                task.Increment(1);
            }).ConfigureAwait(false);
            console.Ok($"Updated assembly content for [bold]{metadata.Name}[/]");
        }

        // Phase 6: Execute upserts and add to solution
        if (plan.TotalUpserts > 0)
        {
            await RunWithProgressAsync("Syncing plugin components", plan.TotalUpserts,
                task => _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken, task)).ConfigureAwait(false);
        }
        else
        {
            await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken).ConfigureAwait(false);
        }
        if (plan.TotalUpserts > 0) console.Ok($"{plan.TotalUpserts} component(s) synced");

        var addToSolutionCount = CountAddToSolutionComponents(plan);
        if (addToSolutionCount > 0)
        {
            await RunWithProgressAsync("Adding plugin components to solution", addToSolutionCount,
                task => _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken, task)).ConfigureAwait(false);
        }
        else
        {
            await _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // -- Plugin package (NuGet .nupkg) sync --
    //
    // Full orchestration (U6): reflect -> R3a zero-DLL rejection -> R9 detect-and-block -> R4 hash
    // compare -> if unchanged, sync each assembly's own steps directly with no package write at all
    // (SyncPackageStepsOnlyAsync) -> if changed, delete any to-be-removed plugin type's steps/custom
    // APIs *before* the package content update (KD4/KTD13, ExecuteDeletesAsync with PluginTypes.Deletes
    // left empty since Dataverse's package sync removes the now-empty type automatically) -> write
    // package content (create or update, R5/R5a) -> confirm the auto-created records per assembly with
    // a bounded retry (R6/KTD14) -> write the hash marker -> re-plan per assembly against the
    // post-update snapshot and run the remaining upserts/adds (R7, KD5, KTD15 — N independently-scoped
    // snapshots/plans, never merged). WarnOrphanAssembliesAsync/WarnOrphanStepsAsync run here too, at the
    // same place the classic path calls them. They used to be skipped because the orphan check compared
    // against a single assembly name, so a multi-assembly package read its own other assemblies as
    // orphans (R11/KTD16) — ExcludePushedAssemblies takes the whole pushed set now, and this path unions
    // the package's own reflected assembly names into it regardless of what the caller passed, so that
    // hazard is gone. Deferring to deploy's pipeline redirect instead is not an option for DEV: deploy
    // imports into a *target* environment and `deploy dev` is rejected outright, which left a nupkg-only
    // solution with no orphan cleanup route at all.

    // Instance (not const) so tests can shrink the budget instead of paying ~4 real seconds to drive the
    // self-registration fallback (U3) to full expiry. Production callers never touch these.
    internal int PackageAssemblyCheckMaxAttempts { get; set; } = 5;
    internal TimeSpan PackageAssemblyCheckDelay { get; set; } = TimeSpan.FromSeconds(1);

    public async Task<bool> SyncSolutionFromPackageAsync(
        IOrganizationServiceAsync2 service,
        string nupkgPath,
        string projectAssemblyName,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        bool forceDeleteOrphans = false,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? pushedAssemblyNames = null)
    {
        if (string.IsNullOrWhiteSpace(nupkgPath))
            throw new ArgumentException("nupkgPath is required and cannot be empty.", nameof(nupkgPath));

        var assemblies = console.Status().FlowlineSpinner().Start("Analyzing plugin package...", _ => _assemblyReader.AnalyzePackage(nupkgPath));
        var nupkgContent = await File.ReadAllBytesAsync(nupkgPath, cancellationToken).ConfigureAwait(false);
        return await SyncSolutionFromPackageAsync(service, assemblies, nupkgContent, nupkgPath, projectAssemblyName, solutionName, runMode, forceDeleteOrphans, cancellationToken, pushedAssemblyNames).ConfigureAwait(false);
    }

    // Public so callers that must reflect the package themselves before this call (e.g. standalone push
    // resolving the primary assembly name — R2a) can pass the already-reflected metadata through instead
    // of paying for a second AnalyzePackage pass over the same .nupkg.
    public async Task<bool> SyncSolutionFromPackageAsync(
        IOrganizationServiceAsync2 service,
        List<PluginAssemblyMetadata> assemblies,
        byte[] nupkgContent,
        string nupkgPath,
        string projectAssemblyName,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        bool forceDeleteOrphans = false,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? pushedAssemblyNames = null)
    {
        // R3a: zero-DLL rejection — first check, ahead of detect-and-block and change detection,
        // since neither has anything to operate against without at least one reflected assembly.
        if (assemblies.Count == 0)
            throw new InvalidOperationException(
                $"No DLL implementing IPlugin was found in lib/<tfm>/ of package '{nupkgPath}' — the plugin package cannot be deployed empty.");

        // KD5 framing note: a project normally packs to one plugin-bearing DLL — a second one only
        // shows up via a deliberate ProjectReference into another plugin project, so it's worth
        // flagging in case that reference was accidental.
        if (assemblies.Count > 1)
            console.Info($"Package contains {assemblies.Count} plugin-bearing assemblies: {string.Join(", ", assemblies.Select(a => $"[bold]{a.Name}[/]"))}");

        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        var primary = assemblies.FirstOrDefault(a => string.Equals(a.Name, projectAssemblyName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No reflected assembly in '{nupkgPath}' matches the project's own build output assembly name '{projectAssemblyName}'.");

        // R9: detect-and-block — reuses the classic-path lookup pattern (GetOrRegisterAssemblyAsync),
        // extended with packageid so an empty packageid means a genuinely classic (non-package) assembly.
        // When packageid IS populated, this same record is the package's primary assembly (KTD2) —
        // reused below for change detection instead of a second query. Checked across every assembly
        // in the package (KD5: a package can carry more than one plugin-bearing DLL), not just the
        // primary — a classically-registered secondary assembly hits the same Dataverse fault at the
        // content write below (WritePackageContentAsync) if left unchecked.
        var assemblyQuery = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("pluginassemblyid", "name", "version", "publickeytoken", "culture", "description", "packageid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.In, assemblies.Select(a => (object)a.Name).ToArray()) } }
        };
        var assemblyResult = await service.RetrieveMultipleAsync(assemblyQuery, cancellationToken).ConfigureAwait(false);

        var classicConflicts = assemblyResult.Entities
            .Where(e => e.GetAttributeValue<EntityReference>("packageid") == null)
            .Select(e => e.GetAttributeValue<string>("name"))
            .ToList();

        if (classicConflicts.Count > 0)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Assembl{(classicConflicts.Count == 1 ? "y" : "ies")} {string.Join(", ", classicConflicts.Select(n => $"'{n}'"))} " +
                "already registered in Dataverse as classic (non-package) — remove manually before pushing this project as a plugin package. " +
                "Automated migration is not supported.");

        var existingAssembly = assemblyResult.Entities.FirstOrDefault(e =>
            string.Equals(e.GetAttributeValue<string>("name"), projectAssemblyName, StringComparison.OrdinalIgnoreCase));

        // Phase 0: solution existence/support check + live publisher prefix (KTD11) — same resolution
        // the classic path already uses, just captured here instead of discarded.
        var solutionInfo = await console.Status().FlowlineSpinner()
            .StartAsync($"Looking up solution [bold]{solutionName}[/]...",
                _ => _solutionReader.GetSupportedSolutionInfoAsync(service, solutionName, cancellationToken))
            .ConfigureAwait(false);
        console.Info("Solution found");

        // Union in this package's own reflected assembly names rather than trusting the caller's set —
        // a caller that passes null (or only the primary name) would otherwise have the package's
        // secondary assemblies flagged as orphans of the very push registering them (R11/KTD16).
        var packageAwarePushedNames = assemblies.Select(a => a.Name)
                                                .Concat(pushedAssemblyNames ?? [])
                                                .Where(n => !string.IsNullOrWhiteSpace(n))
                                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                                .ToList();

        var blockedAssemblyIds = await WarnOrphanAssembliesAsync(service, projectAssemblyName, packageAwarePushedNames, solutionName, forceDeleteOrphans, runMode, cancellationToken).ConfigureAwait(false);
        await WarnOrphanStepsAsync(service, projectAssemblyName, packageAwarePushedNames, blockedAssemblyIds, solutionName, forceDeleteOrphans, runMode, cancellationToken).ConfigureAwait(false);

        var prefix = solutionInfo.PublisherPrefix;

        var packageUniqueName = $"{prefix}_{projectAssemblyName}";

        var packageQuery = new QueryExpression("pluginpackage")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("pluginpackageid", "name", "uniquename", "version"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, packageUniqueName) } }
        };
        var packageResult = await service.RetrieveMultipleAsync(packageQuery, cancellationToken).ConfigureAwait(false);
        var existingPackage = packageResult.Entities.FirstOrDefault();

        // R4: hash the whole local .nupkg file's bytes (not one DLL) — catches dependency-only changes
        // a per-DLL hash would miss. Compared against the marker on the primary assembly's description.
        var hash = Convert.ToHexString(SHA256.HashData(nupkgContent));
        var storedHash = existingAssembly != null ? ParseStoredHash(existingAssembly.GetAttributeValue<string>("description")) : null;

        if (existingPackage != null && storedHash == hash)
        {
            // R4/R11 (item 8): package content unchanged — no package write at all, but each assembly's
            // steps are still diffed and synced (or previewed under --dry-run) directly against its own
            // scoped snapshot (drift correction). Never touches WarnOrphanAssembliesAsync/WarnOrphanStepsAsync
            // (R11/KTD16).
            return await SyncPackageStepsOnlyAsync(service, assemblies, existingPackage.Id, solutionName, runMode, forceDeleteOrphans, cancellationToken).ConfigureAwait(false);
        }

        // R1/R3 (KTD1): one comparison of reflected-versus-registered assemblies, run before the
        // snapshot-and-plan step below so a failure to determine the assembly-set change surfaces before
        // package content is written. Absorbs the former FindDroppedPackageAssembliesAsync — same query,
        // now producing both the added and dropped sets instead of only the dropped one. R2: the added
        // set is previewed under --dry-run below.
        //
        // Dataverse rejects a content update that drops an assembly whose plugin types still carry step
        // registrations — documented behavior: "If your update removes any plug-in assemblies, or types
        // which are used in plug-in step registrations, the update will be rejected. You must manually
        // remove any step registrations..." KD4 below already clears this for a class removed from a
        // surviving assembly; an assembly that disappears from the package has no plan of its own, so
        // nothing cleared its steps and the whole update failed.
        var (addedAssemblies, droppedAssemblies) = await CompareAssemblySetAsync(service, existingPackage?.Id, assemblies, cancellationToken).ConfigureAwait(false);

        // Snapshot + plan per assembly against CURRENT (pre-update) state — this is the one plan shown
        // to the user via WritePlanTree, in both --dry-run and --verbose (real run), so the two never
        // diverge (the post-update re-plan further down is execution-only, since Dataverse's own package
        // sync mutates plugin type records mid-flight — KD4). An assembly not yet registered under this
        // package (a brand-new package, or a brand-new secondary assembly, KD5) has nothing to diff
        // against, so it falls back to a fresh dummy assembly id — the same trick
        // GetOrRegisterAssemblyAsync's brand-new-assembly dry-run branch already uses — so
        // LoadSnapshotAsync naturally comes back empty and the plan renders as a full "create" tree
        // instead of being skipped.
        IReadOnlyList<(PluginAssemblyMetadata Metadata, Entity? Assembly, RegistrationSnapshot? Snapshot)> preSnapshots;
        if (existingPackage != null)
            preSnapshots = await _reader.LoadPackageSnapshotsAsync(service, existingPackage.Id, assemblies, solutionName, cancellationToken).ConfigureAwait(false);
        else
            preSnapshots = assemblies.Select(m => (m, (Entity?)null, (RegistrationSnapshot?)null)).ToList();
        var preKnownPluginTypeIds = AllPluginTypeIds(preSnapshots);

        var prePlans = new List<(PluginAssemblyMetadata Metadata, RegistrationPlan Plan)>();
        foreach (var (metadata, assemblyEntity, snapshot) in preSnapshots)
        {
            if (assemblyEntity != null && snapshot != null)
            {
                prePlans.Add((metadata, _planner.Plan(snapshot, metadata, assemblyEntity, solutionName, preKnownPluginTypeIds, forceDeleteOrphans)));
                continue;
            }

            // A not-yet-registered assembly owns no plugin types at all, so its snapshot attributes
            // nothing — the Custom API sweep leaves every prefix-visible API alone by construction.
            var planAssembly = new Entity("pluginassembly") { Id = Guid.NewGuid() };
            var planSnapshot = await _reader.LoadSnapshotAsync(service, planAssembly.Id, metadata, solutionName, cancellationToken).ConfigureAwait(false);

            prePlans.Add((metadata, _planner.Plan(planSnapshot, metadata, planAssembly, solutionName, preKnownPluginTypeIds, forceDeleteOrphans)));
        }

        // One summary for the whole package, written after the package line below — the package is the
        // parent of every assembly here, so per-assembly summaries would each report a fraction of the
        // push and none of them would account for the package content write itself.
        var counts = new PlanCounts();
        foreach (var (metadata, plan) in prePlans)
            counts += WritePlanTree(metadata, needsUpdate: false, plan, runMode, writeSummary: false);

        if (runMode == RunMode.DryRun)
        {
            // R2: name each pending assembly-set change. Gated on an existing package — a brand-new
            // package has every reflected assembly as "added" (CompareAssemblySetAsync's own rule), but
            // that's just the create, already covered by the "would create" line below; there's nothing
            // incremental to call out.
            if (existingPackage != null)
                foreach (var added in addedAssemblies)
                    console.Info($"  [green]+[/] [bold]{Safe(added.Name)}.dll[/] — would add to the package");
            foreach (var dropped in droppedAssemblies)
                console.Info($"  [red]-[/] [bold]{Safe(dropped.GetAttributeValue<string>("name"))}.dll[/] — would drop from the package, clearing its registrations first");
            console.Info(existingPackage == null
                ? $"  [green]+[/] Package [bold]{packageUniqueName}[/] ({primary.Version}) — would create"
                : $"  [yellow]~[/] Package [bold]{packageUniqueName}[/] — would update content");
            counts += existingPackage == null ? new PlanCounts(0, 1, 0) : new PlanCounts(0, 0, 1);
            console.Ok($"Dry run: {counts}. Run without --dry-run to apply.");
            return true;
        }

        // KD4/KTD13: for an existing package, any assembly whose class was removed must have its
        // steps/custom APIs deleted *before* the content update — Dataverse rejects the update
        // otherwise. A brand-new package assembly has nothing existing of its own to delete, so its
        // plan naturally has zero PluginTypes.Deletes and this is a no-op for it.
        foreach (var (_, plan) in prePlans)
        {
            if (plan.PluginTypes.Deletes.Count == 0) continue; // no class was removed for this assembly
            await _executor.ExecuteDeletesAsync(service, plan.NonPluginTypeDeletes(), solutionName, runMode == RunMode.NoDelete, cancellationToken).ConfigureAwait(false);
        }

        // Same rule, for an assembly the .nupkg no longer carries at all. Only the blocking children go —
        // the plugin types and the assembly record itself are removed by the content update, the way KD4
        // already relies on for a removed class.
        foreach (var dropped in droppedAssemblies)
        {
            var droppedName = dropped.GetAttributeValue<string>("name");

            if (runMode == RunMode.NoDelete)
            {
                console.Warning($"[bold]{Safe(droppedName)}.dll[/] dropped from the package, but --no-delete is active — Dataverse will reject the update while its steps remain.");
                continue;
            }

            var stub = new PluginAssemblyMetadata("", "", [], "", "", null, "", []);
            var droppedSnapshot = await _reader.LoadSnapshotAsync(service, dropped.Id, stub, solutionName, cancellationToken).ConfigureAwait(false);
            var (droppedApis, droppedRequestParams, droppedResponseProps) = OwnCustomApiRecords(droppedSnapshot);

            console.Warning($"[bold]{Safe(droppedName)}.dll[/] no longer in the package — clearing its registrations so the update can remove it.");
            foreach (var api in droppedApis)
                console.Info($"  {Safe(api.GetAttributeValue<string>("uniquename"))} — cascade delete");
            foreach (var step in droppedSnapshot.Steps)
                console.Info($"  {Safe(step.GetAttributeValue<string>("name"))} — cascade delete");

            foreach (var e in droppedSnapshot.Images)
                await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
            foreach (var e in droppedResponseProps)
                await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
            foreach (var e in droppedRequestParams)
                await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
            foreach (var e in droppedSnapshot.Steps)
                await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
            foreach (var e in droppedApis)
                await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
        }

        var packageId = await WritePackageContentAsync(service, existingPackage, packageUniqueName, primary, nupkgContent, solutionName, cancellationToken).ConfigureAwait(false);

        // R6/KTD14: confirm the auto-created pluginassembly/plugintype records per DLL, bounded retry.
        // R4/R5/KTD2: when the wait expires, this self-registers whatever Dataverse still hasn't picked
        // up rather than throwing — see the method for why.
        var postSnapshots = await LoadPackageSnapshotsWithRetryAsync(service, packageId, packageUniqueName, primary, nupkgContent, assemblies, solutionName, cancellationToken).ConfigureAwait(false);

        var primaryPost = postSnapshots.FirstOrDefault(t => string.Equals(t.Metadata.Name, projectAssemblyName, StringComparison.OrdinalIgnoreCase));
        if (primaryPost.Assembly == null)
            throw new InvalidOperationException($"Primary assembly '{projectAssemblyName}' was not found under package '{packageUniqueName}' after the content update.");

        await WritePackageAssemblyMarkerAsync(service, primaryPost.Assembly, hash, cancellationToken).ConfigureAwait(false);

        // Re-plan per assembly against the post-update snapshot (types have changed for any assembly
        // with removed classes) and run the remaining deletes/upserts/adds. Execution-only — the tree
        // already shown above (from the pre-update snapshot) is what the user sees; this re-plan exists
        // only because Dataverse's package sync mutates plugin type records as a side effect of the
        // content update, which the pre-update snapshot can't have known about. The pre-update pass
        // above only ever deletes steps/custom-APIs for an assembly whose PLUGIN TYPE was removed (the
        // specific ordering KD4 requires before the content update) — a step or Custom API removed from
        // a plugin type that itself survives is never covered by that gate. Guard the post-update delete
        // on PluginTypes.Deletes being empty here so an assembly the pre-update pass already handled
        // isn't reprocessed a second time once Dataverse's own package sync has removed its emptied type.
        // PluginTypes.Deletes itself is never acted on (KD2/KD4/KTD13 — Dataverse handles that removal).
        var postKnownPluginTypeIds = AllPluginTypeIds(postSnapshots);
        foreach (var (metadata, assemblyEntity, snapshot) in postSnapshots)
        {
            if (assemblyEntity == null || snapshot == null)
                throw new InvalidOperationException($"Assembly '{metadata.Name}' was not found under package '{packageUniqueName}' after the content update.");

            var plan = _planner.Plan(snapshot, metadata, assemblyEntity, solutionName, postKnownPluginTypeIds, forceDeleteOrphans);
            if (plan.PluginTypes.Deletes.Count == 0)
                await _executor.ExecuteDeletesAsync(service, plan.NonPluginTypeDeletes(), solutionName, runMode == RunMode.NoDelete, cancellationToken).ConfigureAwait(false);
            await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken).ConfigureAwait(false);
            await _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // R4/R11 no-op path (item 8, U6): package content is unchanged, so nothing could have removed a
    // plugin type — Plan()'s obsolete-sweep is driven by local metadata, which is byte-identical to the
    // last push. Still diffs and syncs each assembly's own steps directly (drift correction), without
    // ever calling WarnOrphanAssembliesAsync/WarnOrphanStepsAsync (R11/KTD16).
    async Task<bool> SyncPackageStepsOnlyAsync(
        IOrganizationServiceAsync2 service,
        List<PluginAssemblyMetadata> assemblies,
        Guid packageId,
        string solutionName,
        RunMode runMode,
        bool forceDeleteOrphans,
        CancellationToken cancellationToken)
    {
        var snapshots = await _reader.LoadPackageSnapshotsAsync(service, packageId, assemblies, solutionName, cancellationToken).ConfigureAwait(false);
        var knownPluginTypeIds = AllPluginTypeIds(snapshots);

        var plans = new List<(PluginAssemblyMetadata Metadata, RegistrationPlan Plan)>();
        foreach (var (metadata, assemblyEntity, snapshot) in snapshots)
        {
            if (assemblyEntity == null || snapshot == null) continue;

            var plan = _planner.Plan(snapshot, metadata, assemblyEntity, solutionName, knownPluginTypeIds, forceDeleteOrphans);
            if (plan.TotalChanges == 0) continue;

            plans.Add((metadata, plan));
        }

        if (plans.Count == 0)
        {
            console.Skip("Plugin package already up to date — skipping");
            return false;
        }

        // Same plan drives both the --dry-run preview and the --verbose display below (WritePlanTree
        // branches on runMode internally) and the real execution — no package content write happens on
        // this path, so unlike the changed-package flow above there's no pre/post snapshot split needed.
        // No package write here, so the total is exactly the per-assembly plans — still one summary
        // for the push, not one per assembly.
        var counts = new PlanCounts();
        foreach (var (metadata, plan) in plans)
            counts += WritePlanTree(metadata, needsUpdate: false, plan, runMode, writeSummary: false);

        if (runMode == RunMode.DryRun)
        {
            console.Ok($"Dry run: {counts}. Run without --dry-run to apply.");
            return true;
        }

        foreach (var (_, plan) in plans)
        {
            await _executor.ExecuteDeletesAsync(service, plan, solutionName, runMode == RunMode.NoDelete, cancellationToken).ConfigureAwait(false);
            await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken).ConfigureAwait(false);
            await _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken).ConfigureAwait(false);
        }

        console.Ok("Plugin package content unchanged — synced drifted step registration(s)");
        return true;
    }

    // R6/KTD14: a handful of short, bounded 1-second polls — defense-in-depth for the untested case of
    // larger packages/slower environments, not a hedge against real observed latency (verified
    // synchronous in practice). KTD2: measurement showed the retry is a check, not a latency
    // accommodation, so an expiry means Dataverse isn't going to auto-register the assembly at all —
    // wherever it happens, not only on the add-to-an-existing-package path. R5: instead of throwing,
    // register whatever is still missing directly, then run KTD6's write-again-and-reload so the newly
    // registered assembly reaches the caller with a real snapshot instead of the null pair that used to
    // trip the caller's post-update guard.
    async Task<IReadOnlyList<(PluginAssemblyMetadata Metadata, Entity? Assembly, RegistrationSnapshot? Snapshot)>> LoadPackageSnapshotsWithRetryAsync(
        IOrganizationServiceAsync2 service,
        Guid packageId,
        string packageUniqueName,
        PluginAssemblyMetadata primary,
        byte[] nupkgContent,
        List<PluginAssemblyMetadata> assemblies,
        string solutionName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= PackageAssemblyCheckMaxAttempts; attempt++)
        {
            var snapshots = await _reader.LoadPackageSnapshotsAsync(service, packageId, assemblies, solutionName, cancellationToken).ConfigureAwait(false);
            if (snapshots.All(s => s.Assembly != null))
                return snapshots;

            if (attempt < PackageAssemblyCheckMaxAttempts)
            {
                await Task.Delay(PackageAssemblyCheckDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var missing in snapshots.Where(s => s.Assembly == null))
                await RegisterPackageAssemblyDirectlyAsync(service, packageId, packageUniqueName, missing.Metadata, solutionName, cancellationToken).ConfigureAwait(false);

            // KTD6: a freshly registered assembly owns no plugin types yet — only the content write
            // populates them, and it has to run after the create. The order is load-bearing: two content
            // writes with no create between them were observed to register nothing, both times.
            await WritePackageContentAsync(service, new Entity("pluginpackage", packageId), packageUniqueName, primary, nupkgContent, solutionName, cancellationToken).ConfigureAwait(false);
            return await _reader.LoadPackageSnapshotsAsync(service, packageId, assemblies, solutionName, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Unreachable.");
    }

    // KTD3/KTD4: smallest field set the create accepts, arrived at against a live environment by starting
    // from the minimum and adding only what Dataverse rejected the create without. It uses the same direct
    // request-plus-solution-name pattern GetOrRegisterAssemblyAsync already uses below — no wrapper helper
    // exists in this file, and one call site doesn't justify inventing one.
    //
    // Two rejections shaped this set. Without isolationmode: "'<assembly>' is not allowed to be registered
    // in full-trust mode, assembly must be registered in isolation." Then, with only name/package/isolation:
    // "Unable to load plug-in assembly." A package-owned row carries no content of its own — the bytes live
    // in the package — so Dataverse resolves which DLL the row refers to from the assembly's full identity.
    // Name alone doesn't identify it; version, culture and public key token do. That is why this sets
    // identity the classic path deliberately leaves unset: there, Dataverse reads identity out of the
    // uploaded content field, and here there is no such field to read.
    //
    // R6: no --force specifier gates this — the push has no other way to succeed, and creating a record is
    // additive.
    async Task RegisterPackageAssemblyDirectlyAsync(
        IOrganizationServiceAsync2 service,
        Guid packageId,
        string packageUniqueName,
        PluginAssemblyMetadata metadata,
        string solutionName,
        CancellationToken cancellationToken)
    {
        var entity = new Entity("pluginassembly")
        {
            ["name"]           = metadata.Name,
            ["packageid"]      = new EntityReference("pluginpackage", packageId),
            ["isolationmode"]  = new OptionSetValue(2), // 2 = Sandbox (cloud only)
            ["version"]        = metadata.Version,
            ["culture"]        = metadata.Culture,
            ["publickeytoken"] = metadata.PublicKeyToken
        };

        try
        {
            await service.ExecuteAsync(
                new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // R8: never phrase this as a timeout — the wait already ran and expired; this is a distinct,
            // harder failure where the package content has already committed a DLL nothing points at.
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Assembly '{metadata.Name}' could not be registered under package '{packageUniqueName}'. " +
                "Package content now contains a DLL with no registration — remove it from the project or " +
                $"register '{metadata.Name}' manually. {ex.Message}", ex);
        }

        console.Ok($"Assembly [bold]{Safe(metadata.Name)}[/] registered directly under package [bold]{Safe(packageUniqueName)}[/] — Dataverse didn't auto-register it.");
    }

    // Extracted from the create/update branches so the U6 orchestrator can call it at the specific
    // point KD4 requires (after any pre-update deletes) without duplicating the Dataverse create/update
    // calls themselves.
    async Task<Guid> WritePackageContentAsync(
        IOrganizationServiceAsync2 service,
        Entity? existingPackage,
        string packageUniqueName,
        PluginAssemblyMetadata primary,
        byte[] nupkgContent,
        string solutionName,
        CancellationToken cancellationToken)
    {
        if (existingPackage == null)
        {
            // R5: name and uniquename both carry the publisher prefix (Dataverse validates name against
            // it at create time); version comes from the nupkg's own nuspec version (KTD4 — create-time only).
            var entity = new Entity("pluginpackage")
            {
                ["name"] = packageUniqueName,
                ["uniquename"] = packageUniqueName,
                ["version"] = primary.Version,
                ["content"] = Convert.ToBase64String(nupkgContent)
            };

            var response = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName }, cancellationToken).ConfigureAwait(false);

            console.Ok($"Package [bold]{packageUniqueName}[/] ({primary.Version}) added");
            return response.id;
        }

        // R5a/KTD4: only content is mutable in place — version is create-time-only and Dataverse
        // rejects an Update that changes it.
        var updateEntity = new Entity("pluginpackage", existingPackage.Id)
        {
            ["content"] = Convert.ToBase64String(nupkgContent)
        };

        await service.UpdateAsync(updateEntity, cancellationToken).ConfigureAwait(false);
        console.Ok($"Package [bold]{packageUniqueName}[/] updated");
        return existingPackage.Id;
    }

    // Marker write (part of R6) — standalone for now. U6's orchestration times this call to run once
    // the primary assembly is confirmed present (U4/U5's multi-assembly snapshot loading); this method
    // just performs the write once told to. KTD3: version must be included in the same Update call as
    // description, re-read unchanged from the passed entity, or Dataverse throws an internal
    // NullReferenceException. No content — a package-owned assembly's own content is always empty (KTD2).
    internal async Task WritePackageAssemblyMarkerAsync(
        IOrganizationServiceAsync2 service,
        Entity primaryAssembly,
        string nupkgHash,
        CancellationToken cancellationToken = default)
    {
        var entity = new Entity("pluginassembly", primaryAssembly.Id)
        {
            ["version"] = primaryAssembly.GetAttributeValue<string>("version"),
            ["description"] = $"{FlowlineMarker} sha256={nupkgHash}"
        };

        await service.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The assembly names in this push other than the one being synced right now.</summary>
    internal static IReadOnlyList<string> SiblingAssemblyNames(string managedAssemblyName, IReadOnlyCollection<string>? pushedAssemblyNames) =>
        SiblingAssemblyNames([managedAssemblyName], pushedAssemblyNames);

    /// <summary>
    /// The assembly names in this push other than the ones synced by the pass currently running.
    /// </summary>
    /// <remarks>
    /// The classic path syncs exactly one assembly per pass; a package syncs every assembly it contains
    /// in one pass, so "the ones being synced right now" is a set, not a name.
    /// </remarks>
    internal static IReadOnlyList<string> SiblingAssemblyNames(
        IEnumerable<string> managedAssemblyNames,
        IReadOnlyCollection<string>? pushedAssemblyNames)
    {
        if (pushedAssemblyNames == null) return [];

        var managed = new HashSet<string>(managedAssemblyNames, StringComparer.OrdinalIgnoreCase);

        return pushedAssemblyNames.Where(n => !string.IsNullOrWhiteSpace(n) && !managed.Contains(n))
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                  .ToList();
    }

    /// <summary>Condition excluding every assembly this push owns from an orphan query.</summary>
    /// <remarks>
    /// A solution can hold more than one plugin project, and each is synced in its own pass. Excluding
    /// only the assembly of the pass currently running would classify every sibling project's assembly
    /// as "in environment — no local source", and under <c>--force delete-orphans</c> cascade-delete it —
    /// then the next pass would recreate it and delete the first one's. One name stays <c>NotEqual</c> so
    /// a single-project push issues exactly the query it always did (R7).
    /// </remarks>
    internal static ConditionExpression ExcludePushedAssemblies(
        string attributeName,
        string managedAssemblyName,
        IReadOnlyCollection<string>? pushedAssemblyNames)
    {
        var siblings = SiblingAssemblyNames(managedAssemblyName, pushedAssemblyNames);

        return siblings.Count == 0
            ? new ConditionExpression(attributeName, ConditionOperator.NotEqual, managedAssemblyName)
            : new ConditionExpression(attributeName, ConditionOperator.NotIn,
                siblings.Prepend(managedAssemblyName).Cast<object>().ToArray());
    }

    /// <summary>The Custom API records a snapshot's own plugin types implement.</summary>
    /// <remarks>
    /// A snapshot's <c>PluginTypes</c>/<c>Steps</c>/<c>Images</c> are scoped to one assembly, but its
    /// <c>CustomApis</c> (and their parameters and properties) are resolved publisher-prefix-wide —
    /// every API under the prefix, across every project and every repo sharing it. Only the ones naming
    /// one of this assembly's plugin types as their implementation are its children; the rest are other
    /// people's APIs and deleting them was a real bug.
    /// </remarks>
    static (List<Entity> Apis, List<Entity> RequestParams, List<Entity> ResponseProps) OwnCustomApiRecords(
        RegistrationSnapshot? snapshot)
    {
        if (snapshot == null) return ([], [], []);

        var typeIds = snapshot.PluginTypes.Values.Select(t => t.Id).ToHashSet();
        var apis = snapshot.CustomApis
            .Where(a => a.GetAttributeValue<EntityReference>("plugintypeid") is { } t && typeIds.Contains(t.Id))
            .ToList();

        var apiIds = apis.Select(a => a.Id).ToHashSet();
        bool Bound(Entity e) => e.GetAttributeValue<EntityReference>("customapiid") is { } a && apiIds.Contains(a.Id);

        return (apis, snapshot.RequestParams.Where(Bound).ToList(), snapshot.ResponseProps.Where(Bound).ToList());
    }

    /// <summary>
    /// The assemblies being added to and dropped from a package (KTD1), by comparing the assemblies
    /// reflected from the local .nupkg against those already registered under the package.
    /// </summary>
    /// <remarks>
    /// One query answers both questions: a reflected assembly with no <c>pluginassembly</c> record under
    /// the package is being added; a registered record whose name the .nupkg no longer carries is being
    /// dropped. <paramref name="existingPackageId"/> is <c>null</c> for a brand-new package, which has
    /// nothing registered yet — every reflected assembly is added and nothing is dropped, without
    /// querying for a package that doesn't exist.
    /// </remarks>
    internal static async Task<(List<PluginAssemblyMetadata> Added, List<Entity> Dropped)> CompareAssemblySetAsync(
        IOrganizationServiceAsync2 service,
        Guid? existingPackageId,
        IReadOnlyList<PluginAssemblyMetadata> reflectedAssemblies,
        CancellationToken cancellationToken)
    {
        if (existingPackageId == null)
            return (reflectedAssemblies.ToList(), []);

        var query = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("pluginassemblyid", "name"),
            Criteria = { Conditions = { new ConditionExpression("packageid", ConditionOperator.Equal, existingPackageId.Value) } }
        };
        var registered = await service.RetrieveAllAsync(query, cancellationToken).ConfigureAwait(false);

        var registeredNames = new HashSet<string>(
            registered.Select(a => a.GetAttributeValue<string>("name")).Where(n => !string.IsNullOrWhiteSpace(n))!,
            StringComparer.OrdinalIgnoreCase);
        var reflectedNames = new HashSet<string>(
            reflectedAssemblies.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);

        var added = reflectedAssemblies.Where(a => !registeredNames.Contains(a.Name)).ToList();
        var dropped = registered
            .Where(a => a.GetAttributeValue<string>("name") is { } n && !string.IsNullOrWhiteSpace(n) && !reflectedNames.Contains(n))
            .ToList();

        return (added, dropped);
    }

    /// <summary>An orphan's owning plugin package, and whether push may delete that package.</summary>
    /// <param name="FullyOrphaned">
    /// Every assembly the package owns is an orphan of this push. False means the package also owns
    /// something this run can't account for — an assembly in another solution, or one being pushed
    /// right now — so deleting the package would take a live assembly with it.
    /// </param>
    sealed record OrphanPackage(Guid Id, string UniqueName, bool FullyOrphaned);

    // Dataverse refuses DeleteAsync on any pluginassembly with a packageid ("Unable to delete plug-in
    // assembly as it is part of plugin package") — deleting its children first does not unlock it. The
    // only lever is deleting the pluginpackage, which cascades assembly + plugintype away. Re-uploading
    // a nupkg without the class does the same, but a leftover package (the shape after a plugin project
    // is renamed) has no local nupkg to re-upload.
    static async Task<Dictionary<Guid, OrphanPackage>> ResolveOrphanPackagesAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyCollection<Entity> orphans,
        CancellationToken cancellationToken)
    {
        var packageIds = orphans.Select(e => e.GetAttributeValue<EntityReference>("packageid"))
                                .Where(r => r != null)
                                .Select(r => r!.Id)
                                .Distinct()
                                .ToList();
        if (packageIds.Count == 0) return [];

        // Org-wide, deliberately unfiltered by solution: the orphan query only sees this solution's
        // members, and a package delete removes everything it owns anywhere.
        var ownedQuery = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("packageid"),
            Criteria = { Conditions = { new ConditionExpression("packageid", ConditionOperator.In, packageIds.Cast<object>().ToArray()) } }
        };
        var owned = await service.RetrieveAllAsync(ownedQuery, cancellationToken).ConfigureAwait(false);

        var nameQuery = new QueryExpression("pluginpackage")
        {
            ColumnSet = new ColumnSet("uniquename"),
            Criteria = { Conditions = { new ConditionExpression("pluginpackageid", ConditionOperator.In, packageIds.Cast<object>().ToArray()) } }
        };
        var names = (await service.RetrieveAllAsync(nameQuery, cancellationToken).ConfigureAwait(false))
            .ToDictionary(e => e.Id, e => e.GetAttributeValue<string>("uniquename"));

        var orphanIds = orphans.Select(e => e.Id).ToHashSet();

        return packageIds.ToDictionary(id => id, id =>
        {
            var members = owned.Where(e => e.GetAttributeValue<EntityReference>("packageid")?.Id == id).ToList();
            // An empty member list means the ownership lookup told us nothing — treat as not deletable
            // rather than letting All() on an empty set green-light a package delete.
            var fullyOrphaned = members.Count > 0 && members.TrueForAll(e => orphanIds.Contains(e.Id));
            return new OrphanPackage(id, names.GetValueOrDefault(id) ?? id.ToString(), fullyOrphaned);
        });
    }

    /// <returns>
    /// The orphan assemblies this pass refused to touch because their package owns more than orphans.
    /// <see cref="WarnOrphanStepsAsync"/> must exclude them: it keys off "assembly not in this push",
    /// which a refused orphan is by definition, and would otherwise delete the very children this pass
    /// declined to destroy.
    /// </returns>
    async Task<IReadOnlyCollection<Guid>> WarnOrphanAssembliesAsync(
        IOrganizationServiceAsync2 service,
        string managedAssemblyName,
        IReadOnlyCollection<string>? pushedAssemblyNames,
        string solutionName,
        bool forceDeleteOrphans,
        RunMode runMode,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("pluginassemblyid", "name", "packageid"),
            Criteria = { Conditions = { ExcludePushedAssemblies("name", managedAssemblyName, pushedAssemblyNames) } }
        };
        var componentLink = query.AddLink("solutioncomponent", "pluginassemblyid", "objectid", JoinOperator.Inner);
        componentLink.LinkCriteria.AddCondition("componenttype", ConditionOperator.Equal, 91); // 91 = PluginAssembly
        var solutionLink = componentLink.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        if (result.Entities.Count == 0) return [];

        var packages = await ResolveOrphanPackagesAsync(service, result.Entities, cancellationToken).ConfigureAwait(false);
        var packagesToDelete = new HashSet<Guid>();
        var blockedAssemblyIds = new List<Guid>();

        foreach (var entity in result.Entities)
        {
            var name = entity.GetAttributeValue<string>("name");
            var packageRef = entity.GetAttributeValue<EntityReference>("packageid");
            var package = packageRef != null ? packages.GetValueOrDefault(packageRef.Id) : null;

            // Nothing here can remove the assembly, so don't destroy its children on the way to a delete
            // that will be refused — warn and move on.
            if (package is { FullyOrphaned: false })
            {
                // Deliberately not "assemblies this solution doesn't have" — the commonest shape is a
                // package whose other assembly this very push just registered, which the solution
                // certainly does have. What makes it undeletable is that it isn't an orphan.
                console.Warning($"[bold]{Safe(name)}.dll[/] in environment — no local source. Package [bold]{Safe(package.UniqueName)}[/] owns assemblies that aren't orphans — not deleting it.");
                blockedAssemblyIds.Add(entity.Id);
                continue;
            }

            var willDelete = forceDeleteOrphans && runMode == RunMode.Normal;
            var showCascade = forceDeleteOrphans || runMode == RunMode.DryRun;

            console.Warning((willDelete, package) switch
            {
                (true, null) => $"[bold]{Safe(name)}.dll[/] in environment — no local source. Deleting.",
                (true, _) => $"[bold]{Safe(name)}.dll[/] in environment — no local source. Deleting package [bold]{Safe(package.UniqueName)}[/].",
                (false, null) => $"[bold]{Safe(name)}.dll[/] in environment — no local source. Use --force delete-orphans to delete.",
                (false, _) => $"[bold]{Safe(name)}.dll[/] in environment — no local source. Use --force delete-orphans to delete package [bold]{Safe(package.UniqueName)}[/]."
            });

            // Load snapshot for cascade display and/or explicit child deletion
            RegistrationSnapshot? orphanSnapshot = null;
            if (showCascade || willDelete)
            {
                // Stub metadata — skips SDK message/filter/user lookups (not needed here)
                var stub = new PluginAssemblyMetadata("", "", [], "", "", null, "", []);
                orphanSnapshot = await _reader.LoadSnapshotAsync(service, entity.Id, stub, solutionName, cancellationToken).ConfigureAwait(false);
            }

            var (orphanCustomApis, orphanRequestParams, orphanResponseProps) = OwnCustomApiRecords(orphanSnapshot);

            if (showCascade && orphanSnapshot != null)
            {
                foreach (var api in orphanCustomApis)
                    console.Info(willDelete
                        ? $"  {Safe(api.GetAttributeValue<string>("uniquename"))} — cascade delete"
                        : $"  [red]-[/] {Safe(api.GetAttributeValue<string>("uniquename"))} — would delete (cascade)");
                foreach (var typeName in orphanSnapshot.PluginTypes.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    console.Info(willDelete
                        ? $"  {Safe(typeName)} — cascade delete"
                        : $"  [red]-[/] {Safe(typeName)} — would delete (cascade)");
                foreach (var step in orphanSnapshot.Steps)
                    console.Info(willDelete
                        ? $"  {Safe(step.GetAttributeValue<string>("name"))} — cascade delete"
                        : $"  [red]-[/] {Safe(step.GetAttributeValue<string>("name"))} — would delete (cascade)");
                foreach (var image in orphanSnapshot.Images)
                    console.Info(willDelete
                        ? $"  {Safe(image.GetAttributeValue<string>("name"))} — cascade delete"
                        : $"  [red]-[/] {Safe(image.GetAttributeValue<string>("name"))} — would delete (cascade)");
            }

            if (willDelete && orphanSnapshot != null)
            {
                // Dataverse blocks assembly DeleteAsync when its child plugin types are referenced by
                // steps or custom API entries (dependency check fires before cascade runs).
                // Must delete children manually in reverse dependency order — same as RunDeletesAsync.
                foreach (var e in orphanSnapshot.Images)
                    await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
                foreach (var e in orphanResponseProps)
                    await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
                foreach (var e in orphanRequestParams)
                    await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
                foreach (var e in orphanSnapshot.Steps)
                    await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
                foreach (var e in orphanCustomApis)
                    await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
                foreach (var (_, pluginType) in orphanSnapshot.PluginTypes)
                    await service.DeleteAsync(pluginType.LogicalName, pluginType.Id, cancellationToken).ConfigureAwait(false);

                // A package-owned assembly goes away with its package, and only once every orphan
                // sibling in that package has had its children cleared — so the package delete waits
                // until the loop is done.
                if (package != null) packagesToDelete.Add(package.Id);
                else await service.DeleteAsync("pluginassembly", entity.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var packageId in packagesToDelete)
            await service.DeleteAsync("pluginpackage", packageId, cancellationToken).ConfigureAwait(false);

        return blockedAssemblyIds;
    }

    // Catches steps left behind after a plugin project rename: the old assembly (and its plugin
    // type) can end up removed from the solution entirely while its steps stay explicit solution
    // members, which fails a fresh-environment import with a missing PluginType dependency —
    // WarnOrphanAssembliesAsync above only catches this when the foreign assembly is itself still
    // a solution member.
    async Task WarnOrphanStepsAsync(
        IOrganizationServiceAsync2 service,
        string managedAssemblyName,
        IReadOnlyCollection<string>? pushedAssemblyNames,
        IReadOnlyCollection<Guid> blockedAssemblyIds,
        string solutionName,
        bool forceDeleteOrphans,
        RunMode runMode,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            Criteria = { Conditions = { new ConditionExpression("componenttype", ConditionOperator.Equal, 92) } } // 92 = SdkMessageProcessingStep
        };
        var solutionLink = query.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);
        var stepLink = query.AddLink("sdkmessageprocessingstep", "objectid", "sdkmessageprocessingstepid", JoinOperator.Inner);
        stepLink.Columns = new ColumnSet("name");
        stepLink.EntityAlias = "step";
        var typeLink = stepLink.AddLink("plugintype", "plugintypeid", "plugintypeid", JoinOperator.Inner);
        var asmLink = typeLink.AddLink("pluginassembly", "pluginassemblyid", "pluginassemblyid", JoinOperator.Inner);
        asmLink.Columns = new ColumnSet("name");
        asmLink.EntityAlias = "asm";
        asmLink.LinkCriteria.AddCondition(ExcludePushedAssemblies("name", managedAssemblyName, pushedAssemblyNames));
        // An assembly the orphan pass refused to remove keeps its steps — deleting them here would
        // undo that refusal one line later. Excluded server-side, same as the pushed-assembly rule.
        if (blockedAssemblyIds.Count > 0)
            asmLink.LinkCriteria.AddCondition("pluginassemblyid", ConditionOperator.NotIn, blockedAssemblyIds.Cast<object>().ToArray());

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        if (result.Entities.Count == 0) return;

        var willDelete = forceDeleteOrphans && runMode == RunMode.Normal;

        var imagesByStep = new Dictionary<Guid, List<Entity>>();
        if (willDelete)
        {
            var stepIds = result.Entities.Select(e => (object)e.GetAttributeValue<Guid>("objectid")).ToArray();
            var imageQuery = new QueryExpression("sdkmessageprocessingstepimage")
            {
                ColumnSet = new ColumnSet("sdkmessageprocessingstepid"),
                Criteria = { Conditions = { new ConditionExpression("sdkmessageprocessingstepid", ConditionOperator.In, stepIds) } }
            };
            var images = await service.RetrieveMultipleAsync(imageQuery, cancellationToken).ConfigureAwait(false);
            foreach (var image in images.Entities)
            {
                var stepId = image.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")!.Id;
                if (!imagesByStep.TryGetValue(stepId, out var list))
                    imagesByStep[stepId] = list = [];
                list.Add(image);
            }
        }

        foreach (var component in result.Entities)
        {
            var stepId = component.GetAttributeValue<Guid>("objectid");
            var stepName = component.GetAttributeValue<AliasedValue>("step.name")?.Value as string ?? stepId.ToString();
            var asmName = component.GetAttributeValue<AliasedValue>("asm.name")?.Value as string ?? "unknown";

            console.Warning(willDelete
                ? $"Step '{Safe(stepName)}' registered under '{Safe(asmName)}.dll' (not the pushed assembly) — orphaned. Deleting."
                : $"Step '{Safe(stepName)}' registered under '{Safe(asmName)}.dll' (not the pushed assembly) — orphaned. Use --force delete-orphans to delete.");

            if (!willDelete) continue;

            if (imagesByStep.TryGetValue(stepId, out var stepImages))
                foreach (var image in stepImages)
                    await service.DeleteAsync(image.LogicalName, image.Id, cancellationToken).ConfigureAwait(false);

            await service.DeleteAsync("sdkmessageprocessingstep", stepId, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task<(Entity entity, bool needsUpdate, int cascadeDeleteCount)> GetOrRegisterAssemblyAsync(
        IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName, RunMode runMode, bool forceRecreateAssembly = false, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("pluginassembly")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("pluginassemblyid", "name", "version", "publickeytoken", "culture", "description", "packageid"),
            Criteria =
            {
                Conditions = { new ConditionExpression("name", ConditionOperator.Equal, metadata.Name) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var existing = result.Entities.FirstOrDefault();

        if (existing != null && existing.GetAttributeValue<EntityReference>("packageid") != null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Assembly '{metadata.Name}' is already registered in Dataverse as part of a plugin " +
                "package — push the .nupkg package instead of the raw assembly. Automated migration is not supported.");

        if (existing == null)
        {
            if (runMode == RunMode.DryRun)
            {
                console.Info($"  [green]+[/] Assembly [bold]{metadata.Name}[/] ({metadata.Version}) — would create");
                // Return a dummy entity so that the caller can continue with the dry-run
                return (new Entity("pluginassembly") { Id = Guid.NewGuid() }, false, 0);
            }

            var entity = new Entity("pluginassembly")
            {
                ["name"]          = metadata.Name,
                ["content"]       = Convert.ToBase64String(metadata.Content),
                ["version"]       = metadata.Version,
                ["isolationmode"] = new OptionSetValue(2), // 2 = Sandbox (cloud only)
                ["description"]   = $"{FlowlineMarker} sha256={metadata.Hash}"
            };

            var response = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName }, cancellationToken).ConfigureAwait(false);

            console.Ok($"Assembly [bold]{metadata.Name}[/] ({metadata.Version}) added");

            entity.Id = response.id;
            return (entity, false, 0);
        }

        var identityChanges = DetectIdentityChanges(existing, metadata);
        if (identityChanges != null)
        {
            var reason = string.Join(", ", identityChanges);
            var isDowngrade = IsVersionDowngrade(existing, metadata);

            if (!forceRecreateAssembly && runMode == RunMode.Normal)
            {
                var reasonText = isDowngrade ? $"version downgraded ({reason})" : $"identity changed ({reason})";
                console.Error($"Assembly [bold]{metadata.Name}[/] {reasonText} — Dataverse needs a delete and recreate. Use --force recreate-assembly to allow.");
                // Plain text, no Spectre markup: an exception message is escaped before it is printed
                // (it can carry arbitrary Dataverse text), so markup here surfaces as literal
                // "[bold]…[/]" in the error line. Markup belongs on the console.* call above.
                throw new FlowlineException(ExitCode.ForceRequired, $"Assembly '{metadata.Name}' {reasonText}. Use --force recreate-assembly to allow.");
            }

            // Load existing registrations before deletion to show what cascades
            var oldSnapshot = await _reader.LoadSnapshotAsync(service, existing.Id, metadata, solutionName, cancellationToken).ConfigureAwait(false);
            var cascadeDeleteCount = oldSnapshot.PluginTypes.Count + oldSnapshot.Steps.Count + oldSnapshot.Images.Count;

            switch (runMode)
            {
                case RunMode.DryRun:
                    var blockNote = !forceRecreateAssembly ? " — would be blocked without --force recreate-assembly" : "";
                    console.Warning($"Assembly [bold]{metadata.Name}[/] identity changed ({reason}){blockNote} — would delete and recreate");
                    WriteCascade(oldSnapshot, dryRun: true);
                    return (new Entity("pluginassembly") { Id = Guid.NewGuid() }, false, cascadeDeleteCount);
                case RunMode.NoDelete:
                    console.Error($"Assembly [bold]{metadata.Name}[/] identity changed ({reason}) — Dataverse needs a delete and recreate. Re-run without --no-delete to apply, or use --dry-run to preview.");
                    throw new InvalidOperationException($"Assembly '{metadata.Name}' identity changed ({reason}). Cannot continue in no-delete mode — re-run without --no-delete to apply, or use --dry-run to preview.");
                case RunMode.Normal:
                    var forceNote = isDowngrade ? " (version downgrade, --force recreate-assembly)" : " (--force recreate-assembly)";
                    console.Warning($"Assembly [bold]{metadata.Name}[/] identity changed ({reason}){forceNote} — deleting and recreating all registrations");
                    WriteCascade(oldSnapshot, dryRun: false);
                    await service.DeleteAsync("pluginassembly", existing.Id, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(runMode), runMode, null);
            }

            var freshEntity = new Entity("pluginassembly")
            {
                ["name"]          = metadata.Name,
                ["content"]       = Convert.ToBase64String(metadata.Content),
                ["version"]       = metadata.Version,
                ["isolationmode"] = new OptionSetValue(2),
                ["description"]   = $"{FlowlineMarker} sha256={metadata.Hash}"
            };

            var freshResponse = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest { Target = freshEntity, ["SolutionUniqueName"] = solutionName },
                cancellationToken).ConfigureAwait(false);

            freshEntity.Id = freshResponse.id;
            console.Ok($"Assembly [bold]{metadata.Name}[/] recreated");
            return (freshEntity, false, 0); // cascade items already logged; fresh assembly starts empty
        }

        await AddSolutionComponentAsync(service, existing.Id, solutionName, cancellationToken).ConfigureAwait(false);
        var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
        return (existing, storedHash != metadata.Hash, 0);
    }

    // UQ1_PluginType unique index on dbo.PluginTypeBase is (friendlyname, solutionId, isworkflowactivity, ...).
    // All unmanaged plugin types share the "Active" solution (fd140aae-4df4-11dd-bd17-0019b9312238) as their
    // solutionId, which makes friendlyname org-globally unique — not scoped to the assembly.
    // This check queries friendlyname (not typename/name) because that is the actual constraint column.
    async Task CheckFriendlyNameCollisionsAsync(
        IOrganizationServiceAsync2 service,
        Guid assemblyId,
        string[] friendlyNames,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("friendlyname", "typename", "pluginassemblyid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("friendlyname", ConditionOperator.In, friendlyNames.Cast<object>().ToArray()),
                    new ConditionExpression("pluginassemblyid", ConditionOperator.NotEqual, assemblyId)
                }
            }
        };
        var asmLink = query.AddLink("pluginassembly", "pluginassemblyid", "pluginassemblyid", JoinOperator.LeftOuter);
        asmLink.Columns = new ColumnSet("name");
        asmLink.EntityAlias = "asm";

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        if (result.Entities.Count == 0) return;

        var conflicts = result.Entities
            .Select(e => (
                TypeName: e.GetAttributeValue<string>("typename") ?? e.GetAttributeValue<string>("friendlyname") ?? "(unknown)",
                Assembly: (e.GetAttributeValue<AliasedValue>("asm.name")?.Value as string ?? "unknown") + ".dll"
            ))
            .ToList();

        throw new FlowlineException(ExitCode.ValidationFailed,
                $"Plugin type name collision — {conflicts.Count} type(s) already registered in another assembly. Add a namespace or rename the class(es).")
            .WithData(data =>
            {
                foreach (var (typeName, assemblyName) in conflicts)
                    data.Add(typeName, $"{Safe(typeName)} already registered in {Safe(assemblyName)}");
            });
    }

    // Shared by every progress-tracked phase below — each adds one task to a console.Progress() run and
    // awaits a body that receives it, differing only in the label, the task's maxValue, and the body.
    async Task RunWithProgressAsync(string label, int maxValue, Func<ProgressTask, Task> body) =>
        await console.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(label, maxValue: maxValue);
                await body(task).ConfigureAwait(false);
            })
            .ConfigureAwait(false);

    void WriteCascade(RegistrationSnapshot snapshot, bool dryRun)
    {
        var prefix = dryRun ? "  [red]-[/] " : "";
        var suffix = dryRun ? " — would delete (cascade)" : " — cascade delete";
        foreach (var name in snapshot.PluginTypes.Keys)
            console.Info($"{prefix}Plugin type '{name}'{suffix}");
        foreach (var step in snapshot.Steps)
            console.Info($"{prefix}Step '{step.GetAttributeValue<string>("name")}'{suffix}");
        foreach (var image in snapshot.Images)
            console.Info($"{prefix}Image '{image.GetAttributeValue<string>("name")}'{suffix}");
    }

    /// <summary>What a dry run would do, in the three verbs the summary line reports.</summary>
    /// <remarks>
    /// Summable because a plugin <b>package</b> push plans one <see cref="RegistrationPlan"/> per
    /// assembly it owns but writes a single package: the per-assembly counts and the package's own
    /// create/update are one user-facing total, not one summary each.
    /// </remarks>
    readonly record struct PlanCounts(int Deletes, int Creates, int Updates)
    {
        public static PlanCounts operator +(PlanCounts a, PlanCounts b) =>
            new(a.Deletes + b.Deletes, a.Creates + b.Creates, a.Updates + b.Updates);

        public override string ToString() => $"{Deletes} delete(s), {Creates} create(s), {Updates} update(s)";
    }

    /// <param name="writeSummary">
    /// False for a package push, whose caller sums every assembly's counts with the package's own
    /// create/update and writes one summary for the lot.
    /// </param>
    /// <returns>What this plan would do, for callers that aggregate before reporting.</returns>
    PlanCounts WritePlanTree(PluginAssemblyMetadata metadata, bool needsUpdate, RegistrationPlan plan, RunMode runMode, int cascadeDeleteCount = 0, bool writeSummary = true)
    {
        // --- Name parse helpers ---
        static string TypeFromStep(string stepName)
        {
            var idx = stepName.IndexOf(": ", StringComparison.Ordinal);
            return idx > 0 ? stepName[..idx] : stepName;
        }
        static string DescFromStep(string stepName)
        {
            var idx = stepName.IndexOf(": ", StringComparison.Ordinal);
            return idx > 0 ? stepName[(idx + 2)..] : stepName;
        }
        static string ImageShortName(string imageName)
        {
            const string marker = "' on '";
            var idx = imageName.IndexOf(marker, StringComparison.Ordinal);
            return idx > 0 ? imageName[..idx] : imageName;
        }
        static string StepFromImage(string imageName)
        {
            const string marker = "' on '";
            var idx = imageName.IndexOf(marker, StringComparison.Ordinal);
            return idx > 0 ? imageName[(idx + marker.Length)..] : imageName;
        }

        // --- Symbol / verb helpers ---
        static string Sym(bool delete, bool create) =>
            delete ? "[red]-[/]" : (create ? "[green]+[/]" : "[yellow]~[/]");
        string Verb(bool delete, bool create) => runMode == RunMode.DryRun
            ? (delete ? "would delete" : create ? "would create" : "would update")
            : (delete ? "delete" : create ? "create" : "update");

        // --- Lookups ---
        // Steps use the fully qualified class name; type actions may use only the short name.
        // Build a short→full map from step names so both sides resolve to the same key.
        static string ShortName(string name)
        {
            var idx = name.LastIndexOf('.');
            return idx >= 0 ? name[(idx + 1)..] : name;
        }

        var shortToFull = plan.Steps.Deletes.Select(d => TypeFromStep(d.Name))
            .Concat(plan.Steps.Upserts.Select(u => TypeFromStep(u.Name)))
            .Where(n => n.Contains('.'))
            .GroupBy(ShortName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string ResolveFullName(string name) =>
            shortToFull.TryGetValue(name, out var full) ? full : name;

        var typeDeletes = plan.PluginTypes.Deletes
            .ToDictionary(d => ResolveFullName(d.Name), StringComparer.OrdinalIgnoreCase);
        var typeUpserts = plan.PluginTypes.Upserts
            .ToDictionary(u => ResolveFullName(u.Name), StringComparer.OrdinalIgnoreCase);

        var stepDelsByType = plan.Steps.Deletes
            .GroupBy(d => TypeFromStep(d.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var stepUpsByType = plan.Steps.Upserts
            .GroupBy(u => TypeFromStep(u.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var imgDelsByStep = plan.Images.Deletes
            .GroupBy(d => StepFromImage(d.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var imgUpsByStep = plan.Images.Upserts
            .GroupBy(u => StepFromImage(u.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Custom API groups by plugin type short name (for embedding under the type node)
        var customApisByTypeName = plan.CustomApiGroups
            .Where(g => g.PluginTypeName != null)
            .GroupBy(g => g.PluginTypeName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // All type names: explicit type actions + types implied by step names + custom API plugin types
        var allTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        allTypeNames.UnionWith(typeDeletes.Keys);
        allTypeNames.UnionWith(typeUpserts.Keys);
        allTypeNames.UnionWith(stepDelsByType.Keys);
        allTypeNames.UnionWith(stepUpsByType.Keys);
        allTypeNames.UnionWith(customApisByTypeName.Keys);

        // --- Assembly root ---
        var assemblyLabel = needsUpdate
            ? $"[yellow]~[/] {Safe(metadata.Name)} ({Safe(metadata.Version)}) — {Verb(false, false)} content"
            : $"{Safe(metadata.Name)} ({Safe(metadata.Version)})";
        var tree = new Tree(assemblyLabel);

        // --- Plugin types → Steps → Images ---
        foreach (var typeName in allTypeNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            string typeLabel;
            if (typeDeletes.ContainsKey(typeName))
                typeLabel = $"{Sym(true, false)} [dim]plugin[/] {Safe(typeName)} — {Verb(true, false)}";
            else if (typeUpserts.TryGetValue(typeName, out var tu))
                typeLabel = $"{Sym(false, tu.IsCreate)} [dim]plugin[/] {Safe(typeName)} — {Verb(false, tu.IsCreate)}";
            else
                typeLabel = $"[dim]plugin {Safe(typeName)}[/]";

            var typeNode = tree.AddNode(typeLabel);

            var delSteps = stepDelsByType.GetValueOrDefault(typeName) ?? [];
            var upsSteps = stepUpsByType.GetValueOrDefault(typeName) ?? [];

            var allStepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            allStepNames.UnionWith(delSteps.Select(d => d.Name));
            allStepNames.UnionWith(upsSteps.Select(u => u.Name));

            foreach (var stepName in allStepNames.OrderBy(DescFromStep, StringComparer.OrdinalIgnoreCase))
            {
                string stepDesc = DescFromStep(stepName);
                string stepLabel;
                if (delSteps.Any(d => string.Equals(d.Name, stepName, StringComparison.OrdinalIgnoreCase)))
                {
                    stepLabel = $"{Sym(true, false)} [dim]step[/] {Safe(stepDesc)} — {Verb(true, false)}";
                }
                else
                {
                    var su = upsSteps.First(u => string.Equals(u.Name, stepName, StringComparison.OrdinalIgnoreCase));
                    var meta = $"stage={OptionValue(su.Entity, "stage")} mode={OptionValue(su.Entity, "mode")} rank={OptionValue(su.Entity, "rank")}";
                    stepLabel = $"{Sym(false, su.IsCreate)} [dim]step[/] {Safe(stepDesc)} [dim]{meta}[/] — {Verb(false, su.IsCreate)}";
                }

                var stepNode = typeNode.AddNode(stepLabel);

                var delImgs = imgDelsByStep.GetValueOrDefault(stepName) ?? [];
                var upsImgs = imgUpsByStep.GetValueOrDefault(stepName) ?? [];

                foreach (var img in delImgs.OrderBy(d => ImageShortName(d.Name), StringComparer.OrdinalIgnoreCase))
                    stepNode.AddNode($"{Sym(true, false)} [dim]img[/] {Safe(ImageShortName(img.Name))} — {Verb(true, false)}");

                foreach (var img in upsImgs.OrderBy(u => ImageShortName(u.Name), StringComparer.OrdinalIgnoreCase))
                {
                    var alias   = Safe(img.Entity.GetAttributeValue<string>("entityalias") ?? "(none)");
                    var itype   = OptionValue(img.Entity, "imagetype");
                    var attrs   = Safe(img.Entity.GetAttributeValue<string>("attributes") ?? "(all)");
                    var imgType = itype == "0" ? "pre-img" : itype == "1" ? "post-img" : "img";
                    stepNode.AddNode($"{Sym(false, img.IsCreate)} [dim]{imgType}[/] {Safe(ImageShortName(img.Name))} [dim]alias={alias} attributes={attrs}[/] — {Verb(false, img.IsCreate)}");
                }
            }

            // --- Custom APIs for this plugin type ---
            if (customApisByTypeName.TryGetValue(ShortName(typeName), out var typeApiGroups))
            {
                foreach (var group in typeApiGroups.OrderBy(g => g.ApiName, StringComparer.OrdinalIgnoreCase))
                {
                    IHasTreeNodes apiNode;
                    if (group.Api.Deletes.Count == 1 && group.Api.Upserts.Count == 0)
                    {
                        var d = group.Api.Deletes[0];
                        apiNode = typeNode.AddNode($"{Sym(true, false)} [dim]api[/] {Safe(d.Name)} — {Verb(true, false)}");
                    }
                    else if (group.Api.Deletes.Count == 0 && group.Api.Upserts.Count == 1)
                    {
                        var u = group.Api.Upserts[0];
                        apiNode = typeNode.AddNode($"{Sym(false, u.IsCreate)} [dim]api[/] {Safe(u.Name)} [dim]binding={OptionValue(u.Entity, "bindingtype")} function={BoolValue(u.Entity, "isfunction")} private={BoolValue(u.Entity, "isprivate")}[/] — {Verb(false, u.IsCreate)}");
                    }
                    else
                    {
                        apiNode = typeNode.AddNode($"[dim]{Safe(group.ApiName)}[/]");
                        foreach (var d in group.Api.Deletes)
                            apiNode.AddNode($"{Sym(true, false)} [dim]api[/] {Safe(d.Name)} — {Verb(true, false)}");
                        foreach (var u in group.Api.Upserts)
                            apiNode.AddNode($"{Sym(false, u.IsCreate)} [dim]api[/] {Safe(u.Name)} [dim]binding={OptionValue(u.Entity, "bindingtype")} function={BoolValue(u.Entity, "isfunction")} private={BoolValue(u.Entity, "isprivate")}[/] — {Verb(false, u.IsCreate)}");
                    }

                    foreach (var d in group.RequestParams.Deletes.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                        apiNode.AddNode($"{Sym(true, false)} [dim]req-param[/] {Safe(d.Name)} — {Verb(true, false)}");
                    foreach (var u in group.RequestParams.Upserts.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
                        apiNode.AddNode($"{Sym(false, u.IsCreate)} [dim]req-param[/] {Safe(u.Name)} [dim]type={OptionValue(u.Entity, "type")} optional={BoolValue(u.Entity, "isoptional")}[/] — {Verb(false, u.IsCreate)}");
                    foreach (var d in group.ResponseProps.Deletes.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                        apiNode.AddNode($"{Sym(true, false)} [dim]res=prop[/] {Safe(d.Name)} — {Verb(true, false)}");
                    foreach (var u in group.ResponseProps.Upserts.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
                        apiNode.AddNode($"{Sym(false, u.IsCreate)} [dim]res-prop[/] {Safe(u.Name)} [dim]type={OptionValue(u.Entity, "type")}[/] — {Verb(false, u.IsCreate)}");
                }
            }
        }

        // --- Custom APIs planned without a plugin type of their own to sit under (see the sweep in
        // PluginPlanner.Plan — these are ours, and source no longer declares them).
        var sweptApiGroups = plan.CustomApiGroups.Where(g => g.PluginTypeName == null).ToList();
        if (sweptApiGroups.Count > 0)
        {
            var sweptNode = tree.AddNode("[dim]Custom APIs (no source left)[/]");
            foreach (var group in sweptApiGroups.OrderBy(g => g.ApiName, StringComparer.OrdinalIgnoreCase))
            {
                IHasTreeNodes apiNode;
                if (group.Api.Deletes.Count == 1 && group.Api.Upserts.Count == 0)
                {
                    var d = group.Api.Deletes[0];
                    apiNode = sweptNode.AddNode($"{Sym(true, false)} [dim]api[/] {Safe(d.Name)} — {Verb(true, false)}");
                }
                else
                {
                    apiNode = sweptNode.AddNode($"[dim]{Safe(group.ApiName)}[/]");
                    foreach (var d in group.Api.Deletes)
                        apiNode.AddNode($"{Sym(true, false)} [dim]api[/] {Safe(d.Name)} — {Verb(true, false)}");
                }

                foreach (var d in group.RequestParams.Deletes.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    apiNode.AddNode($"{Sym(true, false)} [dim]req[/] {Safe(d.Name)} — {Verb(true, false)}");
                foreach (var d in group.ResponseProps.Deletes.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    apiNode.AddNode($"{Sym(true, false)} [dim]res[/] {Safe(d.Name)} — {Verb(true, false)}");
            }
        }

        var createCount = plan.PluginTypes.Upserts.Count(u => u.IsCreate)
                          + plan.Steps.Upserts.Count(u => u.IsCreate)
                          + plan.CustomApis.Upserts.Count(u => u.IsCreate)
                          + plan.Images.Upserts.Count(u => u.IsCreate)
                          + plan.RequestParams.Upserts.Count(u => u.IsCreate)
                          + plan.ResponseProps.Upserts.Count(u => u.IsCreate);
        // The assembly content write is a real update — the execute path treats it as one (it runs its
        // own phase, and a no-change run is only skipped when !needsUpdate) and the assembly-only dry
        // run already reports it as "1 update". Leaving it out made the summary read "0 update(s)"
        // directly above its own "~ … would update content" line.
        var counts = new PlanCounts(
            plan.TotalDeletes + cascadeDeleteCount,
            createCount,
            plan.TotalUpserts - createCount + (needsUpdate ? 1 : 0));

        if (runMode == RunMode.DryRun)
        {
            console.Write(tree);
            if (writeSummary)
                console.Ok($"Dry run: {counts}. Run without --dry-run to apply.");
        }
        else
        {
            console.Verbose(tree);
        }

        return counts;
    }

    void WriteSnapshotVerbose(RegistrationSnapshot snapshot)
    {
        var tree = new Tree("[dim]Dataverse snapshot[/]") { Style = Style.Parse("dim") };
        tree.AddNode($"[dim]Publisher prefix: {Safe(snapshot.PublisherPrefix)}[/]");

        var pluginTypesNode = tree.AddNode($"[dim]Plugin types ({snapshot.PluginTypes.Count})[/]");
        foreach (var pluginType in snapshot.PluginTypes.Values.OrderBy(NameForPluginType, StringComparer.OrdinalIgnoreCase))
        {
            var pluginTypeId = pluginType.Id;
            var isWorkflow = BoolValue(pluginType, "isworkflowactivity");
            var pluginTypeNode = pluginTypesNode.AddNode(
                $"[dim]{Safe(NameForPluginType(pluginType))} ({pluginTypeId}){(isWorkflow ? " [[workflow]]" : "")}[/]");

            var steps = snapshot.Steps
                .Where(step => SameReference(step.GetAttributeValue<EntityReference>("plugintypeid"), pluginTypeId))
                .OrderBy(step => step.GetAttributeValue<string>("name"), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (steps.Count > 0)
            {
                var stepsNode = pluginTypeNode.AddNode($"[dim]Steps ({steps.Count})[/]");
                foreach (var step in steps)
                {
                    var stepId = step.Id;
                    var stepNode = stepsNode.AddNode(
                        $"[dim]{Safe(step.GetAttributeValue<string>("name") ?? stepId.ToString())} " +
                        $"stage={OptionValue(step, "stage")} mode={OptionValue(step, "mode")} rank={OptionValue(step, "rank")}[/]");

                    var filteringAttributes = step.GetAttributeValue<string>("filteringattributes");
                    if (!string.IsNullOrWhiteSpace(filteringAttributes))
                        stepNode.AddNode($"[dim]Filtering attributes: {Safe(filteringAttributes)}[/]");

                    var impersonatingUser = step.GetAttributeValue<EntityReference>("impersonatinguserid");
                    if (impersonatingUser != null)
                        stepNode.AddNode($"[dim]Run as: {impersonatingUser.Id}[/]");

                    var images = snapshot.Images
                        .Where(image => SameReference(image.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid"), stepId))
                        .OrderBy(image => image.GetAttributeValue<string>("name"), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (images.Count > 0)
                    {
                        var imagesNode = stepNode.AddNode($"[dim]Images ({images.Count})[/]");
                        foreach (var image in images)
                            imagesNode.AddNode(
                                $"[dim]{Safe(image.GetAttributeValue<string>("name") ?? image.Id.ToString())} " +
                                $"alias={Safe(image.GetAttributeValue<string>("entityalias") ?? "(none)")} " +
                                $"type={OptionValue(image, "imagetype")} " +
                                $"attributes={Safe(image.GetAttributeValue<string>("attributes") ?? "(all)")}[/]");
                    }
                }
            }

            var customApis = snapshot.CustomApis
                .Where(api => SameReference(api.GetAttributeValue<EntityReference>("plugintypeid"), pluginTypeId))
                .OrderBy(api => api.GetAttributeValue<string>("uniquename"), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (customApis.Count > 0)
            {
                var apisNode = pluginTypeNode.AddNode($"[dim]Custom APIs ({customApis.Count})[/]");
                foreach (var api in customApis)
                {
                    var apiId = api.Id;
                    var apiNode = apisNode.AddNode(
                        $"[dim]{Safe(api.GetAttributeValue<string>("uniquename") ?? apiId.ToString())} " +
                        $"binding={OptionValue(api, "bindingtype")} function={BoolValue(api, "isfunction")} private={BoolValue(api, "isprivate")}[/]");

                    var boundEntity = api.GetAttributeValue<string>("boundentitylogicalname");
                    if (!string.IsNullOrWhiteSpace(boundEntity))
                        apiNode.AddNode($"[dim]Bound entity: {Safe(boundEntity)}[/]");

                    var requestParams = snapshot.RequestParams
                        .Where(param => SameReference(param.GetAttributeValue<EntityReference>("customapiid"), apiId))
                        .OrderBy(param => param.GetAttributeValue<string>("uniquename"), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (requestParams.Count > 0)
                    {
                        var paramsNode = apiNode.AddNode($"[dim]Request parameters ({requestParams.Count})[/]");
                        foreach (var param in requestParams)
                            paramsNode.AddNode(
                                $"[dim]{Safe(param.GetAttributeValue<string>("uniquename") ?? param.Id.ToString())} " +
                                $"type={OptionValue(param, "type")} optional={BoolValue(param, "isoptional")} " +
                                $"entity={Safe(param.GetAttributeValue<string>("logicalentityname") ?? "(none)")}[/]");
                    }

                    var responseProps = snapshot.ResponseProps
                        .Where(prop => SameReference(prop.GetAttributeValue<EntityReference>("customapiid"), apiId))
                        .OrderBy(prop => prop.GetAttributeValue<string>("uniquename"), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (responseProps.Count > 0)
                    {
                        var propsNode = apiNode.AddNode($"[dim]Response properties ({responseProps.Count})[/]");
                        foreach (var prop in responseProps)
                            propsNode.AddNode(
                                $"[dim]{Safe(prop.GetAttributeValue<string>("uniquename") ?? prop.Id.ToString())} " +
                                $"type={OptionValue(prop, "type")} entity={Safe(prop.GetAttributeValue<string>("logicalentityname") ?? "(none)")}[/]");
                    }
                }
            }
        }

        AddUnlinkedNodes(tree, "Unlinked steps", snapshot.Steps,
            e => e.GetAttributeValue<EntityReference>("plugintypeid"),
            snapshot.PluginTypes.Values.Select(e => e.Id).ToHashSet());
        AddUnlinkedNodes(tree, "Unlinked images", snapshot.Images,
            e => e.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid"),
            snapshot.Steps.Select(e => e.Id).ToHashSet());
        // Not "unlinked" — snapshot.CustomApis is publisher-wide, so most entries here are simply
        // other projects' APIs. Informational only; nothing on this branch drives a delete.
        AddUnlinkedNodes(tree, "Custom APIs not implemented by this assembly", snapshot.CustomApis,
            e => e.GetAttributeValue<EntityReference>("plugintypeid"),
            snapshot.PluginTypes.Values.Select(e => e.Id).ToHashSet());

        if (snapshot.SdkMessageIds.Count > 0)
        {
            var messagesNode = tree.AddNode($"[dim]SDK messages ({snapshot.SdkMessageIds.Count})[/]");
            foreach (var (name, _) in snapshot.SdkMessageIds.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                messagesNode.AddNode($"[dim]{Safe(name)}[/]");
        }

        if (snapshot.FilterIds.Count > 0)
        {
            var msgById = snapshot.SdkMessageIds.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
            var filtersNode = tree.AddNode($"[dim]SDK message filters ({snapshot.FilterIds.Count})[/]");
            foreach (var (key, _) in snapshot.FilterIds
                .OrderBy(kvp => msgById.TryGetValue(kvp.Key.MessageId, out var n) ? n : kvp.Key.MessageId.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(kvp => kvp.Key.EntityName, StringComparer.OrdinalIgnoreCase))
            {
                var msgName = msgById.TryGetValue(key.MessageId, out var resolvedName) ? resolvedName : key.MessageId.ToString()[..8] + "…";
                var entity  = key.EntityName ?? "(any)";
                var secondary = key.SecondaryEntity != null ? $" · {Safe(key.SecondaryEntity)}" : "";
                filtersNode.AddNode($"[dim]{Safe(msgName)} on {Safe(entity)}{secondary}[/]");
            }
        }

        if (snapshot.SystemUserIds.Count > 0)
        {
            var usersNode = tree.AddNode($"[dim]System users ({snapshot.SystemUserIds.Count})[/]");
            foreach (var id in snapshot.SystemUserIds.OrderBy(id => id))
                usersNode.AddNode($"[dim]{id}[/]");
        }

        console.Verbose(tree);
    }

    void AddUnlinkedNodes(Tree tree, string title, IReadOnlyList<Entity> items,
        Func<Entity, EntityReference?> parentSelector, IReadOnlySet<Guid> knownParentIds)
    {
        var unlinked = items
            .Where(item =>
            {
                var parent = parentSelector(item);
                return parent == null || parent.Id == Guid.Empty || !knownParentIds.Contains(parent.Id);
            })
            .OrderBy(NameForEntity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unlinked.Count == 0) return;

        var section = tree.AddNode($"[dim]{title} ({unlinked.Count})[/]");
        foreach (var item in unlinked)
            section.AddNode($"[dim]{Safe(NameForEntity(item))} ({item.Id})[/]");
    }

    async Task UpdateAssemblyContentAsync(IOrganizationServiceAsync2 service, Entity entity, PluginAssemblyMetadata metadata, CancellationToken cancellationToken)
    {
        entity["content"]     = Convert.ToBase64String(metadata.Content);
        entity["version"]     = metadata.Version;
        entity["description"] = $"{FlowlineMarker} sha256={metadata.Hash}";
        await service.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    async Task AddSolutionComponentAsync(IOrganizationServiceAsync2 service, Guid assemblyId, string solutionName, CancellationToken cancellationToken)
    {
        var request = new OrganizationRequest("AddSolutionComponent")
        {
            ["ComponentId"]               = assemblyId,
            ["ComponentType"]             = 91, // PluginAssembly
            ["SolutionUniqueName"]        = solutionName,
            ["AddRequiredComponents"]     = false,
            ["DoNotIncludeSubcomponents"] = false
        };
        await service.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // Union of every loaded assembly's own PluginTypes ids across one package's snapshot batch — passed
    // to PluginPlanner.Plan as "what this push owns". For a package that union is genuinely own-scope:
    // all N assemblies ship and are versioned together. It never authorises a delete on its own — see
    // the Custom API sweep in PluginPlanner.Plan, which only ever deletes against the planning
    // assembly's OWN plugin types and uses this set to recognise "a sibling assembly in my package
    // owns this, its pass handles it".
    static IReadOnlySet<Guid> AllPluginTypeIds(IEnumerable<(PluginAssemblyMetadata Metadata, Entity? Assembly, RegistrationSnapshot? Snapshot)> snapshots) =>
        snapshots.Where(s => s.Snapshot != null).SelectMany(s => s.Snapshot!.PluginTypes.Values.Select(t => t.Id)).ToHashSet();

    static string? ParseStoredHash(string? description)
    {
        if (description == null) return null;
        var idx = description.IndexOf("sha256=", StringComparison.Ordinal);
        return idx < 0 ? null : description[(idx + 7)..].Split(' ')[0].Trim();
    }

    static List<string>? DetectIdentityChanges(Entity existing, PluginAssemblyMetadata metadata)
    {
        var registeredPkt     = existing.GetAttributeValue<string>("publickeytoken");
        var registeredCulture = existing.GetAttributeValue<string>("culture") ?? "neutral";
        var registeredVersion = existing.GetAttributeValue<string>("version");

        bool pktChanged        = !string.Equals(registeredPkt, metadata.PublicKeyToken, StringComparison.OrdinalIgnoreCase);
        bool cultureChanged    = !string.Equals(registeredCulture, metadata.Culture, StringComparison.OrdinalIgnoreCase);
        bool majorMinorChanged = HasMajorOrMinorVersionChange(registeredVersion, metadata.Version);

        if (!pktChanged && !cultureChanged && !majorMinorChanged) return null;

        var reasons = new List<string>();
        if (pktChanged)        reasons.Add($"public key token ({registeredPkt ?? "null"} -> {metadata.PublicKeyToken ?? "null"})");
        if (cultureChanged)    reasons.Add($"culture ({registeredCulture} -> {metadata.Culture})");
        if (majorMinorChanged) reasons.Add($"major/minor version ({registeredVersion} -> {metadata.Version})");
        return reasons;
    }

    internal static bool HasMajorOrMinorVersionChange(string? registered, string local)
    {
        if (string.IsNullOrWhiteSpace(registered)) return false;
        if (!Version.TryParse(registered, out var reg)) return false;
        if (!Version.TryParse(local, out var loc))      return false;
        return reg.Major != loc.Major || reg.Minor != loc.Minor;
    }

    static bool IsVersionDowngrade(Entity existing, PluginAssemblyMetadata metadata)
    {
        var registeredVersion = existing.GetAttributeValue<string>("version");
        if (!Version.TryParse(registeredVersion, out var reg)) return false;
        if (!Version.TryParse(metadata.Version, out var loc)) return false;
        return loc < reg;
    }

    static int CountAddToSolutionComponents(RegistrationPlan plan) =>
        plan.AllPlans.Sum(p => p.AddSolutionComponents.Count);

    static bool SameReference(EntityReference? reference, Guid id) =>
        reference != null && reference.Id == id;

    static string NameForPluginType(Entity entity) =>
        entity.GetAttributeValue<string>("typename")
        ?? entity.GetAttributeValue<string>("name")
        ?? entity.Id.ToString();

    static string NameForEntity(Entity entity) =>
        entity.GetAttributeValue<string>("uniquename")
        ?? entity.GetAttributeValue<string>("name")
        ?? entity.Id.ToString();

    static string OptionValue(Entity entity, string attribute) =>
        entity.Attributes.TryGetValue(attribute, out var value)
            ? value switch
            {
                OptionSetValue option => option.Value.ToString(),
                int integer => integer.ToString(),
                null => "(none)",
                _ => value.ToString() ?? "(none)"
            }
            : "(none)";

    static bool BoolValue(Entity entity, string attribute) =>
        entity.Attributes.TryGetValue(attribute, out var value) && value is bool boolean && boolean;

    static string Safe(string value) => Markup.Escape(value);
}
