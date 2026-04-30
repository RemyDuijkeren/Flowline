using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class WebResourceSyncService(IFlowlineOutput output)
{
    public async Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string webresourceRoot,
        string solutionName,
        string? patchSolutionName = null,
        bool publishAfterSync = true)
    {
        // 1. Get Solution Info
        var (solutionId, prefix) = await GetSolutionIdAndPrefix(service, solutionName).ConfigureAwait(false);

        // 2. Get CRM Web Resources
        var crmWebResources = await GetWebResourcesForSolution(service, solutionId).ConfigureAwait(false);

        List<Entity> patchWebResources = new();
        if (patchSolutionName != null)
        {
            var (patchSolutionId, _) = await GetSolutionIdAndPrefix(service, patchSolutionName).ConfigureAwait(false);
            patchWebResources = await GetWebResourcesForSolution(service, patchSolutionId).ConfigureAwait(false);
        }

        // Combine and resolve overrides (patch over base)
        var wrPatchIds = patchWebResources.Select(x => x.Id).ToHashSet();
        var combinedWebResources = patchWebResources.Concat(crmWebResources.Where(x => !wrPatchIds.Contains(x.Id))).ToList();

        // 3. Get Local Web Resources
        var wrPrefix = $"{prefix}_{solutionName}";
        var localWebResources = GetLocalWebResources(webresourceRoot, wrPrefix);

        // 4. Calculate Actions
        var crmNames = combinedWebResources.ToDictionary(x => x.GetAttributeValue<string>("name"), x => x);
        var localNames = localWebResources.Keys.ToHashSet();

        var actions = new List<(WebResourceAction Action, Entity Entity)>();

        // Create
        foreach (var name in localNames.Except(crmNames.Keys))
        {
            var entity = LocalResourceToEntity(localWebResources[name], name);
            actions.Add((WebResourceAction.Create, entity));
        }

        // Delete
        foreach (var name in crmNames.Keys.Except(localNames))
        {
            actions.Add((WebResourceAction.Delete, crmNames[name]));
        }

        // Update
        foreach (var name in localNames.Intersect(crmNames.Keys))
        {
            var crmWr = crmNames[name];
            var localWr = LocalResourceToEntity(localWebResources[name], name);

            var crmContent = crmWr.GetAttributeValue<string>("content");
            var localContent = localWr.GetAttributeValue<string>("content");
            var crmDisplayName = crmWr.GetAttributeValue<string>("displayname");
            var localDisplayName = localWr.GetAttributeValue<string>("displayname");

            if (crmContent != localContent || crmDisplayName != localDisplayName)
            {
                crmWr["content"] = localContent;
                crmWr["displayname"] = localDisplayName;
                
                var action = patchWebResources.Any(x => x.Id == crmWr.Id) 
                    ? WebResourceAction.Update 
                    : WebResourceAction.UpdateAndAddToPatchSolution;
                
                actions.Add((action, crmWr));
            }
        }

        // 5. Execute Actions
        if (actions.Count == 0)
        {
            output.Skip("Web resources already up to date — skipping");
            return;
        }

        foreach (var (action, entity) in actions)
        {
            var name = entity.GetAttributeValue<string>("name");
            switch (action)
            {
                case WebResourceAction.Create:
                    var createReq = new CreateRequest { Target = entity };
                    createReq["SolutionUniqueName"] = patchSolutionName ?? solutionName;
                    await service.ExecuteAsync(createReq).ConfigureAwait(false);
                    output.Info($"[green]Created [bold]{name}[/][/]");
                    break;

                case WebResourceAction.Update:
                    await service.UpdateAsync(entity).ConfigureAwait(false);
                    output.Info($"[green]Updated [bold]{name}[/][/]");
                    break;

                case WebResourceAction.UpdateAndAddToPatchSolution:
                    await service.UpdateAsync(entity).ConfigureAwait(false);
                    if (patchSolutionName != null)
                    {
                        var addReq = new OrganizationRequest("AddSolutionComponent");
                        addReq["ComponentId"] = entity.Id;
                        addReq["ComponentType"] = 61; // WebResource
                        addReq["SolutionUniqueName"] = patchSolutionName;
                        addReq["AddRequiredComponents"] = false;
                        await service.ExecuteAsync(addReq).ConfigureAwait(false);
                    }
                    output.Info($"[green]Updated [bold]{name}[/][/]");
                    break;

                case WebResourceAction.Delete:
                    await service.DeleteAsync(entity.LogicalName, entity.Id).ConfigureAwait(false);
                    output.Info($"[green]Deleted [bold]{name}[/][/]");
                    break;
            }
        }

        // 6. Publish
        if (publishAfterSync)
        {
            var pubReq = new OrganizationRequest("PublishAllXml");
            await service.ExecuteAsync(pubReq).ConfigureAwait(false);
        }
    }

    private async Task<(Guid Id, string Prefix)> GetSolutionIdAndPrefix(IOrganizationServiceAsync2 service, string uniqueName)
    {
        var query = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("solutionid"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);

        var linkPublisher = query.AddLink("publisher", "publisherid", "publisherid");
        linkPublisher.Columns.AddColumns("customizationprefix");
        linkPublisher.EntityAlias = "publisher";

        var result = await service.RetrieveMultipleAsync(query).ConfigureAwait(false);
        if (result.Entities.Count == 0)
            throw new Exception($"Solution {uniqueName} not found.");

        var solution = result.Entities[0];
        var prefix = (string)((AliasedValue)solution["publisher.customizationprefix"]).Value;
        return (solution.Id, prefix);
    }

    private async Task<List<Entity>> GetWebResourcesForSolution(IOrganizationServiceAsync2 service, Guid solutionId)
    {
        var query = new QueryExpression("webresource")
        {
            ColumnSet = new ColumnSet("name", "content", "displayname", "webresourcetype"),
            Criteria = new FilterExpression()
        };

        var linkComponent = query.AddLink("solutioncomponent", "webresourceid", "objectid");
        linkComponent.LinkCriteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
        linkComponent.LinkCriteria.AddCondition("componenttype", ConditionOperator.Equal, 61); // WebResource

        var result = await service.RetrieveMultipleAsync(query).ConfigureAwait(false);
        return result.Entities.ToList();
    }

    private Dictionary<string, string> GetLocalWebResources(string root, string prefix)
    {
        var result = new Dictionary<string, string>();
        var extensions = Enum.GetNames<WebResourceType>().Select(e => "." + e.ToLower()).Distinct().ToList();
        
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()));

        foreach (var file in files)
        {
            if (file.EndsWith("_nosync", StringComparison.OrdinalIgnoreCase)) continue;

            var relativePath = Path.GetRelativePath(root, file).Replace("\\", "/");
            var name = $"{prefix}/{relativePath}";
            result[name] = file;
        }

        return result;
    }

    private Entity LocalResourceToEntity(string path, string name)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (!Enum.TryParse<WebResourceType>(ext, out var type))
        {
            // Fallback for some common extensions if needed
        }

        var entity = new Entity("webresource");
        entity["name"] = name;
        entity["displayname"] = Path.GetFileName(name);
        entity["webresourcetype"] = new OptionSetValue((int)type);
        entity["content"] = Convert.ToBase64String(File.ReadAllBytes(path));

        if (type == WebResourceType.XAP)
        {
            entity["silverlightversion"] = "4.0";
        }

        return entity;
    }
}
