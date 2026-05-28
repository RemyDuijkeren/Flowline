using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.Services;

public static class OrganizationServiceExtensions
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
}
