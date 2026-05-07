using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class WebResourceReader
{
    const int WebResourceComponentType = 61;
    const string DefaultSolutionUniqueName = "Default";
    readonly SolutionReader _solutionReader = new();

    public async Task<WebResourceSyncSnapshot> LoadSnapshotAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        CancellationToken cancellationToken = default)
    {
        var baseSolution = await _solutionReader.GetSupportedSolutionInfoAsync(service, solutionName, cancellationToken).ConfigureAwait(false);

        var baseResourcesTask = GetWebResourcesForSolutionAsync(service, baseSolution.Id, cancellationToken);
        var localResourcesTask = Task.Run(() => GetLocalWebResources(webresourceRoot, $"{baseSolution.PublisherPrefix}_{solutionName}"), cancellationToken);

        await Task.WhenAll(baseResourcesTask, localResourcesTask).ConfigureAwait(false);

        var ownershipTasks = baseResourcesTask.Result.Select(async entity =>
        {
            var ownership = await GetOwnershipAsync(service, entity.Id, solutionName, cancellationToken).ConfigureAwait(false);
            return ToDataverseWebResource(entity, ownership);
        });

        var dataverseResources = await Task.WhenAll(ownershipTasks).ConfigureAwait(false);

        return new WebResourceSyncSnapshot(
            baseSolution,
            localResourcesTask.Result,
            dataverseResources.ToDictionary(r => r.Name, r => r, StringComparer.OrdinalIgnoreCase).AsReadOnly());
    }

    async Task<IReadOnlyList<Entity>> GetWebResourcesForSolutionAsync(
        IOrganizationServiceAsync2 service, Guid solutionId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("webresource")
        {
            ColumnSet = new ColumnSet("name", "content", "displayname", "webresourcetype", "silverlightversion")
        };

        var linkComponent = query.AddLink("solutioncomponent", "webresourceid", "objectid");
        linkComponent.LinkCriteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
        linkComponent.LinkCriteria.AddCondition("componenttype", ConditionOperator.Equal, WebResourceComponentType);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<WebResourceOwnership> GetOwnershipAsync(
        IOrganizationServiceAsync2 service, Guid webResourceId, string currentSolutionName, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet(false),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("objectid", ConditionOperator.Equal, webResourceId),
                    new ConditionExpression("componenttype", ConditionOperator.Equal, WebResourceComponentType)
                }
            }
        };

        var solutionLink = query.AddLink("solution", "solutionid", "solutionid");
        solutionLink.Columns = new ColumnSet("uniquename", "ismanaged");
        solutionLink.EntityAlias = "solution";
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.NotEqual, DefaultSolutionUniqueName);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var solutionRefs = result.Entities
            .Select(e => new
            {
                Name = GetAliasedValue<string>(e, "solution.uniquename"),
                IsManaged = GetAliasedValue<bool>(e, "solution.ismanaged")
            })
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .ToList();

        var unmanaged = solutionRefs.Where(s => s.IsManaged == false).ToList();
        var isInCurrent = unmanaged.Any(s => string.Equals(s.Name, currentSolutionName, StringComparison.OrdinalIgnoreCase));
        return new WebResourceOwnership(unmanaged.Count, isInCurrent);
    }

    static DataverseWebResource ToDataverseWebResource(Entity entity, WebResourceOwnership ownership) =>
        new(
            entity.Id,
            entity.GetAttributeValue<string>("name"),
            entity.GetAttributeValue<string>("displayname"),
            (WebResourceType)(entity.GetAttributeValue<OptionSetValue>("webresourcetype")?.Value ?? 0),
            entity.GetAttributeValue<string>("content"),
            entity,
            ownership);

    static IReadOnlyDictionary<string, LocalWebResource> GetLocalWebResources(string root, string prefix)
    {
        if (!Directory.Exists(root))
            return new Dictionary<string, LocalWebResource>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

        var result = new Dictionary<string, LocalWebResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (Path.GetFileNameWithoutExtension(file).EndsWith("_nosync", StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(root, file).Replace("\\", "/");
            var name = $"{prefix}/{relativePath}";
            result[name] = LocalResourceFromFile(file, name, relativePath);
        }

        return result.AsReadOnly();
    }

    static LocalWebResource LocalResourceFromFile(string path, string name, string relativePath)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (!Enum.TryParse<WebResourceType>(ext, true, out var type))
            type = WebResourceType.Unknown;

        return new LocalWebResource(
            name,
            relativePath,
            path,
            Path.GetFileName(name),
            type,
            Convert.ToBase64String(File.ReadAllBytes(path)));
    }

    static T? GetAliasedValue<T>(Entity entity, string attributeName)
    {
        if (!entity.Attributes.TryGetValue(attributeName, out var value))
            return default;
        return value is AliasedValue aliased ? (T?)aliased.Value : (T?)value;
    }
}
