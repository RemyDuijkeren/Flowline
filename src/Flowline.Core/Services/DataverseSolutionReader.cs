using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class DataverseSolutionReader
{
    public async Task<DataverseSolutionInfo> GetSolutionInfoAsync(
        IOrganizationServiceAsync2 service,
        string uniqueName,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("solution")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("solutionid", "uniquename", "ismanaged", "parentsolutionid"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, uniqueName) } }
        };

        var linkPublisher = query.AddLink("publisher", "publisherid", "publisherid");
        linkPublisher.Columns.AddColumns("customizationprefix");
        linkPublisher.EntityAlias = "publisher";

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var solution = result.Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{uniqueName}' not found in Dataverse.");

        var prefix = GetAliasedValue<string>(solution, "publisher.customizationprefix")
            ?? throw new InvalidOperationException($"Could not read publisher prefix for solution '{uniqueName}'.");

        return new DataverseSolutionInfo(
            solution.Id,
            uniqueName,
            prefix,
            solution.GetAttributeValue<bool>("ismanaged"),
            solution.GetAttributeValue<EntityReference>("parentsolutionid"));
    }

    public async Task<DataverseSolutionInfo> GetSupportedSolutionInfoAsync(
        IOrganizationServiceAsync2 service,
        string uniqueName,
        CancellationToken cancellationToken = default)
    {
        var solution = await GetSolutionInfoAsync(service, uniqueName, cancellationToken).ConfigureAwait(false);
        if (solution.ParentSolution != null)
            throw new InvalidOperationException(
                $"Solution '{uniqueName}' is a patch solution. Flowline does not support Dataverse patch solutions; use a Git branch, bump the solution version, and deploy a normal solution update.");

        return solution;
    }

    static T? GetAliasedValue<T>(Entity entity, string attributeName)
    {
        if (!entity.Attributes.TryGetValue(attributeName, out var value))
            return default;
        return value is AliasedValue aliased ? (T?)aliased.Value : (T?)value;
    }
}
