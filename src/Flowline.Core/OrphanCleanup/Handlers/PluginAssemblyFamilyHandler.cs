using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Detects PluginAssembly (91) / PluginType (90) / Step (92) / StepImage (93), classifies each into
// Prio1/Prio2/Prio3, and ships Active.
//
// Both live queries (name resolution and enabled-state) catch and degrade rather than propagate — a
// transient Dataverse fault must not abort the whole deploy. FaultException quietly skips, anything
// else warns and skips, falling back to each query's "unresolved" display/Prio path.
public sealed class PluginAssemblyFamilyHandler(IAnsiConsole console) : IOrphanHandler
{
    public HandlerStatus Status => HandlerStatus.Active;

    // Same entityLogicalName/idAttribute/nameAttribute triples as OrphanCleanupService.NameResolvableTypes'
    // 91/90/92/93 rows, used for live display-name resolution.
    static readonly Dictionary<int, (string EntityLogicalName, string IdAttribute, string NameAttribute)> Lookups = new()
    {
        [91] = ("pluginassembly", "pluginassemblyid", "name"),
        [90] = ("plugintype", "plugintypeid", "typename"),
        [92] = ("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "name"),
        [93] = ("sdkmessageprocessingstepimage", "sdkmessageprocessingstepimageid", "name"),
    };

    // Per-family SequenceHint — deepest child executes first (StepImage = 0) through shallowest parent
    // last (PluginAssembly = 3).
    static readonly Dictionary<int, int> SequenceHints = new()
    {
        [93] = 0, // StepImage
        [92] = 1, // Step
        [90] = 2, // PluginType
        [91] = 3, // PluginAssembly
    };

    static readonly Dictionary<int, string> TypeLabels = new()
    {
        [91] = "PluginAssembly",
        [90] = "PluginType",
        [92] = "SdkMessageProcessingStep",
        [93] = "SdkMessageProcessingStepImage",
    };

    // The redirected pluginpackage-delete finding stays in this family (SequenceHints[91] = 3), but a
    // bound CustomApi is normally detected later by CustomApiFamilyHandler, so it would execute AFTER
    // the package delete. Dataverse rejects a pluginpackage delete while a CustomApi still references it
    // — these hints pull the CustomApi cleanup into this family's own ordering instead, below slot 3.
    const int CustomApiChildSequenceHint = 1; // CustomApiRequestParameter / CustomApiResponseProperty
    const int CustomApiParentSequenceHint = 2; // CustomApi itself

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        var claimed = candidates.Where(c => Lookups.ContainsKey(c.ComponentType)).ToList();
        if (claimed.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        // Every candidate matching this family's gate is claimed regardless of Prio — never suppressed
        // out of Findings.
        var claimedIds = claimed.Select(c => c.ObjectId).ToHashSet();

        var names = await ResolveNamesAsync(context.Service, claimed, console, ct).ConfigureAwait(false);

        // A pluginassembly owned by a pluginpackage can't be deleted directly — live-check packageid so
        // BuildAllFindings can redirect to a pluginpackage-delete finding instead of one that fails at
        // execute time.
        var assemblyIds = claimed.Where(c => c.ComponentType == 91).Select(c => c.ObjectId).ToList();
        var packageIds = assemblyIds.Count > 0
            ? await ResolvePackageIdsAsync(context.Service, assemblyIds, console, ct).ConfigureAwait(false)
            : new Dictionary<Guid, EntityReference>();

        // Only candidates already in this run's batch are touched — a CustomApi/param/prop this query
        // finds that ISN'T already an orphan candidate is still validly declared locally.
        var localCustomApiNames = ComponentClassifier.ScanCustomApiNames(context.DataverseSolutionSrcRoot);
        var (childCleanupFindings, childCleanupDegraded) = packageIds.Count > 0
            ? await ResolvePackageChildCleanupFindingsAsync(context.Service, packageIds.Keys, candidates, claimedIds, localCustomApiNames, console, ct).ConfigureAwait(false)
            : ([], false);

        // A transient fault partway through ResolvePackageChildCleanupFindingsAsync must not leave the
        // package-delete finding as if cleanup were confirmed complete — when degraded, every
        // currently-redirected package is skipped entirely this run and picked up again once the lookup
        // succeeds.
        var skipRedirectedFindingsThisRun = childCleanupDegraded;

        // RunMode.NoDelete/DryRun is the only signal knowable at classify time — the
        // reactively-deferred/still-blocking-at-post-import case is not implemented by this handler.
        // U5/KTD2: DryRun forces the same blanket Prio1 as NoDelete, so a dry-run preview shows the exact
        // priority grouping a real (managed -> NoDelete) deploy would, instead of taking the live
        // enabled-state query branch below.
        if (context.Mode is RunMode.NoDelete or RunMode.DryRun)
            return new HandlerDetectionResult(
                BuildAllFindings(claimed, names, packageIds, _ => OrphanPriority.Prio1, skipRedirectedFindingsThisRun)
                    .Concat(childCleanupFindings.Select(f => f with { Priority = OrphanPriority.Prio1 }))
                    .ToList(),
                claimedIds);

        // Prio2 applies only to PluginType and Step ("the live PluginType/Step is Enabled") — StepImage
        // and PluginAssembly have no Enabled concept of their own and default to Prio3.
        var stepIds = claimed.Where(c => c.ComponentType == 92).Select(c => c.ObjectId).ToList();
        var typeIds = claimed.Where(c => c.ComponentType == 90).Select(c => c.ObjectId).ToList();

        var (stepEnabled, typeHasEnabledStep) = stepIds.Count > 0 || typeIds.Count > 0
            ? await QueryEnabledStateAsync(context.Service, stepIds, typeIds, console, ct).ConfigureAwait(false)
            : (new Dictionary<Guid, bool>(), new HashSet<Guid>());

        OrphanPriority PriorityFor((Guid ObjectId, int ComponentType) candidate) => candidate.ComponentType switch
        {
            92 => stepEnabled.TryGetValue(candidate.ObjectId, out var enabled) && enabled
                ? OrphanPriority.Prio2 : OrphanPriority.Prio3,
            90 => typeHasEnabledStep.Contains(candidate.ObjectId)
                ? OrphanPriority.Prio2 : OrphanPriority.Prio3,
            _ => OrphanPriority.Prio3,
        };

        var findings = BuildAllFindings(claimed, names, packageIds, PriorityFor, skipRedirectedFindingsThisRun)
            .Concat(childCleanupFindings)
            .ToList();

        return new HandlerDetectionResult(findings, claimedIds);
    }

    // Builds one HandlerFinding per candidate, redirecting PluginAssembly candidates with a resolved
    // packageid to the parent pluginpackage instead. Unresolved candidates keep the unchanged
    // assembly-delete finding via BuildFinding.
    static List<HandlerFinding> BuildAllFindings(
        List<(Guid ObjectId, int ComponentType)> claimed,
        Dictionary<Guid, string> names,
        Dictionary<Guid, EntityReference> packageIds,
        Func<(Guid ObjectId, int ComponentType), OrphanPriority> priorityFor,
        bool skipRedirectedFindings = false)
    {
        var findings = new List<HandlerFinding>(claimed.Count);
        var emittedPackageIds = new HashSet<Guid>();

        foreach (var candidate in claimed)
        {
            var priority = priorityFor(candidate);

            if (candidate.ComponentType == 91 && packageIds.TryGetValue(candidate.ObjectId, out var packageRef))
            {
                // A degraded child-cleanup lookup means we can't confirm blocking CustomApi/steps were
                // scheduled for deletion first — skip entirely rather than risk a delete that fails or
                // leaves referencing children uncleaned.
                if (skipRedirectedFindings) continue;

                // Multiple orphaned assemblies sharing the same parent package collapse to one
                // package-delete finding, not one per assembly.
                if (!emittedPackageIds.Add(packageRef.Id)) continue;

                var assemblyDisplay = TypeName(candidate.ComponentType, candidate.ObjectId, names.TryGetValue(candidate.ObjectId, out var name) ? name : null);
                findings.Add(new HandlerFinding(
                    packageRef.Id,
                    candidate.ComponentType,
                    $"PluginPackage {packageRef.Id} (owns {assemblyDisplay})",
                    OrphanAction.Delete,
                    priority,
                    SequenceHints[candidate.ComponentType],
                    OrphanTiming.PreImportEligible,
                    "pluginpackage"));
                continue;
            }

            findings.Add(BuildFinding(candidate, names, priority));
        }

        return findings;
    }

    static HandlerFinding BuildFinding((Guid ObjectId, int ComponentType) candidate, Dictionary<Guid, string> names, OrphanPriority priority)
    {
        var detail = names.TryGetValue(candidate.ObjectId, out var name) ? name : null;
        var displayName = TypeName(candidate.ComponentType, candidate.ObjectId, detail);
        return new HandlerFinding(candidate.ObjectId, candidate.ComponentType, displayName, OrphanAction.Delete, priority, SequenceHints[candidate.ComponentType], OrphanTiming.PreImportEligible);
    }

    // Batched live check of packageid on each PluginAssembly candidate — a transient failure degrades
    // every candidate in this batch to the un-redirected assembly-delete finding rather than aborting
    // detection for the whole family.
    static Task<Dictionary<Guid, EntityReference>> ResolvePackageIdsAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<Guid> assemblyIds,
        IAnsiConsole console,
        CancellationToken ct) =>
        DataverseFaultTolerance.TryQueryAsync(async () =>
        {
            var query = new QueryExpression("pluginassembly")
            {
                ColumnSet = new ColumnSet("packageid"),
                Criteria = { Conditions = { new ConditionExpression("pluginassemblyid", ConditionOperator.In, assemblyIds.Select(id => (object)id).ToArray()) } }
            };
            var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

            var result = new Dictionary<Guid, EntityReference>();
            foreach (var entity in entities)
            {
                var packageRef = entity.GetAttributeValue<EntityReference>("packageid");
                if (packageRef != null)
                    result[entity.Id] = packageRef;
            }
            return result;
        }, [], console, msg => $"pluginassembly packageid lookup failed ({msg}) — degrading to un-redirected assembly-delete finding this run.");

