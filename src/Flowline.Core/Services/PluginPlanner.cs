using Microsoft.Xrm.Sdk;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class PluginPlanner(IAnsiConsole output, FlowlineRuntimeOptions opt)
{
    const string FlowlineMarker = "[flowline]";

    static readonly Dictionary<string, string> s_messagePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Assign"]                = "Target",
        ["Create"]                = "id",
        ["Delete"]                = "Target",
        ["DeliverIncoming"]       = "emailid",
        ["DeliverPromote"]        = "emailid",
        ["Merge"]                 = "Target",
        ["Route"]                 = "Target",
        ["Send"]                  = "emailid",
        ["SetState"]              = "entityMoniker",
        ["SetStateDynamicEntity"] = "entityMoniker",
        ["Update"]                = "Target",
    };

    public RegistrationPlan Plan(RegistrationSnapshot snapshot, PluginAssemblyMetadata metadata, Entity assembly, string solutionName)
    {
        var plan = new RegistrationPlan();

        foreach (var asmPluginType in metadata.Plugins)
        {
            if (!snapshot.PluginTypes.TryGetValue(asmPluginType.FullName, out var dvPluginType))
            {
                dvPluginType = new Entity("plugintype", Guid.NewGuid())
                {
                    ["typename"]        = asmPluginType.FullName,
                    ["name"]            = asmPluginType.FullName,
                    ["friendlyname"]    = asmPluginType.Name,
                    ["pluginassemblyid"] = assembly.ToEntityReference(),
                    ["description"]     = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };

                if (asmPluginType.IsWorkflow)
                    dvPluginType["workflowactivitygroupname"] = $"{metadata.Name} ({metadata.Version})";

                plan.PluginTypes.Upserts[asmPluginType.Name] = new UpsertAction(asmPluginType.Name, dvPluginType, IsCreate: true);
            }

            if (asmPluginType.IsWorkflow) continue;

            if (asmPluginType.IsCustomApi)
            {
                var (customApiPlan, requestParamPlan, responsePropPlan) = PlanCustomApi(snapshot, dvPluginType, asmPluginType.CustomApis, solutionName);
                plan.CustomApis.Add(customApiPlan);
                plan.RequestParams.Add(requestParamPlan);
                plan.ResponseProps.Add(responsePropPlan);
            }
            else
            {
                var (stepPlan, imagePlan) = PlanPluginSteps(snapshot, dvPluginType, asmPluginType, solutionName);
                plan.Steps.Add(stepPlan);
                plan.Images.Add(imagePlan);
            }
        }

        var asmPluginTypes = metadata.Plugins.ToDictionary(p => p.FullName, p => p).AsReadOnly();
        foreach (var obsoletePluginType in snapshot.PluginTypes.Where(t => !asmPluginTypes.ContainsKey(t.Key)))
        {
            if (obsoletePluginType.Value.GetAttributeValue<bool>("isworkflowactivity"))
            {
                plan.PluginTypes.Deletes[obsoletePluginType.Key] = new DeleteAction(obsoletePluginType.Key, "plugintype", obsoletePluginType.Value.Id);
                continue;
            }

            // Try both paths — only the one with registered items will produce actions
            var (customApiPlan, requestParamPlan, responsePropPlan) = PlanCustomApi(snapshot, obsoletePluginType.Value, [], solutionName);
            plan.CustomApis.Add(customApiPlan);
            plan.RequestParams.Add(requestParamPlan);
            plan.ResponseProps.Add(responsePropPlan);

            var obsoleteMetadata = new PluginTypeMetadata(
                obsoletePluginType.Value.GetAttributeValue<string>("name") ?? obsoletePluginType.Key,
                obsoletePluginType.Key,
                [],
                [],
                false);
            var (stepPlan, imagePlan) = PlanPluginSteps(snapshot, obsoletePluginType.Value, obsoleteMetadata, solutionName);
            plan.Steps.Add(stepPlan);
            plan.Images.Add(imagePlan);

            plan.PluginTypes.Deletes[obsoletePluginType.Key] = new DeleteAction(obsoletePluginType.Key, "plugintype", obsoletePluginType.Value.Id);
        }

        AddCrossSolutionWarnings(plan, snapshot, solutionName);

        return plan;
    }

    static void AddCrossSolutionWarnings(RegistrationPlan plan, RegistrationSnapshot snapshot, string solutionName)
    {
        var updates = plan.Steps.Upserts.Values
            .Concat(plan.Images.Upserts.Values)
            .Concat(plan.CustomApis.Upserts.Values)
            .Concat(plan.RequestParams.Upserts.Values)
            .Concat(plan.ResponseProps.Upserts.Values)
            .Where(a => !a.IsCreate);

        foreach (var action in updates)
        {
            if (!snapshot.ComponentSolutionMembership.TryGetValue(action.Entity.Id, out var solutions))
                continue;

            var others = solutions
                .Where(s => !string.Equals(s, solutionName, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (others.Count > 0)
                plan.Warnings.Add($"Updating {action.Entity.LogicalName} '{action.Name}' which also exists in other solutions: {string.Join(", ", others)}.");
        }
    }

    (ActionPlan stepPlan, ActionPlan imagePlan) PlanPluginSteps(
        RegistrationSnapshot snapshot, Entity typeEntity, PluginTypeMetadata asmPluginType, string solutionName)
    {
        ActionPlan stepPlan = new();
        ActionPlan imagesPlan = new();
        var asmPluginSteps = asmPluginType.Steps;

        var dvSteps = snapshot.Steps
            .Where(s => (s.GetAttributeValue<EntityReference>("plugintypeid")?.Id ?? Guid.Empty) == typeEntity.Id)
            .ToDictionary(s => s.GetAttributeValue<string>("name"), s => s, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        foreach (var asmStep in asmPluginSteps)
        {
            if (!snapshot.SdkMessageIds.TryGetValue(asmStep.Message, out var messageId))
                throw new InvalidOperationException(
                    $"Step '{asmStep.Name}' references message '{asmStep.Message}' which does not exist in this environment. " +
                    $"Check the message name on [Step] for '{asmPluginType.FullName}'.");

            snapshot.FilterIds.TryGetValue((messageId, asmStep.EntityName, asmStep.SecondaryEntity), out var filterId);

            var entityRequested = !string.IsNullOrEmpty(asmStep.EntityName) &&
                                  !string.Equals(asmStep.EntityName, "none", StringComparison.OrdinalIgnoreCase);
            if (entityRequested && !filterId.HasValue)
                throw new InvalidOperationException(
                    $"Step '{asmStep.Name}' references entity '{asmStep.EntityName}' which is not supported for message '{asmStep.Message}' in this environment. " +
                    $"Check the entity name on [Step] for '{asmPluginType.FullName}'.");

            if (asmStep.RunAs.HasValue && !snapshot.SystemUserIds.Contains(asmStep.RunAs.Value))
                throw new InvalidOperationException(
                    $"Step '{asmStep.Name}' references RunAs system user '{asmStep.RunAs.Value}' which does not exist in this environment. " +
                    $"Check RunAs on [Step] for '{asmPluginType.FullName}'.");

            if (dvSteps.TryGetValue(asmStep.Name, out var dvStep))
            {
                stepPlan.AddSolutionComponents[asmStep.Name] =
                    new AddToSolutionAction(asmStep.Name, "sdkmessageprocessingstep", dvStep.Id, solutionName,
                        snapshot.ComponentTypeById.GetValueOrDefault(dvStep.Id, 92));

                var changed =
                    dvStep.GetAttributeValue<string>("configuration") != asmStep.Configuration ||
                    dvStep.GetAttributeValue<string>("filteringattributes") != asmStep.FilteringAttributes ||
                    dvStep.GetAttributeValue<OptionSetValue>("stage")?.Value != asmStep.Stage ||
                    dvStep.GetAttributeValue<OptionSetValue>("mode")?.Value != asmStep.Mode ||
                    dvStep.GetAttributeValue<int?>("rank") != asmStep.Order ||
                    dvStep.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id != messageId ||
                    dvStep.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id != filterId ||
                    dvStep.GetAttributeValue<bool>("asyncautodelete") != asmStep.AsyncAutoDelete ||
                    dvStep.GetAttributeValue<EntityReference?>("impersonatinguserid")?.Id != asmStep.RunAs;

                if (!changed)
                {
                    imagesPlan.Add(PlanImages(snapshot, dvStep, asmStep.Images, asmStep.Message));
                    continue;
                }

                dvStep["stage"]                = new OptionSetValue(asmStep.Stage);
                dvStep["mode"]                 = new OptionSetValue(asmStep.Mode);
                dvStep["rank"]                 = asmStep.Order;
                dvStep["filteringattributes"]  = asmStep.FilteringAttributes;
                dvStep["configuration"]        = asmStep.Configuration;
                dvStep["asyncautodelete"]      = asmStep.AsyncAutoDelete;
                dvStep["impersonatinguserid"]  = asmStep.RunAs.HasValue ? new EntityReference("systemuser", asmStep.RunAs.Value) : null;
                dvStep["sdkmessageid"]         = new EntityReference("sdkmessage", messageId);
                dvStep["sdkmessagefilterid"]   = filterId.HasValue ? new EntityReference("sdkmessagefilter", filterId.Value) : null;

                stepPlan.Upserts[asmStep.Name] = new UpsertAction(asmStep.Name, dvStep, IsCreate: false);
            }
            else
            {
                var entity = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
                {
                    ["name"]               = asmStep.Name,
                    ["plugintypeid"]       = typeEntity.ToEntityReference(),
                    ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
                    ["stage"]              = new OptionSetValue(asmStep.Stage),
                    ["mode"]               = new OptionSetValue(asmStep.Mode),
                    ["rank"]               = asmStep.Order,
                    ["filteringattributes"] = asmStep.FilteringAttributes,
                    ["configuration"]      = asmStep.Configuration,
                    ["asyncautodelete"]    = asmStep.AsyncAutoDelete,
                    ["impersonatinguserid"] = asmStep.RunAs.HasValue ? new EntityReference("systemuser", asmStep.RunAs.Value) : null,
                    ["description"]        = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };
                if (filterId.HasValue)
                    entity["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);

                stepPlan.Upserts[asmStep.Name] = new UpsertAction(asmStep.Name, entity, IsCreate: true, SolutionName: solutionName);
                dvStep = entity;
            }

            imagesPlan.Add(PlanImages(snapshot, dvStep, asmStep.Images, asmStep.Message));
        }

        foreach (var obsoleteStep in dvSteps.Where(s => asmPluginSteps.All(p => p.Name != s.Key)))
        {
            var stepName = obsoleteStep.Value.GetAttributeValue<string>("name");
            stepPlan.Deletes[stepName] = new DeleteAction(stepName, "sdkmessageprocessingstep", obsoleteStep.Value.Id);

            foreach (var obsoleteImage in snapshot.Images.Where(i => (i.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty) == obsoleteStep.Value.Id))
            {
                var imageName = obsoleteImage.GetAttributeValue<string>("name");
                imagesPlan.Deletes[imageName] = new DeleteAction(imageName, "sdkmessageprocessingstepimage", obsoleteImage.Id);
            }
        }

        return (stepPlan, imagesPlan);
    }

    ActionPlan PlanImages(RegistrationSnapshot snapshot, Entity stepEntity, List<PluginImageMetadata> asmImages, string message)
    {
        ActionPlan plan = new();

        var dvImages = snapshot.Images
            .Where(i => (i.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty) == stepEntity.Id)
            .ToDictionary(i => i.GetAttributeValue<string>("name"), i => i, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        foreach (var asmImage in asmImages)
        {
            if (dvImages.TryGetValue(asmImage.Name, out var dvImage))
            {
                var changed =
                    dvImage.GetAttributeValue<string>("entityalias") != asmImage.Alias ||
                    (dvImage.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? 0) != asmImage.ImageType ||
                    dvImage.GetAttributeValue<string>("attributes") != asmImage.Attributes;

                if (!changed) continue;

                dvImage["name"]        = asmImage.Name;
                dvImage["entityalias"] = asmImage.Alias;
                dvImage["imagetype"]   = new OptionSetValue(asmImage.ImageType);
                dvImage["attributes"]  = asmImage.Attributes;

                plan.Upserts[asmImage.Name] = new UpsertAction(asmImage.Name, dvImage, IsCreate: false);
            }
            else
            {
                if (!s_messagePropertyNames.TryGetValue(message, out var propertyName))
                    throw new InvalidOperationException(
                        $"Image '{asmImage.Name}' cannot be registered — message '{message}' does not support step images. " +
                        $"Supported messages: {string.Join(", ", s_messagePropertyNames.Keys)}.");

                var entity = new Entity("sdkmessageprocessingstepimage")
                {
                    ["name"]                       = asmImage.Name,
                    ["entityalias"]                = asmImage.Alias,
                    ["imagetype"]                  = new OptionSetValue(asmImage.ImageType),
                    ["attributes"]                 = asmImage.Attributes,
                    ["messagepropertyname"]        = propertyName,
                    ["sdkmessageprocessingstepid"] = stepEntity.ToEntityReference(),
                    ["description"]                = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };

                plan.Upserts[asmImage.Name] = new UpsertAction(asmImage.Name, entity, IsCreate: true);
            }
        }

        foreach (var obsoleteImage in dvImages.Where(i => asmImages.All(a => a.Name != i.Key)))
            plan.Deletes[obsoleteImage.Key] = new DeleteAction(obsoleteImage.Key, "sdkmessageprocessingstepimage", obsoleteImage.Value.Id);

        return plan;
    }

    (ActionPlan customApiPlan, ActionPlan requestParamPlan, ActionPlan responsePropPlan) PlanCustomApi(
        RegistrationSnapshot snapshot, Entity typeEntity, List<CustomApiMetadata> asmCustomApis, string solutionName)
    {
        ActionPlan apiPlan = new();
        ActionPlan paramPlan = new();
        ActionPlan propPlan = new();

        var prefix = snapshot.PublisherPrefix;
        var dvApis = snapshot.CustomApis
            .ToDictionary(e => e.GetAttributeValue<string>("uniquename"), e => e)
            .AsReadOnly();

        foreach (var asmApi in asmCustomApis)
        {
            var fullApiName = $"{prefix}_{asmApi.UniqueName}";

            if (!dvApis.TryGetValue(fullApiName, out var dvApi))
            {
                var newApi = NewCustomApiEntity(fullApiName, asmApi, typeEntity);
                apiPlan.Upserts[asmApi.UniqueName] = new UpsertAction(asmApi.UniqueName, newApi, IsCreate: true, SolutionName: solutionName);
                paramPlan.Add(PlanRequestParameters(snapshot, prefix, newApi.Id, asmApi.UniqueName, asmApi.RequestParameters, solutionName));
                propPlan.Add(PlanResponseProperties(snapshot, prefix, newApi.Id, asmApi.UniqueName, asmApi.ResponseProperties, solutionName));
                continue;
            }

            var immutableChanged =
                (dvApi.GetAttributeValue<OptionSetValue>("bindingtype")?.Value ?? 0) != asmApi.BindingType ||
                dvApi.GetAttributeValue<string>("boundentitylogicalname") != asmApi.BoundEntityLogicalName ||
                dvApi.GetAttributeValue<bool>("isfunction") != asmApi.IsFunction ||
                (dvApi.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype")?.Value ?? 0) != asmApi.AllowedStepType;

            if (immutableChanged)
            {
                output.Warning($"Custom API '{fullApiName}' has immutable field changes — deleting and recreating.");

                apiPlan.Deletes[asmApi.UniqueName] = new DeleteAction(asmApi.UniqueName, "customapi", dvApi.Id);
                paramPlan.Add(PlanRequestParameters(snapshot, prefix, dvApi.Id, fullApiName, [], solutionName));
                propPlan.Add(PlanResponseProperties(snapshot, prefix, dvApi.Id, fullApiName, [], solutionName));

                var newApi = NewCustomApiEntity(fullApiName, asmApi, typeEntity);
                apiPlan.Upserts[asmApi.UniqueName] = new UpsertAction(asmApi.UniqueName, newApi, IsCreate: true, SolutionName: solutionName);
                paramPlan.Add(PlanRequestParameters(snapshot, prefix, newApi.Id, fullApiName, asmApi.RequestParameters, solutionName));
                propPlan.Add(PlanResponseProperties(snapshot, prefix, newApi.Id, fullApiName, asmApi.ResponseProperties, solutionName));
                continue;
            }

            apiPlan.AddSolutionComponents[fullApiName] = new AddToSolutionAction(fullApiName, "customapi", dvApi.Id, solutionName,
                snapshot.ComponentTypeById[dvApi.Id]);
            paramPlan.Add(PlanRequestParameters(snapshot, prefix, dvApi.Id, fullApiName, asmApi.RequestParameters, solutionName));
            propPlan.Add(PlanResponseProperties(snapshot, prefix, dvApi.Id, fullApiName, asmApi.ResponseProperties, solutionName));

            var mutableChanged =
                dvApi.GetAttributeValue<EntityReference>("plugintypeid")?.Id != typeEntity.Id ||
                dvApi.GetAttributeValue<string>("displayname") != asmApi.DisplayName ||
                dvApi.GetAttributeValue<string>("description") != asmApi.Description ||
                dvApi.GetAttributeValue<bool>("isprivate") != asmApi.IsPrivate ||
                dvApi.GetAttributeValue<string>("executeprivilegename") != asmApi.ExecutePrivilege;

            if (!mutableChanged) continue;

            dvApi["plugintypeid"]       = typeEntity.ToEntityReference();
            dvApi["displayname"]        = asmApi.DisplayName;
            dvApi["description"]        = asmApi.Description;
            dvApi["isprivate"]          = asmApi.IsPrivate;
            dvApi["executeprivilegename"] = asmApi.ExecutePrivilege;
            apiPlan.Upserts[asmApi.UniqueName] = new UpsertAction(asmApi.UniqueName, dvApi, IsCreate: false);
        }

        // Fix: compare with prefix-qualified name so only truly absent APIs are treated as obsolete
        foreach (var obsoleteApi in dvApis.Where(a => asmCustomApis.All(c => $"{prefix}_{c.UniqueName}" != a.Key)))
        {
            apiPlan.Deletes[obsoleteApi.Key] = new DeleteAction(obsoleteApi.Key, "customapi", obsoleteApi.Value.Id);
            paramPlan.Add(PlanRequestParameters(snapshot, prefix, obsoleteApi.Value.Id, obsoleteApi.Key, [], solutionName));
            propPlan.Add(PlanResponseProperties(snapshot, prefix, obsoleteApi.Value.Id, obsoleteApi.Key, [], solutionName));
        }

        return (apiPlan, paramPlan, propPlan);
    }

    ActionPlan PlanRequestParameters(
        RegistrationSnapshot snapshot, string prefix, Guid customApiId, string customApiName,
        List<RequestParameterMetadata> asmRequestParams, string solutionName)
    {
        ActionPlan plan = new();

        var dvRequestParams = snapshot.RequestParams
            .Where(r => r.GetAttributeValue<EntityReference>("customapiid")?.Id == customApiId)
            .ToDictionary(r => r.GetAttributeValue<string>("uniquename"), r => r, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        foreach (var asmParam in asmRequestParams)
        {
            var parameterKey = $"{customApiName}.{asmParam.UniqueName}";

            if (!dvRequestParams.TryGetValue(asmParam.UniqueName, out var dvParam))
            {
                plan.Upserts[parameterKey] = new UpsertAction(asmParam.UniqueName,
                    NewRequestParameterEntity(asmParam, customApiId), IsCreate: true, SolutionName: solutionName);
                continue;
            }

            // TODO: Maybe not immutable, because IsValidForUpdate=true? Check if we can update it.
            var immutableChanged =
                (dvParam.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != asmParam.Type ||
                dvParam.GetAttributeValue<bool>("isoptional") != asmParam.IsOptional ||
                dvParam.GetAttributeValue<string>("logicalentityname") != asmParam.EntityName;

            if (immutableChanged)
            {
                output.Warning($"Request parameter '{asmParam.DisplayName}' has immutable field changes — deleting and recreating.");
                plan.Deletes[parameterKey] = new DeleteAction(asmParam.UniqueName, "customapirequestparameter", dvParam.Id);
                plan.Upserts[parameterKey] = new UpsertAction(asmParam.UniqueName,
                    NewRequestParameterEntity(asmParam, customApiId), IsCreate: true, SolutionName: solutionName);
                continue;
            }

            plan.AddSolutionComponents[parameterKey] =
                new AddToSolutionAction(asmParam.UniqueName, "customapirequestparameter", dvParam.Id, solutionName,
                    snapshot.ComponentTypeById[dvParam.Id]);

            var mutableChanged =
                dvParam.GetAttributeValue<string>("name") != asmParam.Name ||
                dvParam.GetAttributeValue<string>("displayname") != asmParam.DisplayName ||
                dvParam.GetAttributeValue<string>("description") != asmParam.Description;

            if (!mutableChanged) continue;

            dvParam["name"]        = asmParam.Name;
            dvParam["displayname"] = asmParam.DisplayName;
            dvParam["description"] = asmParam.Description;
            plan.Upserts[parameterKey] = new UpsertAction(asmParam.UniqueName, dvParam, IsCreate: false);
        }

        foreach (var obsoleteParam in dvRequestParams.Where(r => asmRequestParams.All(p => p.UniqueName != r.Key)))
        {
            var name = obsoleteParam.Value.GetAttributeValue<string>("uniquename");
            plan.Deletes[name] = new DeleteAction(name, "customapirequestparameter", obsoleteParam.Value.Id);
        }

        return plan;
    }

    ActionPlan PlanResponseProperties(
        RegistrationSnapshot snapshot, string prefix, Guid customApiId, string customApiName,
        List<ResponsePropertyMetadata> asmResponseProps, string solutionName)
    {
        ActionPlan plan = new();

        var dvResponseProps = snapshot.ResponseProps
            .Where(r => r.GetAttributeValue<EntityReference>("customapiid")?.Id == customApiId)
            .ToDictionary(r => r.GetAttributeValue<string>("uniquename"), r => r, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        foreach (var asmProp in asmResponseProps)
        {
            var propertyKey = $"{customApiName}.{asmProp.UniqueName}";

            if (!dvResponseProps.TryGetValue(asmProp.UniqueName, out var dvProp))
            {
                plan.Upserts[propertyKey] = new UpsertAction(asmProp.UniqueName,
                    NewResponsePropertyEntity(asmProp, customApiId), IsCreate: true, SolutionName: solutionName);
                continue;
            }

            // TODO: Maybe not immutable, because IsValidForUpdate=true? Check if we can update it.
            var immutableChanged =
                (dvProp.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != asmProp.Type ||
                dvProp.GetAttributeValue<string>("logicalentityname") != asmProp.EntityName;

            if (immutableChanged)
            {
                output.Warning($"Response Property '{asmProp.DisplayName}' has immutable field changes — deleting and recreating.");
                plan.Deletes[propertyKey] = new DeleteAction(asmProp.UniqueName, "customapiresponseproperty", dvProp.Id);
                plan.Upserts[propertyKey] = new UpsertAction(asmProp.UniqueName,
                    NewResponsePropertyEntity(asmProp, customApiId), IsCreate: true, SolutionName: solutionName);
                continue;
            }

            plan.AddSolutionComponents[propertyKey] =
                new AddToSolutionAction(asmProp.UniqueName, "customapiresponseproperty", dvProp.Id, solutionName,
                    snapshot.ComponentTypeById[dvProp.Id]);

            var mutableChanged =
                dvProp.GetAttributeValue<string>("name") != asmProp.Name ||
                dvProp.GetAttributeValue<string>("displayname") != asmProp.DisplayName ||
                dvProp.GetAttributeValue<string>("description") != asmProp.Description;

            if (!mutableChanged) continue;

            dvProp["name"]        = asmProp.Name;
            dvProp["displayname"] = asmProp.DisplayName;
            dvProp["description"] = asmProp.Description;
            plan.Upserts[propertyKey] = new UpsertAction(asmProp.UniqueName, dvProp, IsCreate: false);
        }

        foreach (var obsoleteProp in dvResponseProps.Where(r => asmResponseProps.All(p => p.UniqueName != r.Key)))
        {
            var name = obsoleteProp.Value.GetAttributeValue<string>("uniquename");
            plan.Deletes[name] = new DeleteAction(name, "customapiresponseproperty", obsoleteProp.Value.Id);
        }

        return plan;
    }

    static Entity NewCustomApiEntity(string uniqueName, CustomApiMetadata asmApi, Entity typeEntity) =>
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

    static Entity NewRequestParameterEntity(RequestParameterMetadata asmParam, Guid customApiId) =>
        new("customapirequestparameter")
        {
            ["uniquename"]        = asmParam.UniqueName,
            ["name"]              = asmParam.Name,
            ["displayname"]       = asmParam.DisplayName,
            ["description"]       = asmParam.Description,
            ["type"]              = new OptionSetValue(asmParam.Type),
            ["isoptional"]        = asmParam.IsOptional,
            ["logicalentityname"] = asmParam.EntityName,
            ["customapiid"]       = new EntityReference("customapi", customApiId),
        };

    static Entity NewResponsePropertyEntity(ResponsePropertyMetadata asmProp, Guid customApiId) =>
        new("customapiresponseproperty")
        {
            ["uniquename"]        = asmProp.UniqueName,
            ["name"]              = asmProp.Name,
            ["displayname"]       = asmProp.DisplayName ?? asmProp.UniqueName,
            ["description"]       = string.IsNullOrWhiteSpace(asmProp.Description) ? (asmProp.DisplayName ?? asmProp.UniqueName) : asmProp.Description,
            ["type"]              = new OptionSetValue(asmProp.Type),
            ["logicalentityname"] = asmProp.EntityName,
            ["customapiid"]       = new EntityReference("customapi", customApiId),
        };
}
