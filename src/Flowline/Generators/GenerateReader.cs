using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Services;

namespace Flowline.Generators;

public static class GenerateReader
{
    // componenttype = 1 = Entity in solutioncomponent
    private const int EntityComponentType = 1;
    private const int MaxConcurrentMetadataRequests = 20;

    /// <summary>
    /// Returns logical names of all entities registered as solution components for the given solution.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetSolutionEntityLogicalNamesAsync(
        IOrganizationServiceAsync2 service,
        Guid solutionId,
        CancellationToken cancellationToken = default)
    {
        // Step 1: all entity MetadataIds from solutioncomponent (paged — large solutions can exceed 5000)
        var componentQuery = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("solutionid", ConditionOperator.Equal, solutionId),
                    new ConditionExpression("componenttype", ConditionOperator.Equal, EntityComponentType)
                }
            }
        };

        var components = await service.RetrieveAllAsync(componentQuery, cancellationToken).ConfigureAwait(false);

        var metadataIds = components
            .Select(e => e.GetAttributeValue<Guid>("objectid"))
            .Where(id => id != Guid.Empty)
            .ToList();

        if (metadataIds.Count == 0)
            return [];

        // Step 2: resolve MetadataId → LogicalName in parallel, capped to avoid Dataverse throttling
        using var semaphore = new SemaphoreSlim(MaxConcurrentMetadataRequests, MaxConcurrentMetadataRequests);

        var tasks = metadataIds.Select(async id =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var req = new RetrieveEntityRequest
                {
                    MetadataId = id,
                    EntityFilters = EntityFilters.Entity,
                    RetrieveAsIfPublished = false
                };
                var resp = (RetrieveEntityResponse)await service.ExecuteAsync(req, cancellationToken).ConfigureAwait(false);
                return resp.EntityMetadata?.LogicalName;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var logicalNames = await Task.WhenAll(tasks).ConfigureAwait(false);

        return logicalNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Returns unique names (message names) of all custom APIs registered as solution components for the given solution.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetSolutionCustomApiMessageNamesAsync(
        IOrganizationServiceAsync2 service,
        Guid solutionId,
        CancellationToken cancellationToken = default)
    {
        // Join customapi → solutioncomponent via customapiid = objectid, filter by solutionid.
        // Does not filter by componenttype — avoids hardcoding the numeric code for CustomAPI.
        var query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet("uniquename")
        };

        var link = query.AddLink("solutioncomponent", "customapiid", "objectid", JoinOperator.Inner);
        link.LinkCriteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);

        var entities = await service.RetrieveAllAsync(query, cancellationToken).ConfigureAwait(false);

        return entities
            .Select(e => e.GetAttributeValue<string>("uniquename"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }
}
