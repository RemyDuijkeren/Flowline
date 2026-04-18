using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public interface IPluginSyncService
{
    Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        IsolationMode isolationMode);
}

public class PluginSyncService : IPluginSyncService
{
    readonly IAssemblyAnalysisService _analysisService;

    public PluginSyncService(IAssemblyAnalysisService analysisService)
    {
        _analysisService = analysisService;
    }

    public async Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        IsolationMode isolationMode)
    {
        var metadata = _analysisService.Analyze(dllPath, isolationMode);
        var assembly = await GetOrCreateAssembly(service, metadata, solutionName);

        var existingTypes = await GetPluginTypes(service, assembly.Id);
        var typeNames = existingTypes.ToDictionary(t => t.GetAttributeValue<string>("typename"), t => t);

        foreach (var plugin in metadata.Plugins)
        {
            Entity typeEntity;
            if (!typeNames.TryGetValue(plugin.FullName, out typeEntity!))
            {
                typeEntity = new Entity("plugintype");
                typeEntity["typename"] = plugin.FullName;
                typeEntity["name"] = plugin.FullName;
                typeEntity["friendlyname"] = plugin.Name;
                typeEntity["pluginassemblyid"] = assembly.ToEntityReference();
                typeEntity.Id = await service.CreateAsync(typeEntity);
            }

            // Sync Steps
            var existingSteps = await GetSteps(service, typeEntity.Id);
            var stepNames = existingSteps.ToDictionary(s => s.GetAttributeValue<string>("name"), s => s);

            foreach (var step in plugin.Steps)
            {
                Entity stepEntity;
                if (!stepNames.TryGetValue(step.Name, out stepEntity!))
                {
                    stepEntity = new Entity("sdkmessageprocessingstep");
                    stepEntity["name"] = step.Name;
                    stepEntity["plugintypeid"] = typeEntity.ToEntityReference();
                    stepEntity["stage"] = new OptionSetValue(step.Stage);
                    stepEntity["mode"] = new OptionSetValue(step.Mode);
                    stepEntity["rank"] = step.Order;
                    stepEntity["filteringattributes"] = step.FilteringAttributes;
                    stepEntity["configuration"] = step.Configuration;

                    // We need to look up SdkMessage and SdkMessageFilter IDs here in a real implementation
                    // Simplified for now: assuming message and entity lookup logic is available

                    await service.CreateAsync(stepEntity);
                }
                else
                {
                    // Update
                    stepEntity["stage"] = new OptionSetValue(step.Stage);
                    stepEntity["mode"] = new OptionSetValue(step.Mode);
                    stepEntity["rank"] = step.Order;
                    stepEntity["filteringattributes"] = step.FilteringAttributes;
                    stepEntity["configuration"] = step.Configuration;
                    await service.UpdateAsync(stepEntity);
                }
            }
        }
    }

    async Task<Entity> GetOrCreateAssembly(IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName)
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

            // entity["sourcetype"] = new OptionSetValue(0); // 0=Database (default), 4=File Store (NuGet package)
            // Nuget is stored in the pluginpackage table
            // Upload the .nupkg to the pluginpackage.package file column pluginpackage.package is a File column,
            // not a memo/base64 blob column. Microsoft documents it as a file column with max size 10 GB, and
            // Dataverse file columns must be uploaded using the file upload APIs (InitializeFileBlocksUpload / UploadBlock / CommitFileBlocksUpload) rather
            // than setting them directly in a normal create/update payload.

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

    async Task<List<Entity>> GetPluginTypes(IOrganizationServiceAsync2 service, Guid assemblyId)
    {
        var query = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("typename", "name"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);
        return (await service.RetrieveMultipleAsync(query)).Entities.ToList();
    }

    async Task<List<Entity>> GetSteps(IOrganizationServiceAsync2 service, Guid typeId)
    {
        var query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet("name", "stage", "mode", "rank", "filteringattributes", "configuration"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("plugintypeid", ConditionOperator.Equal, typeId);
        return (await service.RetrieveMultipleAsync(query)).Entities.ToList();
    }
}
