using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Plugins;
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
    // Auto: the cross-environment id-drift false positive is fixed upstream — ComponentClassifier now
    // resolves live plugin assemblies by their portable simple name (not the re-minted GUID), so a live,
    // in-solution assembly is no longer flagged. Only a genuinely-removed assembly reaches this handler,
    // making unattended auto-delete safe.
    public HandlerStatus Status => HandlerStatus.Auto;

    // Same entityLogicalName/idAttribute/nameAttribute triples as ComponentTypeCatalog.NameResolvableTypes'
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

        // R11/R12/KTD7: package content, not the manifest, decides orphan candidacy for a package-owned
        // assembly. ComponentClassifier's manifest-based portable-name match (the fix for cross-
        // environment id drift) still reads "absent from Solution.xml" as "removed on purpose" — true for
        // a classic assembly, false for one a package still carries but the source environment never
        // registered (the exact bug this plan exists to detect). Reusing the packageid resolution above,
        // check each package-owned candidate's live name against its package's locally reflected content
        // instead: present in both, it's not an orphan at all, regardless of the manifest.
        var (excludedAssemblyIds, protectedObjectIds, exclusionDegraded) =
            await ResolvePackageContentExclusionsAsync(context, packageIds, names, claimed, console, ct).ConfigureAwait(false);

        // Fix 2: BuildAllFindings collapses every orphaned assembly under one package into a single
        // package-scope delete, so a package holding an excluded assembly A and a genuinely orphaned
        // sibling B would still be deleted whole — destroying A. A package with any excluded assembly is
        // therefore never deleted. B's own execution surface still gets its ordinary orphan findings, so
        // the deleted logic stops running while the assembly stays as inert storage: its steps and
        // plugin types are ordinary candidates in this batch, and its Custom APIs fall to
        // CustomApiFamilyHandler's own Auto pass, since dropping B from redirectAssemblyIds also drops
        // the reordering cascade that only existed to beat the package delete.
        var protectedPackageIds = packageIds
            .Where(kv => excludedAssemblyIds.Contains(kv.Key))
            .Select(kv => kv.Value.Id)
            .ToHashSet();

        // Protected candidates are dropped before any finding is built below — not reported, not deleted,
        // not redirected — but stay in claimedIds above (recognized-but-clean), so
        // DispatchToHandlersAsync's unclaimed-candidate fallback never re-flags what this handler already
        // fully evaluated. A package the imported solution no longer carries at all yields no exclusions
        // (ResolvePackageContentExclusionsAsync finds no reflected content for it), so redirectAssemblyIds
        // below stays exactly packageIds.Keys and the existing package-delete path runs unchanged (R12).
        var claimedForFindings = protectedObjectIds.Count == 0
            ? claimed
            : claimed.Where(c => !protectedObjectIds.Contains(c.ObjectId)).ToList();
        var redirectAssemblyIds = packageIds
            .Where(kv => !excludedAssemblyIds.Contains(kv.Key) && !protectedPackageIds.Contains(kv.Value.Id))
            .Select(kv => kv.Key)
            .ToList();

        // Only candidates already in this run's batch are touched — a CustomApi/param/prop this query
        // finds that ISN'T already an orphan candidate is still validly declared locally.
        var localCustomApiNames = ComponentClassifier.ScanCustomApiNames(context.DataverseSolutionSrcRoot);
        var (childCleanupFindings, childCleanupDegraded) = redirectAssemblyIds.Count > 0
            ? await ResolvePackageChildCleanupFindingsAsync(context.Service, redirectAssemblyIds, candidates, claimedIds, localCustomApiNames, console, ct).ConfigureAwait(false)
            : ([], false);

        // A transient fault partway through ResolvePackageChildCleanupFindingsAsync must not leave the
        // package-delete finding as if cleanup were confirmed complete — when degraded, every
        // currently-redirected package is skipped entirely this run and picked up again once the lookup
        // succeeds.
        //
        // Fix 1: an exclusion resolution that couldn't verify what a still-carried package contains fails
        // the same way. The exclusion set going empty on a fault reads identically to "nothing to
        // exclude", which would let a package-scope delete proceed on state this handler never checked.
        var skipRedirectedFindingsThisRun = childCleanupDegraded || exclusionDegraded;

        // RunMode.NoDelete/DryRun is the only signal knowable at classify time — the
        // reactively-deferred/still-blocking-at-post-import case is not implemented by this handler.
        if (context.Mode.IsReportOnly())
            return new HandlerDetectionResult(
                BuildAllFindings(claimedForFindings, names, packageIds, _ => OrphanPriority.Prio1, skipRedirectedFindingsThisRun, protectedPackageIds)
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

        var findings = BuildAllFindings(claimedForFindings, names, packageIds, PriorityFor, skipRedirectedFindingsThisRun, protectedPackageIds)
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
        bool skipRedirectedFindings = false,
        HashSet<Guid>? protectedPackageIds = null)
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

                // Fix 2: the package still carries at least one of its assemblies' DLLs, so deleting it
                // would destroy live content. This assembly gets no finding of its own either — a
                // package-owned pluginassembly can't be deleted directly, which is why the redirect
                // exists. Its plugin types and steps stay ordinary candidates and are deleted on their
                // own, leaving the assembly registered but inert.
                if (protectedPackageIds?.Contains(packageRef.Id) == true) continue;

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

    // Identity stays at HandlerFinding's None default throughout this handler (assembly/type/step/image
    // findings here, the redirected pluginpackage finding above, and the CustomApi-cascade findings in
    // ResolvePackageChildCleanupFindingsAsync below) — none of PluginAssembly/PluginType/Step/StepImage/
    // PluginPackage has a ComponentClassifier scanner confirming its on-disk convention (Plugins/ is a
    // compiled csproj, not scanned source), so a shape here would be a guess. Resolves to Undetermined
    // (R8), which is honest.
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

    // R11/R12/KTD7: which of this batch's package-owned candidates a still-carried package's content
    // actually accounts for. Reflects each locally-present package's .nupkg exactly once for the whole
    // batch (PluginPackageContentReader.ScanReflectedAssemblyNamesByPackage), not once per candidate, then
    // matches each candidate's live name against its own package's set.
    //
    // Fix 1: every input that can go unverified reports Degraded, and the caller then skips the
    // package-scope delete for the run. Leaving a candidate merely un-excluded is NOT the safe direction
    // here: an un-excluded package-owned assembly redirects to deleting its whole pluginpackage, so a
    // silent empty exclusion set is a delete performed on state this never checked. Three inputs can do
    // that — the uniquename lookup faulting, a package directory that can't be read or reflected, and a
    // per-candidate name/uniquename miss.
    //
    // Fix 3: an excluded assembly's live plugin types are protected alongside it — the assembly is meant
    // to stay as inert registered storage, and deleting the types would strip the only thing that makes
    // it runnable. Steps are deliberately NOT protected: a step absent from source was removed on
    // purpose, and inert-but-registered is the intended end state.
    static async Task<(HashSet<Guid> ExcludedAssemblyIds, HashSet<Guid> ProtectedObjectIds, bool Degraded)>
        ResolvePackageContentExclusionsAsync(
            DetectionContext context,
            Dictionary<Guid, EntityReference> packageIds,
            Dictionary<Guid, string> names,
            List<(Guid ObjectId, int ComponentType)> claimed,
            IAnsiConsole console,
            CancellationToken ct)
    {
        var excluded = new HashSet<Guid>();
        if (packageIds.Count == 0) return (excluded, excluded, false);

        // Local scan first: it costs no round trip, and when the imported solution carries no package
        // content at all nothing can be excluded whatever the live lookups say — so R12's package-gone
        // path never degrades on an unrelated transient fault.
        var (reflectedByPackage, scanFailures) =
            PluginPackageContentReader.ScanReflectedAssemblyNamesByPackage(context.DataverseSolutionSrcRoot);

        var degraded = scanFailures.Count > 0;
        foreach (var (packageDir, error) in scanFailures)
            console.Warning($"Package '{Markup.Escape(packageDir)}' in the imported solution couldn't be read ({Markup.Escape(error.Message)}) — its package-content orphan check is skipped this run.");

        if (reflectedByPackage.Count == 0 && !degraded) return (excluded, excluded, false);

        var packageUniqueNames = await ResolvePackageUniqueNamesAsync(
            context.Service, packageIds.Values.Select(p => p.Id).Distinct().ToList(), console, ct,
            onFault: () => degraded = true).ConfigureAwait(false);

        foreach (var (assemblyId, packageRef) in packageIds)
        {
            // Either miss leaves this candidate unmatchable against local content. Reporting it as
            // degraded is what stops the package delete from running on an unverified assembly.
            if (!names.TryGetValue(assemblyId, out var assemblyName) ||
                !packageUniqueNames.TryGetValue(packageRef.Id, out var uniqueName))
            {
                degraded = true;
                continue;
            }

            if (reflectedByPackage.TryGetValue(uniqueName, out var reflectedNames) && reflectedNames.Contains(assemblyName))
                excluded.Add(assemblyId);
        }

        if (excluded.Count == 0) return (excluded, excluded, degraded);

        var (protectedTypeIds, typeLookupDegraded) =
            await ResolveProtectedPluginTypeIdsAsync(context.Service, excluded, console, ct).ConfigureAwait(false);

        // Fix 3 fail-closed: without the type list we can't tell a protected assembly's types from any
        // other candidate's, so no plugin type in this batch is deleted this run.
        if (typeLookupDegraded)
        {
            degraded = true;
            protectedTypeIds.UnionWith(claimed.Where(c => c.ComponentType == 90).Select(c => c.ObjectId));
        }

        var protectedObjectIds = new HashSet<Guid>(excluded);
        protectedObjectIds.UnionWith(protectedTypeIds);
        return (excluded, protectedObjectIds, degraded);
    }

    // Fix 3: the live plugin types belonging to assemblies the package content still accounts for.
    // Mirrors ResolvePackageIdsAsync's degrade shape, but the caller treats a fault as fail-closed
    // rather than fail-open — see ResolvePackageContentExclusionsAsync.
    static async Task<(HashSet<Guid> TypeIds, bool Degraded)> ResolveProtectedPluginTypeIdsAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyCollection<Guid> excludedAssemblyIds,
        IAnsiConsole console,
        CancellationToken ct)
    {
        var degraded = false;
        var typeIds = await DataverseFaultTolerance.TryQueryAsync(async () =>
        {
            EntityNameLookup.EnsureInLimit(excludedAssemblyIds.Count, "IDs", "Too many package-content assemblies to protect their plugin types this run.");

            var query = new QueryExpression("plugintype")
            {
                ColumnSet = new ColumnSet(false),
                Criteria = { Conditions = { new ConditionExpression("pluginassemblyid", ConditionOperator.In, excludedAssemblyIds.Select(id => (object)id).ToArray()) } }
            };
            var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
            return entities.Select(e => e.Id).ToHashSet();
        }, [], console, msg => $"plugintype lookup for package-content assemblies failed ({msg}) — plugin type deletes are skipped this run.",
            onFault: () => degraded = true);

        return (typeIds, degraded);
    }

    // Batched live lookup of packageid -> uniquename, the local identity ScanReflectedAssemblyNamesByPackage
    // keys its per-package sets by. Fix 1: reports the fault through onFault rather than degrading to an
    // empty result, which the caller could not tell apart from "nothing to exclude" — see
    // ResolvePackageContentExclusionsAsync. Detection for the rest of the family still continues.
    static Task<Dictionary<Guid, string>> ResolvePackageUniqueNamesAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<Guid> packageIds,
        IAnsiConsole console,
        CancellationToken ct,
        Action onFault) =>
        DataverseFaultTolerance.TryQueryAsync(async () =>
        {
            var query = new QueryExpression("pluginpackage")
            {
                ColumnSet = new ColumnSet("uniquename"),
                Criteria = { Conditions = { new ConditionExpression("pluginpackageid", ConditionOperator.In, packageIds.Select(id => (object)id).ToArray()) } }
            };
            var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

            var result = new Dictionary<Guid, string>();
            foreach (var entity in entities)
            {
                var uniqueName = entity.GetAttributeValue<string>("uniquename");
                if (uniqueName != null)
                    result[entity.Id] = uniqueName;
            }
            return result;
        }, [], console, msg => $"pluginpackage uniquename lookup failed ({msg}) — package deletes are skipped this run.", onFault);

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
                // Enforced here rather than by delegating the whole query to EntityNameLookup: this needs a
                // ColumnSet(false) id-only query, which EntityNameLookup doesn't support.
                EntityNameLookup.EnsureInLimit(parentIds.Count, "IDs", $"Package has too many {entityLogicalName} candidates for cleanup this run.");

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
