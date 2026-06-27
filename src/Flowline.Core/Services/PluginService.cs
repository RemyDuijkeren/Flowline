using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class PluginService(IAnsiConsole output, FlowlineRuntimeOptions opt, ILogger<PluginService> logger)
{
    const string FlowlineMarker = "[flowline]";

    readonly PluginReader _reader = new();
    readonly PluginPlanner _planner = new(output, opt.IsVerbose);
    readonly PluginExecutor _executor = new(output, opt.IsVerbose);
    readonly SolutionReader _solutionReader = new();
    readonly PluginAssemblyReader _assemblyReader = new(output, opt.IsVerbose);

    public async Task SyncAssemblyOnlyAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            throw new ArgumentException("dllPath is required and cannot be empty.", nameof(dllPath));

        var metadata = output.Status().Start("Analyzing plugin assembly...", _ => _assemblyReader.Analyze(dllPath));
        await SyncAssemblyOnlyAsync(service, metadata, solutionName, runMode, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SyncAssemblyOnlyAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        await output.Status()
                    .StartAsync($"Looking up solution [bold]{solutionName}[/]...",
                        _ => _solutionReader.GetSupportedSolutionInfoAsync(service, solutionName, cancellationToken))
                    .ConfigureAwait(false);
        output.Info("Solution found and supported");

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
        if (identityChanges != null)
            throw new InvalidOperationException($"Assembly '{metadata.Name}' identity changed ({string.Join(", ", identityChanges)}) — cannot update assembly-only. Run push without --scope assemblyonly to delete and recreate registrations.");

        var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
        if (storedHash == metadata.Hash)
        {
            output.Skip("Assembly already up to date — skipping");
            return;
        }

        if (runMode == RunMode.DryRun)
        {
            output.Info($"  [yellow]~[/] Assembly '{metadata.Name} ({metadata.Version})' — would update content");
            output.Ok("Dry run: 1 update. Run without --dry-run to apply.");
            return;
        }

        await output.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Updating plugin assembly", maxValue: 1);
                await UpdateAssemblyContentAsync(service, existing, metadata, cancellationToken).ConfigureAwait(false);
                task.Increment(1);
            })
            .ConfigureAwait(false);
        output.Ok($"Assembly [bold]{metadata.Name}[/] ({metadata.Version}) updated");
    }

    public async Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            throw new ArgumentException("dllPath is required and cannot be empty.", nameof(dllPath));

        var metadata = output.Status().Start("Analyzing plugin assembly...", ctx => _assemblyReader.Analyze(dllPath));
        await SyncSolutionAsync(service, metadata, solutionName, runMode, force, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        // Phase 0: Check if solution exists and is supported
        await output.Status()
                    .StartAsync($"Looking up solution [bold]{solutionName}[/]...",
                        _ => _solutionReader.GetSupportedSolutionInfoAsync(service, solutionName, cancellationToken))
                    .ConfigureAwait(false);
        output.Info("Solution found and supported");

        // Phase 1: Get or register assembly
        var (assembly, needsUpdate, cascadeDeleteCount) = await GetOrRegisterAssemblyAsync(service, metadata, solutionName, runMode, force, cancellationToken).ConfigureAwait(false);
        output.Ok($"Assembly registered [bold]{metadata.Name}[/] ({metadata.Version})");
        logger.LogInformation("Assembly synced: {Name}", metadata.Name);

        await WarnOrphanAssembliesAsync(service, metadata.Name, solutionName, force, runMode, cancellationToken).ConfigureAwait(false);

        // Phase 2: Load snapshot (all Dataverse state in parallel)
        var snapshot = await output.Status()
            .StartAsync("Loading plugin registration snapshot...", _ => _reader.LoadSnapshotAsync(service, assembly.Id, metadata, solutionName, cancellationToken))
            .ConfigureAwait(false);
        WriteSnapshotVerbose(snapshot);
        output.Info("Snapshot plugins loaded");

        // Phase 3: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot, metadata, assembly, solutionName);
        output.Info("Registration plan ready");
        logger.LogInformation("Registration plan ready: {PluginTypeCount} plugin types, {StepCount} steps",
            plan.PluginTypes.Upserts.Count + plan.PluginTypes.Deletes.Count,
            plan.Steps.Upserts.Count + plan.Steps.Deletes.Count);

        foreach (var warning in plan.Warnings)
            output.Warning(warning);

        if (needsUpdate && snapshot.ComponentSolutionMembership.TryGetValue(assembly.Id, out var assemblyMembership))
        {
            var otherSolutions = assemblyMembership
                .Where(s => !string.Equals(s, solutionName, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (otherSolutions.Count > 0)
                output.Warning($"Updating assembly '{metadata.Name}' which also exists in other solutions: {string.Join(", ", otherSolutions)}.");
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
            return;

        if (!needsUpdate && plan.TotalChanges == 0)
        {
            output.Skip("Plugins already up to date — skipping");
            return;
        }

        // Phase 4: Execute the deletes first — must precede assembly update and upserts
        if (runMode == RunMode.NoDelete || plan.TotalDeletes == 0)
        {
            await _executor.ExecuteDeletesAsync(service, plan, solutionName, runMode == RunMode.NoDelete, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await output.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Deleting stale plugin components", maxValue: plan.TotalDeletes);
                    await _executor.ExecuteDeletesAsync(service, plan, solutionName, false, cancellationToken, task).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        if (plan.TotalDeletes > 0) output.Ok($"{plan.TotalDeletes} stale component(s) deleted");

        // Phase 5: Update assembly content — must happen before new plugin types are registered
        if (needsUpdate)
        {
            await output.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Updating plugin assembly", maxValue: 1);
                    await UpdateAssemblyContentAsync(service, assembly, metadata, cancellationToken).ConfigureAwait(false);
                    task.Increment(1);
                })
                .ConfigureAwait(false);
            output.Ok($"Updated assembly content for [bold]{metadata.Name}[/]");
        }

        // Phase 6: Execute upserts and add to solution
        if (plan.TotalUpserts > 0)
        {
            await output.Progress()
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
        if (plan.TotalUpserts > 0) output.Ok($"{plan.TotalUpserts} component(s) synced");

        var addToSolutionCount = CountAddToSolutionComponents(plan);
        if (addToSolutionCount > 0)
        {
            await output.Progress()
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
    }

    async Task WarnOrphanAssembliesAsync(
        IOrganizationServiceAsync2 service,
        string managedAssemblyName,
        string solutionName,
        bool force,
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

            var willDelete = force && runMode == RunMode.Normal;
            var showCascade = force || runMode == RunMode.DryRun;

            output.Warning(willDelete
                ? $"[bold]{Safe(name)}.dll[/] in environment — no local source. Deleting."
                : $"[bold]{Safe(name)}.dll[/] in environment — no local source. Use --force to delete.");

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
                    output.Info(willDelete
                        ? $"  {Safe(typeName)} — cascade delete"
                        : $"  [red]-[/] {Safe(typeName)} — would delete (cascade)");
                foreach (var step in orphanSnapshot.Steps)
                    output.Info(willDelete
                        ? $"  {Safe(step.GetAttributeValue<string>("name"))} — cascade delete"
                        : $"  [red]-[/] {Safe(step.GetAttributeValue<string>("name"))} — would delete (cascade)");
                foreach (var image in orphanSnapshot.Images)
                    output.Info(willDelete
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

    async Task<(Entity entity, bool needsUpdate, int cascadeDeleteCount)> GetOrRegisterAssemblyAsync(
        IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName, RunMode runMode, bool force = false, CancellationToken cancellationToken = default)
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
                output.Info($"  [green]+[/] Assembly '{metadata.Name}' — would create");
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

            output.Ok($"Assembly [bold]{metadata.Name}[/] added");

            entity.Id = response.id;
            return (entity, false, 0);
        }

        var identityChanges = DetectIdentityChanges(existing, metadata);
        if (identityChanges != null)
        {
            var reason = string.Join(", ", identityChanges);
            var isDowngrade = IsVersionDowngrade(existing, metadata);

            if (isDowngrade && !force && runMode == RunMode.Normal)
            {
                output.Error($"Assembly '{metadata.Name}' version downgraded ({reason}) — Dataverse needs a delete and recreate. Use --force to allow downgrade.");
                throw new FlowlineException(ExitCode.ForceRequired, $"Assembly '{metadata.Name}' version downgraded ({reason}). Use --force to allow.");
            }

            // Load existing registrations before deletion to show what cascades
            var oldSnapshot = await _reader.LoadSnapshotAsync(service, existing.Id, metadata, solutionName, cancellationToken).ConfigureAwait(false);
            var cascadeDeleteCount = oldSnapshot.PluginTypes.Count + oldSnapshot.Steps.Count + oldSnapshot.Images.Count;

            switch (runMode)
            {
                case RunMode.DryRun:
                    var downgradeNote = isDowngrade ? " — would be blocked without --force" : "";
                    output.Warning($"Assembly '{metadata.Name}' identity changed ({reason}){downgradeNote} — would delete and recreate");
                    WriteCascadePreview(oldSnapshot);
                    return (new Entity("pluginassembly") { Id = Guid.NewGuid() }, false, cascadeDeleteCount);
                case RunMode.NoDelete:
                    output.Error($"Assembly '{metadata.Name}' identity changed ({reason}) — Dataverse needs a delete and recreate. Re-run without --no-delete to apply, or use --dry-run to preview.");
                    throw new InvalidOperationException($"Assembly '{metadata.Name}' identity changed ({reason}). Cannot continue in no-delete mode — re-run without --no-delete to apply, or use --dry-run to preview.");
                case RunMode.Normal:
                    var forceNote = isDowngrade ? " (version downgrade, --force)" : "";
                    output.Warning($"Assembly '{metadata.Name}' identity changed ({reason}){forceNote} — deleting and recreating all registrations");
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
            output.Ok($"Assembly [bold]{metadata.Name}[/] recreated");
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
            .WithDetail(console =>
            {
                foreach (var (typeName, assemblyName) in conflicts)
                    console.MarkupLine($"  [yellow]{Safe(typeName)}[/] already registered in [bold]{Safe(assemblyName)}[/]");
            });
    }

    void WriteCascadePreview(RegistrationSnapshot snapshot)
    {
        foreach (var name in snapshot.PluginTypes.Keys)
            output.Info($"  [red]-[/] Plugin type '{name}' — would delete (cascade)");
        foreach (var step in snapshot.Steps)
            output.Info($"  [red]-[/] Step '{step.GetAttributeValue<string>("name")}' — would delete (cascade)");
        foreach (var image in snapshot.Images)
            output.Info($"  [red]-[/] Image '{image.GetAttributeValue<string>("name")}' — would delete (cascade)");
    }

    void WriteCascadeNormal(RegistrationSnapshot snapshot)
    {
        foreach (var name in snapshot.PluginTypes.Keys)
            output.Info($"Plugin type '{name}' — cascade delete");
        foreach (var step in snapshot.Steps)
            output.Info($"Step '{step.GetAttributeValue<string>("name")}' — cascade delete");
        foreach (var image in snapshot.Images)
            output.Info($"Image '{image.GetAttributeValue<string>("name")}' — cascade delete");
    }

    void WritePlanTree(PluginAssemblyMetadata metadata, bool needsUpdate, RegistrationPlan plan, RunMode runMode, int cascadeDeleteCount = 0)
    {
        if (runMode != RunMode.DryRun && !opt.IsVerbose) return;

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
        // Steps use the fully-qualified class name; type actions may use only the short name.
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
                    var imgType = itype == "0" ? "preimg" : itype == "1" ? "postimg" : "img";
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
                        apiNode.AddNode($"{Sym(true, false)} [dim]req[/] {Safe(d.Name)} — {Verb(true, false)}");
                    foreach (var u in group.RequestParams.Upserts.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
                        apiNode.AddNode($"{Sym(false, u.IsCreate)} [dim]req[/] {Safe(u.Name)} [dim]type={OptionValue(u.Entity, "type")} optional={BoolValue(u.Entity, "isoptional")}[/] — {Verb(false, u.IsCreate)}");
                    foreach (var d in group.ResponseProps.Deletes.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                        apiNode.AddNode($"{Sym(true, false)} [dim]res[/] {Safe(d.Name)} — {Verb(true, false)}");
                    foreach (var u in group.ResponseProps.Upserts.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
                        apiNode.AddNode($"{Sym(false, u.IsCreate)} [dim]res[/] {Safe(u.Name)} [dim]type={OptionValue(u.Entity, "type")}[/] — {Verb(false, u.IsCreate)}");
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

        output.Write(tree);

        if (runMode == RunMode.DryRun)
        {
            var creates = plan.PluginTypes.Upserts.Count(u => u.IsCreate)
                          + plan.Steps.Upserts.Count(u => u.IsCreate)
                          + plan.CustomApis.Upserts.Count(u => u.IsCreate)
                          + plan.Images.Upserts.Count(u => u.IsCreate)
                          + plan.RequestParams.Upserts.Count(u => u.IsCreate)
                          + plan.ResponseProps.Upserts.Count(u => u.IsCreate);
            var updates = plan.TotalUpserts - creates;
            output.Ok($"Dry run: {plan.TotalDeletes + cascadeDeleteCount} delete(s), {creates} create(s), {updates} update(s). Run without --dry-run to apply.");
        }
    }

    void WriteSnapshotVerbose(RegistrationSnapshot snapshot)
    {
        if (!opt.IsVerbose) return;

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

        output.Write(tree);
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
