using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class PluginRegistrationService(IFlowlineOutput output)
{
    const string FlowlineMarker = "[flowline]";

    static readonly Dictionary<string, string> s_messagePropertyNames = new(StringComparer.OrdinalIgnoreCase)
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
        bool save = false, CancellationToken cancellationToken = default)
    {
        // Phase 1: Get Assembly
        var (assembly, needsUpdate) = await GetOrRegisterAssemblyAsync(service, metadata, solutionName, cancellationToken);

        // Phase 2: Delete obsolete types, steps, images, and APIs

        // Phase 3: Update Assembly content
        if (needsUpdate)
        {
            await UpdateAssemblyContentAsync(service, assembly, metadata, cancellationToken);
            output.Info($"[green]Updated assembly content for [bold]{metadata.Name}[/][/]");
        }

        // Phase 4: Register Plugins, Workflows, and Custom APIs
        await RegisterPluginsAsync(service, metadata, assembly, save, cancellationToken);
        await RegisterWorkflowActivitiesAsync(service, metadata, assembly, save, cancellationToken);
        await RegisterCustomApisAsync(service, metadata, assembly.Id, solutionName, save, cancellationToken);

        // Phase 5: Finalize registration
    }

    async Task RegisterPluginsAsync(IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, Entity assembly, bool save, CancellationToken cancellationToken = default)
    {
        var existingTypes = (await GetPluginTypesAsync(service, assembly.Id, cancellationToken))
            .Where(t => !t.GetAttributeValue<bool>("isworkflowactivity"))
            .ToList();
        var typeNames = existingTypes.ToDictionary(t => t.GetAttributeValue<string>("typename"), t => t);

        var customApiTypeNames = metadata.CustomApis.Select(a => a.PluginTypeFullName).ToHashSet();
        var messageCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var filterCache = new Dictionary<(Guid messageId, string entityName, string secondaryEntity), Guid?>();

        foreach (var plugin in metadata.Plugins.Where(p => !p.IsWorkflow))
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
                typeEntity.Id = await service.CreateAsync(typeEntity, cancellationToken);
            }

            // Custom API backing types have their steps managed by Dataverse — skip step registration for them.
            if (!customApiTypeNames.Contains(plugin.FullName))
                await RegisterPluginStepsAsync(service, typeEntity, plugin.Steps, messageCache, filterCache, save, cancellationToken);
        }

        var localNames = metadata.Plugins.Where(p => !p.IsWorkflow).Select(p => p.FullName).ToHashSet();
        if (!save)
        {
            foreach (var obsolete in existingTypes.Where(t => !localNames.Contains(t.GetAttributeValue<string>("typename"))))
            {
                var steps = await GetStepsAsync(service, obsolete.Id, cancellationToken);
                foreach (var step in steps)
                    await service.DeleteAsync("sdkmessageprocessingstep", step.Id, cancellationToken);
                await service.DeleteAsync("plugintype", obsolete.Id, cancellationToken);
            }
        }
        else
        {
            foreach (var obsolete in existingTypes.Where(t => !localNames.Contains(t.GetAttributeValue<string>("typename"))))
                output.Skip($"Plugin type '{obsolete.GetAttributeValue<string>("typename")}' not in source — kept (--save)");
        }
    }

    async Task RegisterWorkflowActivitiesAsync(IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, Entity assembly, bool save, CancellationToken cancellationToken = default)
    {
        var existingTypes = (await GetPluginTypesAsync(service, assembly.Id, cancellationToken))
            .Where(t => t.GetAttributeValue<bool>("isworkflowactivity"))
            .ToList();
        var typeNames = existingTypes.ToDictionary(t => t.GetAttributeValue<string>("typename"), t => t);

        foreach (var plugin in metadata.Plugins.Where(p => p.IsWorkflow))
        {
            if (!typeNames.ContainsKey(plugin.FullName))
            {
                var typeEntity = new Entity("plugintype")
                {
                    ["typename"] = plugin.FullName,
                    ["name"] = plugin.FullName,
                    ["friendlyname"] = plugin.Name,
                    ["pluginassemblyid"] = assembly.ToEntityReference(),
                    ["workflowactivitygroupname"] = $"{metadata.Name} ({metadata.Version})",
                    ["description"] = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };
                await service.CreateAsync(typeEntity, cancellationToken);
            }
        }

        var localNames = metadata.Plugins.Where(p => p.IsWorkflow).Select(p => p.FullName).ToHashSet();
        if (!save)
        {
            foreach (var obsolete in existingTypes.Where(t => !localNames.Contains(t.GetAttributeValue<string>("typename"))))
                await service.DeleteAsync("plugintype", obsolete.Id, cancellationToken);
        }
        else
        {
            foreach (var obsolete in existingTypes.Where(t => !localNames.Contains(t.GetAttributeValue<string>("typename"))))
                output.Skip($"Workflow activity '{obsolete.GetAttributeValue<string>("typename")}' not in source — kept (--save)");
        }
    }

    async Task RegisterPluginStepsAsync(
        IOrganizationServiceAsync2 service,
        Entity typeEntity,
        List<PluginStepMetadata> steps,
        Dictionary<string, Guid> messageCache,
        Dictionary<(Guid messageId, string entityName, string secondaryEntity), Guid?> filterCache,
        bool save,
        CancellationToken cancellationToken = default)
    {
        var existingSteps = await GetStepsAsync(service, typeEntity.Id, cancellationToken);
        var stepNames = existingSteps.ToDictionary(s => s.GetAttributeValue<string>("name"), s => s);

        foreach (var step in steps)
        {
            if (!stepNames.TryGetValue(step.Name, out var stepEntity))
            {
                if (!messageCache.TryGetValue(step.Message, out var messageId))
                {
                    messageId = await LookupSdkMessageIdAsync(service, step.Message, cancellationToken);
                    messageCache[step.Message] = messageId;
                }

                var secondaryEntity = step.SecondaryEntity ?? "none";
                if (!filterCache.TryGetValue((messageId, step.EntityName, secondaryEntity), out var filterId))
                {
                    filterId = await LookupSdkMessageFilterIdAsync(service, messageId, step.EntityName, step.SecondaryEntity, cancellationToken);
                    filterCache[(messageId, step.EntityName, secondaryEntity)] = filterId;
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
                stepEntity.Id = await service.CreateAsync(stepEntity,cancellationToken);
            }
            else
            {
                stepEntity["stage"] = new OptionSetValue(step.Stage);
                stepEntity["mode"] = new OptionSetValue(step.Mode);
                stepEntity["rank"] = step.Order;
                stepEntity["filteringattributes"] = step.FilteringAttributes;
                stepEntity["configuration"] = step.Configuration;
                await service.UpdateAsync(stepEntity, cancellationToken);
            }

            await RegisterImagesAsync(service, stepEntity, step.Images, step.Message, save, cancellationToken);
        }

        // DLL is the source of truth — delete any step that is no longer in local metadata
        var localStepNames = steps.Select(s => s.Name).ToHashSet();
        var obsoleteSteps = existingSteps
            .Where(s => !localStepNames.Contains(s.GetAttributeValue<string>("name")))
            .ToList();
        if (!save)
        {
            foreach (var obsolete in obsoleteSteps)
                await service.DeleteAsync("sdkmessageprocessingstep", obsolete.Id, cancellationToken);
        }
        else
        {
            foreach (var obsolete in obsoleteSteps)
                output.Skip($"Step '{obsolete.GetAttributeValue<string>("name")}' not in source — kept (--save)");
        }
    }

    async Task RegisterImagesAsync(IOrganizationServiceAsync2 service, Entity stepEntity, List<PluginImageMetadata> images, string message, bool save, CancellationToken cancellationToken = default)
    {
        var existing = await GetImagesAsync(service, stepEntity.Id, cancellationToken);
        var existingByName = existing.ToDictionary(e => e.GetAttributeValue<string>("name"), e => e);

        foreach (var image in images)
        {
            if (!existingByName.TryGetValue(image.Name, out var imageEntity))
            {
                if (!s_messagePropertyNames.TryGetValue(message, out var propertyName))
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
                await service.CreateAsync(entity, cancellationToken);
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
                    await service.UpdateAsync(imageEntity, cancellationToken);
                }
            }
        }

        var localNames = images.Select(i => i.Name).ToHashSet();
        var obsolete = existing.Where(e => !localNames.Contains(e.GetAttributeValue<string>("name"))).ToList();
        if (!save)
        {
            foreach (var e in obsolete)
                await service.DeleteAsync("sdkmessageprocessingstepimage", e.Id, cancellationToken);
        }
        else
        {
            foreach (var e in obsolete)
                output.Skip($"Image '{e.GetAttributeValue<string>("name")}' not in source — kept (--save)");
        }
    }

    async Task RegisterCustomApisAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        Guid assemblyId,
        string solutionName,
        bool save,
        CancellationToken cancellationToken = default)
    {
        List<CustomApiMetadata> customApis = metadata.CustomApis;
        if (customApis.Count <= 0)
        {
            output.Info($"No Custom APIs found in metadata.");
            return;
        }

        var prefix = await GetPublisherPrefixAsync(service, solutionName, cancellationToken);

        var pluginTypeMap = (await GetPluginTypesAsync(service, assemblyId, cancellationToken))
            .ToDictionary(t => t.GetAttributeValue<string>("typename"), t => t);

        var existing = await GetCustomApisAsync(service, assemblyId, cancellationToken);
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
                await CreateCustomApiAsync(service, api, uniqueName, pluginType, solutionName, cancellationToken);
            else
                await SyncExistingCustomApiAsync(service, api, uniqueName, existingApi, pluginType, save, cancellationToken);
        }

        var localUniqueNames = customApis.Select(a => $"{prefix}_{a.UniqueName}").ToHashSet();
        var obsolete = existing.Where(e => !localUniqueNames.Contains(e.GetAttributeValue<string>("uniquename"))).ToList();
        if (!save)
        {
            foreach (var e in obsolete)
                await DeleteCustomApiAsync(service, e.Id, cancellationToken);
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
        string solutionName,
        CancellationToken cancellationToken = default)
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
            apiId = ((CreateResponse)await service.ExecuteAsync(req, cancellationToken)).id;
        }
        else
        {
            apiId = await service.CreateAsync(entity, cancellationToken);
        }

        foreach (var param in api.RequestParameters)
            await CreateRequestParameterAsync(service, param, apiId, uniqueName, solutionName, cancellationToken);

        foreach (var prop in api.ResponseProperties)
            await CreateResponsePropertyAsync(service, prop, apiId, uniqueName, solutionName, cancellationToken);
    }

    async Task SyncExistingCustomApiAsync(
        IOrganizationServiceAsync2 service,
        CustomApiMetadata api,
        string uniqueName,
        Entity existingApi,
        Entity pluginType,
        bool save,
        CancellationToken cancellationToken = default)
    {
        var immutableChanged =
            (existingApi.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0) != api.BindingType ||
            existingApi.GetAttributeValue<string>("boundentitylogicalname") != api.BoundEntityLogicalName ||
            existingApi.GetAttributeValue<bool>("isfunction") != api.IsFunction ||
            (existingApi.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 0) != api.AllowedStepType;

        if (immutableChanged)
        {
            output.Info($"[yellow]Warning:[/] '{uniqueName}' has immutable field changes — deleting and recreating.");
            await DeleteCustomApiAsync(service, existingApi.Id, cancellationToken);
            await CreateCustomApiAsync(service, api, uniqueName, pluginType, solutionName: "", cancellationToken: cancellationToken);
            return;
        }

        existingApi["displayname"]          = api.DisplayName;
        existingApi["description"]          = api.Description;
        existingApi["isprivate"]            = api.IsPrivate;
        existingApi["executeprivilegename"] = api.ExecutePrivilege;
        await service.UpdateAsync(existingApi, cancellationToken);

        var existingParams = await GetRequestParametersAsync(service, existingApi.Id, cancellationToken);
        await SyncRequestParametersAsync(service, api.RequestParameters, existingParams, existingApi.Id, uniqueName, save, cancellationToken);

        var existingProps = await GetResponsePropertiesAsync(service, existingApi.Id, cancellationToken);
        await SyncResponsePropertiesAsync(service, api.ResponseProperties, existingProps, existingApi.Id, uniqueName, save, cancellationToken);
    }

    async Task SyncRequestParametersAsync(
        IOrganizationServiceAsync2 service,
        List<CustomApiRequestParameterMetadata> parameters,
        List<Entity> existing,
        Guid apiId,
        string apiUniqueName,
        bool save,
        CancellationToken cancellationToken = default)
    {
        var existingByName = existing.ToDictionary(e => e.GetAttributeValue<string>("uniquename"), e => e);

        foreach (var param in parameters)
        {
            if (!existingByName.TryGetValue(param.UniqueName, out var existingParam))
            {
                await CreateRequestParameterAsync(service, param, apiId, apiUniqueName, solutionName: "", cancellationToken: cancellationToken);
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
                    await service.DeleteAsync("customapirequestparameter", existingParam.Id, cancellationToken);
                    await CreateRequestParameterAsync(service, param, apiId, apiUniqueName, solutionName: "", cancellationToken: cancellationToken);
                }
                else
                {
                    existingParam["displayname"] = param.DisplayName;
                    existingParam["description"] = param.Description;
                    await service.UpdateAsync(existingParam, cancellationToken);
                }
            }
        }

        var localNames = parameters.Select(p => p.UniqueName).ToHashSet();
        var obsolete = existing.Where(e => !localNames.Contains(e.GetAttributeValue<string>("uniquename"))).ToList();
        if (!save)
        {
            foreach (var e in obsolete)
                await service.DeleteAsync("customapirequestparameter", e.Id, cancellationToken);
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
        bool save,
        CancellationToken cancellationToken = default)
    {
        var existingByName = existing.ToDictionary(e => e.GetAttributeValue<string>("uniquename"), e => e);

        foreach (var prop in properties)
        {
            if (!existingByName.TryGetValue(prop.UniqueName, out var existingProp))
            {
                await CreateResponsePropertyAsync(service, prop, apiId, apiUniqueName, solutionName: "", cancellationToken: cancellationToken);
            }
            else
            {
                var immutableChanged =
                    (existingProp.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != prop.Type ||
                    existingProp.GetAttributeValue<string>("logicalentityname") != prop.EntityName;

                if (immutableChanged)
                {
                    output.Info($"[yellow]Warning:[/] Response property '{prop.UniqueName}' on '{apiUniqueName}' has immutable field changes — deleting and recreating.");
                    await service.DeleteAsync("customapiresponseproperty", existingProp.Id, cancellationToken);
                    await CreateResponsePropertyAsync(service, prop, apiId, apiUniqueName, solutionName: "", cancellationToken: cancellationToken);
                }
                else
                {
                    existingProp["displayname"] = prop.DisplayName;
                    existingProp["description"] = prop.Description;
                    await service.UpdateAsync(existingProp, cancellationToken);
                }
            }
        }

        var localNames = properties.Select(p => p.UniqueName).ToHashSet();
        var obsolete = existing.Where(e => !localNames.Contains(e.GetAttributeValue<string>("uniquename"))).ToList();
        if (!save)
        {
            foreach (var e in obsolete)
                await service.DeleteAsync("customapiresponseproperty", e.Id, cancellationToken);
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
        string solutionName,
        CancellationToken cancellationToken = default)
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
            await service.ExecuteAsync(req, cancellationToken);
        }
        else
        {
            await service.CreateAsync(entity, cancellationToken);
        }
    }

    async Task CreateResponsePropertyAsync(
        IOrganizationServiceAsync2 service,
        CustomApiResponsePropertyMetadata prop,
        Guid apiId,
        string apiUniqueName,
        string solutionName,
        CancellationToken cancellationToken = default)
    {
        var entity = new Entity("customapiresponseproperty")
        {
            ["uniquename"]        = prop.UniqueName,
            ["name"]              = $"{apiUniqueName}.{prop.UniqueName}",
            ["displayname"]       = prop.DisplayName ?? prop.UniqueName,
            ["description"]       = string.IsNullOrWhiteSpace(prop.Description) ? (prop.DisplayName ?? prop.UniqueName) : prop.Description,
            ["type"]              = new OptionSetValue(prop.Type),
            ["logicalentityname"] = prop.EntityName,
            ["customapiid"]       = new EntityReference("customapi", apiId),
        };

        if (!string.IsNullOrEmpty(solutionName))
        {
            var req = new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName };
            await service.ExecuteAsync(req, cancellationToken);
        }
        else
        {
            await service.CreateAsync(entity, cancellationToken);
        }
    }

    async Task DeleteCustomApiAsync(IOrganizationServiceAsync2 service, Guid apiId, CancellationToken cancellationToken = default)
    {
        foreach (var p in await GetResponsePropertiesAsync(service, apiId, cancellationToken))
            await service.DeleteAsync("customapiresponseproperty", p.Id, cancellationToken);

        foreach (var p in await GetRequestParametersAsync(service, apiId, cancellationToken))
            await service.DeleteAsync("customapirequestparameter", p.Id, cancellationToken);

        await service.DeleteAsync("customapi", apiId, cancellationToken);
    }

    async Task<(Entity entity, bool needsUpdate)> GetOrRegisterAssemblyAsync(IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("pluginassembly")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("pluginassemblyid", "name", "version", "description"),
            Criteria =
            {
                Conditions = { new ConditionExpression("name", ConditionOperator.Equal, metadata.Name) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken);
        var existing = result.Entities.FirstOrDefault();

        if (existing == null)
        {
            var entity = new Entity("pluginassembly")
            {
                ["name"] = metadata.Name,
                ["content"] = Convert.ToBase64String(metadata.Content),
                ["version"] = metadata.Version,
                ["isolationmode"] = new OptionSetValue(2), // 2 = Sandbox (cloud only)
                ["description"] = $"{FlowlineMarker} sha256={metadata.Hash}"
            };

            var response = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName });

            entity.Id = response.id;
            return (entity, false);
        }
        else
        {
            var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
            return (existing, storedHash != metadata.Hash);
        }
    }

    async Task UpdateAssemblyContentAsync(IOrganizationServiceAsync2 service, Entity existing, PluginAssemblyMetadata metadata, CancellationToken cancellationToken = default)
    {
        existing["content"] = Convert.ToBase64String(metadata.Content);
        existing["version"] = metadata.Version;
        existing["description"] = $"{FlowlineMarker} sha256={metadata.Hash}";

        await service.UpdateAsync(existing, cancellationToken);
    }

    static string? ParseStoredHash(string? description)
    {
        if (description == null) return null;
        var idx = description.IndexOf("sha256=", StringComparison.Ordinal);
        return idx < 0 ? null : description[(idx + 7)..].Split(' ')[0].Trim();
    }

    async Task<string> GetPublisherPrefixAsync(IOrganizationServiceAsync2 service, string solutionName, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("solution")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("publisherid"),
            Criteria = { Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, solutionName) } },
            LinkEntities =
            {
                new LinkEntity("solution", "publisher", "publisherid", "publisherid", JoinOperator.Inner)
                {
                    Columns = new ColumnSet("customizationprefix"),
                    EntityAlias = "pub"
                }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken);
        var solution = result.Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{solutionName}' not found in Dataverse.");

        return solution.GetAttributeValue<AliasedValue>("pub.customizationprefix")?.Value as string
            ?? throw new InvalidOperationException($"Could not read publisher prefix for solution '{solutionName}'.");
    }

    async Task<List<Entity>> GetPluginTypesAsync(IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("typename", "name", "isworkflowactivity"),
            Criteria =
            {
                Conditions = { new ConditionExpression("pluginassemblyid", ConditionOperator.Equal, assemblyId) }
            }
        };

        return (await service.RetrieveMultipleAsync(query, cancellationToken)).Entities.ToList();
    }

    async Task<List<Entity>> GetStepsAsync(IOrganizationServiceAsync2 service, Guid typeId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet("name", "stage", "mode", "rank", "filteringattributes", "configuration"),
            Criteria =
            {
                Conditions = { new ConditionExpression("plugintypeid", ConditionOperator.Equal, typeId) }
            }
        };

        return (await service.RetrieveMultipleAsync(query, cancellationToken)).Entities.ToList();
    }

    async Task<List<Entity>> GetImagesAsync(IOrganizationServiceAsync2 service, Guid stepId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessageprocessingstepimage")
        {
            ColumnSet = new ColumnSet("name", "entityalias", "imagetype", "attributes"),
            Criteria =
            {
                Conditions = { new ConditionExpression("sdkmessageprocessingstepid", ConditionOperator.Equal, stepId) }
            }
        };

        return (await service.RetrieveMultipleAsync(query, cancellationToken)).Entities.ToList();
    }

    async Task<List<Entity>> GetCustomApisAsync(IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
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

        return (await service.RetrieveMultipleAsync(query, cancellationToken)).Entities.ToList();
    }

    async Task<List<Entity>> GetRequestParametersAsync(IOrganizationServiceAsync2 service, Guid apiId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapirequestparameter")
        {
            ColumnSet = new ColumnSet("uniquename", "name", "displayname", "description", "type", "isoptional", "logicalentityname"),
            Criteria =
            {
                Conditions = { new ConditionExpression("customapiid", ConditionOperator.Equal, apiId) }
            }
        };

        return (await service.RetrieveMultipleAsync(query,cancellationToken)).Entities.ToList();
    }

    async Task<List<Entity>> GetResponsePropertiesAsync(IOrganizationServiceAsync2 service, Guid apiId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapiresponseproperty")
        {
            ColumnSet = new ColumnSet("uniquename", "name", "displayname", "description", "type", "logicalentityname"),
            Criteria =
            {
                Conditions = { new ConditionExpression("customapiid", ConditionOperator.Equal, apiId) }
            }
        };

        return (await service.RetrieveMultipleAsync(query, cancellationToken)).Entities.ToList();
    }

    async Task<Guid> LookupSdkMessageIdAsync(IOrganizationServiceAsync2 service, string messageName, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessage")
        {
            ColumnSet = new ColumnSet("sdkmessageid"),
            Criteria =
            {
                Conditions = { new ConditionExpression("name", ConditionOperator.Equal, messageName) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken);
        return result.Entities.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException($"Dataverse message '{messageName}' not found in sdkmessage.");
    }

    async Task<Guid?> LookupSdkMessageFilterIdAsync(IOrganizationServiceAsync2 service, Guid messageId, string entityName, string? secondaryEntity = null, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessagefilter")
        {
            ColumnSet = new ColumnSet("sdkmessagefilterid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("sdkmessageid", ConditionOperator.Equal, messageId),
                    new ConditionExpression("primaryobjecttypecode", ConditionOperator.Equal, entityName)
                }
            }
        };

        if (secondaryEntity != null)
            query.Criteria.AddCondition("secondaryobjecttypecode", ConditionOperator.Equal, secondaryEntity);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken);
        return result.Entities.FirstOrDefault()?.Id;
    }
}
