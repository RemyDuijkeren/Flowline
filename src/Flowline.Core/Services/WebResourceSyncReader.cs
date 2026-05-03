using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class WebResourceSyncReader
{
    const int WebResourceComponentType = 61;
    const string DefaultSolutionUniqueName = "Default";

    public async Task<WebResourceSyncSnapshot> LoadSnapshotAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        string? patchSolutionName,
        CancellationToken cancellationToken = default)
    {
        var baseSolution = await GetSolutionInfoAsync(service, solutionName, cancellationToken).ConfigureAwait(false);

        WebResourceSolutionInfo? patchSolution = null;
        Task<IReadOnlyList<Entity>> patchResourcesTask = Task.FromResult<IReadOnlyList<Entity>>([]);
        if (!string.IsNullOrWhiteSpace(patchSolutionName))
        {
            patchSolution = await GetSolutionInfoAsync(service, patchSolutionName, cancellationToken).ConfigureAwait(false);
            patchResourcesTask = GetWebResourcesForSolutionAsync(service, patchSolution.Id, cancellationToken);
        }

        var baseResourcesTask = GetWebResourcesForSolutionAsync(service, baseSolution.Id, cancellationToken);
        var localResourcesTask = Task.Run(() => GetLocalWebResources(webresourceRoot, $"{baseSolution.PublisherPrefix}_{solutionName}"), cancellationToken);

        await Task.WhenAll(baseResourcesTask, patchResourcesTask, localResourcesTask).ConfigureAwait(false);

        var patchResources = patchResourcesTask.Result;
        var patchIds = patchResources.Select(e => e.Id).ToHashSet();
        var combined = patchResources
            .Select(e => (Entity: e, IsInPatch: true))
            .Concat(baseResourcesTask.Result.Where(e => !patchIds.Contains(e.Id)).Select(e => (Entity: e, IsInPatch: false)))
            .ToList();

        var targetSolutionName = patchSolution?.UniqueName ?? solutionName;
        var ownershipTasks = combined.Select(async item =>
        {
            var ownership = await GetOwnershipAsync(service, item.Entity.Id, targetSolutionName, cancellationToken).ConfigureAwait(false);
            return ToDataverseWebResource(item.Entity, item.IsInPatch, ownership);
        });

        var dataverseResources = await Task.WhenAll(ownershipTasks).ConfigureAwait(false);

        return new WebResourceSyncSnapshot(
            baseSolution,
            patchSolution,
            localResourcesTask.Result,
            dataverseResources.ToDictionary(r => r.Name, r => r, StringComparer.OrdinalIgnoreCase).AsReadOnly());
    }

    async Task<WebResourceSolutionInfo> GetSolutionInfoAsync(
        IOrganizationServiceAsync2 service, string uniqueName, CancellationToken cancellationToken)
    {
        var query = new QueryExpression("solution")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("solutionid", "uniquename", "ismanaged"),
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

        return new WebResourceSolutionInfo(solution.Id, uniqueName, prefix, solution.GetAttributeValue<bool>("ismanaged"));
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

    static DataverseWebResource ToDataverseWebResource(Entity entity, bool isInPatch, WebResourceOwnership ownership) =>
        new(
            entity.Id,
            entity.GetAttributeValue<string>("name"),
            entity.GetAttributeValue<string>("displayname"),
            entity.GetAttributeValue<OptionSetValue>("webresourcetype")?.Value ?? 0,
            entity.GetAttributeValue<string>("content"),
            isInPatch,
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
            result[name] = LocalResourceFromFile(file, name);
        }

        return result.AsReadOnly();
    }

    static LocalWebResource LocalResourceFromFile(string path, string name)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (!Enum.TryParse<WebResourceType>(ext, out var type))
            throw new InvalidOperationException($"Unsupported web resource extension '.{ext.ToLowerInvariant()}' for '{path}'.");

        var silverlightVersion = type == WebResourceType.XAP ? "4.0" : null;
        return new LocalWebResource(
            name,
            path,
            Path.GetFileName(name),
            (int)type,
            Convert.ToBase64String(File.ReadAllBytes(path)),
            silverlightVersion);
    }

    static T? GetAliasedValue<T>(Entity entity, string attributeName)
    {
        if (!entity.Attributes.TryGetValue(attributeName, out var value))
            return default;
        return value is AliasedValue aliased ? (T?)aliased.Value : (T?)value;
    }
}