    // Dataverse's practical ConditionOperator.In ceiling — same limit EntityNameLookup.cs centralizes.
    // QueryChildIdsAsync enforces it directly (rather than delegating to EntityNameLookup) since it needs
    // ColumnSet(false) id-only queries, which EntityNameLookup doesn't support.
    const int ConditionOperatorInLimit = 2000;

    // Pulls any CustomApi (and its RequestParameter/ResponseProperty children) bound to a redirected
    // assembly's plugin types into this family's own findings, ordered ahead of the package-delete slot
    // instead of leaving them to CustomApiFamilyHandler's later-executing pass. See
    // CustomApiChildSequenceHint.
    //
    // Returns whether any query degraded alongside the findings — the caller must not treat a degraded
    // run as "cleanup confirmed complete" (see DetectAsync's skipRedirectedFindingsThisRun).
    static async Task<(List<HandlerFinding> Findings, bool Degraded)> ResolvePackageChildCleanupFindingsAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyCollection<Guid> redirectedAssemblyIds,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        HashSet<Guid> claimedIds,
        CustomApiNames localCustomApiNames,
        IAnsiConsole console,
        CancellationToken ct)
    {
        var (pluginTypeIds, customApiIds, requestParamIds, responsePropIds, degraded) =
            await ResolveCascadedChildIdsAsync(service, redirectedAssemblyIds, console, ct).ConfigureAwait(false);

        if (pluginTypeIds.Count == 0 || customApiIds.Count == 0)
            return ([], degraded);

        var names = await ResolveChildNamesAsync(service, customApiIds, requestParamIds, responsePropIds, console, ct).ConfigureAwait(false);

        var candidateComponentTypes = candidates.ToDictionary(c => c.ObjectId, c => c.ComponentType);
        var findings = new List<HandlerFinding>();

        // CustomApi (and its children) has no GUID in local source — uniquename is the only local
        // identity. A CustomApi recreated with a new id under an unchanged uniquename must not be
        // claimed here; CustomApiFamilyHandler's normal path already protects this case but never runs
        // once this handler claims the id first.
        void AddIfOrphaned(IEnumerable<Guid> ids, string entityLogicalName, string displayLabel, int sequenceHint, IReadOnlySet<string> localNames)
        {
            foreach (var id in ids)
            {
                // Only an id already present in this run's orphan candidates gets touched — otherwise
                // it's still validly declared locally and this handler must leave it alone.
                if (!candidateComponentTypes.TryGetValue(id, out var componentType)) continue;

                var name = names.TryGetValue(id, out var n) ? n : null;
                if (name != null && localNames.Contains(name)) continue; // still declared locally — not orphaned

                if (!claimedIds.Add(id)) continue; // already claimed by something else this run

                findings.Add(new HandlerFinding(
                    id, componentType,
                    name != null ? $"{displayLabel} '{name}' ({id})" : $"{displayLabel} {id}",
                    OrphanAction.Delete, OrphanPriority.Prio2, sequenceHint, OrphanTiming.PreImportEligible, entityLogicalName));
            }
        }

        AddIfOrphaned(requestParamIds, "customapirequestparameter", "CustomApiRequestParameter", CustomApiChildSequenceHint, localCustomApiNames.RequestParameterNames);
        AddIfOrphaned(responsePropIds, "customapiresponseproperty", "CustomApiResponseProperty", CustomApiChildSequenceHint, localCustomApiNames.ResponsePropertyNames);
        AddIfOrphaned(customApiIds, "customapi", "CustomApi", CustomApiParentSequenceHint, localCustomApiNames.ApiUniqueNames);

        return (findings, degraded);
    }

    // The 4-level cascade behind ResolvePackageChildCleanupFindingsAsync: pluginType -> customApi ->
    // (requestParameter, responseProperty). Each level short-circuits on an empty parent set instead of
    // querying — a redirected assembly with no plugin types has nothing further to look up.
    static async Task<(HashSet<Guid> PluginTypeIds, HashSet<Guid> CustomApiIds, HashSet<Guid> RequestParamIds, HashSet<Guid> ResponsePropIds, bool Degraded)>
        ResolveCascadedChildIdsAsync(
            IOrganizationServiceAsync2 service,
            IReadOnlyCollection<Guid> redirectedAssemblyIds,
            IAnsiConsole console,
            CancellationToken ct)
    {
        var degraded = false;

        Task<HashSet<Guid>> QueryChildIdsAsync(string entityLogicalName, string filterAttribute, IReadOnlyCollection<Guid> parentIds)
        {
            if (parentIds.Count == 0) return Task.FromResult(new HashSet<Guid>());

            return DataverseFaultTolerance.TryQueryAsync(async () =>
            {
                if (parentIds.Count > ConditionOperatorInLimit)
                    throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {parentIds.Count} IDs (max {ConditionOperatorInLimit}). Package has too many {entityLogicalName} candidates for cleanup this run.");

                var query = new QueryExpression(entityLogicalName)
                {
                    ColumnSet = new ColumnSet(false),
                    Criteria = { Conditions = { new ConditionExpression(filterAttribute, ConditionOperator.In, parentIds.Select(id => (object)id).ToArray()) } }
                };
                var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
                return entities.Select(e => e.Id).ToHashSet();
            }, [], console, msg => $"{entityLogicalName} lookup for package-delete cleanup failed ({msg}) — left for a future run.", onFault: () => degraded = true);
        }

        var pluginTypeIds = await QueryChildIdsAsync("plugintype", "pluginassemblyid", redirectedAssemblyIds).ConfigureAwait(false);
        if (pluginTypeIds.Count == 0) return (pluginTypeIds, [], [], [], degraded);

        var customApiIds = await QueryChildIdsAsync("customapi", "plugintypeid", pluginTypeIds).ConfigureAwait(false);
        if (customApiIds.Count == 0) return (pluginTypeIds, customApiIds, [], [], degraded);

        var requestParamIds = await QueryChildIdsAsync("customapirequestparameter", "customapiid", customApiIds).ConfigureAwait(false);
        var responsePropIds = await QueryChildIdsAsync("customapiresponseproperty", "customapiid", customApiIds).ConfigureAwait(false);

        return (pluginTypeIds, customApiIds, requestParamIds, responsePropIds, degraded);
    }

    static async Task<Dictionary<Guid, string>> ResolveChildNamesAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyCollection<Guid> customApiIds,
        IReadOnlyCollection<Guid> requestParamIds,
        IReadOnlyCollection<Guid> responsePropIds,
        IAnsiConsole console,
        CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var (entityLogicalName, idAttribute, ids) in new[]
                 {
                     ("customapi", "customapiid", customApiIds),
                     ("customapirequestparameter", "customapirequestparameterid", requestParamIds),
                     ("customapiresponseproperty", "customapiresponsepropertyid", responsePropIds),
                 })
        {
            var resolved = await DataverseFaultTolerance.TryQueryAsync(
                () => EntityNameLookup.GetEntityNamesAsync(service, entityLogicalName, idAttribute, "name", ids, ct),
                [], console, msg => $"{entityLogicalName} name resolution failed ({msg}) — display falls back to bare id this run.");
            foreach (var (id, name) in resolved)
                names[id] = name;
        }
        return names;
    }

    static string TypeName(int componentType, Guid objectId, string? detail) =>
        detail != null ? $"{TypeLabels[componentType]} '{detail}' ({objectId})" : $"{TypeLabels[componentType]} {objectId}";

    static async Task<Dictionary<Guid, string>> ResolveNamesAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> claimed,
        IAnsiConsole console,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();

        foreach (var group in claimed.GroupBy(c => c.ComponentType))
        {
            var lookup = Lookups[group.Key];
            var names = await DataverseFaultTolerance.TryQueryAsync(
                () => EntityNameLookup.GetEntityNamesAsync(service, lookup.EntityLogicalName, lookup.IdAttribute, lookup.NameAttribute, group.Select(c => c.ObjectId), ct),
                [], console, msg => $"{lookup.EntityLogicalName} name resolution failed ({msg}) — display falls back to bare id this run.");
            foreach (var (id, name) in names)
                result[id] = name;
        }

        return result;
    }

    // Single query resolves both Step's own statecode (Prio2 when Enabled) and PluginType's
    // has-any-enabled-step check, since PluginType carries no statecode of its own.
    // Unresolved enabled-state defaults Step/PluginType to Prio3, same as an empty result set (record
    // already gone).
    static Task<(Dictionary<Guid, bool> StepEnabled, HashSet<Guid> TypeHasEnabledStep)> QueryEnabledStateAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<Guid> stepIds,
        IReadOnlyList<Guid> typeIds,
        IAnsiConsole console,
        CancellationToken ct) =>
        DataverseFaultTolerance.TryQueryAsync(async () =>
        {
            var filter = new FilterExpression(LogicalOperator.Or);
            if (stepIds.Count > 0)
                filter.Conditions.Add(new ConditionExpression("sdkmessageprocessingstepid", ConditionOperator.In, stepIds.Select(id => (object)id).ToArray()));
            if (typeIds.Count > 0)
                filter.Conditions.Add(new ConditionExpression("plugintypeid", ConditionOperator.In, typeIds.Select(id => (object)id).ToArray()));

            var query = new QueryExpression("sdkmessageprocessingstep")
            {
                ColumnSet = new ColumnSet("plugintypeid", "statecode"),
                Criteria  = filter,
            };

            var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

            var stepEnabled = new Dictionary<Guid, bool>();
            var typeHasEnabledStep = new HashSet<Guid>();

            foreach (var entity in entities)
            {
                var enabled = entity.GetAttributeValue<OptionSetValue>("statecode")?.Value == 0;
                stepEnabled[entity.Id] = enabled;

                var pluginTypeId = entity.GetAttributeValue<EntityReference>("plugintypeid")?.Id;
                if (enabled && pluginTypeId.HasValue)
                    typeHasEnabledStep.Add(pluginTypeId.Value);
            }

            return (stepEnabled, typeHasEnabledStep);
        }, (new Dictionary<Guid, bool>(), new HashSet<Guid>()), console, msg => $"PluginType/Step enabled-state lookup failed ({msg}) — defaulting to Prio3 this run.");
}
