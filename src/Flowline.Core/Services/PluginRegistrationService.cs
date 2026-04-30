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
        var (assembly, needsUpdate) = await GetOrRegisterAssemblyAsync(service, metadata, solutionName, cancellationToken).ConfigureAwait(false);
        var context = new RegistrationContext(service, metadata, assembly, solutionName, save, cancellationToken);
        output.Info($"Assembly '{metadata.Name}' ({metadata.Version}) found in solution '{solutionName}'.");

        // Phase 2: Plan registration
        var registrationPlan = await PlanRegistrationAsync(context).ConfigureAwait(false);
        output.Info($"Registration plan created");

        // Phase 3: Execute deletes
        await ExecuteDeleteAsync(registrationPlan, context.Save, context.CancellationToken).ConfigureAwait(false);
        output.Info($"Deleted obsolete components for [bold]{context.Metadata.Name}[/]");

        // Phase 4: Update Assembly content
        if (needsUpdate)
        {
            await WarnIfComponentExistsInOtherSolutionsAsync(context.Service, context.Assembly.Id, solutionName, "pluginassembly", context.Metadata.Name, context.CancellationToken).ConfigureAwait(false);
            context.Assembly["content"] = Convert.ToBase64String(context.Metadata.Content);
            context.Assembly["version"] = context.Metadata.Version;
            context.Assembly["description"] = $"{FlowlineMarker} sha256={context.Metadata.Hash}";
            await context.Service.UpdateAsync(context.Assembly, context.CancellationToken).ConfigureAwait(false);
            output.Info($"[green]Updated assembly content for [bold]{context.Metadata.Name}[/][/]");
        }
        else
        {
            output.Skip($"Assembly '{metadata.Name}' is unchanged — skipping upload.");
        }

        // Phase 5: Execute upserts from plan
        await ExecuteUpsertAsync(registrationPlan, context.CancellationToken).ConfigureAwait(false);

        // Phase 6: Add components to solution from plan
        await ExecuteAddSolutionComponentsAsync(registrationPlan, context.CancellationToken).ConfigureAwait(false);
    }

    async Task<RegistrationPlan> PlanRegistrationAsync(RegistrationContext context)
    {
        var plan = new RegistrationPlan();

        var registeredPluginTypes = await GetRegisteredPluginTypesAsync(context.Service, context.Assembly.Id, context.CancellationToken).ConfigureAwait(false);
        output.Verbose($"Found {registeredPluginTypes.Count} registered plugin types.");
        foreach (var p in registeredPluginTypes.Keys) output.Verbose($"- {p}");

        // Registering plugin types
        foreach (var asmPluginType in context.Metadata.Plugins)
        {
            if (!registeredPluginTypes.TryGetValue(asmPluginType.FullName, out var dvPluginType))
            {
                // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/reference/entities/plugintype
                dvPluginType = new Entity("plugintype", Guid.NewGuid())
                {
                    ["typename"] = asmPluginType.FullName,
                    ["name"] = asmPluginType.FullName,
                    ["friendlyname"] = asmPluginType.Name,
                    ["pluginassemblyid"] = context.Assembly.ToEntityReference(),
                    ["description"] = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };

                if (asmPluginType.IsWorkflow)
                    dvPluginType["workflowactivitygroupname"] = $"{context.Metadata.Name} ({context.Metadata.Version})";

                plan.PluginTypes.Upserts.Add(asmPluginType.Name, () => context.Service.CreateAsync(dvPluginType, context.CancellationToken));
            }

            // No additional steps to register for workflow types.
            if (asmPluginType.IsWorkflow) continue;

            if (asmPluginType.IsCustomApi)
            {
                // CustomApi
                var actionPlans = await PlanRegisterCustomApiAsync(context, dvPluginType, asmPluginType.CustomApis).ConfigureAwait(false);
                plan.CustomApis.Add(actionPlans.customApiPlan);
                plan.RequestParams.Add(actionPlans.requestParamPlan);
                plan.ResponseProps.Add(actionPlans.responsePropPlan);
            }
            else
            {
                // Plugin
                var actionPlans = await PlanRegistrationPluginStepsAsync(context, dvPluginType, asmPluginType.Steps).ConfigureAwait(false);
                plan.Steps.Add(actionPlans.stepPlan);
                plan.Images.Add(actionPlans.imagePlan);
            }
        }

        // Delete obsolete plugin types, steps, images, and Custom APIs.
        var asmPluginTypes = context.Metadata.Plugins.ToDictionary(p => p.FullName, p => p).AsReadOnly();
        var obsoletePluginTypes = registeredPluginTypes.Where(t => !asmPluginTypes.ContainsKey(t.Key));
        foreach (var obsoletePluginType in obsoletePluginTypes)
        {
            // Workflow types have no steps — delete immediately.
            if (obsoletePluginType.Value.GetAttributeValue<bool>("isworkflowactivity"))
            {
                plan.PluginTypes.Deletes.Add(obsoletePluginType.Key, () => context.Service.DeleteAsync("plugintype", obsoletePluginType.Value.Id, context.CancellationToken));
                continue;
            }

            // CustomApi
            var actionPlans = await PlanRegisterCustomApiAsync(context, obsoletePluginType.Value, []).ConfigureAwait(false);
            plan.CustomApis.Add(actionPlans.customApiPlan);
            plan.RequestParams.Add(actionPlans.requestParamPlan);
            plan.ResponseProps.Add(actionPlans.responsePropPlan);

            // Plugin
            var actionPlans1 = await PlanRegistrationPluginStepsAsync(context, obsoletePluginType.Value, []).ConfigureAwait(false);
            plan.Steps.Add(actionPlans1.stepPlan);
            plan.Images.Add(actionPlans1.imagePlan);

            plan.PluginTypes.Deletes.Add(obsoletePluginType.Key, () => context.Service.DeleteAsync("plugintype", obsoletePluginType.Value.Id, context.CancellationToken));
        }

        return plan;
    }

    async Task<(ActionPlan stepPlan, ActionPlan imagePlan)> PlanRegistrationPluginStepsAsync(
        RegistrationContext context,
        Entity typeEntity,
        List<PluginStepMetadata> asmPluginSteps)
    {
        ActionPlan stepPlan = new();
        ActionPlan imagesPlan = new();

        var allRegisteredSteps = await GetRegisteredStepsAsync(context.Service, context.Assembly.Id, context.CancellationToken).ConfigureAwait(false);
        var dvSteps = allRegisteredSteps
                              .Where(s => (s.GetAttributeValue<EntityReference>("plugintypeid")?.Id ?? Guid.Empty) == typeEntity.Id)
                              .ToDictionary(s => s.GetAttributeValue<string>("name"), s => s, StringComparer.OrdinalIgnoreCase)
                              .AsReadOnly();

        output.Verbose($"- Found {dvSteps.Count} registered plugin steps for {typeEntity.GetAttributeValue<string>("name")}.");
        foreach (var registeredStepName in dvSteps.Keys) output.Verbose($"  - {registeredStepName}");

        // Register plugin steps
        foreach (var asmStep in asmPluginSteps)
        {
            var messageId = await LookupSdkMessageIdAsync(context.Service, asmStep.Message, context.CancellationToken).ConfigureAwait(false);
            var filterId = await LookupSdkMessageFilterIdAsync(context.Service, messageId, asmStep.EntityName, asmStep.SecondaryEntity, context.CancellationToken).ConfigureAwait(false);

            if (dvSteps.TryGetValue(asmStep.Name, out var dvStep))
            {
                // Already registered, so update if changed
                stepPlan.AddSolutionComponents.Add(
                    asmStep.Name,
                    () => AddSolutionComponentAsync(context.Service, "sdkmessageprocessingstep", dvStep.Id, context.SolutionName, context.CancellationToken));

                var changed = dvStep.GetAttributeValue<string>("configuration") != asmStep.Configuration ||
                              dvStep.GetAttributeValue<string>("filteringattributes") != asmStep.FilteringAttributes ||
                              dvStep.GetAttributeValue<OptionSetValue>("stage")?.Value != asmStep.Stage ||
                              dvStep.GetAttributeValue<OptionSetValue>("mode")?.Value != asmStep.Mode ||
                              dvStep.GetAttributeValue<int?>("rank") != asmStep.Order ||
                              dvStep.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id != messageId ||
                              dvStep.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id != filterId;

                if (!changed) continue;

                await WarnIfComponentExistsInOtherSolutionsAsync(context.Service, dvStep.Id, context.SolutionName, "sdkmessageprocessingstep", asmStep.Name, context.CancellationToken).ConfigureAwait(false);

                dvStep["stage"] = new OptionSetValue(asmStep.Stage);
                dvStep["mode"] = new OptionSetValue(asmStep.Mode);
                dvStep["rank"] = asmStep.Order;
                dvStep["filteringattributes"] = asmStep.FilteringAttributes;
                dvStep["configuration"] = asmStep.Configuration;
                dvStep["sdkmessageid"] = new EntityReference("sdkmessage", messageId);
                if (filterId.HasValue)
                    dvStep["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);
                // impersonatinguserid?

                stepPlan.Upserts.Add(asmStep.Name, () => context.Service.UpdateAsync(dvStep, context.CancellationToken));
            }
            else
            {
                // Not registered yet, so register it.
                var entity = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
                {
                    ["name"] = asmStep.Name,
                    ["plugintypeid"] = typeEntity.ToEntityReference(),
                    ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
                    ["stage"] = new OptionSetValue(asmStep.Stage),
                    ["mode"] = new OptionSetValue(asmStep.Mode),
                    ["rank"] = asmStep.Order,
                    ["filteringattributes"] = asmStep.FilteringAttributes,
                    ["configuration"] = asmStep.Configuration,
                    ["description"] = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };
                if (filterId.HasValue)
                    entity["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);

                var createStepRequest = new CreateRequest { Target = entity, ["SolutionUniqueName"] = context.SolutionName };
                stepPlan.Upserts.Add(asmStep.Name, () => context.Service.ExecuteAsync(createStepRequest, context.CancellationToken));

                dvStep = entity;
            }

            // Register Images
            imagesPlan.Add(await PlanRegistrationImagesAsync(context, dvStep, asmStep.Images, asmStep.Message).ConfigureAwait(false));
        }

        // Delete obsolete plugin steps
        var obsoletePluginSteps = dvSteps.Where(s => asmPluginSteps.All(p => p.Name != s.Key));
        foreach (var obsoleteStep in obsoletePluginSteps)
        {
            stepPlan.Deletes.Add(
                obsoleteStep.Value.GetAttributeValue<string>("name"), // TODO can we retrieve 'plugintypeidname'?
                () => context.Service.DeleteAsync("sdkmessageprocessingstep", obsoleteStep.Value.Id, context.CancellationToken));

            // Delete images
            var allRegisteredImages = await GetRegisteredImagesAsync(context.Service, context.Assembly.Id, context.CancellationToken).ConfigureAwait(false);
            var obsoleteImages = allRegisteredImages.Where(i => (i.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty) == obsoleteStep.Value.Id);
            foreach (var obsoleteImage in obsoleteImages)
            {
                imagesPlan.Deletes.Add(
                    obsoleteImage.GetAttributeValue<string>("name"),
                    () => context.Service.DeleteAsync("sdkmessageprocessingstepimage", obsoleteImage.Id, context.CancellationToken));
            }
        }

        return (stepPlan, imagesPlan);
    }

    async Task<ActionPlan> PlanRegistrationImagesAsync(RegistrationContext context, Entity stepEntity, List<PluginImageMetadata> asmImages, string message)
    {
        ActionPlan plan = new();

        var allRegisteredImages = await GetRegisteredImagesAsync(context.Service, context.Assembly.Id, context.CancellationToken).ConfigureAwait(false);
        var dvImages = allRegisteredImages
            .Where(i => (i.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty) == stepEntity.Id)
            .ToDictionary(i => i.GetAttributeValue<string>("name"), i => i, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        output.Verbose($"  - Found {dvImages.Count} registered plugin steps for {message}.");
        foreach (var i in dvImages.Keys) output.Verbose($"    - {i}");

        // Register Images
        foreach (var asmImage in asmImages)
        {
            if (dvImages.TryGetValue(asmImage.Name, out var dvImage))
            {
                // Already registered, so update if changed
                var changed =
                    dvImage.GetAttributeValue<string>("entityalias") != asmImage.Alias ||
                    (dvImage.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? 0) != asmImage.ImageType ||
                    dvImage.GetAttributeValue<string>("attributes") != asmImage.Attributes;

                if (!changed) continue;

                dvImage["name"] = asmImage.Name;
                dvImage["entityalias"] = asmImage.Alias;
                dvImage["imagetype"] = new OptionSetValue(asmImage.ImageType);
                dvImage["attributes"] = asmImage.Attributes;

                plan.Upserts.Add(asmImage.Name, () => context.Service.UpdateAsync(dvImage, context.CancellationToken));
            }
            else
            {
                // Not registered yet, so register it.
                // TODO Check if message supports images => Move to AssemblyAnalysisService
                if (!s_messagePropertyNames.TryGetValue(message, out var propertyName))
                    throw new InvalidOperationException($"Message '{message}' does not support step images.");

                var entity = new Entity("sdkmessageprocessingstepimage")
                {
                    ["name"] = asmImage.Name,
                    ["entityalias"] = asmImage.Alias,
                    ["imagetype"] = new OptionSetValue(asmImage.ImageType),
                    ["attributes"] = asmImage.Attributes,
                    ["messagepropertyname"] = propertyName,
                    ["sdkmessageprocessingstepid"] = stepEntity.ToEntityReference(),
                    ["description"] = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };

                plan.Upserts.Add(asmImage.Name, () => context.Service.CreateAsync(entity, context.CancellationToken));
            }
        }

        // Delete obsolete images
        var obsoleteImages = dvImages.Where(i => asmImages.All(a => a.Name != i.Key));
        foreach (var obsoleteImage in obsoleteImages)
        {
            plan.Deletes.Add(obsoleteImage.Key,
                () => context.Service.DeleteAsync("sdkmessageprocessingstepimage", obsoleteImage.Value.Id, context.CancellationToken));
        }

        return plan;
    }

    async Task<(ActionPlan customApiPlan, ActionPlan requestParamPlan, ActionPlan responsePropPlan)> PlanRegisterCustomApiAsync(RegistrationContext context, Entity typeEntity, List<CustomApiMetadata> asmCustomApis)
    {
        ActionPlan apiPlan = new();
        ActionPlan paramPlan = new();
        ActionPlan propPlan = new();

        // Filter by uniquename prefix — not by plugintypeid, because the dependency is reversed.
        // A re-registration of the assembly can create new plugintype records, leaving customapi.plugintypeid stale.
        var prefix = await GetPublisherPrefixAsync(context.Service, context.SolutionName, context.CancellationToken).ConfigureAwait(false);
        var allRegisteredCustomApis = await GetRegisteredCustomApisAsync(context.Service, prefix, context.CancellationToken).ConfigureAwait(false);
        var dvApis = allRegisteredCustomApis
                                   .ToDictionary(e => e.GetAttributeValue<string>("uniquename"), e => e)
                                   .AsReadOnly();

        output.Verbose($"- Found {dvApis.Count} registered Custom APIs for {typeEntity.GetAttributeValue<string>("name")}.");
        foreach (var a in dvApis.Keys) output.Verbose($"  - {a}");

        // Register CustomApis
        foreach (var asmApi in asmCustomApis)
        {
            var uniqueName = $"{prefix}_{asmApi.UniqueName}";

            if (!dvApis.TryGetValue(uniqueName, out var dvApi))
            {
                // Not registered yet, so register it.
                var newApi = NewCustomApiEntity(uniqueName, asmApi);
                apiPlan.Upserts.Add(asmApi.UniqueName, () =>
                    context.Service.ExecuteAsync(new CreateRequest { Target = newApi, ["SolutionUniqueName"] = context.SolutionName }, context.CancellationToken));
                paramPlan.Add(await PlanRegisterRequestParametersAsync(context, prefix, newApi.Id, asmApi.RequestParameters).ConfigureAwait(false));
                propPlan.Add(await PlanRegisterResponsePropertiesAsync(context, prefix, newApi.Id, asmApi.ResponseProperties).ConfigureAwait(false));
                continue;
            }

            // Already registered, so update if changed
            var immutableChanged =
                (dvApi.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0) != asmApi.BindingType ||
                dvApi.GetAttributeValue<string>("boundentitylogicalname") != asmApi.BoundEntityLogicalName ||
                dvApi.GetAttributeValue<bool>("isfunction") != asmApi.IsFunction ||
                (dvApi.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 0) != asmApi.AllowedStepType;

            // If immutable, then don't update it, but delete and re-register.
            if (immutableChanged)
            {
                output.Info($"[yellow]Warning:[/] Custom API '{uniqueName}' has immutable field changes — deleting and recreating.");

                // Delete existing CustomApi
                apiPlan.Deletes.Add(asmApi.UniqueName, () => context.Service.DeleteAsync("customapi", dvApi.Id, context.CancellationToken));
                paramPlan.Add(await PlanRegisterRequestParametersAsync(context, prefix, dvApi.Id, []).ConfigureAwait(false));
                propPlan.Add(await PlanRegisterResponsePropertiesAsync(context, prefix, dvApi.Id, []).ConfigureAwait(false));

                // Re-register CustomApi
                var newApi = NewCustomApiEntity(uniqueName, asmApi);
                apiPlan.Upserts.Add(asmApi.UniqueName, () =>
                    context.Service.ExecuteAsync(new CreateRequest { Target = newApi, ["SolutionUniqueName"] = context.SolutionName }, context.CancellationToken));
                paramPlan.Add(await PlanRegisterRequestParametersAsync(context, prefix, newApi.Id, asmApi.RequestParameters).ConfigureAwait(false));
                propPlan.Add(await PlanRegisterResponsePropertiesAsync(context, prefix, newApi.Id, asmApi.ResponseProperties).ConfigureAwait(false));

                continue;
            }

            apiPlan.AddSolutionComponents.Add(uniqueName, () =>
                AddSolutionComponentAsync(context.Service, "customapi", dvApi.Id, context.SolutionName, context.CancellationToken));

            paramPlan.Add(await PlanRegisterRequestParametersAsync(context, prefix, dvApi.Id, asmApi.RequestParameters).ConfigureAwait(false));
            propPlan.Add(await PlanRegisterResponsePropertiesAsync(context, prefix, dvApi.Id, asmApi.ResponseProperties).ConfigureAwait(false));

            var mutableChanged = dvApi.GetAttributeValue<EntityReference>("plugintypeid")?.Id != typeEntity.Id ||
                                 dvApi.GetAttributeValue<string>("displayname") != asmApi.DisplayName ||
                                 dvApi.GetAttributeValue<string>("description") != asmApi.Description ||
                                 dvApi.GetAttributeValue<bool>("isprivate") != asmApi.IsPrivate ||
                                 dvApi.GetAttributeValue<string>("executeprivilegename") != asmApi.ExecutePrivilege;

            if (!mutableChanged) continue;

            await WarnIfComponentExistsInOtherSolutionsAsync(context.Service, dvApi.Id, context.SolutionName, "customapi", uniqueName, context.CancellationToken).ConfigureAwait(false);

            dvApi["plugintypeid"] = typeEntity.ToEntityReference();
            dvApi["displayname"] = asmApi.DisplayName;
            dvApi["description"] = asmApi.Description;
            dvApi["isprivate"] = asmApi.IsPrivate;
            dvApi["executeprivilegename"] = asmApi.ExecutePrivilege;
            apiPlan.Upserts.Add(asmApi.UniqueName, () => context.Service.UpdateAsync(dvApi, context.CancellationToken));
        }

        // Delete obsolete CustomApis
        var obsoleteCustomApis = dvApis.Where(a => asmCustomApis.All(c => c.UniqueName != a.Key));
        foreach (var obsoleteApi in obsoleteCustomApis)
        {
            apiPlan.Deletes.Add(obsoleteApi.Key, () => context.Service.DeleteAsync("customapi", obsoleteApi.Value.Id, context.CancellationToken));
            paramPlan.Add(await PlanRegisterRequestParametersAsync(context, prefix, obsoleteApi.Value.Id, []).ConfigureAwait(false));
            propPlan.Add(await PlanRegisterResponsePropertiesAsync(context, prefix, obsoleteApi.Value.Id, []).ConfigureAwait(false));
        }

        return (apiPlan, paramPlan, propPlan);

        // Local functions
        Entity NewCustomApiEntity(string uniqueName, CustomApiMetadata asmApi) =>
            new("customapi", Guid.NewGuid())
            {
                ["uniquename"]                      = uniqueName,
                ["name"]                            = uniqueName,
                ["displayname"]                     = asmApi.DisplayName,
                ["description"]                     = asmApi.Description,
                ["bindingtype"]                     = new OptionSetValue(asmApi.BindingType),
                ["boundentitylogicalname"]          = asmApi.BoundEntityLogicalName,
                ["isfunction"]                      = asmApi.IsFunction,
                ["isprivate"]                       = asmApi.IsPrivate,
                ["allowedcustomprocessingsteptype"] = new OptionSetValue(asmApi.AllowedStepType),
                ["executeprivilegename"]            = asmApi.ExecutePrivilege,
                ["plugintypeid"]                    = typeEntity.ToEntityReference(),
            };
    }

    async Task<ActionPlan> PlanRegisterRequestParametersAsync(RegistrationContext context, string prefix, Guid asmCustomApiId, List<RequestParameterMetadata> asmRequestParams)
    {
        ActionPlan plan = new();

        var allRegisteredRequestParams = await GetRegisteredRequestParametersAsync(context.Service, prefix, context.CancellationToken).ConfigureAwait(false);
        var dvRequestParams = allRegisteredRequestParams
            .Where(r => r.GetAttributeValue<EntityReference>("customapiid")?.Id == asmCustomApiId)
            .ToDictionary(r => r.GetAttributeValue<string>("uniquename"), r => r, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        output.Verbose($"  - Found {dvRequestParams.Count} registered request parameters for Custom API {asmCustomApiId}.");
        foreach (var rp in dvRequestParams.Keys) output.Verbose($"    - {rp}");

        // Register RequestParameters
        foreach (var asmParam in asmRequestParams)
        {
            if (!dvRequestParams.TryGetValue(asmParam.UniqueName, out var dvParam))
            {
                // Not registered yet, so register it.
                var newParam = NewRequestParameterEntity(asmParam);
                plan.Upserts.Add(
                    asmParam.UniqueName,
                    () => context.Service.ExecuteAsync(new CreateRequest { Target = newParam, ["SolutionUniqueName"] = context.SolutionName }, context.CancellationToken));
                continue;
            }

            // Already registered, so update if changed

            // TODO: Maybe not immutable, because IsValidForUpdate=true? Check if we can update it.
            var immutableChanged =
                (dvParam.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != asmParam.Type ||
                dvParam.GetAttributeValue<bool>("isoptional") != asmParam.IsOptional ||
                dvParam.GetAttributeValue<string>("logicalentityname") != asmParam.EntityName;

            if (immutableChanged)
            {
                // If immutable, then don't update it, but delete and re-register.
                output.Info($"[yellow]Warning:[/] Request parameter '{asmParam.DisplayName}' has immutable field changes — deleting and recreating.");
                plan.Deletes.Add(asmParam.UniqueName, () => context.Service.DeleteAsync("customapirequestparameter", dvParam.Id, context.CancellationToken));
                var newParam = NewRequestParameterEntity(asmParam);
                plan.Upserts.Add(
                    asmParam.UniqueName,
                    () => context.Service.ExecuteAsync(new CreateRequest { Target = newParam, ["SolutionUniqueName"] = context.SolutionName }, context.CancellationToken));
                continue;
            }

            plan.AddSolutionComponents.Add(
                asmParam.UniqueName,
                () => AddSolutionComponentAsync(context.Service, "customapirequestparameter", dvParam.Id, context.SolutionName, context.CancellationToken));

            var mutableChanged =
                dvParam.GetAttributeValue<string>("name") != asmParam.Name ||
                dvParam.GetAttributeValue<string>("displayname") != asmParam.DisplayName ||
                dvParam.GetAttributeValue<string>("description") != asmParam.Description;

            if (!mutableChanged) continue;

            await WarnIfComponentExistsInOtherSolutionsAsync(context.Service, dvParam.Id, context.SolutionName, "customapirequestparameter", asmParam.UniqueName, context.CancellationToken).ConfigureAwait(false);
            dvParam["name"] = asmParam.Name;
            dvParam["displayname"] = asmParam.DisplayName;
            dvParam["description"] = asmParam.Description;
            plan.Upserts.Add(asmParam.UniqueName, () => context.Service.UpdateAsync(dvParam, context.CancellationToken));
        }

        // Delete obsolete RequestParameters
        var obsoleteRequestParams = dvRequestParams.Where(r => asmRequestParams.All(p => p.UniqueName != r.Key));
        foreach (var obsoleteRequestParam in obsoleteRequestParams)
        {
            plan.Deletes.Add(
                obsoleteRequestParam.Value.GetAttributeValue<string>("uniquename"),
                () => context.Service.DeleteAsync("customapirequestparameter", obsoleteRequestParam.Value.Id, context.CancellationToken));
        }

        return plan;

        // Local functions
        Entity NewRequestParameterEntity(RequestParameterMetadata asmParam) =>
            new("customapirequestparameter")
            {
                ["uniquename"]        = asmParam.UniqueName,
                ["name"]              = asmParam.Name,
                ["displayname"]       = asmParam.DisplayName,
                ["description"]       = asmParam.Description,
                ["type"]              = new OptionSetValue(asmParam.Type),
                ["isoptional"]        = asmParam.IsOptional,
                ["logicalentityname"] = asmParam.EntityName,
                ["customapiid"]       = new EntityReference("customapi", asmCustomApiId),
            };
    }

    async Task<ActionPlan> PlanRegisterResponsePropertiesAsync(RegistrationContext context, string prefix, Guid asmCustomApiId,
        List<ResponsePropertyMetadata> asmResponseProps)
    {
        ActionPlan plan = new();

        var allRegisteredResponseProps = await GetRegisteredResponsePropertiesAsync(context.Service, prefix, context.CancellationToken).ConfigureAwait(false);
        var dvResponseProps = allRegisteredResponseProps
            .Where(r => r.GetAttributeValue<EntityReference>("customapiid")?.Id == asmCustomApiId)
            .ToDictionary(r => r.GetAttributeValue<string>("uniquename"), r => r, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        output.Verbose($"  - Found {dvResponseProps.Count} registered response properties for Custom API {asmCustomApiId}.");
        foreach (var rp in dvResponseProps.Keys) output.Verbose($"    - {rp}");

        // Register ResponseProperties
        foreach (var asmProp in asmResponseProps)
        {
            if (!dvResponseProps.TryGetValue(asmProp.UniqueName, out var dvProp))
            {
                // No response property found, register new one
                var newProp = NewResponsePropertyEntity(asmProp);
                plan.Upserts.Add(
                    asmProp.UniqueName,
                    () => context.Service.ExecuteAsync(new CreateRequest { Target = newProp, ["SolutionUniqueName"] = context.SolutionName }, context.CancellationToken));
                continue;
            }

            // Already registered, so update if changed

            // TODO: Maybe not immutable, because IsValidForUpdate=true? Check if we can update it.
            var immutableChanged =
                (dvProp.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != asmProp.Type ||
                dvProp.GetAttributeValue<string>("logicalentityname") != asmProp.EntityName;

            if (immutableChanged)
            {
                // If immutable, then don't update it, but delete and re-register.
                output.Info($"[yellow]Warning:[/] Response property '{asmProp.DisplayName}' has immutable field changes — deleting and recreating.");
                plan.Deletes.Add(asmProp.UniqueName, () => context.Service.DeleteAsync("customapiresponseproperty", dvProp.Id, context.CancellationToken));
                var newProp = NewResponsePropertyEntity(asmProp);
                plan.Upserts.Add(
                    asmProp.UniqueName,
                    () => context.Service.ExecuteAsync(new CreateRequest { Target = newProp, ["SolutionUniqueName"] = context.SolutionName }, context.CancellationToken));
                continue;
            }

            plan.AddSolutionComponents.Add(
                asmProp.UniqueName,
                () => AddSolutionComponentAsync(context.Service, "customapiresponseproperty", dvProp.Id, context.SolutionName, context.CancellationToken));

            var mutableChanged =
                dvProp.GetAttributeValue<string>("name") != asmProp.Name ||
                dvProp.GetAttributeValue<string>("displayname") != asmProp.DisplayName ||
                dvProp.GetAttributeValue<string>("description") != asmProp.Description;

            if (!mutableChanged) continue;

            await WarnIfComponentExistsInOtherSolutionsAsync(context.Service, dvProp.Id, context.SolutionName, "customapiresponseproperty", asmProp.UniqueName, context.CancellationToken).ConfigureAwait(false);
            dvProp["name"] = asmProp.Name;
            dvProp["displayname"] = asmProp.DisplayName;
            dvProp["description"] = asmProp.Description;
            plan.Upserts.Add(asmProp.UniqueName, () => context.Service.UpdateAsync(dvProp, context.CancellationToken));
        }

        // Delete obsolete ResponseProperties
        var obsoleteResponseProps = dvResponseProps.Where(r => asmResponseProps.All(p => p.UniqueName != r.Key));
        foreach (var obsoleteProp in obsoleteResponseProps)
        {
            plan.Deletes.Add(
                obsoleteProp.Value.GetAttributeValue<string>("uniquename"),
                () => context.Service.DeleteAsync("customapiresponseproperty", obsoleteProp.Value.Id, context.CancellationToken));
        }

        return plan;

        // Local functions
        Entity NewResponsePropertyEntity(ResponsePropertyMetadata asmProp) =>
            new("customapiresponseproperty")
            {
                ["uniquename"]        = asmProp.UniqueName,
                ["name"]              = asmProp.Name,
                ["displayname"]       = asmProp.DisplayName ?? asmProp.UniqueName,
                ["description"]       = string.IsNullOrWhiteSpace(asmProp.Description) ? (asmProp.DisplayName ?? asmProp.UniqueName) : asmProp.Description,
                ["type"]              = new OptionSetValue(asmProp.Type),
                ["logicalentityname"] = asmProp.EntityName,
                ["customapiid"]       = new EntityReference("customapi", asmCustomApiId),
            };
    }

    async Task ExecuteDeleteAsync(RegistrationPlan plan, bool save, CancellationToken cancellationToken)
    {
        // Level 3 - Delete (Images, ResponseProps, RequestParams)
        if (save)
        {
            foreach (var s in plan.Images.Deletes.Keys) output.Skip($"Image '{s}' not in source — kept (--save)");
            foreach (var s in plan.ResponseProps.Deletes.Keys) output.Skip($"Response property '{s}' not in source — kept (--save)");
            foreach (var s in plan.RequestParams.Deletes.Keys) output.Skip($"Request parameter '{s}' not in source — kept (--save)");
        }
        else
        {
            await Task.WhenAll(
                ExecuteBoundedParallelAsync(plan.Images.Deletes.Values, MaxParallelism, delete => delete(), cancellationToken),
                ExecuteBoundedParallelAsync(plan.ResponseProps.Deletes.Values, MaxParallelism, delete => delete(), cancellationToken),
                ExecuteBoundedParallelAsync(plan.RequestParams.Deletes.Values, MaxParallelism, delete => delete(), cancellationToken)).ConfigureAwait(false);

            foreach (var s in plan.Images.Deletes.Keys) output.Verbose($"Images '{s}' not in source — deleted");
            foreach (var s in plan.ResponseProps.Deletes.Keys) output.Verbose($"Response property '{s}' not in source — deleted");
            foreach (var s in plan.RequestParams.Deletes.Keys) output.Verbose($"Request Parameter '{s}' not in source — deleted");

            if (plan.Images.Deletes.Count > 0) output.Info($"Deleted {plan.Images.Deletes.Count} Images");
            if (plan.ResponseProps.Deletes.Count > 0) output.Info($"Deleted {plan.ResponseProps.Deletes.Count} Response Properties");
            if (plan.RequestParams.Deletes.Count > 0) output.Info($"Deleted {plan.RequestParams.Deletes.Count} Request Parameters");
        }

        // Level 2 - Delete (Steps, CustomApis)
        if (save)
        {
            foreach (var s in plan.Steps.Deletes.Keys) output.Skip($"Step '{s}' not in source — kept (--save)");
            foreach (var s in plan.CustomApis.Deletes.Keys) output.Skip($"Custom API '{s}' not in source — kept (--save)");
        }
        else
        {
            await Task.WhenAll(
                ExecuteBoundedParallelAsync(plan.Steps.Deletes.Values, MaxParallelism, delete => delete(), cancellationToken),
                ExecuteBoundedParallelAsync(plan.CustomApis.Deletes.Values, MaxParallelism, delete => delete(), cancellationToken)).ConfigureAwait(false);

            foreach (var s in plan.Steps.Deletes.Keys) output.Verbose($"Step '{s}' not in source — deleted");
            foreach (var s in plan.CustomApis.Deletes.Keys) output.Verbose($"Custom API '{s}' not in source — deleted");

            if (plan.Steps.Deletes.Count > 0) output.Info($"Deleted {plan.Steps.Deletes.Count} Plugin Steps");
            if (plan.CustomApis.Deletes.Count > 0) output.Info($"Deleted {plan.CustomApis.Deletes.Count} Custom APIs");
        }

        // Level 1 - Delete (PluginTypes)
        if (save)
        {
            foreach (var s in plan.PluginTypes.Deletes.Keys) output.Skip($"Plugin type '{s}' not in source — kept (--save)");
        }
        else
        {
            await ExecuteBoundedParallelAsync(plan.PluginTypes.Deletes.Values, MaxParallelism, delete => delete(), cancellationToken).ConfigureAwait(false);
            foreach (var s in plan.PluginTypes.Deletes.Keys) output.Verbose($"Plugin type '{s}' not in source — deleted");
            if (plan.PluginTypes.Deletes.Count > 0) output.Info($"Deleted {plan.PluginTypes.Deletes.Count} PluginTypes");
        }
    }

    async Task ExecuteUpsertAsync(RegistrationPlan plan, CancellationToken cancellationToken)
    {
        await ExecuteBoundedParallelAsync(plan.PluginTypes.Upserts.Values, 1, upsert => upsert(), cancellationToken).ConfigureAwait(false);
        foreach (var s in plan.PluginTypes.Upserts.Keys) output.Verbose($"Plugin type '{s}' upserted");
        if(plan.PluginTypes.Upserts.Count > 0) output.Info($"Upserted {plan.PluginTypes.Upserts.Count} PluginTypes");

        await Task.WhenAll(
            ExecuteBoundedParallelAsync(plan.Steps.Upserts.Values, MaxParallelism, upsert => upsert(), cancellationToken),
            ExecuteBoundedParallelAsync(plan.CustomApis.Upserts.Values, MaxParallelism, upsert => upsert(), cancellationToken)).ConfigureAwait(false);
        foreach (var s in plan.Steps.Upserts.Keys) output.Verbose($"Step '{s}' upserted");
        if (plan.Steps.Upserts.Count > 0) output.Info($"Upserted {plan.Steps.Upserts.Count} Plugin Steps");
        foreach (var s in plan.CustomApis.Upserts.Keys) output.Verbose($"Custom API '{s}' upserted");
        if (plan.CustomApis.Upserts.Count > 0) output.Info($"Upserted {plan.CustomApis.Upserts.Count} Custom APIs");

        await Task.WhenAll(
            ExecuteBoundedParallelAsync(plan.Images.Upserts.Values, MaxParallelism, upsert => upsert(), cancellationToken),
            ExecuteBoundedParallelAsync(plan.ResponseProps.Upserts.Values, MaxParallelism, upsert => upsert(), cancellationToken),
            ExecuteBoundedParallelAsync(plan.RequestParams.Upserts.Values, MaxParallelism, upsert => upsert(), cancellationToken)).ConfigureAwait(false);
        foreach (var s in plan.Images.Upserts.Keys) output.Verbose($"Image '{s}' upserted");
        if (plan.Images.Upserts.Count > 0) output.Info($"Upserted {plan.Images.Upserts.Count} Images");
        foreach (var s in plan.ResponseProps.Upserts.Keys) output.Verbose($"Response property '{s}' upserted");
        if (plan.ResponseProps.Upserts.Count > 0) output.Info($"Upserted {plan.ResponseProps.Upserts.Count} Response Properties");
        foreach (var s in plan.RequestParams.Upserts.Keys) output.Verbose($"Request Parameter '{s}' upserted");
        if (plan.RequestParams.Upserts.Count > 0) output.Info($"Upserted {plan.RequestParams.Upserts.Count} Request Parameters");
    }

    async Task ExecuteAddSolutionComponentsAsync(RegistrationPlan plan, CancellationToken cancellationToken)
    {
        var all = plan.PluginTypes.AddSolutionComponents.Values
            .Concat(plan.Steps.AddSolutionComponents.Values)
            .Concat(plan.CustomApis.AddSolutionComponents.Values)
            .Concat(plan.RequestParams.AddSolutionComponents.Values)
            .Concat(plan.ResponseProps.AddSolutionComponents.Values)
            .Concat(plan.Images.AddSolutionComponents.Values)
            .ToList();

        if (all.Count == 0)
            return;

        await ExecuteBoundedParallelAsync(all, MaxParallelism, action => action(), cancellationToken).ConfigureAwait(false);
        foreach (var s in plan.Steps.AddSolutionComponents.Keys) output.Verbose($"Step '{s}' added to solution");
        foreach (var s in plan.CustomApis.AddSolutionComponents.Keys) output.Verbose($"Custom API '{s}' added to solution");
        foreach (var s in plan.ResponseProps.AddSolutionComponents.Keys) output.Verbose($"Response property '{s}' added to solution");
        foreach (var s in plan.RequestParams.AddSolutionComponents.Keys) output.Verbose($"Request Parameter '{s}' added to solution");
        if (plan.Steps.AddSolutionComponents.Count > 0) output.Info($"Added {plan.Steps.AddSolutionComponents.Count} Plugin Steps to solution");
        if (plan.CustomApis.AddSolutionComponents.Count > 0) output.Info($"Added {plan.CustomApis.AddSolutionComponents.Count} Custom APIs to solution");
        if (plan.ResponseProps.AddSolutionComponents.Count > 0) output.Info($"Added {plan.ResponseProps.AddSolutionComponents.Count} Response Properties to solution");
        if (plan.RequestParams.AddSolutionComponents.Count > 0) output.Info($"Added {plan.RequestParams.AddSolutionComponents.Count} Request Parameters to solution");
    }

    static async Task ExecuteBoundedParallelAsync<T>(IReadOnlyCollection<T> items, int maxParallelism, Func<T, Task> action, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        using var gate = new SemaphoreSlim(maxParallelism);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action(item).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
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

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
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
                new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName }).ConfigureAwait(false);

            output.Info($"[green]Added assembly for [bold]{metadata.Name}[/][/]");

            entity.Id = response.id;
            return (entity, false);
        }

        await AddSolutionComponentAsync(service, "pluginassembly", existing.Id, solutionName, cancellationToken).ConfigureAwait(false);
        var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
        return (existing, storedHash != metadata.Hash);
    }

    /// <summary>Asynchronously adds a component to a specified solution in Microsoft Dataverse.</summary>
    /// <remarks>This operation is idempotent, meaning it can be safely retried without changing the result beyond the initial application.</remarks>
    /// <param name="service">The asynchronous organization service used to execute the request. </param>
    /// <param name="entityLogicalName">The logical name of the entity representing the component to be added.</param>
    /// <param name="componentId">The unique identifier of the component to be added.</param>
    /// <param name="solutionName">The unique name of the solution to which the component will be added.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests, with a default value of <see cref="CancellationToken.None"/>.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    async Task AddSolutionComponentAsync(IOrganizationServiceAsync2 service, string entityLogicalName, Guid componentId, string solutionName,
        CancellationToken cancellationToken = default)
    {
        var componentType = await GetComponentTypeAsync(service, entityLogicalName, componentId, cancellationToken).ConfigureAwait(false);

        var addComponentRequest = new OrganizationRequest("AddSolutionComponent")
        {
            ["ComponentId"] = componentId,
            ["ComponentType"] = componentType,
            ["SolutionUniqueName"] = solutionName,
            ["AddRequiredComponents"] = false,
            ["DoNotIncludeSubcomponents"] = false
        };

        await service.ExecuteAsync(addComponentRequest, cancellationToken).ConfigureAwait(false);
        output.Verbose($"Added {entityLogicalName} '{componentId}' to solution '{solutionName}'.");
    }

    async Task<int> GetComponentTypeAsync(IOrganizationServiceAsync2 service, string entityLogicalName, Guid componentId, CancellationToken cancellationToken = default)
    {
        if (_componentTypeByEntityLogicalName.TryGetValue(entityLogicalName, out var componentType))
            return componentType;

        var query = new QueryExpression("solutioncomponent")
        {
            TopCount = 1,
            ColumnSet = new ColumnSet("componenttype"),
            Criteria =
            {
                Conditions = { new ConditionExpression("objectid", ConditionOperator.Equal, componentId) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var fetchedComponentType = result.Entities.FirstOrDefault()?.GetAttributeValue<OptionSetValue>("componenttype")?.Value;
        if (!fetchedComponentType.HasValue)
            throw new InvalidOperationException($"Could not resolve solution component type for {entityLogicalName} '{componentId}' from 'solutioncomponent'.");

        _componentTypeByEntityLogicalName[entityLogicalName] = fetchedComponentType.Value;
        return fetchedComponentType.Value;
    }

    /// <summary>Checks if a specified component exists in other solutions within Microsoft Dataverse and logs a warning if found.</summary>
    /// <remarks>When updating the component, this can have an impact on other solutions, so we issue a warning.</remarks>
    /// <param name="service">The asynchronous organization service used to execute the operations.</param>
    /// <param name="componentId">The unique identifier of the component to be validated.</param>
    /// <param name="currentSolutionName">The name of the currently targeted solution in which the component is being modified.</param>
    /// <param name="entityLogicalName">The logical name of the entity representing the component.</param>
    /// <param name="componentDisplayName">The display name of the component being verified, used for logging purposes.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests, with a default value of <see cref="CancellationToken.None"/>.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    async Task WarnIfComponentExistsInOtherSolutionsAsync(IOrganizationServiceAsync2 service, Guid componentId, string currentSolutionName,
        string entityLogicalName, string componentDisplayName, CancellationToken cancellationToken = default)
    {
        var componentType = await GetComponentTypeAsync(service, entityLogicalName, componentId, cancellationToken).ConfigureAwait(false);

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

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var otherSolutions = result.Entities
                                   .Select(e => e.GetAttributeValue<AliasedValue>("sol.uniquename")?.Value as string)
                                   .Where(name =>
                                       !string.IsNullOrWhiteSpace(name) &&
                                       !string.Equals(name, currentSolutionName, StringComparison.OrdinalIgnoreCase) &&
                                       !string.Equals(name, DefaultSolutionUniqueName, StringComparison.OrdinalIgnoreCase))
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                   .Cast<string>()
                                   .ToList();

        if (otherSolutions.Count == 0)
            return;

        output.Info($"[yellow]Warning:[/] Updating {entityLogicalName} '{componentDisplayName}' which also exists in other solutions: {string.Join(", ", otherSolutions)}.");
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

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var solution = result.Entities.FirstOrDefault()
            ?? throw new InvalidOperationException($"Solution '{solutionName}' not found in Dataverse.");

        return solution.GetAttributeValue<AliasedValue>("pub.customizationprefix")?.Value as string
            ?? throw new InvalidOperationException($"Could not read publisher prefix for solution '{solutionName}'.");
    }

    async Task<IReadOnlyDictionary<string, Entity>> GetRegisteredPluginTypesAsync(IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("typename", "name", "isworkflowactivity"),
            Criteria =
            {
                Conditions = { new ConditionExpression("pluginassemblyid", ConditionOperator.Equal, assemblyId) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.Entities.ToDictionary(
            t => t.GetAttributeValue<string>("typename"),
            t => t,
            StringComparer.OrdinalIgnoreCase).AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredCustomApisAsync(IOrganizationServiceAsync2 service, string prefix, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapi")
        {
            ColumnSet = new ColumnSet(
                "uniquename", "name", "displayname", "description", "bindingtype", "boundentitylogicalname", "isfunction",
                "isprivate", "allowedcustomprocessingsteptype", "executeprivilegename", "plugintypeid"),
            Criteria =
            {
                Conditions = { new ConditionExpression("uniquename", ConditionOperator.BeginsWith, $"{prefix}_") }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredStepsAsync(IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        // category, => 'CustomAPI' for custom APIs
        // stage, => 30 for Custom APIs (can't delete 30!)
        var query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepid", "name", "description", "plugintypeid", "stage", "mode", "rank",
                "filteringattributes", "configuration", "asyncautodelete", "statecode", "category", "sdkmessageid", "solutionid"),
            Criteria =
            {
                Conditions =
                {
                    new ConditionExpression("category", ConditionOperator.NotEqual, "CustomAPI"),
                    new ConditionExpression("stage", ConditionOperator.NotEqual, 30)
                }
            },
            LinkEntities =
            {
                new LinkEntity("sdkmessageprocessingstep", "plugintype", "plugintypeid", "plugintypeid", JoinOperator.Inner)
                {
                    LinkCriteria =
                    {
                        Conditions = { new ConditionExpression("pluginassemblyid", ConditionOperator.Equal, assemblyId) }
                    }
                }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredImagesAsync(IOrganizationServiceAsync2 service, Guid assemblyId, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("sdkmessageprocessingstepimage")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepimageid", "sdkmessageprocessingstepid", "name", "entityalias", "imagetype", "attributes"),
        };

        var stepLink = query.AddLink("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "sdkmessageprocessingstepid");
        var typeLink = stepLink.AddLink("plugintype", "plugintypeid", "plugintypeid");
        typeLink.LinkCriteria.AddCondition("pluginassemblyid", ConditionOperator.Equal, assemblyId);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredRequestParametersAsync(IOrganizationServiceAsync2 service, string prefix, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapirequestparameter")
        {
            ColumnSet = new ColumnSet("customapirequestparameterid", "customapiid", "uniquename", "name", "displayname", "description", "type", "isoptional", "logicalentityname")
        };

        var apiLink = query.AddLink("customapi", "customapiid", "customapiid");
        apiLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.BeginsWith, $"{prefix}_");

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
    }

    async Task<IReadOnlyList<Entity>> GetRegisteredResponsePropertiesAsync(IOrganizationServiceAsync2 service, string prefix, CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression("customapiresponseproperty")
        {
            ColumnSet = new ColumnSet("customapiresponsepropertyid", "customapiid", "uniquename", "name", "displayname", "description", "type", "logicalentityname")
        };

        var apiLink = query.AddLink("customapi", "customapiid", "customapiid");
        apiLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.BeginsWith, $"{prefix}_");

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.AsReadOnly();
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

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
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

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Entities.FirstOrDefault()?.Id;
    }
}
