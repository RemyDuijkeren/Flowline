using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.Services.OrphanCleanup.Handlers;

// U2: migrates PluginAssembly (91) / PluginType (90) / Step (92) / StepImage (93) detection,
// classification, and ordering from OrphanCleanupService's NameResolvableTypes/ExecutionOrder into a
// handler, preserving today's exact Auto/Delete behavior and name resolution, plus the new
// Prio1/Prio2/Prio3 axis (KTD8). Ships Active per KTD2 — this family already has a verified
// local-source check today (R14).
public sealed class PluginAssemblyFamilyHandler : IOrphanHandler
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

    // Migrated from OrphanCleanupService.ExecutionOrder's [93, 92, 90, 91] subset, re-expressed as a
    // per-family SequenceHint (KTD1) — deepest child executes first (StepImage = 0) through shallowest
    // parent last (PluginAssembly = 3), preserving today's exact deletion order.
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

    public async Task<IReadOnlyList<HandlerFinding>> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        var claimed = candidates.Where(c => Lookups.ContainsKey(c.ComponentType)).ToList();
        if (claimed.Count == 0) return [];

        var names = await ResolveNamesAsync(context.Service, claimed, ct).ConfigureAwait(false);

        // KTD8 Prio1: RunMode.NoDelete is the only signal knowable at classify time — see the plan's
        // Timing note on why the reactively-deferred/still-blocking-at-post-import case is out of
        // scope this round (this handler does not implement it).
        if (context.Mode == RunMode.NoDelete)
            return claimed.Select(c => BuildFinding(c, names, OrphanPriority.Prio1)).ToList();

        // KTD8 Prio2 names exactly PluginType and Step ("the live PluginType/Step is Enabled") —
        // StepImage and PluginAssembly have no Enabled concept of their own and default to Prio3.
        var stepIds = claimed.Where(c => c.ComponentType == 92).Select(c => c.ObjectId).ToList();
        var typeIds = claimed.Where(c => c.ComponentType == 90).Select(c => c.ObjectId).ToList();

        var (stepEnabled, typeHasEnabledStep) = stepIds.Count > 0 || typeIds.Count > 0
            ? await QueryEnabledStateAsync(context.Service, stepIds, typeIds, ct).ConfigureAwait(false)
            : (new Dictionary<Guid, bool>(), new HashSet<Guid>());

        var findings = new List<HandlerFinding>(claimed.Count);
        foreach (var candidate in claimed)
        {
            var priority = candidate.ComponentType switch
            {
                92 => stepEnabled.TryGetValue(candidate.ObjectId, out var enabled) && enabled
                    ? OrphanPriority.Prio2 : OrphanPriority.Prio3,
                90 => typeHasEnabledStep.Contains(candidate.ObjectId)
                    ? OrphanPriority.Prio2 : OrphanPriority.Prio3,
                _  => OrphanPriority.Prio3,
            };

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

    // Same display format as OrphanCleanupService.TypeName for this family.
    static string TypeName(int componentType, Guid objectId, string? detail) =>
        detail != null ? $"{TypeLabels[componentType]} '{detail}' ({objectId})" : $"{TypeLabels[componentType]} {objectId}";

    static async Task<Dictionary<Guid, string>> ResolveNamesAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> claimed,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();

        foreach (var group in claimed.GroupBy(c => c.ComponentType))
        {
            var lookup = Lookups[group.Key];
            var names = await GetEntityNamesAsync(service, lookup.EntityLogicalName, lookup.IdAttribute, lookup.NameAttribute, group.Select(c => c.ObjectId), ct).ConfigureAwait(false);
            foreach (var (id, name) in names)
                result[id] = name;
        }

        return result;
    }

    // Migrated from OrphanCleanupService.GetEntityNamesAsync — same bulk IN-query pattern.
    static async Task<Dictionary<Guid, string>> GetEntityNamesAsync(
        IOrganizationServiceAsync2 service,
        string entityLogicalName,
        string idAttribute,
        string nameAttribute,
        IEnumerable<Guid> ids,
        CancellationToken ct)
    {
        var idList = ids.Distinct().Where(id => id != Guid.Empty).ToList();
        if (idList.Count == 0) return [];

        var query = new QueryExpression(entityLogicalName)
        {
            ColumnSet = new ColumnSet(nameAttribute),
            Criteria  = { Conditions = { new ConditionExpression(idAttribute, ConditionOperator.In, idList.Select(id => (object)id).ToArray()) } }
        };

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return entities
            .Where(e => !string.IsNullOrEmpty(e.GetAttributeValue<string>(nameAttribute)))
            .ToDictionary(e => e.Id, e => e.GetAttributeValue<string>(nameAttribute)!);
    }

    // Single sdkmessageprocessingstep query resolves both Step's own statecode (Prio2 when Enabled —
    // statecode 0, per the SdkMessageProcessingStep table reference) and PluginType's has-any-enabled-
    // step check, since PluginType itself carries no statecode of its own.
    static async Task<(Dictionary<Guid, bool> StepEnabled, HashSet<Guid> TypeHasEnabledStep)> QueryEnabledStateAsync(
        IOrganizationServiceAsync2 service,
        IReadOnlyList<Guid> stepIds,
        IReadOnlyList<Guid> typeIds,
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
    }
}
