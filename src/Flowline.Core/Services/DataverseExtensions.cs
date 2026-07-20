using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.Services;

public static class DataverseExtensions
{
    /// <summary>
    /// Retrieves all pages of a query, returning a flat list of all matching entities.
    /// Use instead of RetrieveMultipleAsync when the result set may exceed the default page size (5000).
    /// </summary>
    public static async Task<List<Entity>> RetrieveAllAsync(
        this IOrganizationServiceAsync2 service,
        QueryExpression query,
        CancellationToken cancellationToken = default)
    {
        var all = new List<Entity>();
        query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1, ReturnTotalRecordCount = false };

        EntityCollection page;
        do
        {
            page = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Entities);
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = page.PagingCookie;
        } while (page.MoreRecords);

        return all;
    }

    /// <summary>
    /// Reads an attribute that may have come back wrapped in an <see cref="AliasedValue"/> — as
    /// linked-entity columns do — unwrapping it when present. Returns default when absent.
    /// </summary>
    public static T? GetAliasedValue<T>(this Entity entity, string attributeName)
    {
        if (!entity.Attributes.TryGetValue(attributeName, out var value))
            return default;
        return value is AliasedValue aliased ? (T?)aliased.Value : (T?)value;
    }
}
