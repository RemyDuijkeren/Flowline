using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.Services.OrphanCleanup;

// Shared bulk name-lookup helper — queries entityLogicalName by idAttribute IN (ids), returning only
// non-null names. Consolidates what was independently duplicated in OrphanCleanupService,
// PluginAssemblyFamilyHandler, RoleHandler, and WebResourceHandler — three of those four copies had
// silently dropped the 2000-id ConditionOperator.In ceiling guard (Dataverse's practical value-count
// limit for the IN operator); this is the one copy every caller now shares, guard included.
public static class EntityNameLookup
{
    public static async Task<Dictionary<Guid, string>> GetEntityNamesAsync(
        IOrganizationServiceAsync2 service,
        string entityLogicalName,
        string idAttribute,
        string nameAttribute,
        IEnumerable<Guid> ids,
        CancellationToken ct)
    {
        var idList = ids.Distinct().Where(id => id != Guid.Empty).ToList();
        if (idList.Count == 0) return [];
        if (idList.Count > 2000)
            throw new InvalidOperationException($"ConditionOperator.In limit exceeded: {idList.Count} IDs (max 2000). Solution has too many {entityLogicalName} orphans for name resolution.");

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
}
