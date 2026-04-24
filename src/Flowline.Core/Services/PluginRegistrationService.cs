using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class PluginRegistrationService(IFlowlineOutput output)
{
    const int MaxParallelism = 8;
    const string FlowlineMarker = "[flowline]";
    const string DefaultSolutionUniqueName = "Default";
    const int PluginAssemblyComponentType = 91;
    const int SdkMessageProcessingStepComponentType = 92;

    readonly Dictionary<string, int> _componentTypeByEntityLogicalName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pluginassembly"] = PluginAssemblyComponentType,
        ["sdkmessageprocessingstep"] = SdkMessageProcessingStepComponentType
    };

    sealed record DeletePlan(
        IReadOnlyList<EntityReference> Images,
        IReadOnlyList<EntityReference> Steps,
        IReadOnlyList<EntityReference> PluginTypes,
        IReadOnlyList<EntityReference> WorkflowTypes,
        IReadOnlyList<EntityReference> CustomApis,
        IReadOnlyList<EntityReference> CustomApiRequestParameters,
        IReadOnlyList<EntityReference> CustomApiResponseProperties);

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
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        // Phase 1: Get Assembly
        var (assembly, needsUpdate) = await GetOrRegisterAssemblyAsync(service, metadata, solutionName, cancellationToken);
        output.Info($"Assembly '{metadata.Name}' ({metadata.Version}) found in solution '{solutionName}'.");

        // Phase 2: Plan + Delete obsolete components
        if (!save)
        {
            var deletesPhase2 = await BuildDeletePlanAsync(service, metadata, assembly.Id, solutionName, cancellationToken);
            await ExecuteDeletePhase2Async(service, deletesPhase2, cancellationToken);
            output.Info($"Deleted obsolete components for [bold]{metadata.Name}[/]");
        }

        // Phase 3: Update Assembly content
        if (needsUpdate)
        {
            await UpdateAssemblyContentAsync(service, assembly, metadata, cancellationToken);
            output.Info($"[green]Updated assembly content for [bold]{metadata.Name}[/][/]");
        }
        else
        {
            output.Skip($"Assembly '{metadata.Name}' is unchanged — skipping upload.");
        }

        // Phase 4: Upsert Plugin Types, Plugin Steps, and Custom APIs
        output.Verbose("phase=3 wave=plugin-types status=started");
        var pluginTypeMap = await EnsurePluginTypesAsync(service, metadata, assembly, save, allowDeletes: false, cancellationToken);
        output.Info("phase=3 wave=plugin-types status=completed");

        output.Verbose("phase=3 wave=plugin-steps-images status=started");
        await RegisterPluginsAsync(service, metadata, pluginTypeMap, solutionName, save, allowDeletes: false, cancellationToken);
        output.Info("phase=3 wave=plugin-steps-images status=completed");

        output.Verbose("phase=3 wave=custom-api-and-params status=started");
        await RegisterCustomApisAsync(service, metadata, assembly.Id, pluginTypeMap, solutionName, save, allowDeletes: false, cancellationToken);
        output.Info("phase=3 wave=custom-api-and-params status=completed");

        // Phase 5: Finalize registration
    }

    async Task<Dictionary<string, Entity>> EnsurePluginTypesAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        Entity assembly,
        bool save,
        bool allowDeletes,
        CancellationToken cancellationToken = default)
    {
        var existingTypes = (await GetPluginTypesAsync(service, assembly.Id, cancellationToken)).ToList();
        var existingTypeNames = existingTypes.ToDictionary(t => t.GetAttributeValue<string>("typename"), t => t);

        foreach (var plugin in metadata.Plugins)
        {
            // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/reference/entities/plugintype
            if (!existingTypeNames.TryGetValue(plugin.FullName, out var typeEntity))
            {
                typeEntity = new Entity("plugintype", Guid.NewGuid())
                {
                    ["typename"] = plugin.FullName,
                    ["name"] = plugin.FullName,
                    ["friendlyname"] = plugin.Name,
                    ["pluginassemblyid"] = assembly.ToEntityReference(),
                    ["description"] = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };

                if (plugin.IsWorkflow)
                    typeEntity["workflowactivitygroupname"] = $"{metadata.Name} ({metadata.Version})";

                await service.CreateAsync(typeEntity, cancellationToken);
                existingTypeNames[plugin.FullName] = typeEntity;
            }
        }

        var localNames = metadata.Plugins.Select(p => p.FullName).ToHashSet();
        var obsoleteTypes = existingTypes.Where(t => !localNames.Contains(t.GetAttributeValue<string>("typename"))).ToList();
        if (!save && allowDeletes)
        {
            foreach (var obsolete in obsoleteTypes)
            {
                if (obsolete.GetAttributeValue<bool>("isworkflowactivity"))
                {
                    await service.DeleteAsync("plugintype", obsolete.Id, cancellationToken);
                    continue;
                }

                var steps = await GetStepsAsync(service, obsolete.Id, cancellationToken);
                foreach (var step in steps)
                {
                    if (!IsStageModifiable(step))
                        continue;

                    await service.DeleteAsync("sdkmessageprocessingstep", step.Id, cancellationToken);
                }

                if (steps.Any(step => !IsStageModifiable(step)))
                    continue;

                await service.DeleteAsync("plugintype", obsolete.Id, cancellationToken);
            }
        }
        else
        {
            foreach (var obsolete in obsoleteTypes)
            {
                if (obsolete.GetAttributeValue<bool>("isworkflowactivity"))
                    output.Skip($"Workflow activity '{obsolete.GetAttributeValue<string>("typename")}' not in source — kept (--save)");
                else
                    output.Skip($"Plugin type '{obsolete.GetAttributeValue<string>("typename")}' not in source — kept (--save)");
            }
        }

        return metadata.Plugins.ToDictionary(p => p.FullName, p => existingTypeNames[p.FullName], StringComparer.OrdinalIgnoreCase);
    }

    async Task RegisterPluginsAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        IReadOnlyDictionary<string, Entity> pluginTypeMap,
        string solutionName,
        bool save,
        bool allowDeletes,
        CancellationToken cancellationToken = default)
    {
        var messageCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var filterCache = new Dictionary<(Guid messageId, string entityName, string secondaryEntity), Guid?>();

        foreach (var plugin in metadata.Plugins.Where(p => !p.IsWorkflow))
        {
            if (!pluginTypeMap.TryGetValue(plugin.FullName, out var typeEntity))
            {
                output.Info($"[yellow]Warning:[/] Plugin type '{plugin.FullName}' not found — skipping step registration.");
                continue;
            }

            // Custom API backing types have their steps managed by Dataverse — skip step registration for them.
            if (!plugin.IsCustomApi)
                await RegisterPluginStepsAsync(service, typeEntity, plugin.Steps, solutionName, messageCache, filterCache, save, allowDeletes, cancellationToken);
        }
    }

    async Task RegisterPluginStepsAsync(
        IOrganizationServiceAsync2 service,
        Entity typeEntity,
        List<PluginStepMetadata> steps,
        string solutionName,
        Dictionary<string, Guid> messageCache,
        Dictionary<(Guid messageId, string entityName, string secondaryEntity), Guid?> filterCache,
        bool save,
        bool allowDeletes,
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

                var entity = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
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

                var createStepRequest = new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName };
                await service.ExecuteAsync(createStepRequest, cancellationToken);
                stepEntity = entity;
            }
            else
            {
                await WarnIfComponentExistsInOtherSolutionsAsync(
                    service,
                    SdkMessageProcessingStepComponentType,
                    stepEntity.Id,
                    solutionName,
                    "sdkmessageprocessingstep",
                    step.Name,
                    cancellationToken);

                await EnsureComponentInSolutionAsync(service, "sdkmessageprocessingstep", stepEntity.Id, solutionName, cancellationToken);

                stepEntity["stage"] = new OptionSetValue(step.Stage);
                stepEntity["mode"] = new OptionSetValue(step.Mode);
                stepEntity["rank"] = step.Order;
                stepEntity["filteringattributes"] = step.FilteringAttributes;
                stepEntity["configuration"] = step.Configuration;

                await service.UpdateAsync(stepEntity, cancellationToken);
            }

            await RegisterImagesAsync(service, stepEntity, step.Images, step.Message, save, allowDeletes, cancellationToken);
        }

        // DLL is the source of truth — delete any step that is no longer in local metadata
        var localStepNames = steps.Select(s => s.Name).ToHashSet();
        var obsoleteSteps = existingSteps
            .Where(s => !localStepNames.Contains(s.GetAttributeValue<string>("name")))
            .ToList();
        if (!save && allowDeletes)
        {
            foreach (var obsolete in obsoleteSteps)
            {
                if (!IsStageModifiable(obsolete))
                    continue;

                await service.DeleteAsync("sdkmessageprocessingstep", obsolete.Id, cancellationToken);
            }
        }
        else
        {
            foreach (var obsolete in obsoleteSteps)
                output.Skip($"Step '{obsolete.GetAttributeValue<string>("name")}' not in source — kept (--save)");
        }
    }

    async Task RegisterImagesAsync(IOrganizationServiceAsync2 service, Entity stepEntity, List<PluginImageMetadata> images, string message, bool save, bool allowDeletes, CancellationToken cancellationToken = default)
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
        if (!save && allowDeletes)
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
        IReadOnlyDictionary<string, Entity> pluginTypeMap,
        string solutionName,
        bool save,
        bool allowDeletes,
        CancellationToken cancellationToken = default)
    {
        List<CustomApiMetadata> customApis = metadata.CustomApis;
        if (customApis.Count <= 0)
        {
            output.Info($"No Custom APIs found in metadata.");
            return;
        }

        var prefix = await GetPublisherPrefixAsync(service, solutionName, cancellationToken);

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
                await SyncExistingCustomApiAsync(service, api, uniqueName, existingApi, pluginType, solutionName, save, allowDeletes, cancellationToken);
        }

        var localUniqueNames = customApis.Select(a => $"{prefix}_{a.UniqueName}").ToHashSet();
        var obsolete = existing.Where(e => !localUniqueNames.Contains(e.GetAttributeValue<string>("uniquename"))).ToList();
        if (!save && allowDeletes)
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

        var req = new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName };
        var apiId = ((CreateResponse)await service.ExecuteAsync(req, cancellationToken)).id;

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
        string solutionName,
        bool save,
        bool allowDeletes,
        CancellationToken cancellationToken = default)
    {
        var customApiComponentType = await GetComponentTypeFromSolutionComponentAsync(service, existingApi.Id, "customapi", cancellationToken);

        await WarnIfComponentExistsInOtherSolutionsAsync(
            service,
            customApiComponentType,
            existingApi.Id,
            solutionName,
            "customapi",
            uniqueName,
            cancellationToken);

        await EnsureComponentInSolutionAsync(service, "customapi", existingApi.Id, solutionName, cancellationToken);

        var immutableChanged =
            (existingApi.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0) != api.BindingType ||
            existingApi.GetAttributeValue<string>("boundentitylogicalname") != api.BoundEntityLogicalName ||
            existingApi.GetAttributeValue<bool>("isfunction") != api.IsFunction ||
            (existingApi.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 0) != api.AllowedStepType;

        if (immutableChanged)
        {
            output.Info($"[yellow]Warning:[/] '{uniqueName}' has immutable field changes — deleting and recreating.");
            if (!allowDeletes)
                throw new InvalidOperationException($"Delete required for immutable Custom API changes on '{uniqueName}', but deletes are blocked in phase 3.");

            await DeleteCustomApiAsync(service, existingApi.Id, cancellationToken);
            await CreateCustomApiAsync(service, api, uniqueName, pluginType, solutionName, cancellationToken);
            return;
        }

        existingApi["displayname"]          = api.DisplayName;
        existingApi["description"]          = api.Description;
        existingApi["isprivate"]            = api.IsPrivate;
        existingApi["executeprivilegename"] = api.ExecutePrivilege;
        await service.UpdateAsync(existingApi, cancellationToken);

        var existingParams = await GetRequestParametersAsync(service, existingApi.Id, cancellationToken);
        await SyncRequestParametersAsync(service, api.RequestParameters, existingParams, existingApi.Id, uniqueName, solutionName, save, allowDeletes, cancellationToken);

        var existingProps = await GetResponsePropertiesAsync(service, existingApi.Id, cancellationToken);
        await SyncResponsePropertiesAsync(service, api.ResponseProperties, existingProps, existingApi.Id, uniqueName, solutionName, save, allowDeletes, cancellationToken);
    }

    async Task SyncRequestParametersAsync(
        IOrganizationServiceAsync2 service,
        List<CustomApiRequestParameterMetadata> parameters,
        List<Entity> existing,
        Guid apiId,
        string apiUniqueName,
        string solutionName,
        bool save,
        bool allowDeletes,
        CancellationToken cancellationToken = default)
    {
        var existingByName = existing.ToDictionary(e => e.GetAttributeValue<string>("uniquename"), e => e);

        foreach (var param in parameters)
        {
            if (!existingByName.TryGetValue(param.UniqueName, out var existingParam))
            {
                await CreateRequestParameterAsync(service, param, apiId, apiUniqueName, solutionName, cancellationToken);
            }
            else
            {
                var requestParameterComponentType = await GetComponentTypeFromSolutionComponentAsync(service, existingParam.Id, "customapirequestparameter", cancellationToken);
                await EnsureComponentInSolutionAsync(service, "customapirequestparameter", existingParam.Id, solutionName, cancellationToken);

                var immutableChanged =
                    (existingParam.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != param.Type ||
                    existingParam.GetAttributeValue<bool>("isoptional") != param.IsOptional ||
                    existingParam.GetAttributeValue<string>("logicalentityname") != param.EntityName;

                if (immutableChanged)
                {
                    if (!allowDeletes)
                        throw new InvalidOperationException($"Delete required for immutable request parameter '{param.UniqueName}' on '{apiUniqueName}', but deletes are blocked in phase 3.");

                    output.Info($"[yellow]Warning:[/] Request parameter '{param.UniqueName}' on '{apiUniqueName}' has immutable field changes — deleting and recreating.");
                    await service.DeleteAsync("customapirequestparameter", existingParam.Id, cancellationToken);
                    await CreateRequestParameterAsync(service, param, apiId, apiUniqueName, solutionName, cancellationToken);
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
        if (!save && allowDeletes)
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
        string solutionName,
        bool save,
        bool allowDeletes,
        CancellationToken cancellationToken = default)
    {
        var existingByName = existing.ToDictionary(e => e.GetAttributeValue<string>("uniquename"), e => e);

        foreach (var prop in properties)
        {
            if (!existingByName.TryGetValue(prop.UniqueName, out var existingProp))
            {
                await CreateResponsePropertyAsync(service, prop, apiId, apiUniqueName, solutionName, cancellationToken);
            }
            else
            {
                var responsePropertyComponentType = await GetComponentTypeFromSolutionComponentAsync(service, existingProp.Id, "customapiresponseproperty", cancellationToken);
                await EnsureComponentInSolutionAsync(service, "customapiresponseproperty", existingProp.Id, solutionName, cancellationToken);

                var immutableChanged =
                    (existingProp.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != prop.Type ||
                    existingProp.GetAttributeValue<string>("logicalentityname") != prop.EntityName;

                if (immutableChanged)
                {
                    if (!allowDeletes)
                        throw new InvalidOperationException($"Delete required for immutable response property '{prop.UniqueName}' on '{apiUniqueName}', but deletes are blocked in phase 3.");

                    output.Info($"[yellow]Warning:[/] Response property '{prop.UniqueName}' on '{apiUniqueName}' has immutable field changes — deleting and recreating.");
                    await service.DeleteAsync("customapiresponseproperty", existingProp.Id, cancellationToken);
                    await CreateResponsePropertyAsync(service, prop, apiId, apiUniqueName, solutionName, cancellationToken);
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
        if (!save && allowDeletes)
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

        await service.ExecuteAsync(new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName }, cancellationToken);
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

        await service.ExecuteAsync(new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName }, cancellationToken);
    }

    async Task DeleteCustomApiAsync(IOrganizationServiceAsync2 service, Guid apiId, CancellationToken cancellationToken = default)
    {
        foreach (var p in await GetResponsePropertiesAsync(service, apiId, cancellationToken))
            await service.DeleteAsync("customapiresponseproperty", p.Id, cancellationToken);

        foreach (var p in await GetRequestParametersAsync(service, apiId, cancellationToken))
            await service.DeleteAsync("customapirequestparameter", p.Id, cancellationToken);

        await service.DeleteAsync("customapi", apiId, cancellationToken);
    }

    async Task<DeletePlan> BuildDeletePlanAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        Guid assemblyId,
        string solutionName,
        CancellationToken cancellationToken)
    {
        var existingTypes = await GetPluginTypesAsync(service, assemblyId, cancellationToken);
        var existingPluginTypes = existingTypes.Where(t => !t.GetAttributeValue<bool>("isworkflowactivity")).ToList();
        var existingWorkflowTypes = existingTypes.Where(t => t.GetAttributeValue<bool>("isworkflowactivity")).ToList();

        var localPluginTypeNames = metadata.Plugins.Where(p => !p.IsWorkflow).Select(p => p.FullName).ToHashSet();
        var localWorkflowTypeNames = metadata.Plugins.Where(p => p.IsWorkflow).Select(p => p.FullName).ToHashSet();

        var obsoletePluginTypes = existingPluginTypes
            .Where(t => !localPluginTypeNames.Contains(t.GetAttributeValue<string>("typename")))
            .ToList();

        var obsoleteWorkflowTypes = existingWorkflowTypes
            .Where(t => !localWorkflowTypeNames.Contains(t.GetAttributeValue<string>("typename")))
            .ToList();

        var allSteps = await GetAllStepsForAssemblyAsync(service, assemblyId, cancellationToken);
        var stepsByTypeId = allSteps.ToLookup(s => s.GetAttributeValue<EntityReference>("plugintypeid")?.Id ?? Guid.Empty);

        var obsoleteTypeIds = obsoletePluginTypes.Select(t => t.Id).ToHashSet();
        var localStepsByTypeName = metadata.Plugins
            .Where(p => !p.IsWorkflow)
            .ToDictionary(p => p.FullName, p => p.Steps.Select(s => s.Name).ToHashSet());

        var stepDeletes = new List<EntityReference>();

        foreach (var obsoleteType in obsoletePluginTypes)
        {
            foreach (var step in stepsByTypeId[obsoleteType.Id])
            {
                if (!IsStageModifiable(step))
                    continue;

                stepDeletes.Add(step.ToEntityReference());
            }
        }

        foreach (var existingType in existingPluginTypes.Where(t => !obsoleteTypeIds.Contains(t.Id)))
        {
            var existingStepList = stepsByTypeId[existingType.Id].ToList();
            var typeName = existingType.GetAttributeValue<string>("typename");
            if (!localStepsByTypeName.TryGetValue(typeName, out var localStepNames))
                continue;

            foreach (var step in existingStepList.Where(s => !localStepNames.Contains(s.GetAttributeValue<string>("name"))))
            {
                if (!IsStageModifiable(step))
                    continue;

                stepDeletes.Add(step.ToEntityReference());
            }
        }

        var protectedTypeIds = obsoletePluginTypes
            .Where(t => stepsByTypeId[t.Id].Any(step => !IsStageModifiable(step)))
            .Select(t => t.Id)
            .ToHashSet();

        if (protectedTypeIds.Count > 0)
        {
            obsoletePluginTypes = obsoletePluginTypes
                .Where(t => !protectedTypeIds.Contains(t.Id))
                .ToList();
        }

        var allImages = await GetAllImagesForAssemblyAsync(service, assemblyId, cancellationToken);
        var deletedStepIds = stepDeletes.Select(s => s.Id).ToHashSet();
        var imageDeletes = allImages
            .Where(i => deletedStepIds.Contains(i.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty))
            .Select(i => i.ToEntityReference())
            .ToList();

        var existingApis = await GetCustomApisAsync(service, assemblyId, cancellationToken);
        List<Entity> obsoleteApis = [];
        if (metadata.CustomApis.Count > 0 || existingApis.Count > 0)
        {
            var prefix = await GetPublisherPrefixAsync(service, solutionName, cancellationToken);
            var localApiUniqueNames = metadata.CustomApis.Select(a => $"{prefix}_{a.UniqueName}").ToHashSet();
            obsoleteApis = existingApis
                .Where(e => !localApiUniqueNames.Contains(e.GetAttributeValue<string>("uniquename")))
                .ToList();
        }

        var allRequestParams = await GetAllRequestParametersForAssemblyAsync(service, assemblyId, cancellationToken);
        var allResponseProps = await GetAllResponsePropertiesForAssemblyAsync(service, assemblyId, cancellationToken);
        var obsoleteApiIds = obsoleteApis.Select(a => a.Id).ToHashSet();

        return new DeletePlan(
            imageDeletes,
            stepDeletes,
            obsoletePluginTypes.Select(t => t.ToEntityReference()).ToList(),
            obsoleteWorkflowTypes.Select(t => t.ToEntityReference()).ToList(),
            obsoleteApis.Select(a => a.ToEntityReference()).ToList(),
            allRequestParams.Where(p => obsoleteApiIds.Contains(p.GetAttributeValue<EntityReference>("customapiid")?.Id ?? Guid.Empty)).Select(p => p.ToEntityReference()).ToList(),
            allResponseProps.Where(p => obsoleteApiIds.Contains(p.GetAttributeValue<EntityReference>("customapiid")?.Id ?? Guid.Empty)).Select(p => p.ToEntityReference()).ToList());
    }

    async Task ExecuteDeletePhase2Async(IOrganizationServiceAsync2 service, DeletePlan plan, CancellationToken cancellationToken)
    {
        output.Verbose($"phase=2 wave=images count={plan.Images.Count} status=started");
        await ExecuteBoundedParallelAsync(plan.Images, MaxParallelism, image => service.DeleteAsync("sdkmessageprocessingstepimage", image.Id, cancellationToken), cancellationToken);
        output.Verbose("phase=2 wave=images status=completed");

        output.Verbose($"phase=2 wave=steps count={plan.Steps.Count} status=started");
        await ExecuteBoundedParallelAsync(plan.Steps, MaxParallelism, step => service.DeleteAsync("sdkmessageprocessingstep", step.Id, cancellationToken), cancellationToken);
        output.Verbose("phase=2 wave=steps status=completed");

        output.Verbose($"phase=2 wave=customapi-response-properties count={plan.CustomApiResponseProperties.Count} status=started");
        await ExecuteBoundedParallelAsync(plan.CustomApiResponseProperties, MaxParallelism, prop => service.DeleteAsync("customapiresponseproperty", prop.Id, cancellationToken), cancellationToken);
        output.Verbose("phase=2 wave=customapi-response-properties status=completed");

        output.Verbose($"phase=2 wave=customapi-request-parameters count={plan.CustomApiRequestParameters.Count} status=started");
        await ExecuteBoundedParallelAsync(plan.CustomApiRequestParameters, MaxParallelism, param => service.DeleteAsync("customapirequestparameter", param.Id, cancellationToken), cancellationToken);
        output.Verbose("phase=2 wave=customapi-request-parameters status=completed");

        output.Verbose($"phase=2 wave=types-apis-workflows count={plan.PluginTypes.Count + plan.CustomApis.Count + plan.WorkflowTypes.Count} status=started");
        await ExecuteBoundedParallelAsync(plan.PluginTypes, MaxParallelism, type => service.DeleteAsync("plugintype", type.Id, cancellationToken), cancellationToken);
        await ExecuteBoundedParallelAsync(plan.CustomApis, MaxParallelism, api => service.DeleteAsync("customapi", api.Id, cancellationToken), cancellationToken);
        await ExecuteBoundedParallelAsync(plan.WorkflowTypes, MaxParallelism, type => service.DeleteAsync("plugintype", type.Id, cancellationToken), cancellationToken);
        output.Verbose("phase=2 wave=types-apis-workflows status=completed");
    }

    static async Task ExecuteBoundedParallelAsync<T>(
        IReadOnlyCollection<T> items,
        int maxParallelism,
        Func<T, Task> action,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        using var gate = new SemaphoreSlim(maxParallelism);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await action(item);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
    }

    static bool IsStageModifiable(Entity step)
    {
        var stage = step.GetAttributeValue<OptionSetValue>("stage")?.Value ?? step.GetAttributeValue<int>("stage");
        return stage is 10 or 20 or 40;
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
            await WarnIfComponentExistsInOtherSolutionsAsync(
                service,
                PluginAssemblyComponentType,
                existing.Id,
                solutionName,
                "pluginassembly",
                existing.GetAttributeValue<string>("name") ?? existing.Id.ToString(),
                cancellationToken);

            await EnsureComponentInSolutionAsync(service, "pluginassembly", existing.Id, solutionName, cancellationToken);

            var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
            return (existing, storedHash != metadata.Hash);
        }
    }

    async Task EnsureComponentInSolutionAsync(
        IOrganizationServiceAsync2 service,
        string entityLogicalName,
        Guid componentId,
        string solutionName,
        CancellationToken cancellationToken = default)
    {
        var componentType = await GetComponentTypeAsync(service, entityLogicalName, componentId, cancellationToken);

        var addComponentRequest = new OrganizationRequest("AddSolutionComponent")
        {
            ["ComponentId"] = componentId,
            ["ComponentType"] = componentType,
            ["SolutionUniqueName"] = solutionName,
            ["AddRequiredComponents"] = false,
            ["DoNotIncludeSubcomponents"] = false
        };

        await service.ExecuteAsync(addComponentRequest, cancellationToken);
        output.Verbose($"Added {entityLogicalName} '{componentId}' to solution '{solutionName}'.");
    }

    async Task<int> GetComponentTypeAsync(
        IOrganizationServiceAsync2 service,
        string entityLogicalName,
        Guid componentId,
        CancellationToken cancellationToken = default)
    {
        if (_componentTypeByEntityLogicalName.TryGetValue(entityLogicalName, out var componentType))
            return componentType;

        componentType = await GetComponentTypeFromSolutionComponentAsync(service, componentId, entityLogicalName, cancellationToken);
        _componentTypeByEntityLogicalName[entityLogicalName] = componentType;
        return componentType;
    }

    async Task WarnIfComponentExistsInOtherSolutionsAsync(
        IOrganizationServiceAsync2 service,
        int componentType,
        Guid componentId,
        string currentSolutionName,
        string entityLogicalName,
        string componentDisplayName,
        CancellationToken cancellationToken = default)
    {
        var otherSolutions = await GetOtherSolutionUniqueNamesForComponentAsync(service, componentType, componentId, currentSolutionName, cancellationToken);
        if (otherSolutions.Count == 0)
            return;

        output.Info($"[yellow]Warning:[/] Updating {entityLogicalName} '{componentDisplayName}' which also exists in other solutions: {string.Join(", ", otherSolutions)}.");
    }

    async Task<List<string>> GetOtherSolutionUniqueNamesForComponentAsync(
        IOrganizationServiceAsync2 service,
        int componentType,
        Guid componentId,
        string currentSolutionName,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet(false),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("componenttype", ConditionOperator.Equal, componentType),
                    new ConditionExpression("objectid", ConditionOperator.Equal, componentId)
                }
            },
            LinkEntities =
            {
                new LinkEntity("solutioncomponent", "solution", "solutionid", "solutionid", JoinOperator.Inner)
                {
                    Columns = new ColumnSet("uniquename"),
                    EntityAlias = "sol"
                }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken);

        return result.Entities
            .Select(e => e.GetAttributeValue<AliasedValue>("sol.uniquename")?.Value as string)
            .Where(name =>
                !string.IsNullOrWhiteSpace(name) &&
                !string.Equals(name, currentSolutionName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, DefaultSolutionUniqueName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    async Task UpdateAssemblyContentAsync(IOrganizationServiceAsync2 service, Entity existing, PluginAssemblyMetadata metadata, CancellationToken cancellationToken = default)
    {
        existing["content"] = Convert.ToBase64String(metadata.Content);
        existing["version"] = metadata.Version;
        existing["description"] = $"{FlowlineMarker} sha256={metadata.Hash}";

        await service.UpdateAsync(existing, cancellationToken);
    }

    async Task<int> GetComponentTypeFromSolutionComponentAsync(
        IOrganizationServiceAsync2 service,
        Guid objectId,
        string entityLogicalName,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("componenttype"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("objectid", ConditionOperator.Equal, objectId)
                }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken);
        var componentType = result.Entities.FirstOrDefault()?.GetAttributeValue<OptionSetValue>("componenttype")?.Value;
        if (componentType.HasValue)
            return componentType.Value;

        throw new InvalidOperationException($"Could not resolve solution component type for {entityLogicalName} '{objectId}' from 'solutioncomponent'.");
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

    async Task<List<Entity>> GetAllStepsForAssemblyAsync(IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "name", "plugintypeid", "stage")
        };

        var typeLink = query.AddLink("plugintype", "plugintypeid", "plugintypeid");
        typeLink.LinkCriteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);

        return (await service.RetrieveMultipleAsync(query, cancellationToken)).Entities.ToList();
    }

    async Task<List<Entity>> GetAllImagesForAssemblyAsync(IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessageprocessingstepimage")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepimageid", "sdkmessageprocessingstepid", "name")
        };

        var stepLink = query.AddLink("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "sdkmessageprocessingstepid");
        var typeLink = stepLink.AddLink("plugintype", "plugintypeid", "plugintypeid");
        typeLink.LinkCriteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);

        return (await service.RetrieveMultipleAsync(query, cancellationToken)).Entities.ToList();
    }

    async Task<List<Entity>> GetAllRequestParametersForAssemblyAsync(IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapirequestparameter")
        {
            ColumnSet = new ColumnSet("customapirequestparameterid", "customapiid", "uniquename")
        };

        var apiLink = query.AddLink("customapi", "customapiid", "customapiid");
        var typeLink = apiLink.AddLink("plugintype", "plugintypeid", "plugintypeid");
        typeLink.LinkCriteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);

        return (await service.RetrieveMultipleAsync(query, cancellationToken)).Entities.ToList();
    }

    async Task<List<Entity>> GetAllResponsePropertiesForAssemblyAsync(IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapiresponseproperty")
        {
            ColumnSet = new ColumnSet("customapiresponsepropertyid", "customapiid", "uniquename")
        };

        var apiLink = query.AddLink("customapi", "customapiid", "customapiid");
        var typeLink = apiLink.AddLink("plugintype", "plugintypeid", "plugintypeid");
        typeLink.LinkCriteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);

        return (await service.RetrieveMultipleAsync(query, cancellationToken)).Entities.ToList();
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
