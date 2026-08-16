using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Services;

namespace Flowline.Core.OrphanCleanup;

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
        CancellationToken ct) =>
        (await GetEntityNamesAndRowIdsAsync(service, entityLogicalName, idAttribute, nameAttribute, ids, ct).ConfigureAwait(false)).Names;

    /// <summary>
    /// Same query as <see cref="GetEntityNamesAsync"/>, additionally returning every row id the query
    /// matched. A row with a null or empty name is dropped from Names but kept in RowIds — for a caller
    /// tracking which candidates a table claimed, the row existing at all is the evidence, independent of
    /// whether a name came back.
    /// </summary>
    public static async Task<(Dictionary<Guid, string> Names, HashSet<Guid> RowIds)> GetEntityNamesAndRowIdsAsync(
        IOrganizationServiceAsync2 service,
        string entityLogicalName,
        string idAttribute,
        string nameAttribute,
        IEnumerable<Guid> ids,
        CancellationToken ct)
    {
        var idList = ids.Distinct().Where(id => id != Guid.Empty).ToList();
        if (idList.Count == 0) return ([], []);
        EnsureInLimit(idList.Count, "IDs", $"Solution has too many {entityLogicalName} orphans for name resolution.");

        var query = new QueryExpression(entityLogicalName)
        {
            ColumnSet = new ColumnSet(nameAttribute),
            Criteria  = { Conditions = { new ConditionExpression(idAttribute, ConditionOperator.In, idList.Select(id => (object)id).ToArray()) } }
        };

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        var names = entities
            .Where(e => !string.IsNullOrEmpty(e.GetAttributeValue<string>(nameAttribute)))
            .ToDictionary(e => e.Id, e => e.GetAttributeValue<string>(nameAttribute)!);
        return (names, entities.Select(e => e.Id).ToHashSet());
    }

    /// <summary>Dataverse's practical ConditionOperator.In value-count ceiling.</summary>
    public const int ConditionOperatorInLimit = 2000;

    /// <summary>
    /// Throws when a batch would exceed <see cref="ConditionOperatorInLimit"/>. <paramref name="unit"/>
    /// names what is being counted ("IDs", "names"); <paramref name="detail"/> says which batch overflowed.
    /// Callers that want an oversized batch to degrade rather than abort call this inside their own
    /// <c>DataverseFaultTolerance.TryQueryAsync</c> wrapper.
    /// </summary>
    public static void EnsureInLimit(int count, string unit, string detail)
    {
        if (count > ConditionOperatorInLimit)
            throw new InvalidOperationException(
                $"ConditionOperator.In limit exceeded: {count} {unit} (max {ConditionOperatorInLimit}). {detail}");
    }
}
