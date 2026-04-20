using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class PluginRegistrationService(IFlowlineOutput output)
{
    internal const string FlowlineMarker = "[flowline]";

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

    public async Task SyncAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        string solutionName,
        bool save = false)
    {
        var assembly = await GetOrRegisterAssembly(service, metadata, solutionName);
        await RegisterPluginTypesAsync(service, metadata, assembly, save);

        if (metadata.CustomApis.Count > 0)
        {
            var prefix = await GetPublisherPrefixAsync(service, solutionName);
            await RegisterCustomApisAsync(service, metadata.CustomApis, assembly.Id, prefix, solutionName, save);
        }
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

    async Task RegisterCustomApisAsync(
        IOrganizationServiceAsync2 service,
        List<CustomApiMetadata> customApis,
        Guid assemblyId,
        string prefix,
        string solutionName,
        bool save)
    {
        var pluginTypeMap = (await GetPluginTypes(service, assemblyId))
            .ToDictionary(t => t.GetAttributeValue<string>("typename"), t => t);

        var existing = await GetCustomApis(service, assemblyId);
        var existingByUniqueName = existing.ToDictionary(
            e => e.GetAttributeValue<string>("uniquename"), e => e);

        foreach (var api in customApis)
        {
            var uniqueName = $"{prefix}_{api.UniqueName}";

            if (!pluginTypeMap.TryGetValue(api.PluginTypeFullName, out var pluginType))
            {
                output.Info($"[yellow]Warning:[/] Plugin type '{api.PluginTypeFullName}' not found — skipping Custom API '{uniqueName}'.");
                continue;
            }

            if (!existingByUniqueName.TryGetValue(uniqueName, out var existingApi))
                await CreateCustomApiAsync(service, api, uniqueName, pluginType, solutionName);
            else
                await SyncExistingCustomApiAsync(service, api, uniqueName, existingApi, pluginType, save);
        }

        var localUniqueNames = customApis.Select(a => $"{prefix}_{a.UniqueName}").ToHashSet();
        var obsolete = existing.Where(e => !localUniqueNames.Contains(e.GetAttributeValue<string>("uniquename"))).ToList();
        if (!save)
        {
            foreach (var e in obsolete)
                await DeleteCustomApiAsync(service, e.Id);
        }
        else
        {
            foreach (var e in obsolete)
                output.Skip($"Custom API '{e.GetAttributeValue<string>("uniquename")}' not in source — kept (--save)");
        }
    }

    async Task CreateCustomApiAsync(
        IOrganizationServiceAsync2 service,
        CustomApiMetadata api,
        string uniqueName,
        Entity pluginType,
        string solutionName)
    {
        var entity = new Entity("customapi")
        {
            ["uniquename"]                      = uniqueName,
            ["name"]                            = uniqueName,
            ["displayname"]                     = api.DisplayName,
            ["description"]                     = api.Description,
            ["bindingtype"]                     = new OptionSetValue(api.BindingType),
            ["boundentitylogicalname"]          = api.BoundEntityLogicalName,
            ["isfunction"]                      = api.IsFunction,
            ["isprivate"]                       = api.IsPrivate,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(api.AllowedStepType),
            ["executeprivilegename"]            = api.ExecutePrivilege,
            ["plugintypeid"]                    = pluginType.ToEntityReference(),
        };

        Guid apiId;
        if (!string.IsNullOrEmpty(solutionName))
        {
            var req = new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName };
            apiId = ((CreateResponse)await service.ExecuteAsync(req)).id;
        }
        else
        {
            apiId = await service.CreateAsync(entity);
        }

        foreach (var param in api.RequestParameters)
            await CreateRequestParameterAsync(service, param, apiId, uniqueName, solutionName);

        foreach (var prop in api.ResponseProperties)
            await CreateResponsePropertyAsync(service, prop, apiId, uniqueName, solutionName);
    }

    async Task SyncExistingCustomApiAsync(
        IOrganizationServiceAsync2 service,
        CustomApiMetadata api,
        string uniqueName,
        Entity existingApi,
        Entity pluginType,
        bool save)
    {
        var immutableChanged =
            (existingApi.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0) != api.BindingType ||
            existingApi.GetAttributeValue<string>("boundentitylogicalname") != api.BoundEntityLogicalName ||
            existingApi.GetAttributeValue<bool>("isfunction") != api.IsFunction ||
            (existingApi.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 0) != api.AllowedStepType;

        if (immutableChanged)
        {
            output.Info($"[yellow]Warning:[/] '{uniqueName}' has immutable field changes — deleting and recreating.");
            await DeleteCustomApiAsync(service, existingApi.Id);
            await CreateCustomApiAsync(service, api, uniqueName, pluginType, solutionName: "");
            return;
        }

        existingApi["displayname"]          = api.DisplayName;
        existingApi["description"]          = api.Description;
        existingApi["isprivate"]            = api.IsPrivate;
        existingApi["executeprivilegename"] = api.ExecutePrivilege;
        await service.UpdateAsync(existingApi);

        var existingParams = await GetRequestParameters(service, existingApi.Id);
        await SyncRequestParametersAsync(service, api.RequestParameters, existingParams, existingApi.Id, uniqueName, save);

        var existingProps = await GetResponseProperties(service, existingApi.Id);
        await SyncResponsePropertiesAsync(service, api.ResponseProperties, existingProps, existingApi.Id, uniqueName, save);
    }

    async Task SyncRequestParametersAsync(
        IOrganizationServiceAsync2 service,
        List<CustomApiRequestParameterMetadata> parameters,
        List<Entity> existing,
        Guid apiId,
        string apiUniqueName,
        bool save)
    {
        var existingByName = existing.ToDictionary(e => e.GetAttributeValue<string>("uniquename"), e => e);

        foreach (var param in parameters)
        {
            if (!existingByName.TryGetValue(param.UniqueName, out var existingParam))
            {
                await CreateRequestParameterAsync(service, param, apiId, apiUniqueName, solutionName: "");
            }
            else
            {
                var immutableChanged =
                    (existingParam.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != param.Type ||
                    existingParam.GetAttributeValue<bool>("isoptional") != param.IsOptional ||
                    existingParam.GetAttributeValue<string>("logicalentityname") != param.EntityName;

                if (immutableChanged)
                {
                    output.Info($"[yellow]Warning:[/] Request parameter '{param.UniqueName}' on '{apiUniqueName}' has immutable field changes — deleting and recreating.");
                    await service.DeleteAsync("customapirequestparameter", existingParam.Id);
                    await CreateRequestParameterAsync(service, param, apiId, apiUniqueName, solutionName: "");
                }
                else
                {
                    existingParam["displayname"] = param.DisplayName;
                    existingParam["description"] = param.Description;
                    await service.UpdateAsync(existingParam);
                }
            }
        }

        var localNames = parameters.Select(p => p.UniqueName).ToHashSet();
        var obsolete = existing.Where(e => !localNames.Contains(e.GetAttributeValue<string>("uniquename"))).ToList();
        if (!save)
        {
            foreach (var e in obsolete)
                await service.DeleteAsync("customapirequestparameter", e.Id);
        }
        else
        {
            foreach (var e in obsolete)
                output.Skip($"Request parameter '{e.GetAttributeValue<string>("uniquename")}' not in source — kept (--save)");
        }
    }

    async Task SyncResponsePropertiesAsync(
        IOrganizationServiceAsync2 service,
        List<CustomApiResponsePropertyMetadata> properties,
        List<Entity> existing,
        Guid apiId,
        string apiUniqueName,
        bool save)
    {
        var existingByName = existing.ToDictionary(e => e.GetAttributeValue<string>("uniquename"), e => e);

        foreach (var prop in properties)
        {
            if (!existingByName.TryGetValue(prop.UniqueName, out var existingProp))
            {
                await CreateResponsePropertyAsync(service, prop, apiId, apiUniqueName, solutionName: "");
            }
            else
            {
                var immutableChanged =
                    (existingProp.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != prop.Type ||
                    existingProp.GetAttributeValue<string>("logicalentityname") != prop.EntityName;

                if (immutableChanged)
                {
                    output.Info($"[yellow]Warning:[/] Response property '{prop.UniqueName}' on '{apiUniqueName}' has immutable field changes — deleting and recreating.");
                    await service.DeleteAsync("customapiresponseproperty", existingProp.Id);
                    await CreateResponsePropertyAsync(service, prop, apiId, apiUniqueName, solutionName: "");
                }
                else
                {
                    existingProp["displayname"] = prop.DisplayName;
                    existingProp["description"] = prop.Description;
                    await service.UpdateAsync(existingProp);
                }
            }
        }

        var localNames = properties.Select(p => p.UniqueName).ToHashSet();
        var obsolete = existing.Where(e => !localNames.Contains(e.GetAttributeValue<string>("uniquename"))).ToList();
        if (!save)
        {
            foreach (var e in obsolete)
                await service.DeleteAsync("customapiresponseproperty", e.Id);
        }
        else
        {
            foreach (var e in obsolete)
                output.Skip($"Response property '{e.GetAttributeValue<string>("uniquename")}' not in source — kept (--save)");
        }
    }

    async Task CreateRequestParameterAsync(
        IOrganizationServiceAsync2 service,
        CustomApiRequestParameterMetadata param,
        Guid apiId,
        string apiUniqueName,
        string solutionName)
    {
        var entity = new Entity("customapirequestparameter")
        {
            ["uniquename"]        = param.UniqueName,
            ["name"]              = $"{apiUniqueName}.{param.UniqueName}",
            ["displayname"]       = param.DisplayName,
            ["description"]       = param.Description,
            ["type"]              = new OptionSetValue(param.Type),
            ["isoptional"]        = param.IsOptional,
            ["logicalentityname"] = param.EntityName,
            ["customapiid"]       = new EntityReference("customapi", apiId),
        };

        if (!string.IsNullOrEmpty(solutionName))
        {
            var req = new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName };
            await service.ExecuteAsync(req);
        }
        else
        {
            await service.CreateAsync(entity);
        }
    }

    async Task CreateResponsePropertyAsync(
        IOrganizationServiceAsync2 service,
        CustomApiResponsePropertyMetadata prop,
        Guid apiId,
        string apiUniqueName,
        string solutionName)
    {
        var entity = new Entity("customapiresponseproperty")
        {
            ["uniquename"]        = prop.UniqueName,
            ["name"]              = $"{apiUniqueName}.{prop.UniqueName}",
            ["displayname"]       = prop.DisplayName,
            ["description"]       = prop.Description,
            ["type"]              = new OptionSetValue(prop.Type),
            ["logicalentityname"] = prop.EntityName,
            ["customapiid"]       = new EntityReference("customapi", apiId),
        };

        if (!string.IsNullOrEmpty(solutionName))
        {
            var req = new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName };
            await service.ExecuteAsync(req);
        }
        else
        {
            await service.CreateAsync(entity);
        }
    }

    async Task DeleteCustomApiAsync(IOrganizationServiceAsync2 service, Guid apiId)
    {
        foreach (var p in await GetResponseProperties(service, apiId))
            await service.DeleteAsync("customapiresponseproperty", p.Id);

        foreach (var p in await GetRequestParameters(service, apiId))
            await service.DeleteAsync("customapirequestparameter", p.Id);

        await service.DeleteAsync("customapi", apiId);
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

    async Task<string> GetPublisherPrefixAsync(IOrganizationServiceAsync2 service, string solutionName)
    {
        var query = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("publisherid"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);

        var pubLink = query.AddLink("publisher", "publisherid", "publisherid");
        pubLink.Columns = new ColumnSet("customizationprefix");
        pubLink.EntityAlias = "pub";

        var result = await service.RetrieveMultipleAsync(query);
        var solution = result.Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{solutionName}' not found in Dataverse.");

        return solution.GetAttributeValue<AliasedValue>("pub.customizationprefix")?.Value as string
            ?? throw new InvalidOperationException($"Could not read publisher prefix for solution '{solutionName}'.");
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

    async Task<List<Entity>> GetCustomApis(IOrganizationServiceAsync2 service, Guid assemblyId)
    {
        var query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet(
                "uniquename", "name", "displayname", "description",
                "bindingtype", "boundentitylogicalname", "isfunction",
                "isprivate", "allowedcustomprocessingsteptype", "executeprivilegename",
                "plugintypeid")
        };
        var typeLink = query.AddLink("plugintype", "plugintypeid", "plugintypeid");
        typeLink.LinkCriteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);
        return (await service.RetrieveMultipleAsync(query)).Entities.ToList();
    }

    async Task<List<Entity>> GetRequestParameters(IOrganizationServiceAsync2 service, Guid apiId)
    {
        var query = new QueryExpression("customapirequestparameter")
        {
            ColumnSet = new ColumnSet("uniquename", "name", "displayname", "description", "type", "isoptional", "logicalentityname"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("customapiid", ConditionOperator.Equal, apiId);
        return (await service.RetrieveMultipleAsync(query)).Entities.ToList();
    }

    async Task<List<Entity>> GetResponseProperties(IOrganizationServiceAsync2 service, Guid apiId)
    {
        var query = new QueryExpression("customapiresponseproperty")
        {
            ColumnSet = new ColumnSet("uniquename", "name", "displayname", "description", "type", "logicalentityname"),
            Criteria = new FilterExpression()
        };
        query.Criteria.AddCondition("customapiid", ConditionOperator.Equal, apiId);
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
        return result.Entities.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException($"Dataverse message '{messageName}' not found in sdkmessage.");
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
