using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// U2: migrates PluginAssembly (91) / PluginType (90) / Step (92) / StepImage (93) detection,
// classification, and ordering from OrphanCleanupService's NameResolvableTypes and its old ExecutionOrder
// array (the latter removed during U9's orchestrator rewrite) into a handler, preserving today's exact
// Auto/Delete behavior and name resolution, plus the new Prio1/Prio2/Prio3 axis (KTD8). Ships Active per
// KTD2 — this family already has a verified local-source check today (R14).
//
// Code-review fault-isolation fix: this was a Pass-1 (componenttype-gated) handler with zero try/catch —
// a transient Dataverse fault on either live query (name resolution or enabled-state) propagated uncaught
// through DispatchToHandlersAsync, aborting the whole deploy before Pass 2 ever ran. Both queries now
// catch and degrade the same way the entity-detected handlers (Bot/ConnectionReference/CustomApi) already
// do — FaultException<OrganizationServiceFault> quietly skips (KTD6), anything else warns and skips —
// falling back to each query's existing "unresolved" display/Prio path rather than a new fallback shape.
public sealed class PluginAssemblyFamilyHandler(IAnsiConsole console) : IOrphanHandler
{
    public HandlerStatus Status => HandlerStatus.Active;

    // Migrated from OrphanCleanupService.NameResolvableTypes' 91/90/92/93 rows — same
    // entityLogicalName/idAttribute/nameAttribute triples used for live display-name resolution.
    static readonly Dictionary<int, (string EntityLogicalName, string IdAttribute, string NameAttribute)> Lookups = new()
    {
        [91] = ("pluginassembly", "pluginassemblyid", "name"),
        [90] = ("plugintype", "plugintypeid", "typename"),
        [92] = ("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "name"),
        [93] = ("sdkmessageprocessingstepimage", "sdkmessageprocessingstepimageid", "name"),
    };

    // Migrated from OrphanCleanupService's old ExecutionOrder array's [93, 92, 90, 91] subset (removed
    // during U9's orchestrator rewrite), re-expressed as a per-family SequenceHint (KTD1) — deepest child
    // executes first (StepImage = 0) through shallowest parent last (PluginAssembly = 3), preserving
    // today's exact deletion order.
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

    // Live-verified gap fix: the redirected pluginpackage-delete finding stays in this handler's own
    // family (SequenceHints[91] = 3), but a CustomApi bound to one of the redirected assembly's plugin
    // types is normally detected by the separate CustomApiFamilyHandler, whose whole family runs later
    // in FamilyOrder — so it would always execute AFTER the package delete, not before. Dataverse
    // rejects a pluginpackage delete while a CustomApi still references it ("referenced by N other
    // components"), the same way it rejects a plugintype delete/update with active steps (KD4) — these
    // hints pull the CustomApi cleanup into this family's own ordering instead, both below the package's
    // slot 3. Values only need to stay < 3; ties with Step(1)/PluginType(2) are harmless since neither
    // family has an ordering dependency on the other.
    const int CustomApiChildSequenceHint = 1; // CustomApiRequestParameter / CustomApiResponseProperty
    const int CustomApiParentSequenceHint = 2; // CustomApi itself

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        var claimed = candidates.Where(c => Lookups.ContainsKey(c.ComponentType)).ToList();
        if (claimed.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        // Every candidate matching this family's componenttype gate is claimed, regardless of the
        // Prio branch it ends up in below — this handler never suppresses a claimed candidate out of
        // Findings.
        var claimedIds = claimed.Select(c => c.ObjectId).ToHashSet();

        var names = await ResolveNamesAsync(context.Service, claimed, console, ct).ConfigureAwait(false);

        // R10/KD3/KTD10: a pluginassembly owned by a pluginpackage can't be deleted directly
        // ("Unable to delete plug-in assembly as it is part of plugin package") — live-check each
        // PluginAssembly candidate's packageid so BuildAllFindings can redirect it to a
        // pluginpackage-delete finding instead of one that fails at execute time.
        var assemblyIds = claimed.Where(c => c.ComponentType == 91).Select(c => c.ObjectId).ToList();
        var packageIds = assemblyIds.Count > 0
            ? await ResolvePackageIdsAsync(context.Service, assemblyIds, console, ct).ConfigureAwait(false)
            : new Dictionary<Guid, EntityReference>();

        // Only candidates already present in this run's `candidates` batch are touched inside this
        // call — a CustomApi/param/prop this live query finds but which ISN'T already an orphan
        // candidate is still validly declared locally and must be left alone.
        var localCustomApiNames = ComponentClassifier.ScanCustomApiNames(context.PackageSrcRoot);
        var (childCleanupFindings, childCleanupDegraded) = packageIds.Count > 0
            ? await ResolvePackageChildCleanupFindingsAsync(context.Service, packageIds.Keys, candidates, claimedIds, localCustomApiNames, console, ct).ConfigureAwait(false)
            : ([], false);

        // Live-verified gap fix: a transient fault partway through ResolvePackageChildCleanupFindingsAsync
        // (which batches its queries across every currently-redirected package at once) must not leave the
        // package-delete finding built below as if cleanup were confirmed complete — that would reintroduce
        // the exact ordering hazard this whole fix exists to close. When degraded, every currently-redirected
        // package is skipped entirely this run (no finding at all, not even the un-redirected fallback,
        // which is guaranteed to fail per KD3) — the assembly is still an orphan candidate, so it's picked
        // up cleanly again next run once the lookup succeeds.
        var skipRedirectedFindingsThisRun = childCleanupDegraded;

        // KTD8 Prio1: RunMode.NoDelete is the only signal knowable at classify time — see the plan's
        // Timing note on why the reactively-deferred/still-blocking-at-post-import case is out of
        // scope this round (this handler does not implement it).
        if (context.Mode == RunMode.NoDelete)
            return new HandlerDetectionResult(
                BuildAllFindings(claimed, names, packageIds, _ => OrphanPriority.Prio1, skipRedirectedFindingsThisRun)
                    .Concat(childCleanupFindings.Select(f => f with { Priority = OrphanPriority.Prio1 }))
                    .ToList(),
                claimedIds);

        // KTD8 Prio2 names exactly PluginType and Step ("the live PluginType/Step is Enabled") —
        // StepImage and PluginAssembly have no Enabled concept of their own and default to Prio3.
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

    // Builds one HandlerFinding per candidate, redirecting any PluginAssembly (91) candidate whose
    // packageid resolved to a finding against the parent pluginpackage instead (R10/KD3/KTD10).
    // Candidates with no resolved packageid (not package-owned, or the live lookup degraded) keep
    // today's exact unchanged assembly-delete finding via BuildFinding.
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
                // Live-verified gap fix: a degraded child-cleanup lookup this run means we can't confirm
                // the CustomApi/steps blocking this package were found and scheduled for deletion first —
                // skip entirely (not even the un-redirected fallback, which KD3 confirms always fails for
                // a package-owned assembly) rather than attempt a delete likely to fail or, worse, succeed
                // while still-referencing children were never actually cleaned up.
                if (skipRedirectedFindings) continue;

                // KD5: multiple orphaned assemblies sharing the same parent package collapse to one
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

    // R10/KD3/KTD10: batched live check of packageid on each PluginAssembly candidate, following the
    // same fault-tolerant shape as ResolveNamesAsync/QueryEnabledStateAsync — a transient query failure
    // degrades every candidate in this batch back to today's un-redirected assembly-delete finding
    // rather than aborting detection for the whole family.
    static async Task<Dictionary<Guid, EntityReference>> ResolvePackageIdsAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<Guid> assemblyIds,
        IAnsiConsole console,
        CancellationToken ct)
    {
        var query = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("packageid"),
            Criteria = { Conditions = { new ConditionExpression("pluginassemblyid", ConditionOperator.In, assemblyIds.Select(id => (object)id).ToArray()) } }
        };

        List<Entity> entities;
        try
        {
            entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            return new Dictionary<Guid, EntityReference>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.Warning($"pluginassembly packageid lookup failed ({Markup.Escape(ex.Message)}) — degrading to un-redirected assembly-delete finding this run.");
            return new Dictionary<Guid, EntityReference>();
        }

        var result = new Dictionary<Guid, EntityReference>();
        foreach (var entity in entities)
        {
            var packageRef = entity.GetAttributeValue<EntityReference>("packageid");
            if (packageRef != null)
                result[entity.Id] = packageRef;
        }

        return result;
    }

