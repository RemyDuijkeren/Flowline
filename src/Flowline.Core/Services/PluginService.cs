using System.Security.Cryptography;
using Flowline.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;

namespace Flowline.Core.Services;

public class PluginService(IAnsiConsole console, ILogger<PluginService> logger)
{
    const string FlowlineMarker = "[flowline]";

    readonly PluginReader _reader = new();
    readonly PluginPlanner _planner = new(console);
    readonly PluginExecutor _executor = new(console);
    readonly SolutionReader _solutionReader = new();
    readonly PluginAssemblyReader _assemblyReader = new(console);

    public async Task<bool> SyncAssemblyOnlyAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            throw new ArgumentException("dllPath is required and cannot be empty.", nameof(dllPath));

        var metadata = console.Status().FlowlineSpinner().Start("Analyzing plugin assembly...", _ => _assemblyReader.Analyze(dllPath));
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
            ColumnSet = new ColumnSet("pluginassemblyid", "name", "version", "publickeytoken", "culture", "description"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, metadata.Name) } }
        };
        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var existing = result.Entities.FirstOrDefault();

        if (existing == null)
            throw new InvalidOperationException($"Assembly '{metadata.Name}' not found in Dataverse — run push without --scope assemblyonly to register it first.");

        var identityChanges = DetectIdentityChanges(existing, metadata);
        logger.LogDebug("Assembly '{MetadataNamee}' identity changes: {Joinin}", metadata.Name, string.Join(", ", identityChanges ?? Enumerable.Empty<string>()));
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

        await console.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Updating plugin assembly", maxValue: 1);
                await UpdateAssemblyContentAsync(service, existing, metadata, cancellationToken).ConfigureAwait(false);
                task.Increment(1);
            })
            .ConfigureAwait(false);
        console.Ok($"Assembly [bold]{metadata.Name}[/] ({metadata.Version}) updated");
        return true;
    }

    public async Task<bool> SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        bool forceDeleteOrphans = false,
        bool forceRecreateAssembly = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            throw new ArgumentException("dllPath is required and cannot be empty.", nameof(dllPath));

        var metadata = console.Status().FlowlineSpinner().Start("Analyzing plugin assembly...", ctx => _assemblyReader.Analyze(dllPath));
        return await SyncSolutionAsync(service, metadata, solutionName, runMode, forceDeleteOrphans, forceRecreateAssembly, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        bool forceDeleteOrphans = false,
        bool forceRecreateAssembly = false,
        CancellationToken cancellationToken = default)
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

        await WarnOrphanAssembliesAsync(service, metadata.Name, solutionName, forceDeleteOrphans, runMode, cancellationToken).ConfigureAwait(false);
        await WarnOrphanStepsAsync(service, metadata.Name, solutionName, forceDeleteOrphans, runMode, cancellationToken).ConfigureAwait(false);

        // Phase 2: Load snapshot (all Dataverse state in parallel)
        var snapshot = await console.Status().FlowlineSpinner()
            .StartAsync("Lookup plugin registrations...", _ => _reader.LoadSnapshotAsync(service, assembly.Id, metadata, solutionName, cancellationToken))
            .ConfigureAwait(false);
        WriteSnapshotVerbose(snapshot);
        console.Info("Plugin registrations found");

        // Phase 3: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot, metadata, assembly, solutionName);
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
            await console.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Deleting stale plugin components", maxValue: plan.TotalDeletes);
                    await _executor.ExecuteDeletesAsync(service, plan, solutionName, false, cancellationToken, task).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        if (plan.TotalDeletes > 0) console.Ok($"{plan.TotalDeletes} stale component(s) deleted");

        // Phase 5: Update assembly content — must happen before new plugin types are registered
        if (needsUpdate)
        {
            await console.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Updating plugin assembly", maxValue: 1);
                    await UpdateAssemblyContentAsync(service, assembly, metadata, cancellationToken).ConfigureAwait(false);
                    task.Increment(1);
                })
                .ConfigureAwait(false);
            console.Ok($"Updated assembly content for [bold]{metadata.Name}[/]");
        }

        // Phase 6: Execute upserts and add to solution
        if (plan.TotalUpserts > 0)
        {
            await console.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Syncing plugin components", maxValue: plan.TotalUpserts);
                    await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken, task).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        else
        {
            await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken).ConfigureAwait(false);
        }
        if (plan.TotalUpserts > 0) console.Ok($"{plan.TotalUpserts} component(s) synced");

        var addToSolutionCount = CountAddToSolutionComponents(plan);
        if (addToSolutionCount > 0)
        {
            await console.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Adding plugin components to solution", maxValue: addToSolutionCount);
                    await _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken, task).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
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
    // snapshots/plans, never merged). WarnOrphanAssembliesAsync/WarnOrphanStepsAsync are intentionally
    // never called on this path — a multi-assembly package's own other assemblies would misclassify as
    // orphans under the classic path's single-assembly check (R11/KTD16); package-owned orphan cleanup
    // is covered separately by U8's pipeline redirect.

    const int PackageAssemblyCheckMaxAttempts = 5;
    static readonly TimeSpan PackageAssemblyCheckDelay = TimeSpan.FromSeconds(1);

    public async Task<bool> SyncSolutionFromPackageAsync(
        IOrganizationServiceAsync2 service,
        string nupkgPath,
        string projectAssemblyName,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nupkgPath))
            throw new ArgumentException("nupkgPath is required and cannot be empty.", nameof(nupkgPath));

        var assemblies = console.Status().FlowlineSpinner().Start("Analyzing plugin package...", _ => _assemblyReader.AnalyzePackage(nupkgPath));
        var nupkgContent = await File.ReadAllBytesAsync(nupkgPath, cancellationToken).ConfigureAwait(false);
        return await SyncSolutionFromPackageAsync(service, assemblies, nupkgContent, nupkgPath, projectAssemblyName, solutionName, runMode, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default)
    {
        // R3a: zero-DLL rejection — first check, ahead of detect-and-block and change detection,
        // since neither has anything to operate against without at least one reflected assembly.
        if (assemblies.Count == 0)
            throw new InvalidOperationException(
                $"No DLL implementing IPlugin was found in lib/<tfm>/ of package '{nupkgPath}' — the plugin package cannot be deployed empty.");

        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        var primary = assemblies.FirstOrDefault(a => string.Equals(a.Name, projectAssemblyName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No reflected assembly in '{nupkgPath}' matches the project's own build output assembly name '{projectAssemblyName}'.");

        // R9: detect-and-block — reuses the classic-path lookup pattern (GetOrRegisterAssemblyAsync),
        // extended with packageid so an empty packageid means a genuinely classic (non-package) assembly.
        // When packageid IS populated, this same record is the package's primary assembly (KTD2) —
        // reused below for change detection instead of a second query.
        var assemblyQuery = new QueryExpression("pluginassembly")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("pluginassemblyid", "name", "version", "publickeytoken", "culture", "description", "packageid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, projectAssemblyName) } }
        };
        var assemblyResult = await service.RetrieveMultipleAsync(assemblyQuery, cancellationToken).ConfigureAwait(false);
        var existingAssembly = assemblyResult.Entities.FirstOrDefault();

        if (existingAssembly != null && existingAssembly.GetAttributeValue<EntityReference>("packageid") == null)
            throw new InvalidOperationException(
                $"Assembly '{projectAssemblyName}' is already registered in Dataverse as a classic (non-package) assembly — " +
                $"remove it manually before pushing this project as a plugin package. Automated migration is not supported.");

        // Phase 0: solution existence/support check + live publisher prefix (KTD11) — same resolution
        // the classic path already uses, just captured here instead of discarded.
        var solutionInfo = await console.Status().FlowlineSpinner()
            .StartAsync($"Looking up solution [bold]{solutionName}[/]...",
                _ => _solutionReader.GetSupportedSolutionInfoAsync(service, solutionName, cancellationToken))
            .ConfigureAwait(false);
        console.Info("Solution found");
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
            if (runMode == RunMode.DryRun)
            {
                console.Skip("Plugin package already up to date — skipping");
                return false;
            }

            // R4/R11 (item 8): package content unchanged — no package write at all, but each assembly's
            // steps are still diffed and synced directly against its own scoped snapshot (drift
            // correction). Never touches WarnOrphanAssembliesAsync/WarnOrphanStepsAsync (R11/KTD16).
            return await SyncPackageStepsOnlyAsync(service, assemblies, existingPackage.Id, solutionName, runMode, cancellationToken).ConfigureAwait(false);
        }

        if (runMode == RunMode.DryRun)
        {
            console.Info(existingPackage == null
                ? $"  [green]+[/] Package [bold]{packageUniqueName}[/] ({primary.Version}) — would create"
                : $"  [yellow]~[/] Package [bold]{packageUniqueName}[/] — would update content");
            console.Ok("Dry run: 1 update. Run without --dry-run to apply.");
            return true;
        }

        // KD4/KTD13: for an existing package, any assembly whose class was removed must have its
        // steps/custom APIs deleted *before* the content update — Dataverse rejects the update
        // otherwise. A brand-new package (existingPackage == null) has no prior assemblies yet, so
        // there is nothing of theirs to delete.
        if (existingPackage != null)
        {
            var preSnapshots = await _reader.LoadPackageSnapshotsAsync(service, existingPackage.Id, assemblies, solutionName, cancellationToken).ConfigureAwait(false);
            var preKnownPluginTypeIds = AllPluginTypeIds(preSnapshots);
            foreach (var (metadata, assemblyEntity, snapshot) in preSnapshots)
            {
                if (assemblyEntity == null || snapshot == null) continue; // not yet present — nothing of this assembly's to delete

                var plan = _planner.Plan(snapshot, metadata, assemblyEntity, solutionName, preKnownPluginTypeIds);
                if (plan.PluginTypes.Deletes.Count == 0) continue; // no class was removed for this assembly

                var preUpdateDeletes = new RegistrationPlan();
                preUpdateDeletes.Steps.Deletes.AddRange(plan.Steps.Deletes);
                preUpdateDeletes.CustomApis.Deletes.AddRange(plan.CustomApis.Deletes);
                preUpdateDeletes.Images.Deletes.AddRange(plan.Images.Deletes);
                preUpdateDeletes.RequestParams.Deletes.AddRange(plan.RequestParams.Deletes);
                preUpdateDeletes.ResponseProps.Deletes.AddRange(plan.ResponseProps.Deletes);
                // PluginTypes.Deletes intentionally left empty — Dataverse's package sync removes the
                // now-empty plugin type automatically; Flowline must never call DeleteAsync("plugintype", ...).

                await _executor.ExecuteDeletesAsync(service, preUpdateDeletes, solutionName, false, cancellationToken).ConfigureAwait(false);
            }
        }

        var packageId = await WritePackageContentAsync(service, existingPackage, packageUniqueName, primary, nupkgContent, solutionName, cancellationToken).ConfigureAwait(false);

        // R6/KTD14: confirm the auto-created pluginassembly/plugintype records per DLL, bounded retry.
        var postSnapshots = await LoadPackageSnapshotsWithRetryAsync(service, packageId, assemblies, solutionName, cancellationToken).ConfigureAwait(false);

        var primaryPost = postSnapshots.FirstOrDefault(t => string.Equals(t.Metadata.Name, projectAssemblyName, StringComparison.OrdinalIgnoreCase));
        if (primaryPost.Assembly == null)
            throw new InvalidOperationException($"Primary assembly '{projectAssemblyName}' was not found under package '{packageUniqueName}' after the content update.");

        await WritePackageAssemblyMarkerAsync(service, primaryPost.Assembly, hash, cancellationToken).ConfigureAwait(false);

        // Re-plan per assembly against the post-update snapshot (types have changed for any assembly
        // with removed classes) and run the remaining upserts/adds — deletes already ran above, and
        // Flowline never calls DeleteAsync("plugintype", ...) on this path (KD2/KD4/KTD13), so this
        // second pass intentionally never calls ExecuteDeletesAsync again.
        var postKnownPluginTypeIds = AllPluginTypeIds(postSnapshots);
        foreach (var (metadata, assemblyEntity, snapshot) in postSnapshots)
        {
            if (assemblyEntity == null || snapshot == null)
                throw new InvalidOperationException($"Assembly '{metadata.Name}' was not found under package '{packageUniqueName}' after the content update.");

            var plan = _planner.Plan(snapshot, metadata, assemblyEntity, solutionName, postKnownPluginTypeIds);
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
        CancellationToken cancellationToken)
    {
        var snapshots = await _reader.LoadPackageSnapshotsAsync(service, packageId, assemblies, solutionName, cancellationToken).ConfigureAwait(false);
        var knownPluginTypeIds = AllPluginTypeIds(snapshots);

        var anyChanges = false;
        foreach (var (metadata, assemblyEntity, snapshot) in snapshots)
        {
            if (assemblyEntity == null || snapshot == null) continue;

            var plan = _planner.Plan(snapshot, metadata, assemblyEntity, solutionName, knownPluginTypeIds);
            if (plan.TotalChanges == 0) continue;

            anyChanges = true;
            await _executor.ExecuteDeletesAsync(service, plan, solutionName, runMode == RunMode.NoDelete, cancellationToken).ConfigureAwait(false);
            await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken).ConfigureAwait(false);
            await _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken).ConfigureAwait(false);
        }

        if (anyChanges)
            console.Ok("Plugin package content unchanged — synced drifted step registration(s)");
        else
            console.Skip("Plugin package already up to date — skipping");

        return anyChanges;
    }

    // R6/KTD14: a handful of short, bounded 1-second polls — defense-in-depth for the untested case of
    // larger packages/slower environments, not a hedge against real observed latency (verified
    // synchronous in practice). Throws naming the still-missing assemblies if the budget is exceeded.
    async Task<IReadOnlyList<(PluginAssemblyMetadata Metadata, Entity? Assembly, RegistrationSnapshot? Snapshot)>> LoadPackageSnapshotsWithRetryAsync(
        IOrganizationServiceAsync2 service,
        Guid packageId,
        List<PluginAssemblyMetadata> assemblies,
        string solutionName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= PackageAssemblyCheckMaxAttempts; attempt++)
        {
            var snapshots = await _reader.LoadPackageSnapshotsAsync(service, packageId, assemblies, solutionName, cancellationToken).ConfigureAwait(false);
            if (snapshots.All(s => s.Assembly != null))
                return snapshots;

            if (attempt == PackageAssemblyCheckMaxAttempts)
            {
                var missing = string.Join(", ", snapshots.Where(s => s.Assembly == null).Select(s => s.Metadata.Name));
                throw new InvalidOperationException(
                    $"Timed out waiting for Dataverse to auto-create plugin assembly record(s) for: {missing}.");
            }

            await Task.Delay(PackageAssemblyCheckDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Unreachable.");
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

    async Task WarnOrphanAssembliesAsync(
        IOrganizationServiceAsync2 service,
        string managedAssemblyName,
        string solutionName,
        bool forceDeleteOrphans,
        RunMode runMode,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("pluginassemblyid", "name"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.NotEqual, managedAssemblyName) } }
        };
        var componentLink = query.AddLink("solutioncomponent", "pluginassemblyid", "objectid", JoinOperator.Inner);
        componentLink.LinkCriteria.AddCondition("componenttype", ConditionOperator.Equal, 91); // 91 = PluginAssembly
        var solutionLink = componentLink.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        foreach (var entity in result.Entities)
        {
            var name = entity.GetAttributeValue<string>("name");

            var willDelete = forceDeleteOrphans && runMode == RunMode.Normal;
            var showCascade = forceDeleteOrphans || runMode == RunMode.DryRun;

            console.Warning(willDelete
                ? $"[bold]{Safe(name)}.dll[/] in environment — no local source. Deleting."
                : $"[bold]{Safe(name)}.dll[/] in environment — no local source. Use --force delete-orphans to delete.");

            // Load snapshot for cascade display and/or explicit child deletion
            RegistrationSnapshot? orphanSnapshot = null;
            if (showCascade || willDelete)
            {
                // Stub metadata — skips SDK message/filter/user lookups (not needed here)
                var stub = new PluginAssemblyMetadata("", "", [], "", "", null, "", []);
                orphanSnapshot = await _reader.LoadSnapshotAsync(service, entity.Id, stub, solutionName, cancellationToken).ConfigureAwait(false);
            }

            if (showCascade && orphanSnapshot != null)
            {
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
                foreach (var e in orphanSnapshot.ResponseProps)
                    await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
                foreach (var e in orphanSnapshot.RequestParams)
                    await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
                foreach (var e in orphanSnapshot.Steps)
                    await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
                foreach (var e in orphanSnapshot.CustomApis)
                    await service.DeleteAsync(e.LogicalName, e.Id, cancellationToken).ConfigureAwait(false);
                foreach (var (_, pluginType) in orphanSnapshot.PluginTypes)
                    await service.DeleteAsync(pluginType.LogicalName, pluginType.Id, cancellationToken).ConfigureAwait(false);
                await service.DeleteAsync("pluginassembly", entity.Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Catches steps left behind after a plugin project rename: the old assembly (and its plugin
    // type) can end up removed from the solution entirely while its steps stay explicit solution
    // members, which fails a fresh-environment import with a missing PluginType dependency —
    // WarnOrphanAssembliesAsync above only catches this when the foreign assembly is itself still
    // a solution member.
    async Task WarnOrphanStepsAsync(
        IOrganizationServiceAsync2 service,
        string managedAssemblyName,
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
        asmLink.LinkCriteria.AddCondition("name", ConditionOperator.NotEqual, managedAssemblyName);

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
            ColumnSet = new ColumnSet("pluginassemblyid", "name", "version", "publickeytoken", "culture", "description"),
            Criteria =
            {
                Conditions = { new ConditionExpression("name", ConditionOperator.Equal, metadata.Name) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var existing = result.Entities.FirstOrDefault();

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
                throw new FlowlineException(ExitCode.ForceRequired, $"Assembly [bold]{metadata.Name}[/] {reasonText}. Use --force recreate-assembly to allow.");
            }

            // Load existing registrations before deletion to show what cascades
            var oldSnapshot = await _reader.LoadSnapshotAsync(service, existing.Id, metadata, solutionName, cancellationToken).ConfigureAwait(false);
            var cascadeDeleteCount = oldSnapshot.PluginTypes.Count + oldSnapshot.Steps.Count + oldSnapshot.Images.Count;

            switch (runMode)
            {
                case RunMode.DryRun:
                    var blockNote = !forceRecreateAssembly ? " — would be blocked without --force recreate-assembly" : "";
                    console.Warning($"Assembly [bold]{metadata.Name}[/] identity changed ({reason}){blockNote} — would delete and recreate");
                    WriteCascadePreview(oldSnapshot);
                    return (new Entity("pluginassembly") { Id = Guid.NewGuid() }, false, cascadeDeleteCount);
                case RunMode.NoDelete:
                    console.Error($"Assembly [bold]{metadata.Name}[/] identity changed ({reason}) — Dataverse needs a delete and recreate. Re-run without --no-delete to apply, or use --dry-run to preview.");
                    throw new InvalidOperationException($"Assembly [bold]{metadata.Name}[/] identity changed ({reason}). Cannot continue in no-delete mode — re-run without --no-delete to apply, or use --dry-run to preview.");
                case RunMode.Normal:
                    var forceNote = isDowngrade ? " (version downgrade, --force recreate-assembly)" : " (--force recreate-assembly)";
                    console.Warning($"Assembly [bold]{metadata.Name}[/] identity changed ({reason}){forceNote} — deleting and recreating all registrations");
                    WriteCascadeNormal(oldSnapshot);
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

    void WriteCascadePreview(RegistrationSnapshot snapshot)
    {
        foreach (var name in snapshot.PluginTypes.Keys)
            console.Info($"  [red]-[/] Plugin type '{name}' — would delete (cascade)");
        foreach (var step in snapshot.Steps)
            console.Info($"  [red]-[/] Step '{step.GetAttributeValue<string>("name")}' — would delete (cascade)");
        foreach (var image in snapshot.Images)
            console.Info($"  [red]-[/] Image '{image.GetAttributeValue<string>("name")}' — would delete (cascade)");
    }

    void WriteCascadeNormal(RegistrationSnapshot snapshot)
    {
        foreach (var name in snapshot.PluginTypes.Keys)
            console.Info($"Plugin type '{name}' — cascade delete");
        foreach (var step in snapshot.Steps)
            console.Info($"Step '{step.GetAttributeValue<string>("name")}' — cascade delete");
        foreach (var image in snapshot.Images)
            console.Info($"Image '{image.GetAttributeValue<string>("name")}' — cascade delete");
    }

    void WritePlanTree(PluginAssemblyMetadata metadata, bool needsUpdate, RegistrationPlan plan, RunMode runMode, int cascadeDeleteCount = 0)
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

        // --- Unlinked Custom APIs (no plugin type) ---
        var unlinkedApiGroups = plan.CustomApiGroups.Where(g => g.PluginTypeName == null).ToList();
        if (unlinkedApiGroups.Count > 0)
        {
            var unlinkedNode = tree.AddNode("[dim]Custom APIs (unlinked)[/]");
            foreach (var group in unlinkedApiGroups.OrderBy(g => g.ApiName, StringComparer.OrdinalIgnoreCase))
            {
                IHasTreeNodes apiNode;
                if (group.Api.Deletes.Count == 1 && group.Api.Upserts.Count == 0)
                {
                    var d = group.Api.Deletes[0];
                    apiNode = unlinkedNode.AddNode($"{Sym(true, false)} [dim]api[/] {Safe(d.Name)} — {Verb(true, false)}");
                }
                else
                {
                    apiNode = unlinkedNode.AddNode($"[dim]{Safe(group.ApiName)}[/]");
                    foreach (var d in group.Api.Deletes)
                        apiNode.AddNode($"{Sym(true, false)} [dim]api[/] {Safe(d.Name)} — {Verb(true, false)}");
                }

                foreach (var d in group.RequestParams.Deletes.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    apiNode.AddNode($"{Sym(true, false)} [dim]req[/] {Safe(d.Name)} — {Verb(true, false)}");
                foreach (var d in group.ResponseProps.Deletes.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    apiNode.AddNode($"{Sym(true, false)} [dim]res[/] {Safe(d.Name)} — {Verb(true, false)}");
            }
        }

        if (runMode == RunMode.DryRun)
        {
            console.Write(tree);
            var creates = plan.PluginTypes.Upserts.Count(u => u.IsCreate)
                          + plan.Steps.Upserts.Count(u => u.IsCreate)
                          + plan.CustomApis.Upserts.Count(u => u.IsCreate)
                          + plan.Images.Upserts.Count(u => u.IsCreate)
                          + plan.RequestParams.Upserts.Count(u => u.IsCreate)
                          + plan.ResponseProps.Upserts.Count(u => u.IsCreate);
            var updates = plan.TotalUpserts - creates;
            console.Ok($"Dry run: {plan.TotalDeletes + cascadeDeleteCount} delete(s), {creates} create(s), {updates} update(s). Run without --dry-run to apply.");
        }
        else
        {
            console.Verbose(tree);
        }
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
        AddUnlinkedNodes(tree, "Unlinked Custom APIs", snapshot.CustomApis,
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
    // to PluginPlanner.Plan so its "unlinked Custom API" sweep can tell a sibling assembly's still-live
    // Custom API (plugintypeid belongs to another assembly in this same package) apart from a genuinely
    // orphaned one. snapshot.CustomApis is queried by publisher prefix, not per-assembly, so without this
    // every assembly's plan would otherwise see every OTHER assembly's Custom API as unowned and delete it.
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
        plan.PluginTypes.AddSolutionComponents.Count
        + plan.Steps.AddSolutionComponents.Count
        + plan.Images.AddSolutionComponents.Count
        + plan.CustomApis.AddSolutionComponents.Count
        + plan.RequestParams.AddSolutionComponents.Count
        + plan.ResponseProps.AddSolutionComponents.Count;

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
