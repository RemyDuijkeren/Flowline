using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class PluginRegistrationService(IFlowlineOutput output)
{
    const string FlowlineMarker = "[flowline]";

    static readonly Dictionary<string, string> MessagePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Assign"]                  = "Target",
        ["Create"]                  = "id",
        ["Delete"]                  = "Target",
        ["DeliverIncoming"]         = "emailid",
        ["DeliverPromote"]          = "emailid",
        ["Merge"]                   = "Target",
        ["Route"]                   = "Target",
        ["Send"]                    = "emailid",
        ["SetState"]                = "entityMoniker",
        ["SetStateDynamicEntity"]   = "entityMoniker",
        ["Update"]                  = "Target",
    };

    public async Task SyncAsync(IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName, bool save = false)
    {
        var assembly = await GetOrRegisterAssembly(service, metadata, solutionName);
        await RegisterPluginTypesAsync(service, metadata, assembly, save);
    }

    async Task RegisterPluginTypesAsync(IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, Entity assembly, bool save)
    {
        var existingTypes = await GetPluginTypes(service, assembly.Id);
        var typeNames = existingTypes.ToDictionary(t => t.GetAttributeValue<string>("typename"), t => t);

        var messageCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var filterCache = new Dictionary<(Guid messageId, string entityName), Guid?>();

        foreach (var plugin in metadata.Plugins)
        {
            // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/reference/entities/plugintype
            if (!typeNames.TryGetValue(plugin.FullName, out var typeEntity))
            {
                typeEntity = new Entity("plugintype")
                {
                    ["typename"] = plugin.FullName,
                    ["name"] = plugin.FullName,
                    ["friendlyname"] = plugin.Name,
                    ["pluginassemblyid"] = assembly.ToEntityReference(),
                    ["description"] = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };

                if (plugin.IsWorkflow)
                    typeEntity["workflowactivitygroupname"] = $"{metadata.Name} ({metadata.Version})";

                typeEntity.Id = await service.CreateAsync(typeEntity);
            }

            if (!plugin.IsWorkflow)
                await RegisterPluginStepsAsync(service, typeEntity, plugin.Steps, messageCache, filterCache, save);
        }

        // Remove obsolete plugin types (non-workflow only, unless save mode preserves them)
        if (!save)
        {
            var localNames = metadata.Plugins.Select(p => p.FullName).ToHashSet();
            foreach (var obsolete in existingTypes.Where(t => !localNames.Contains(t.GetAttributeValue<string>("typename"))))
            {
                if (!obsolete.GetAttributeValue<bool>("isworkflowactivity"))
                {
                    var steps = await GetSteps(service, obsolete.Id);
                    foreach (var step in steps)
                        await service.DeleteAsync("sdkmessageprocessingstep", step.Id);
                }

                await service.DeleteAsync("plugintype", obsolete.Id);
            }
        }
        else
        {
            var localNames = metadata.Plugins.Select(p => p.FullName).ToHashSet();
            foreach (var obsolete in existingTypes.Where(t => !localNames.Contains(t.GetAttributeValue<string>("typename"))))
                output.Skip($"Plugin type '{obsolete.GetAttributeValue<string>("typename")}' not in source — kept (--save)");
        }
    }

    async Task RegisterPluginStepsAsync(
        IOrganizationServiceAsync2 service,
        Entity typeEntity,
        List<PluginStepMetadata> steps,
        Dictionary<string, Guid> messageCache,
        Dictionary<(Guid messageId, string entityName), Guid?> filterCache,
        bool save)
    {
        var existingSteps = await GetSteps(service, typeEntity.Id);
        var stepNames = existingSteps.ToDictionary(s => s.GetAttributeValue<string>("name"), s => s);

        foreach (var step in steps)
        {
            if (!stepNames.TryGetValue(step.Name, out var stepEntity))
            {
                if (!messageCache.TryGetValue(step.Message, out var messageId))
                {
                    messageId = await LookupSdkMessageIdAsync(service, step.Message);
                    messageCache[step.Message] = messageId;
                }

                if (!filterCache.TryGetValue((messageId, step.EntityName), out var filterId))
                {
                    filterId = await LookupSdkMessageFilterIdAsync(service, messageId, step.EntityName);
                    filterCache[(messageId, step.EntityName)] = filterId;
                }

                var entity = new Entity("sdkmessageprocessingstep")
                {
                    ["name"] = step.Name,
                    ["plugintypeid"] = typeEntity.ToEntityReference(),
                    ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
                    ["stage"] = new OptionSetValue(step.Stage),
                    ["mode"] = new OptionSetValue(step.Mode),
                    ["rank"] = step.Order,
                    ["filteringattributes"] = step.FilteringAttributes,
                    ["configuration"] = step.Configuration,
                    ["description"] = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };
                if (filterId.HasValue)
                    entity["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);

                stepEntity = entity;
                stepEntity.Id = await service.CreateAsync(stepEntity);
            }
            else
            {
                stepEntity["stage"] = new OptionSetValue(step.Stage);
                stepEntity["mode"] = new OptionSetValue(step.Mode);
                stepEntity["rank"] = step.Order;
                stepEntity["filteringattributes"] = step.FilteringAttributes;
                stepEntity["configuration"] = step.Configuration;
                await service.UpdateAsync(stepEntity);
            }

            await RegisterImagesAsync(service, stepEntity, step.Images, step.Message, save);
        }

        // DLL is the source of truth — delete any step that is no longer in local metadata
        var localStepNames = steps.Select(s => s.Name).ToHashSet();
        var obsoleteSteps = existingSteps
            .Where(s => !localStepNames.Contains(s.GetAttributeValue<string>("name")))
            .ToList();
        if (!save)
        {
            foreach (var obsolete in obsoleteSteps)
                await service.DeleteAsync("sdkmessageprocessingstep", obsolete.Id);
        }
        else
        {
            foreach (var obsolete in obsoleteSteps)
                output.Skip($"Step '{obsolete.GetAttributeValue<string>("name")}' not in source — kept (--save)");
        }
    }

    async Task RegisterImagesAsync(IOrganizationServiceAsync2 service, Entity stepEntity, List<PluginImageMetadata> images, string message, bool save)
    {
        var existing = await GetImages(service, stepEntity.Id);
        var existingByName = existing.ToDictionary(e => e.GetAttributeValue<string>("name"), e => e);

        foreach (var image in images)
        {
            if (!existingByName.TryGetValue(image.Name, out var imageEntity))
            {
                if (!MessagePropertyNames.TryGetValue(message, out var propertyName))
                    throw new InvalidOperationException($"Message '{message}' does not support step images.");

                var entity = new Entity("sdkmessageprocessingstepimage")
                {
                    ["name"]                          = image.Name,
                    ["entityalias"]                   = image.Alias,
                    ["imagetype"]                     = new OptionSetValue(image.ImageType),
                    ["attributes"]                    = image.Attributes,
                    ["messagepropertyname"]           = propertyName,
                    ["sdkmessageprocessingstepid"]    = stepEntity.ToEntityReference()
                };
                await service.CreateAsync(entity);
            }
            else
            {
                var changed =
                    imageEntity.GetAttributeValue<string>("entityalias") != image.Alias ||
                    (imageEntity.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? 0) != image.ImageType ||
                    imageEntity.GetAttributeValue<string>("attributes") != image.Attributes;

                if (changed)
                {
                    imageEntity["entityalias"]  = image.Alias;
                    imageEntity["imagetype"]    = new OptionSetValue(image.ImageType);
                    imageEntity["attributes"]   = image.Attributes;
                    await service.UpdateAsync(imageEntity);
                }
            }
        }

        var localNames = images.Select(i => i.Name).ToHashSet();
        var obsolete = existing.Where(e => !localNames.Contains(e.GetAttributeValue<string>("name"))).ToList();
        if (!save)
        {
            foreach (var e in obsolete)
                await service.DeleteAsync("sdkmessageprocessingstepimage", e.Id);
        }
        else
        {
            foreach (var e in obsolete)
                output.Skip($"Image '{e.GetAttributeValue<string>("name")}' not in source — kept (--save)");
        }
    }

    async Task<List<Entity>> GetImages(IOrganizationServiceAsync2 service, Guid stepId)
    {
        var query = new QueryExpression("sdkmessageprocessingstepimage")
        {
            ColumnSet = new ColumnSet("name", "entityalias", "imagetype", "attributes"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("sdkmessageprocessingstepid", ConditionOperator.Equal, stepId);
        return (await service.RetrieveMultipleAsync(query)).Entities.ToList();
    }

    async Task<Entity> GetOrRegisterAssembly(IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName)
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
            var entity = new Entity("pluginassembly")
            {
                ["name"] = metadata.Name,
                ["content"] = Convert.ToBase64String(metadata.Content),
                ["version"] = metadata.Version,
                ["isolationmode"] = new OptionSetValue(2) // 2 = Sandbox (cloud only)
            };

            // entity["sourcetype"] = new OptionSetValue(0); // 0=Database (default), 4=File Store (NuGet package)
            // Nuget is stored in the pluginpackage table
            // Upload the .nupkg to the pluginpackage.package file column pluginpackage.package is a File column,
            // not a memo/base64 blob column. Microsoft documents it as a file column with max size 10 GB, and
            // Dataverse file columns must be uploaded using the file upload APIs (InitializeFileBlocksUpload / UploadBlock / CommitFileBlocksUpload) rather
            // than setting them directly in a normal create/update payload.

            var createReq = new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName };
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
            ColumnSet = new ColumnSet("typename", "name", "isworkflowactivity"),
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

    async Task<Guid> LookupSdkMessageIdAsync(IOrganizationServiceAsync2 service, string messageName)
    {
        var query = new QueryExpression("sdkmessage")
        {
            ColumnSet = new ColumnSet("sdkmessageid"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("name", ConditionOperator.Equal, messageName);

        var result = await service.RetrieveMultipleAsync(query);
        var message = result.Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Dataverse message '{messageName}' not found in sdkmessage.");

        return message.Id;
    }

    async Task<Guid?> LookupSdkMessageFilterIdAsync(IOrganizationServiceAsync2 service, Guid messageId, string entityName)
    {
        var query = new QueryExpression("sdkmessagefilter")
        {
            ColumnSet = new ColumnSet("sdkmessagefilterid"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, messageId);
        query.Criteria.AddCondition("primaryobjecttypecode", ConditionOperator.Equal, entityName);

        var result = await service.RetrieveMultipleAsync(query);
        return result.Entities.FirstOrDefault()?.Id;
    }
}
