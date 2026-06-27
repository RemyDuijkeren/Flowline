using Microsoft.Xrm.Sdk;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class PluginPlanner(IAnsiConsole output, bool isVerbose)
{
    const string FlowlineMarker = "[flowline]";

    static readonly Dictionary<string, string> s_messagePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Assign"]                = "Target",
        ["Create"]                = "Id",
        ["CreateMultiple"]        = "Targets",
        ["Delete"]                = "Target",
        ["DeliverIncoming"]       = "EmailId",
        ["DeliverPromote"]        = "EmailId",
        ["Merge"]                 = "Target",
        ["Route"]                 = "Target",
        ["Send"]                  = "EmailId",
        ["SetState"]              = "EntityMoniker",
        ["SetStateDynamicEntity"] = "EntityMoniker",
        ["Update"]                = "Target",
        ["UpdateMultiple"]        = "Targets",
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
                    // UQ1_PluginType constraint is on (friendlyname, solutionId, ...). All unmanaged
                    // plugin types share the same "Active" solutionId, so friendlyname is effectively
                    // org-globally unique. Using FullName (FQN) ensures classes in different namespaces
                    // get distinct values — e.g. "Plugins.Foo" vs "Extensions.Foo". Do NOT change to
                    // asmPluginType.Name: short names collide across assemblies ("Foo" from two different
                    // namespaced assemblies share the same Name but have different FullNames).
                    ["friendlyname"]    = asmPluginType.FullName,
                    ["pluginassemblyid"] = assembly.ToEntityReference(),
                    ["description"]     = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                };

                if (asmPluginType.IsWorkflow)
                    dvPluginType["workflowactivitygroupname"] = $"{metadata.Name} ({metadata.Version})";

                plan.PluginTypes.Upserts.Add(new UpsertAction(asmPluginType.Name, dvPluginType, IsCreate: true));
            }

            if (asmPluginType.IsWorkflow) continue;

            if (asmPluginType.IsCustomApi)
            {
                var (customApiPlan, requestParamPlan, responsePropPlan, groups) = PlanCustomApi(snapshot, dvPluginType, asmPluginType.CustomApis, solutionName, asmPluginType.Name);
                plan.CustomApis.Add(customApiPlan);
                plan.RequestParams.Add(requestParamPlan);
                plan.ResponseProps.Add(responsePropPlan);
                plan.CustomApiGroups.AddRange(groups);
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
                plan.PluginTypes.Deletes.Add(new DeleteAction(obsoletePluginType.Key, "plugintype", obsoletePluginType.Value.Id));
                continue;
            }

            // Try both paths — only the one with registered items will produce actions
            var typeShortName = obsoletePluginType.Key[(obsoletePluginType.Key.LastIndexOf('.') + 1)..];
            var (customApiPlan, requestParamPlan, responsePropPlan, apiGroups) = PlanCustomApi(snapshot, obsoletePluginType.Value, [], solutionName, typeShortName);
            plan.CustomApis.Add(customApiPlan);
            plan.RequestParams.Add(requestParamPlan);
            plan.ResponseProps.Add(responsePropPlan);
            plan.CustomApiGroups.AddRange(apiGroups);

            var obsoleteMetadata = new PluginTypeMetadata(
                obsoletePluginType.Value.GetAttributeValue<string>("name") ?? obsoletePluginType.Key,
                obsoletePluginType.Key,
                [],
                [],
                false);
            var (stepPlan, imagePlan) = PlanPluginSteps(snapshot, obsoletePluginType.Value, obsoleteMetadata, solutionName);
            plan.Steps.Add(stepPlan);
            plan.Images.Add(imagePlan);

            plan.PluginTypes.Deletes.Add(new DeleteAction(obsoletePluginType.Key, "plugintype", obsoletePluginType.Value.Id));
        }

        // Unlinked Custom APIs — plugintypeid is null/empty or references a plugin type not in the snapshot.
        // No PlanCustomApi call ever covers these, so they'd persist forever without this sweep.
        var knownPluginTypeIds = snapshot.PluginTypes.Values.Select(t => t.Id).ToHashSet();
        foreach (var unlinkedApi in snapshot.CustomApis.Where(a =>
        {
            var typeId = a.GetAttributeValue<EntityReference>("plugintypeid")?.Id;
            return typeId == null || typeId == Guid.Empty || !knownPluginTypeIds.Contains(typeId.Value);
        }))
        {
            var apiName = unlinkedApi.GetAttributeValue<string>("uniquename") ?? unlinkedApi.Id.ToString();
            var del    = new DeleteAction(apiName, "customapi", unlinkedApi.Id);
            var pParam = PlanRequestParameters(snapshot, snapshot.PublisherPrefix, unlinkedApi.Id, apiName, [], solutionName);
            var pProp  = PlanResponseProperties(snapshot, snapshot.PublisherPrefix, unlinkedApi.Id, apiName, [], solutionName);
            plan.CustomApis.Deletes.Add(del);
            plan.RequestParams.Add(pParam);
            plan.ResponseProps.Add(pProp);
            var sApi = new ActionPlan(); sApi.Deletes.Add(del);
            plan.CustomApiGroups.Add(new CustomApiGroup(apiName, sApi, pParam, pProp));
        }

        AddCrossSolutionWarnings(plan, snapshot, solutionName);

        return plan;
    }

    static void AddCrossSolutionWarnings(RegistrationPlan plan, RegistrationSnapshot snapshot, string solutionName)
    {
        var updates = plan.Steps.Upserts
            .Concat(plan.Images.Upserts)
            .Concat(plan.CustomApis.Upserts)
            .Concat(plan.RequestParams.Upserts)
            .Concat(plan.ResponseProps.Upserts)
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

    static bool IsInSolution(RegistrationSnapshot snapshot, Guid componentId, string solutionName) =>
        snapshot.ComponentSolutionMembership.TryGetValue(componentId, out var solutions) &&
        solutions.Any(s => string.Equals(s, solutionName, StringComparison.OrdinalIgnoreCase));

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

        var secondaryMatchedIds = new HashSet<Guid>();
        var asmStepNames = new HashSet<string>(asmPluginSteps.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var asmStep in asmPluginSteps)
        {
            if (!snapshot.SdkMessageIds.TryGetValue(asmStep.Message, out var messageId))
                throw new InvalidOperationException(
                    $"Step '{asmStep.Name}' references message '{asmStep.Message}' which does not exist in this environment. " +
                    $"Check the message name on [Step] for '{asmPluginType.FullName}'.");

            snapshot.FilterIds.TryGetValue((messageId, asmStep.TableName, asmStep.SecondaryTable), out var filterId);

            var tableRequested = !string.IsNullOrEmpty(asmStep.TableName) &&
                                 !string.Equals(asmStep.TableName, "none", StringComparison.OrdinalIgnoreCase);
            if (tableRequested && !filterId.HasValue)
                throw new InvalidOperationException(
                    $"Step '{asmStep.Name}' references table '{asmStep.TableName}' which is not supported for message '{asmStep.Message}' in this environment. " +
                    $"Check the table name on [Step] for '{asmPluginType.FullName}'.");

            if (asmStep.RunAs.HasValue && !snapshot.SystemUserIds.Contains(asmStep.RunAs.Value))
                throw new InvalidOperationException(
                    $"Step '{asmStep.Name}' references RunAs system user '{asmStep.RunAs.Value}' which does not exist in this environment. " +
                    $"Check RunAs on [Step] for '{asmPluginType.FullName}'.");

            if (dvSteps.TryGetValue(asmStep.Name, out var dvStep))
            {
                if (!IsInSolution(snapshot, dvStep.Id, solutionName))
                {
                    stepPlan.AddSolutionComponents.Add(
                        new AddToSolutionAction(asmStep.Name, "sdkmessageprocessingstep", dvStep.Id, solutionName,
                            snapshot.ComponentTypeById.GetValueOrDefault(dvStep.Id, 92)));
                }

                var changed =
                    dvStep.GetAttributeValue<string>("configuration") != asmStep.Configuration ||
                    dvStep.GetAttributeValue<string>("filteringattributes") != asmStep.FilteringColumns ||
                    dvStep.GetAttributeValue<OptionSetValue>("stage")?.Value != asmStep.Stage ||
                    dvStep.GetAttributeValue<OptionSetValue>("mode")?.Value != asmStep.Mode ||
                    dvStep.GetAttributeValue<int?>("rank") != asmStep.Order ||
                    dvStep.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id != messageId ||
                    dvStep.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id != filterId ||
                    dvStep.GetAttributeValue<bool>("asyncautodelete") != asmStep.AsyncAutoDelete ||
                    dvStep.GetAttributeValue<EntityReference?>("impersonatinguserid")?.Id != asmStep.RunAs;

                if (!changed)
                {
                    imagesPlan.Add(PlanImages(snapshot, dvStep, asmStep.Images, asmStep.Message, asmStep.Name));
                    continue;
                }

                dvStep["stage"]                = new OptionSetValue(asmStep.Stage);
                dvStep["mode"]                 = new OptionSetValue(asmStep.Mode);
                dvStep["rank"]                 = asmStep.Order;
                dvStep["filteringattributes"]  = asmStep.FilteringColumns;
                dvStep["configuration"]        = asmStep.Configuration;
                dvStep["asyncautodelete"]      = asmStep.AsyncAutoDelete;
                dvStep["impersonatinguserid"]  = asmStep.RunAs.HasValue ? new EntityReference("systemuser", asmStep.RunAs.Value) : null;
                dvStep["sdkmessageid"]         = new EntityReference("sdkmessage", messageId);
                dvStep["sdkmessagefilterid"]   = filterId.HasValue ? new EntityReference("sdkmessagefilter", filterId.Value) : null;

                stepPlan.Upserts.Add(new UpsertAction(asmStep.Name, dvStep, IsCreate: false));
            }
            else
            {
                // Secondary match: (messageId + filterId + stage) within the same plugin type.
                // Catches steps renamed by multi-[Handles] stage-qualification and prevents delete + create.
                var secondaryMatch = dvSteps.Values.FirstOrDefault(s =>
                    s.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id == messageId &&
                    s.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id == filterId &&
                    s.GetAttributeValue<OptionSetValue>("stage")?.Value == asmStep.Stage &&
                    s.GetAttributeValue<OptionSetValue>("mode")?.Value == asmStep.Mode &&
                    !asmStepNames.Contains(s.GetAttributeValue<string>("name")) &&
                    !secondaryMatchedIds.Contains(s.Id));

                if (secondaryMatch != null)
                {
                    dvStep = secondaryMatch;
                    secondaryMatchedIds.Add(dvStep.Id);

                    if (!IsInSolution(snapshot, dvStep.Id, solutionName))
                    {
                        stepPlan.AddSolutionComponents.Add(
                            new AddToSolutionAction(asmStep.Name, "sdkmessageprocessingstep", dvStep.Id, solutionName,
                                snapshot.ComponentTypeById.GetValueOrDefault(dvStep.Id, 92)));
                    }

                    dvStep["name"]                = asmStep.Name;
                    dvStep["stage"]               = new OptionSetValue(asmStep.Stage);
                    dvStep["mode"]                = new OptionSetValue(asmStep.Mode);
                    dvStep["rank"]                = asmStep.Order;
                    dvStep["filteringattributes"] = asmStep.FilteringColumns;
                    dvStep["configuration"]       = asmStep.Configuration;
                    dvStep["asyncautodelete"]     = asmStep.AsyncAutoDelete;
                    dvStep["impersonatinguserid"] = asmStep.RunAs.HasValue ? new EntityReference("systemuser", asmStep.RunAs.Value) : null;
                    dvStep["sdkmessageid"]        = new EntityReference("sdkmessage", messageId);
                    dvStep["sdkmessagefilterid"]  = filterId.HasValue ? new EntityReference("sdkmessagefilter", filterId.Value) : null;

                    stepPlan.Upserts.Add(new UpsertAction(asmStep.Name, dvStep, IsCreate: false));
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
                        ["filteringattributes"] = asmStep.FilteringColumns,
                        ["configuration"]      = asmStep.Configuration,
                        ["asyncautodelete"]    = asmStep.AsyncAutoDelete,
                        ["impersonatinguserid"] = asmStep.RunAs.HasValue ? new EntityReference("systemuser", asmStep.RunAs.Value) : null,
                        ["description"]        = $"{FlowlineMarker} Created at {DateTime.UtcNow:u}"
                    };
                    if (filterId.HasValue)
                        entity["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);

                    stepPlan.Upserts.Add(new UpsertAction(asmStep.Name, entity, IsCreate: true, SolutionName: solutionName));
                    dvStep = entity;
                }
            }

            imagesPlan.Add(PlanImages(snapshot, dvStep, asmStep.Images, asmStep.Message, asmStep.Name));
        }

        foreach (var obsoleteStep in dvSteps.Where(s =>
            asmPluginSteps.All(p => p.Name != s.Key) &&
            !secondaryMatchedIds.Contains(s.Value.Id)))
        {
            var stepName = obsoleteStep.Value.GetAttributeValue<string>("name");
            stepPlan.Deletes.Add(new DeleteAction(stepName, "sdkmessageprocessingstep", obsoleteStep.Value.Id));

            foreach (var obsoleteImage in snapshot.Images.Where(i => (i.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty) == obsoleteStep.Value.Id))
            {
                var imageName = obsoleteImage.GetAttributeValue<string>("name");
                imagesPlan.Deletes.Add(new DeleteAction($"{imageName}' on '{stepName}", "sdkmessageprocessingstepimage", obsoleteImage.Id));
            }
        }

        return (stepPlan, imagesPlan);
    }

    ActionPlan PlanImages(RegistrationSnapshot snapshot, Entity stepEntity, List<PluginImageMetadata> asmImages, string message, string stepName)
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

                plan.Upserts.Add(new UpsertAction($"{asmImage.Name}' on '{stepName}", dvImage, IsCreate: false));
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

                plan.Upserts.Add(new UpsertAction($"{asmImage.Name}' on '{stepName}", entity, IsCreate: true));
            }
        }

        foreach (var obsoleteImage in dvImages.Where(i => asmImages.All(a => a.Name != i.Key)))
            plan.Deletes.Add(new DeleteAction($"{obsoleteImage.Key}' on '{stepName}", "sdkmessageprocessingstepimage", obsoleteImage.Value.Id));

        return plan;
    }

    (ActionPlan customApiPlan, ActionPlan requestParamPlan, ActionPlan responsePropPlan, List<CustomApiGroup> groups) PlanCustomApi(
        RegistrationSnapshot snapshot, Entity typeEntity, List<CustomApiMetadata> asmCustomApis, string solutionName, string? pluginTypeName = null)
    {
        ActionPlan apiPlan = new();
        ActionPlan paramPlan = new();
        ActionPlan propPlan = new();
        List<CustomApiGroup> groups = new();

        var prefix = snapshot.PublisherPrefix;
        var dvApis = snapshot.CustomApis
            .Where(e => e.GetAttributeValue<EntityReference>("plugintypeid")?.Id == typeEntity.Id)
            .ToDictionary(e => e.GetAttributeValue<string>("uniquename"), e => e)
            .AsReadOnly();

        foreach (var asmApi in asmCustomApis)
        {
            var fullApiName = $"{prefix}_{asmApi.UniqueName}";

            if (!dvApis.TryGetValue(fullApiName, out var dvApi))
            {
                var newApi = NewCustomApiEntity(fullApiName, asmApi, typeEntity);
                var upsert = new UpsertAction(asmApi.UniqueName, newApi, IsCreate: true, SolutionName: solutionName);
                var pParam = PlanRequestParameters(snapshot, prefix, newApi.Id, asmApi.UniqueName, asmApi.RequestParameters, solutionName);
                var pProp  = PlanResponseProperties(snapshot, prefix, newApi.Id, asmApi.UniqueName, asmApi.ResponseProperties, solutionName);
                apiPlan.Upserts.Add(upsert);
                paramPlan.Add(pParam);
                propPlan.Add(pProp);
                var sApi = new ActionPlan(); sApi.Upserts.Add(upsert);
                groups.Add(new CustomApiGroup(fullApiName, sApi, pParam, pProp, pluginTypeName));
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

                var del = new DeleteAction(asmApi.UniqueName, "customapi", dvApi.Id);
                var pParamDel = PlanRequestParameters(snapshot, prefix, dvApi.Id, fullApiName, [], solutionName);
                var pPropDel  = PlanResponseProperties(snapshot, prefix, dvApi.Id, fullApiName, [], solutionName);
                var newApi = NewCustomApiEntity(fullApiName, asmApi, typeEntity);
                var upsert = new UpsertAction(asmApi.UniqueName, newApi, IsCreate: true, SolutionName: solutionName);
                var pParamNew = PlanRequestParameters(snapshot, prefix, newApi.Id, fullApiName, asmApi.RequestParameters, solutionName);
                var pPropNew  = PlanResponseProperties(snapshot, prefix, newApi.Id, fullApiName, asmApi.ResponseProperties, solutionName);
                apiPlan.Deletes.Add(del);
                apiPlan.Upserts.Add(upsert);
                paramPlan.Add(pParamDel); paramPlan.Add(pParamNew);
                propPlan.Add(pPropDel);   propPlan.Add(pPropNew);
                var sApi   = new ActionPlan(); sApi.Deletes.Add(del); sApi.Upserts.Add(upsert);
                var sParam = new ActionPlan(); sParam.Add(pParamDel); sParam.Add(pParamNew);
                var sProp  = new ActionPlan(); sProp.Add(pPropDel);   sProp.Add(pPropNew);
                groups.Add(new CustomApiGroup(fullApiName, sApi, sParam, sProp, pluginTypeName));
                continue;
            }

            if (!IsInSolution(snapshot, dvApi.Id, solutionName))
            {
                apiPlan.AddSolutionComponents.Add(new AddToSolutionAction(fullApiName, "customapi", dvApi.Id, solutionName,
                    snapshot.ComponentTypeById[dvApi.Id]));
            }

            var pParam2 = PlanRequestParameters(snapshot, prefix, dvApi.Id, fullApiName, asmApi.RequestParameters, solutionName);
            var pProp2  = PlanResponseProperties(snapshot, prefix, dvApi.Id, fullApiName, asmApi.ResponseProperties, solutionName);
            paramPlan.Add(pParam2);
            propPlan.Add(pProp2);

            var mutableChanged =
                dvApi.GetAttributeValue<EntityReference>("plugintypeid")?.Id != typeEntity.Id ||
                dvApi.GetAttributeValue<string>("displayname") != asmApi.DisplayName ||
                dvApi.GetAttributeValue<string>("description") != asmApi.Description ||
                dvApi.GetAttributeValue<bool>("isprivate") != asmApi.IsPrivate ||
                dvApi.GetAttributeValue<string>("executeprivilegename") != asmApi.ExecutePrivilege;

            ActionPlan singleApiPlan = new();
            if (mutableChanged)
            {
                dvApi["plugintypeid"]         = typeEntity.ToEntityReference();
                dvApi["displayname"]          = asmApi.DisplayName;
                dvApi["description"]          = asmApi.Description;
                dvApi["isprivate"]            = asmApi.IsPrivate;
                dvApi["executeprivilegename"] = asmApi.ExecutePrivilege;
                var upsert = new UpsertAction(asmApi.UniqueName, dvApi, IsCreate: false);
                apiPlan.Upserts.Add(upsert);
                singleApiPlan.Upserts.Add(upsert);
            }

            if (singleApiPlan.Upserts.Count > 0
                || pParam2.Upserts.Count > 0 || pParam2.Deletes.Count > 0
                || pProp2.Upserts.Count > 0  || pProp2.Deletes.Count > 0)
            {
                groups.Add(new CustomApiGroup(fullApiName, singleApiPlan, pParam2, pProp2, pluginTypeName));
            }
        }

        // Fix: compare with prefix-qualified name so only truly absent APIs are treated as obsolete
        foreach (var obsoleteApi in dvApis.Where(a => asmCustomApis.All(c => $"{prefix}_{c.UniqueName}" != a.Key)))
        {
            var del    = new DeleteAction(obsoleteApi.Key, "customapi", obsoleteApi.Value.Id);
            var pParam = PlanRequestParameters(snapshot, prefix, obsoleteApi.Value.Id, obsoleteApi.Key, [], solutionName);
            var pProp  = PlanResponseProperties(snapshot, prefix, obsoleteApi.Value.Id, obsoleteApi.Key, [], solutionName);
            apiPlan.Deletes.Add(del);
            paramPlan.Add(pParam);
            propPlan.Add(pProp);
            var sApi = new ActionPlan(); sApi.Deletes.Add(del);
            groups.Add(new CustomApiGroup(obsoleteApi.Key, sApi, pParam, pProp, pluginTypeName));
        }

        return (apiPlan, paramPlan, propPlan, groups);
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
            if (!dvRequestParams.TryGetValue(asmParam.UniqueName, out var dvParam))
            {
                plan.Upserts.Add(new UpsertAction(asmParam.UniqueName,
                    NewRequestParameterEntity(asmParam, customApiId), IsCreate: true, SolutionName: solutionName));
                continue;
            }

            // type, isoptional, logicalentityname: immutable after creation despite IsValidForUpdate=true in
            // entity metadata. Platform ignores updates to these fields. Must delete+recreate on change.
            // Source: https://learn.microsoft.com/power-apps/developer/data-platform/create-custom-api-solution#update-a-custom-api-in-a-solution
            var immutableChanged =
                (dvParam.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != asmParam.Type ||
                dvParam.GetAttributeValue<bool>("isoptional") != asmParam.IsOptional ||
                dvParam.GetAttributeValue<string>("logicalentityname") != asmParam.EntityName;

            if (immutableChanged)
            {
                output.Warning($"Request parameter '{asmParam.DisplayName}' has immutable field changes — deleting and recreating.");
                plan.Deletes.Add(new DeleteAction(asmParam.UniqueName, "customapirequestparameter", dvParam.Id));
                plan.Upserts.Add(new UpsertAction(asmParam.UniqueName,
                    NewRequestParameterEntity(asmParam, customApiId), IsCreate: true, SolutionName: solutionName));
                continue;
            }

            if (!IsInSolution(snapshot, dvParam.Id, solutionName))
            {
                plan.AddSolutionComponents.Add(
                    new AddToSolutionAction(asmParam.UniqueName, "customapirequestparameter", dvParam.Id, solutionName,
                        snapshot.ComponentTypeById[dvParam.Id]));
            }

            var mutableChanged =
                dvParam.GetAttributeValue<string>("name") != asmParam.Name ||
                dvParam.GetAttributeValue<string>("displayname") != asmParam.DisplayName ||
                dvParam.GetAttributeValue<string>("description") != asmParam.Description;

            if (!mutableChanged) continue;

            dvParam["name"]        = asmParam.Name;
            dvParam["displayname"] = asmParam.DisplayName;
            dvParam["description"] = asmParam.Description;
            plan.Upserts.Add(new UpsertAction(asmParam.UniqueName, dvParam, IsCreate: false));
        }

        foreach (var obsoleteParam in dvRequestParams.Where(r => asmRequestParams.All(p => p.UniqueName != r.Key)))
        {
            var name = obsoleteParam.Value.GetAttributeValue<string>("uniquename");
            plan.Deletes.Add(new DeleteAction(name, "customapirequestparameter", obsoleteParam.Value.Id));
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
            if (!dvResponseProps.TryGetValue(asmProp.UniqueName, out var dvProp))
            {
                plan.Upserts.Add(new UpsertAction(asmProp.UniqueName,
                    NewResponsePropertyEntity(asmProp, customApiId), IsCreate: true, SolutionName: solutionName));
                continue;
            }

            // type, logicalentityname: immutable after creation despite IsValidForUpdate=true in entity metadata.
            // Platform ignores updates to these fields. Must delete+recreate on change.
            // Source: https://learn.microsoft.com/power-apps/developer/data-platform/create-custom-api-solution#update-a-custom-api-in-a-solution
            var immutableChanged =
                (dvProp.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != asmProp.Type ||
                dvProp.GetAttributeValue<string>("logicalentityname") != asmProp.EntityName;

            if (immutableChanged)
            {
                output.Warning($"Response Property '{asmProp.DisplayName}' has immutable field changes — deleting and recreating.");
                plan.Deletes.Add(new DeleteAction(asmProp.UniqueName, "customapiresponseproperty", dvProp.Id));
                plan.Upserts.Add(new UpsertAction(asmProp.UniqueName,
                    NewResponsePropertyEntity(asmProp, customApiId), IsCreate: true, SolutionName: solutionName));
                continue;
            }

            if (!IsInSolution(snapshot, dvProp.Id, solutionName))
            {
                plan.AddSolutionComponents.Add(
                    new AddToSolutionAction(asmProp.UniqueName, "customapiresponseproperty", dvProp.Id, solutionName,
                        snapshot.ComponentTypeById[dvProp.Id]));
            }

            var mutableChanged =
                dvProp.GetAttributeValue<string>("name") != asmProp.Name ||
                dvProp.GetAttributeValue<string>("displayname") != asmProp.DisplayName ||
                dvProp.GetAttributeValue<string>("description") != asmProp.Description;

            if (!mutableChanged) continue;

            dvProp["name"]        = asmProp.Name;
            dvProp["displayname"] = asmProp.DisplayName;
            dvProp["description"] = asmProp.Description;
            plan.Upserts.Add(new UpsertAction(asmProp.UniqueName, dvProp, IsCreate: false));
        }

        foreach (var obsoleteProp in dvResponseProps.Where(r => asmResponseProps.All(p => p.UniqueName != r.Key)))
        {
            var name = obsoleteProp.Value.GetAttributeValue<string>("uniquename");
            plan.Deletes.Add(new DeleteAction(name, "customapiresponseproperty", obsoleteProp.Value.Id));
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