    // Dataverse's practical ConditionOperator.In ceiling — same limit EntityNameLookup.cs centralizes.
    // QueryChildIdsAsync enforces it directly (rather than delegating to EntityNameLookup) since it needs
    // ColumnSet(false) id-only queries, which EntityNameLookup doesn't support.
    const int ConditionOperatorInLimit = 2000;

    // Live-verified gap fix (see class-level comment on CustomApiChildSequenceHint): pulls any CustomApi
    // — and its own RequestParameter/ResponseProperty children — bound to a redirected assembly's plugin
    // types into this family's own findings, ordered ahead of the package-delete slot, instead of leaving
    // them to CustomApiFamilyHandler's separate, later-executing family pass.
    //
    // Returns whether any query degraded (faulted) alongside the findings — the caller must not treat a
    // degraded run as "cleanup confirmed complete" (see DetectAsync's skipRedirectedFindingsThisRun),
    // since a transient failure here means this run couldn't confirm what still blocks the package delete.
    static async Task<(List<HandlerFinding> Findings, bool Degraded)> ResolvePackageChildCleanupFindingsAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyCollection<Guid> redirectedAssemblyIds,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        HashSet<Guid> claimedIds,
        CustomApiNames localCustomApiNames,
        IAnsiConsole console,
        CancellationToken ct)
    {
        var degraded = false;

        async Task<HashSet<Guid>> QueryChildIdsAsync(string entityLogicalName, string filterAttribute, IReadOnlyCollection<Guid> parentIds)
        {
            if (parentIds.Count == 0) return [];

            try
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
            }
            catch (FaultException<OrganizationServiceFault>)
            {
                degraded = true;
                return [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                degraded = true;
                console.Warning($"{entityLogicalName} lookup for package-delete cleanup failed ({Markup.Escape(ex.Message)}) — left for a future run.");
                return [];
            }
        }

        var pluginTypeIds = await QueryChildIdsAsync("plugintype", "pluginassemblyid", redirectedAssemblyIds).ConfigureAwait(false);
        if (pluginTypeIds.Count == 0) return ([], degraded);

        var customApiIds = await QueryChildIdsAsync("customapi", "plugintypeid", pluginTypeIds).ConfigureAwait(false);
        if (customApiIds.Count == 0) return ([], degraded);

        var requestParamIds = await QueryChildIdsAsync("customapirequestparameter", "customapiid", customApiIds).ConfigureAwait(false);
        var responsePropIds = await QueryChildIdsAsync("customapiresponseproperty", "customapiid", customApiIds).ConfigureAwait(false);

        var names = new Dictionary<Guid, string>();
        foreach (var (entityLogicalName, idAttribute, ids) in new[]
                 {
                     ("customapi", "customapiid", (IReadOnlyCollection<Guid>)customApiIds),
                     ("customapirequestparameter", "customapirequestparameterid", requestParamIds),
                     ("customapiresponseproperty", "customapiresponsepropertyid", responsePropIds),
                 })
        {
            try
            {
                foreach (var (id, name) in await EntityNameLookup.GetEntityNamesAsync(service, entityLogicalName, idAttribute, "name", ids, ct).ConfigureAwait(false))
                    names[id] = name;
            }
            catch (FaultException<OrganizationServiceFault>)
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                console.Warning($"{entityLogicalName} name resolution failed ({Markup.Escape(ex.Message)}) — display falls back to bare id this run.");
            }
        }

        var candidateComponentTypes = candidates.ToDictionary(c => c.ObjectId, c => c.ComponentType);
        var findings = new List<HandlerFinding>();

        // Live-verified gap fix: CustomApi (and its request-param/response-prop children) has no GUID
        // anywhere in local source — uniquename is the only local identity (mirrors CustomApiFamilyHandler's
        // own ScanCustomApiNames-based check). A CustomApi recreated with a new id under an unchanged,
        // still-locally-declared uniquename must not be claimed here just because its current id happens to
        // be present in this run's raw orphan-candidate batch — the equivalent normal path (CustomApiFamilyHandler)
        // already protects this exact case, but never gets the chance to once this handler claims the id first.
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

    // Same display format as OrphanCleanupService's old TypeName helper produced for this family
    // (removed during U9's orchestrator rewrite).
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
            try
            {
                var names = await EntityNameLookup.GetEntityNamesAsync(service, lookup.EntityLogicalName, lookup.IdAttribute, lookup.NameAttribute, group.Select(c => c.ObjectId), ct).ConfigureAwait(false);
                foreach (var (id, name) in names)
                    result[id] = name;
            }
            // KTD6: a business fault (the table genuinely has no matching rows) is not evidence any
            // candidate was deleted — this group's names simply stay unresolved, same as an
            // infrastructure fault, which additionally warns. Either way BuildFinding's existing
            // unresolved-name fallback (bare-id TypeName) already covers it — no new fallback shape.
            catch (FaultException<OrganizationServiceFault>)
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                console.Warning($"{lookup.EntityLogicalName} name resolution failed ({Markup.Escape(ex.Message)}) — display falls back to bare id this run.");
            }
        }

        return result;
    }

    // Single sdkmessageprocessingstep query resolves both Step's own statecode (Prio2 when Enabled —
    // statecode 0, per the SdkMessageProcessingStep table reference) and PluginType's has-any-enabled-
    // step check, since PluginType itself carries no statecode of its own.
    static async Task<(Dictionary<Guid, bool> StepEnabled, HashSet<Guid> TypeHasEnabledStep)> QueryEnabledStateAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<Guid> stepIds,
        IReadOnlyList<Guid> typeIds,
        IAnsiConsole console,
        CancellationToken ct)
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

        List<Entity> entities;
        try
        {
            entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        }
        // Unresolved enabled-state is not evidence of anything — BuildFinding's priority switch already
        // defaults Step/PluginType to Prio3 when neither dictionary has an entry, the same safe fallback
        // an empty result set (record already gone) produces today.
        catch (FaultException<OrganizationServiceFault>)
        {
            return (new Dictionary<Guid, bool>(), new HashSet<Guid>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.Warning($"PluginType/Step enabled-state lookup failed ({Markup.Escape(ex.Message)}) — defaulting to Prio3 this run.");
            return (new Dictionary<Guid, bool>(), new HashSet<Guid>());
        }

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
    }
}
