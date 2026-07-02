using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Spectre.Console;

namespace Flowline.Core.Services;

public enum OrphanAction { Delete, RemoveFromSolution, Manual }

public sealed record OrphanEntry(Guid ObjectId, int ComponentType, string DisplayName, OrphanAction Action, string? EntityName = null);

public class OrphanCleanupService(IAnsiConsole console, FlowlineRuntimeOptions opt)
{
    // R8: step images → steps → types → assemblies; web resources; workflows
    // CustomApi family has env-specific componenttype — handled separately via entity-side detection.
    static readonly int[] ExecutionOrder = [93, 92, 90, 91, 61, 29];

    // CustomApi child entities deleted before parent to avoid dependency errors.
    static readonly string[] CustomApiEntityOrder = ["customapirequestparameter", "customapiresponseproperty", "customapi"];

    static readonly Dictionary<int, string> EntityNames = new()
    {
        [91] = "pluginassembly",
        [90] = "plugintype",
        [92] = "sdkmessageprocessingstep",
        [93] = "sdkmessageprocessingstepimage",
        [61] = "webresource",
        [29] = "workflow",
    };

    public async Task<IReadOnlyList<OrphanEntry>> RunPreImportAsync(IOrganizationServiceAsync2 service,
        string solutionName,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> sNew,
        RunMode mode,
        string? webresourceRoot,
        CancellationToken ct)
    {
        var sOld = await console.Status()
            .StartAsync($"Querying orphan components in [bold]{solutionName}[/]...",
                _ => QuerySolutionComponentsAsync(service, solutionName, ct))
            .ConfigureAwait(false);

        if (sOld.Count == 0)
        {
            console.Skip("No solution components in Dataverse — skipping orphan check.");
            return [];
        }

        var sNewIds = sNew.Select(c => c.ObjectId).ToHashSet();

        if (sNew.Count == 0)
        {
            console.Warning("No components in Solution.xml — orphan check skipped to prevent mass deletion.");
            return [];
        }

        var orphans = sOld.Where(c => !sNewIds.Contains(c.ObjectId)).ToList();

        if (orphans.Count == 0)
        {
            console.Ok("No orphan components.");
            return [];
        }

        var autoOrphans    = orphans.Where(c => ComponentClassifier.Classify(c.ComponentType) == ComponentAction.AutoDelete).ToList();
        var unknownOrphans = orphans.Where(c => ComponentClassifier.Classify(c.ComponentType) == ComponentAction.Manual).ToList();

        // Exempt webresource orphans referenced in // flowline:depends annotations.
        autoOrphans = await ExemptAnnotationReferencedWebResourcesAsync(service, autoOrphans, webresourceRoot, ct).ConfigureAwait(false);

        // CustomApi family has an env-specific componenttype — detect via entity-side queries instead.
        Dictionary<Guid, string> customApiEntities = [];
        if (unknownOrphans.Count > 0)
        {
            try
            {
                customApiEntities = await IdentifyCustomApiEntityTypesAsync(service, unknownOrphans.Select(c => c.ObjectId), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                console.Warning($"CustomApi entity detection failed ({Markup.Escape(ex.Message)}) — unknown orphan components will be marked Manual.");
            }
        }

        var customApiOrphans = unknownOrphans.Where(c => customApiEntities.ContainsKey(c.ObjectId)).ToList();
        var manualOrphans    = unknownOrphans.Where(c => !customApiEntities.ContainsKey(c.ObjectId)).ToList();

        var crossSolutionIds = autoOrphans.Select(c => c.ObjectId).Concat(customApiOrphans.Select(c => c.ObjectId));
        var crossSolution = crossSolutionIds.Any()
            ? await GetCrossSolutionMembershipAsync(service, crossSolutionIds, ct).ConfigureAwait(false)
            : [];

        var entries = new List<OrphanEntry>();

        foreach (var orphan in autoOrphans)
        {
            var otherSolutions = OtherRelevantSolutions(crossSolution, orphan.ObjectId, solutionName);

            var action = otherSolutions.Count > 0 ? OrphanAction.RemoveFromSolution : OrphanAction.Delete;
            entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId), action));
        }

        foreach (var orphan in customApiOrphans)
        {
            var entityName = customApiEntities[orphan.ObjectId];
            var otherSolutions = OtherRelevantSolutions(crossSolution, orphan.ObjectId, solutionName);

            var action = otherSolutions.Count > 0 ? OrphanAction.RemoveFromSolution : OrphanAction.Delete;
            entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId, entityName), action, EntityName: entityName));
        }

        foreach (var orphan in manualOrphans)
            entries.Add(new OrphanEntry(orphan.ObjectId, orphan.ComponentType, TypeName(orphan.ComponentType, orphan.ObjectId), OrphanAction.Manual));

        PrintReport(entries, mode);

        if (mode == RunMode.NoDelete)
            return [];

        return await ExecuteInOrderAsync(service, solutionName, entries, isPostImport: false, ct).ConfigureAwait(false);
    }

    public async Task<int> RunPostImportAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> sNew,
        IReadOnlyList<OrphanEntry> deferred,
        RunMode mode,
        CancellationToken ct)
    {
        if (deferred.Count == 0 || mode == RunMode.NoDelete)
            return 0;

        var sNewIds     = sNew.Select(c => c.ObjectId).ToHashSet();
        var deferredIds = deferred.Select(e => e.ObjectId).ToList();

        var stillPresent  = await GetStillPresentAsync(service, solutionName, deferredIds, ct).ConfigureAwait(false);
        var presentIds    = stillPresent.ToList();
        var crossSolution = presentIds.Count > 0
            ? await GetCrossSolutionMembershipAsync(service, presentIds, ct).ConfigureAwait(false)
            : [];

        var reEntries = new List<OrphanEntry>();
        foreach (var entry in deferred)
        {
            if (!stillPresent.Contains(entry.ObjectId)) continue;
            if (sNewIds.Contains(entry.ObjectId)) continue;

            var otherSolutions = OtherRelevantSolutions(crossSolution, entry.ObjectId, solutionName);

            var action = otherSolutions.Count > 0 ? OrphanAction.RemoveFromSolution : OrphanAction.Delete;
            reEntries.Add(entry with { Action = action });
        }

        if (reEntries.Count == 0)
            return 0;

        console.Skip("Post-import: retrying deferred orphan cleanup...");
        var failed = await ExecuteInOrderAsync(service, solutionName, reEntries, isPostImport: true, ct).ConfigureAwait(false);
        return failed.Count;
    }

    async Task<List<(Guid ObjectId, int ComponentType)>> ExemptAnnotationReferencedWebResourcesAsync(
        IOrganizationServiceAsync2 service,
        List<(Guid ObjectId, int ComponentType)> autoOrphans,
        string? webresourceRoot,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(webresourceRoot)) return autoOrphans;

        var webResourceOrphans = autoOrphans.Where(c => c.ComponentType == 61).ToList();
        if (webResourceOrphans.Count == 0) return autoOrphans;

        var annotationRefs = WebResourceAnnotationParser.CollectAllReferences(webresourceRoot);
        if (annotationRefs.Count == 0) return autoOrphans;

        var orphanNames = await GetWebResourceNamesAsync(service, webResourceOrphans.Select(c => c.ObjectId), ct).ConfigureAwait(false);

        var exemptIds = webResourceOrphans
            .Where(o => orphanNames.TryGetValue(o.ObjectId, out var name) && annotationRefs.Contains(name))
            .Select(o => o.ObjectId)
            .ToHashSet();

        if (exemptIds.Count == 0) return autoOrphans;

        foreach (var (id, name) in orphanNames)
            if (exemptIds.Contains(id))
                console.Skip($"'{name}' preserved — referenced in // flowline:depends annotation.");

        return autoOrphans.Where(o => !exemptIds.Contains(o.ObjectId)).ToList();
    }

    async Task<Dictionary<Guid, string>> GetWebResourceNamesAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<Guid> ids,
        CancellationToken ct)
    {
        var idList = ids.Distinct().Where(id => id != Guid.Empty).ToList();
        if (idList.Count == 0) return [];
        if (idList.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {idList.Count} IDs (max 2000). Solution has too many web resource orphans for name resolution.");

        var query = new QueryExpression("webresource")
        {
            ColumnSet = new ColumnSet("name"),
            Criteria  = { Conditions = { new ConditionExpression("webresourceid", ConditionOperator.In, idList.Select(id => (object)id).ToArray()) } }
        };

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return entities
            .Where(e => !string.IsNullOrEmpty(e.GetAttributeValue<string>("name")))
            .ToDictionary(e => e.Id, e => e.GetAttributeValue<string>("name")!);
    }

    async Task<List<(Guid ObjectId, int ComponentType)>> QuerySolutionComponentsAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        CancellationToken ct)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid", "componenttype")
        };

        var solutionLink = query.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);

        var result = new List<(Guid, int)>(entities.Count);
        foreach (var entity in entities)
        {
            var objectId = entity.GetAttributeValue<Guid>("objectid");
            if (objectId == Guid.Empty) continue;
            var componentType = entity.GetAttributeValue<OptionSetValue>("componenttype")?.Value;
            if (componentType == null) continue;
            result.Add((objectId, componentType.Value));
        }
        return result;
    }

    async Task<Dictionary<Guid, string>> IdentifyCustomApiEntityTypesAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<Guid> objectIds,
        CancellationToken ct)
    {
        var ids = objectIds.Distinct().Where(id => id != Guid.Empty).ToList();
        if (ids.Count == 0)
            return [];
        if (ids.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {ids.Count} IDs (max 2000). Solution has too many CustomApi components for orphan detection.");

        var idArray = ids.Select(id => (object)id).ToArray();

        static QueryExpression EntityQuery(string entityName, string idAttr, object[] ids) =>
            new(entityName)
            {
                ColumnSet = new ColumnSet(false),
                Criteria  = { Conditions = { new ConditionExpression(idAttr, ConditionOperator.In, ids) } }
            };

        var caTask    = service.RetrieveAllAsync(EntityQuery("customapi",                 "customapiid",                 idArray), ct);
        var paramTask = service.RetrieveAllAsync(EntityQuery("customapirequestparameter", "customapirequestparameterid", idArray), ct);
        var propTask  = service.RetrieveAllAsync(EntityQuery("customapiresponseproperty", "customapiresponsepropertyid", idArray), ct);
        await Task.WhenAll(caTask, paramTask, propTask).ConfigureAwait(false);

        var result = new Dictionary<Guid, string>();
        foreach (var e in caTask.Result)    result[e.Id] = "customapi";
        foreach (var e in paramTask.Result) result[e.Id] = "customapirequestparameter";
        foreach (var e in propTask.Result)  result[e.Id] = "customapiresponseproperty";
        return result;
    }

    // Dataverse dual-writes every component added to a custom unmanaged solution into "Default" as
    // well, so "Default" membership is not a reason to keep an otherwise-orphaned component around.
    // Excluding it here mirrors PluginPlanner.AddCrossSolutionWarnings.
    static List<string> OtherRelevantSolutions(Dictionary<Guid, List<string>> crossSolution, Guid objectId, string solutionName) =>
        crossSolution.TryGetValue(objectId, out var sols)
            ? sols.Where(s => !string.Equals(s, solutionName, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

    async Task<Dictionary<Guid, List<string>>> GetCrossSolutionMembershipAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<Guid> objectIds,
        CancellationToken ct)
    {
        var ids = objectIds.Distinct().Where(id => id != Guid.Empty).ToList();
        if (ids.Count == 0)
            return [];
        if (ids.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {ids.Count} IDs (max 2000). Solution has too many orphan components for cross-solution membership check.");

        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            Criteria  = { Conditions = { new ConditionExpression("objectid", ConditionOperator.In, ids.Select(id => (object)id).ToArray()) } },
            LinkEntities =
            {
                new LinkEntity("solutioncomponent", "solution", "solutionid", "solutionid", JoinOperator.Inner)
                {
                    Columns     = new ColumnSet("uniquename"),
                    EntityAlias = "sol"
                }
            }
        };

        var entities   = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        var membership = new Dictionary<Guid, List<string>>();

        foreach (var entity in entities)
        {
            var objectId = entity.GetAttributeValue<Guid>("objectid");
            if (objectId == Guid.Empty) continue;

            var sln = entity.GetAttributeValue<AliasedValue>("sol.uniquename")?.Value as string;
            if (string.IsNullOrEmpty(sln)) continue;

            if (!membership.TryGetValue(objectId, out var sols))
                membership[objectId] = sols = [];
            sols.Add(sln);
        }

        return membership;
    }

    async Task<HashSet<Guid>> GetStillPresentAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        IReadOnlyList<Guid> objectIds,
        CancellationToken ct)
    {
        if (objectIds.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {objectIds.Count} IDs (max 2000). Solution has too many deferred orphan components.");

        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            Criteria  = { Conditions = { new ConditionExpression("objectid", ConditionOperator.In, objectIds.Select(id => (object)id).ToArray()) } }
        };

        var solutionLink = query.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return entities.Select(e => e.GetAttributeValue<Guid>("objectid")).Where(id => id != Guid.Empty).ToHashSet();
    }

    async Task<IReadOnlyList<OrphanEntry>> ExecuteInOrderAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        IReadOnlyList<OrphanEntry> entries,
        bool isPostImport,
        CancellationToken ct)
    {
        var deferred = new List<OrphanEntry>();

        // Stable componenttype entries in R8 order.
        var stableByType = entries.Where(e => e.Action != OrphanAction.Manual && e.EntityName == null)
                                  .GroupBy(e => e.ComponentType)
                                  .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var componentType in ExecutionOrder)
        {
            if (!stableByType.TryGetValue(componentType, out var group)) continue;

            foreach (var entry in group)
                await TryExecuteEntryAsync(service, solutionName, entry, isPostImport, deferred, ct);
        }

        // Entity-detected CustomApi family: children before parent.
        var customApiByEntity = entries.Where(e => e.Action != OrphanAction.Manual && e.EntityName != null)
                                       .GroupBy(e => e.EntityName!)
                                       .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var entityName in CustomApiEntityOrder)
        {
            if (!customApiByEntity.TryGetValue(entityName, out var group)) continue;

            foreach (var entry in group)
                await TryExecuteEntryAsync(service, solutionName, entry, isPostImport, deferred, ct);
        }

        return deferred.AsReadOnly();
    }

    async Task TryExecuteEntryAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        OrphanEntry entry,
        bool isPostImport,
        List<OrphanEntry> deferred,
        CancellationToken ct)
    {
        try
        {
            if (entry.ComponentType == 29 && entry.Action == OrphanAction.Delete)
            {
                var deactivated = await TryDeactivateWorkflowAsync(service, entry.ObjectId, ct).ConfigureAwait(false);
                if (!deactivated)
                {
                    console.Warning($"'{entry.DisplayName}' — workflow deactivation failed, remove manually via maker portal.");
                    return;
                }
            }

            await PerformActionAsync(service, solutionName, entry, ct).ConfigureAwait(false);
            console.Verbose($"{(isPostImport ? "Post-import: " : "")}{entry.DisplayName} {(entry.Action == OrphanAction.Delete ? "deleted" : "removed from solution")}");
        }
        catch (FaultException<OrganizationServiceFault> ex) when (!isPostImport && IsDependencyError(ex))
        {
            console.MarkupLine($"[dim]Deferred: {Markup.Escape(entry.DisplayName)} — dependency, will retry post-import[/]");
            deferred.Add(entry);
        }
        catch (FaultException<OrganizationServiceFault> ex) when (isPostImport)
        {
            console.Warning($"'{entry.DisplayName}' — post-import cleanup failed, remove manually: {Markup.Escape(ex.Message)}");
            deferred.Add(entry);
        }
    }

    static async Task PerformActionAsync(
        IOrganizationServiceAsync2 service,
        string solutionName,
        OrphanEntry entry,
        CancellationToken ct)
    {
        if (entry.Action == OrphanAction.RemoveFromSolution)
        {
            await service.ExecuteAsync(new OrganizationRequest("RemoveSolutionComponent")
            {
                ["ComponentId"]        = entry.ObjectId,
                ["ComponentType"]      = entry.ComponentType,
                ["SolutionUniqueName"] = solutionName
            }, ct).ConfigureAwait(false);
            return;
        }

        var entityName = entry.EntityName ?? (EntityNames.TryGetValue(entry.ComponentType, out var n) ? n : null);
        if (entityName == null) return;
        await service.DeleteAsync(entityName, entry.ObjectId, ct).ConfigureAwait(false);
    }

    static async Task<bool> TryDeactivateWorkflowAsync(IOrganizationServiceAsync2 service, Guid workflowId, CancellationToken ct)
    {
        try
        {
            await service.UpdateAsync(new Entity("workflow", workflowId)
            {
                ["statecode"]  = new OptionSetValue(0),
                ["statuscode"] = new OptionSetValue(1)
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            return false;
        }
    }

    void PrintReport(IReadOnlyList<OrphanEntry> entries, RunMode mode)
    {
        var deleteCount = entries.Count(e => e.Action == OrphanAction.Delete);
        var removeCount = entries.Count(e => e.Action == OrphanAction.RemoveFromSolution);
        var manualCount = entries.Count(e => e.Action == OrphanAction.Manual);

        console.MarkupLine($"[bold]Orphan components ({entries.Count}):[/]");

        foreach (var entry in entries.OrderBy(e => ExecutionOrderIndex(e.ComponentType, e.EntityName)))
        {
            var label = mode == RunMode.NoDelete && entry.Action != OrphanAction.Manual
                ? NoDeleteLabel(entry.Action)
                : ActionLabel(entry.Action);
            console.MarkupLine($"  [{ActionColor(entry.Action)}]{Markup.Escape(entry.DisplayName)} — {label}[/]");
        }

        if (mode == RunMode.NoDelete)
            console.Skip($"{deleteCount} would be deleted, {removeCount} would be removed from solution, {manualCount} manual. (--no-delete active)");
        else
            console.Skip($"{deleteCount} to delete, {removeCount} to remove from solution, {manualCount} manual");
    }

    static int ExecutionOrderIndex(int componentType, string? entityName)
    {
        if (entityName != null)
        {
            var customApiIdx = Array.IndexOf(CustomApiEntityOrder, entityName);
            return ExecutionOrder.Length + (customApiIdx < 0 ? CustomApiEntityOrder.Length : customApiIdx);
        }
        var idx = Array.IndexOf(ExecutionOrder, componentType);
        return idx < 0 ? ExecutionOrder.Length - 1 : idx;
    }

    static string TypeName(int componentType, Guid objectId, string? entityName = null)
    {
        var name = entityName switch
        {
            "customapi"                 => "CustomApi",
            "customapirequestparameter" => "CustomApiRequestParameter",
            "customapiresponseproperty" => "CustomApiResponseProperty",
            _ => componentType switch
            {
                91 => "PluginAssembly",
                90 => "PluginType",
                92 => "SdkMessageProcessingStep",
                93 => "SdkMessageProcessingStepImage",
                61 => "WebResource",
                29 => "Workflow",
                _  => $"Component({componentType})"
            }
        };
        return $"{name} {objectId}";
    }

    static string ActionLabel(OrphanAction action) => action switch
    {
        OrphanAction.Delete             => "delete",
        OrphanAction.RemoveFromSolution => "remove from solution",
        OrphanAction.Manual             => "manual — remove via maker portal",
        _                               => action.ToString()
    };

    static string NoDeleteLabel(OrphanAction action) => action switch
    {
        OrphanAction.Delete             => "would delete",
        OrphanAction.RemoveFromSolution => "would remove from solution",
        _                               => action.ToString()
    };

    static string ActionColor(OrphanAction action) => action switch
    {
        OrphanAction.Delete             => "red",
        OrphanAction.RemoveFromSolution => "yellow",
        OrphanAction.Manual             => "dim",
        _                               => "white"
    };

    static bool IsDependencyError(FaultException<OrganizationServiceFault> ex) =>
        ex.Detail?.ErrorCode == unchecked((int)0x80047002) ||
        (ex.Message?.Contains("depend", StringComparison.OrdinalIgnoreCase) ?? false);
}
