using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public interface IWorkflowSyncService
{
    Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        IsolationMode isolationMode);
}

public class WorkflowSyncService : IWorkflowSyncService
{
    private readonly IAssemblyAnalysisService _analysisService;

    public WorkflowSyncService(IAssemblyAnalysisService analysisService)
    {
        _analysisService = analysisService;
    }

    public async Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        IsolationMode isolationMode)
    {
        // 1. Analyze Assembly
        var metadata = _analysisService.Analyze(dllPath, isolationMode);
        
        // 2. Get/Create Assembly in Dataverse
        var assemblyEntity = await GetOrCreateAssembly(service, metadata, solutionName);
        
        // 3. Sync Workflow Activities (PluginTypes)
        var existingTypes = await GetPluginTypes(service, assemblyEntity.Id);
        var existingTypeNames = existingTypes.ToDictionary(t => t.GetAttributeValue<string>("typename"), t => t);

        foreach (var plugin in metadata.Plugins)
        {
            if (!existingTypeNames.ContainsKey(plugin.FullName))
            {
                // Create
                var pt = new Entity("plugintype");
                pt["name"] = plugin.FullName;
                pt["typename"] = plugin.FullName;
                pt["friendlyname"] = Guid.NewGuid().ToString();
                pt["pluginassemblyid"] = assemblyEntity.ToEntityReference();
                pt["workflowactivitygroupname"] = $"{metadata.Name} ({metadata.Version})";
                
                await service.CreateAsync(pt);
            }
        }

        // Delete obsolete activities
        var localNames = metadata.Plugins.Select(p => p.FullName).ToHashSet();
        foreach (var typeName in existingTypeNames.Keys.Except(localNames))
        {
            await service.DeleteAsync("plugintype", existingTypeNames[typeName].Id);
        }
    }

    private async Task<Entity> GetOrCreateAssembly(IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName)
    {
        var query = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("pluginassemblyid", "name", "version", "content"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("name", ConditionOperator.Equal, metadata.Name);

        var result = await service.RetrieveMultipleAsync(query);
        var existing = result.Entities.FirstOrDefault();

        if (existing == null)
        {
            var entity = new Entity("pluginassembly");
            entity["name"] = metadata.Name;
            entity["content"] = Convert.ToBase64String(metadata.Content);
            entity["version"] = metadata.Version;
            entity["isolationmode"] = new OptionSetValue((int)metadata.IsolationMode);
            
            var createReq = new CreateRequest { Target = entity };
            createReq["SolutionUniqueName"] = solutionName;
            var response = (CreateResponse)await service.ExecuteAsync(createReq);
            entity.Id = response.id;
            return entity;
        }
        else
        {
            existing["content"] = Convert.ToBase64String(metadata.Content);
            existing["version"] = metadata.Version;
            await service.UpdateAsync(existing);
            return existing;
        }
    }

    private async Task<List<Entity>> GetPluginTypes(IOrganizationServiceAsync2 service, Guid assemblyId)
    {
        var query = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("typename", "name"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);
        
        var result = await service.RetrieveMultipleAsync(query);
        return result.Entities.ToList();
    }
}
