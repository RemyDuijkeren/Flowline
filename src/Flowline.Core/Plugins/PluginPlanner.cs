using Microsoft.Xrm.Sdk;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.Plugins;

public class PluginPlanner(IAnsiConsole console)
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

    // packageAssemblyPluginTypeIds: for a multi-assembly package (U4/KD5), snapshot.CustomApis is
    // queried by publisher prefix only (not per-assembly, since a custom API's plugintypeid doesn't
    // narrow to one physical DLL the way steps/images do) — so a sibling assembly's still-live Custom
    // API shows up in every assembly's snapshot. Package callers pass the union of every assembly in
    // that package (AllPluginTypeIds). Callers planning a single classic assembly pass null.
    //
    // It is a DISCRIMINATOR, NOT AN AUTHORISATION — it decides which "left alone" reason gets
    // reported, never whether something may be deleted. Deletion is authorised only by
    // snapshot.PluginTypes: the assembly this pass is planning. Widening the delete-authorising set to
    // the whole package re-enters the bug this design exists to remove, from the other side —
    // assembly A would see assembly B's still-live API as owned-by-the-package but absent from A's
    // source, and delete it. Each pass deletes its own; no pass ever deletes on another's behalf.
    //
    // forceDeleteOrphans: gates the one sweep case Flowline cannot attribute — a Custom API with no
    // plugintypeid at all. Same gate WarnOrphanAssembliesAsync/WarnOrphanStepsAsync sit behind.
    public RegistrationPlan Plan(RegistrationSnapshot snapshot, PluginAssemblyMetadata metadata, Entity assembly, string solutionName, IReadOnlySet<Guid>? packageAssemblyPluginTypeIds = null, bool forceDeleteOrphans = false)
    {
        var plan = new RegistrationPlan();
        var resolvedCustomApiNames = ResolveCustomApiNames(snapshot, metadata);

        // Plugin types PlanCustomApi is called for below — those already get the full upsert/obsolete
        // treatment for every Custom API pointing at them, so the sweep at the end must leave them
        // alone or it would plan a second, duplicate delete for the same record.
        var plannedCustomApiTypeIds = new HashSet<Guid>();

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

                plan.PluginTypes.Upserts.Add(new UpsertAction(asmPluginType.Name, dvPluginType, IsCreate: true, SolutionName: solutionName));
            }

            if (asmPluginType.IsWorkflow) continue;

            if (asmPluginType.IsCustomApi)
            {
                plannedCustomApiTypeIds.Add(dvPluginType.Id);
                var (customApiPlan, requestParamPlan, responsePropPlan, groups) = PlanCustomApi(snapshot, dvPluginType, asmPluginType.CustomApis, solutionName, resolvedCustomApiNames, asmPluginType.Name);
                plan.CustomApis.Add(customApiPlan);
                plan.RequestParams.Add(requestParamPlan);
                plan.ResponseProps.Add(responsePropPlan);
                plan.CustomApiGroups.AddRange(groups);
            }
            else
            {
                var (stepPlan, imagePlan, _) = PlanPluginSteps(snapshot, dvPluginType, asmPluginType, solutionName, plan.Warnings);
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
            plannedCustomApiTypeIds.Add(obsoletePluginType.Value.Id);
            var (customApiPlan, requestParamPlan, responsePropPlan, apiGroups) = PlanCustomApi(snapshot, obsoletePluginType.Value, [], solutionName, resolvedCustomApiNames, typeShortName);
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
            var (stepPlan, imagePlan, protectedStepCount) = PlanPluginSteps(snapshot, obsoletePluginType.Value, obsoleteMetadata, solutionName, plan.Warnings);
            plan.Steps.Add(stepPlan);
            plan.Images.Add(imagePlan);

            // A step with a linked Secure Configuration is left in place by PlanPluginSteps' obsolete-step
            // guard (R3) rather than deleted, so the type it belongs to can no longer be deleted either —
            // Dataverse rejects deleting a plugin type a step still references.
            if (protectedStepCount > 0)
            {
                plan.Warnings.Add(
                    $"Skipping deletion of plugin type '{obsoletePluginType.Key}' — one or more of its steps has a linked Secure Configuration and was left in place.");
            }
            else
            {
                plan.PluginTypes.Deletes.Add(new DeleteAction(obsoletePluginType.Key, "plugintype", obsoletePluginType.Value.Id));
            }
        }

        // -- Custom API sweep: delete only on positive attribution --
        //
        // READ THIS BEFORE WIDENING ANY ID SET HERE. This sweep has twice been "fixed" by adding more
        // plugin type ids to a known-set; both times the actual defect was the direction of ownership.
        //
        // The schema, which is what makes this checkable:
        //   sdkmessageprocessingstep.plugintypeid -> plugintype  — the step is OWNED BY the type. The
        //     reference points UP at its owner; deleting the type cascades the step away. The type is
        //     assembly-scoped, so "ours" is well defined and "type gone" really does mean "step dead".
        //   customapi.plugintypeid -> plugintype  — the API REFERENCES the type as its implementation.
        //     The reference points DOWN. The Custom API is the parent, the plugin type is a detail, and
        //     a Custom API can exist with no plugintypeid at all.
        //
        // So "its plugin type is gone" does NOT mean "the API is orphaned" — losing an implementation
        // does not orphan a contract. And a Custom API has no assembly-shaped handle, so
        // PluginReader.GetRegisteredCustomApisAsync can only filter `uniquename BeginsWith "<prefix>_"`:
        // this sweep's input is every Custom API this publisher ever created, other projects and other
        // repos included. A Custom API is also a public API surface with external callers, so the bar
        // for deleting one is HIGHER than for a step, not lower.
        //
        // Therefore: delete only what this push can positively attribute to itself. Anything else is
        // left alone (reported under --verbose), and "no implementation at all" — which may be a
        // contract still waiting for one — goes behind --force delete-orphans, the same gate
        // WarnOrphanAssembliesAsync/WarnOrphanStepsAsync use for every other unknown.
        var ourPluginTypeIds = snapshot.PluginTypes.Values.Select(t => t.Id).ToHashSet();
        var pushPluginTypeIds = new HashSet<Guid>(ourPluginTypeIds);
        if (packageAssemblyPluginTypeIds != null)
            pushPluginTypeIds.UnionWith(packageAssemblyPluginTypeIds);
        var declaredCustomApiNames = resolvedCustomApiNames.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var ourApiNoLongerInSource in snapshot.CustomApis.Where(IsOursAndNoLongerDeclaredInSource))
        {
            var apiName = ourApiNoLongerInSource.GetAttributeValue<string>("uniquename") ?? ourApiNoLongerInSource.Id.ToString();
            var del    = new DeleteAction(apiName, "customapi", ourApiNoLongerInSource.Id);
            var pParam = PlanRequestParameters(snapshot, snapshot.PublisherPrefix, ourApiNoLongerInSource.Id, apiName, [], solutionName);
            var pProp  = PlanResponseProperties(snapshot, snapshot.PublisherPrefix, ourApiNoLongerInSource.Id, apiName, [], solutionName);
            plan.CustomApis.Deletes.Add(del);
            plan.RequestParams.Add(pParam);
            plan.ResponseProps.Add(pProp);
            var sApi = new ActionPlan(); sApi.Deletes.Add(del);
            plan.CustomApiGroups.Add(new CustomApiGroup(apiName, sApi, pParam, pProp));
        }

        AddCrossSolutionWarnings(plan, snapshot, solutionName);

        return plan;

        // The whole rule, in one place. Named for the test it performs — "ours, and source no longer
        // declares it" — not for a null check on plugintypeid, because a missing plugintypeid is the
        // weakest signal here, not the strongest.
        bool IsOursAndNoLongerDeclaredInSource(Entity api)
        {
            var apiName = api.GetAttributeValue<string>("uniquename") ?? api.Id.ToString();
            var typeId  = api.GetAttributeValue<EntityReference>("plugintypeid")?.Id;

            if (typeId is not { } implementationTypeId || implementationTypeId == Guid.Empty)
            {
                // No implementation at all. Might be a contract waiting for one, so it never goes on a
                // normal push — same --force gate as every other thing we cannot attribute.
                if (forceDeleteOrphans) return true;

                console.Verbose($"Custom API '{apiName}' left alone — nothing implements it, so Flowline can't tell whose it is. Use --force delete-orphans to delete.");
                return false;
            }

            // Implemented by a plugin type outside this push. The prefix-wide query means this is
            // routine, not suspicious: it is another project's or another repo's API.
            if (!pushPluginTypeIds.Contains(implementationTypeId))
            {
                console.Verbose($"Custom API '{apiName}' left alone — its plugin type isn't one this push owns, so it belongs to another project.");
                return false;
            }

            // Owned by another assembly in this same package. That assembly's own pass is the only one
            // that can tell "still declared in source" from "removed", so this pass stays out of it.
            if (!ourPluginTypeIds.Contains(implementationTypeId)) return false;

            // Ours, and PlanCustomApi above already planned it (upsert, or an obsolete-delete).
            if (plannedCustomApiTypeIds.Contains(implementationTypeId)) return false;

            // Ours, still declared in source under this name — the class simply isn't planned as a
            // Custom API this pass (e.g. it kept the plugin type but the name moved).
            return !declaredCustomApiNames.Contains(apiName);
        }
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
            WarnIfInOtherSolutions(plan, snapshot, solutionName, "Updating", action.Entity.LogicalName, action.Name, action.Entity.Id);

        // A step whose identity-key field changed is now deleted, not updated (Key Decisions) — so a
        // shared-solution warning must also fire on deletes, not just updates, or a cross-solution
        // registration can be silently removed with no warning.
        var deletes = plan.Steps.Deletes
            .Concat(plan.Images.Deletes)
            .Concat(plan.CustomApis.Deletes)
            .Concat(plan.RequestParams.Deletes)
            .Concat(plan.ResponseProps.Deletes)
            .Concat(plan.PluginTypes.Deletes);

        foreach (var action in deletes)
            WarnIfInOtherSolutions(plan, snapshot, solutionName, "Deleting", action.EntityLogicalName, action.Name, action.Id);
    }

    static void WarnIfInOtherSolutions(RegistrationPlan plan, RegistrationSnapshot snapshot, string solutionName,
        string verb, string logicalName, string name, Guid componentId)
    {
        if (!snapshot.ComponentSolutionMembership.TryGetValue(componentId, out var solutions))
            return;

        var others = solutions
            .Where(s => !string.Equals(s, solutionName, StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (others.Count > 0)
            plan.Warnings.Add($"{verb} {logicalName} '{name}' which also exists in other solutions: {string.Join(", ", others)}.");
    }

    static bool IsInSolution(RegistrationSnapshot snapshot, Guid componentId, string solutionName) =>
        snapshot.ComponentSolutionMembership.TryGetValue(componentId, out var solutions) &&
        solutions.Any(s => string.Equals(s, solutionName, StringComparison.OrdinalIgnoreCase));

    (ActionPlan stepPlan, ActionPlan imagePlan, int protectedStepCount) PlanPluginSteps(
        RegistrationSnapshot snapshot, Entity typeEntity, PluginTypeMetadata asmPluginType, string solutionName, List<string> warnings)
    {
        ActionPlan stepPlan = new();
        ActionPlan imagesPlan = new();
        var asmPluginSteps = asmPluginType.Steps;

        var dvStepsForType = snapshot.Steps
            .Where(s => (s.GetAttributeValue<EntityReference>("plugintypeid")?.Id ?? Guid.Empty) == typeEntity.Id)
            .ToList();

        // Identity key: (sdkmessageid, sdkmessagefilterid, stage, mode) — the sole lookup path (R1).
        // Grouped, not a dictionary: two pre-Flowline rows can legitimately share a key (R5 collision).
        var dvStepsByKey = dvStepsForType.ToLookup(s => (
            s.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id,
            s.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id,
            s.GetAttributeValue<OptionSetValue>("stage")?.Value,
            s.GetAttributeValue<OptionSetValue>("mode")?.Value));

        var matchedIds = new HashSet<Guid>();

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

            var matches = dvStepsByKey[(messageId, filterId, asmStep.Stage, asmStep.Mode)].ToList();

            Entity? dvStep;
            if (matches.Count == 1)
            {
                dvStep = matches[0];
                matchedIds.Add(dvStep.Id);
            }
            else if (matches.Count > 1)
            {
                // Collision: more than one existing row shares this identity key (only possible for
                // pre-Flowline history — build-time validation rules this out for Flowline-authored steps).
                if (matches.Any(HasLinkedSecureConfiguration))
                    throw new InvalidOperationException(StepCollisionMessage(asmStep, asmPluginType, matches,
                        "one or more of them has a linked Secure Configuration, so automatic resolution is not attempted"));

                var nameMatches = matches
                    .Where(m => string.Equals(m.GetAttributeValue<string>("name"), asmStep.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (nameMatches.Count != 1)
                    throw new InvalidOperationException(StepCollisionMessage(asmStep, asmPluginType, matches,
                        "no single row's name matches what the current code would generate"));

                dvStep = nameMatches[0];
                matchedIds.Add(dvStep.Id);
                // The other colliding row is deliberately left out of matchedIds — it flows into the
                // obsolete-step sweep below, which applies the same Secure Configuration guard (R3).
            }
            else
            {
                dvStep = null;
            }

            if (dvStep != null)
            {
                if (!IsInSolution(snapshot, dvStep.Id, solutionName))
                {
                    stepPlan.AddSolutionComponents.Add(
                        new AddToSolutionAction(asmStep.Name, "sdkmessageprocessingstep", dvStep.Id, solutionName,
                            snapshot.ComponentTypeById.GetValueOrDefault(dvStep.Id, 92)));
                }

                // stage, mode, sdkmessageid, and sdkmessagefilterid are the identity key (R1) — already
                // equal on a matched row by construction, so they are not compared or written back here.
                var changed =
                    dvStep.GetAttributeValue<string>("name") != asmStep.Name ||
                    dvStep.GetAttributeValue<string>("configuration") != asmStep.Configuration ||
                    dvStep.GetAttributeValue<string>("filteringattributes") != asmStep.FilteringColumns ||
                    dvStep.GetAttributeValue<int?>("rank") != asmStep.Order ||
                    dvStep.GetAttributeValue<bool>("asyncautodelete") != asmStep.AsyncAutoDelete ||
                    dvStep.GetAttributeValue<EntityReference?>("impersonatinguserid")?.Id != asmStep.RunAs ||
                    dvStep.GetAttributeValue<string>("description") != asmStep.Description;

                if (changed)
                {
                    dvStep["name"]                 = asmStep.Name;
                    dvStep["rank"]                 = asmStep.Order;
                    dvStep["filteringattributes"]  = asmStep.FilteringColumns;
                    dvStep["configuration"]        = asmStep.Configuration;
                    dvStep["description"]          = asmStep.Description;
                    dvStep["asyncautodelete"]      = asmStep.AsyncAutoDelete;
                    dvStep["impersonatinguserid"]  = asmStep.RunAs.HasValue ? new EntityReference("systemuser", asmStep.RunAs.Value) : null;

                    stepPlan.Upserts.Add(new UpsertAction(asmStep.Name, dvStep, IsCreate: false));
                }
            }
            else
            {
                dvStep = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
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
                    ["description"]        = asmStep.Description
                };
                if (filterId.HasValue)
                    dvStep["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);

                stepPlan.Upserts.Add(new UpsertAction(asmStep.Name, dvStep, IsCreate: true, SolutionName: solutionName));
            }

            imagesPlan.Add(PlanImages(snapshot, dvStep, asmStep.Images, asmStep.Message, asmStep.Name));
        }

        // Obsolete-step sweep: every snapshot row for this type not consumed as a match above (R3).
        var protectedStepCount = 0;
        foreach (var obsoleteStep in dvStepsForType.Where(s => !matchedIds.Contains(s.Id)))
        {
            var stepName = obsoleteStep.GetAttributeValue<string>("name");

            if (HasLinkedSecureConfiguration(obsoleteStep))
            {
                // If this same push also created a new step for this plugin type, both this protected
                // row and the new one are now active in Dataverse — e.g. code changed the step's stage,
                // which recreates it (Key Decisions), but the old row can't be deleted while it still
                // carries a Secure Configuration. Flag that explicitly rather than implying a harmless pause.
                var replacementCreated = stepPlan.Upserts.Any(u => u.IsCreate);
                var suffix = replacementCreated
                    ? " A new step was also created for this plugin type in this push, so both registrations are now active — verify this is intended."
                    : "";
                warnings.Add(
                    $"Skipping deletion of step '{stepName}' — has a linked Secure Configuration; remove manually via the Plugin Registration Tool if intended.{suffix}");
                protectedStepCount++;
                continue;
            }

            stepPlan.Deletes.Add(new DeleteAction(stepName, "sdkmessageprocessingstep", obsoleteStep.Id));

            foreach (var obsoleteImage in snapshot.Images.Where(i => (i.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty) == obsoleteStep.Id))
            {
                var imageName = obsoleteImage.GetAttributeValue<string>("name");
                imagesPlan.Deletes.Add(new DeleteAction($"{imageName}' on '{stepName}", "sdkmessageprocessingstepimage", obsoleteImage.Id));
            }
        }

        return (stepPlan, imagesPlan, protectedStepCount);
    }

    static bool HasLinkedSecureConfiguration(Entity step) =>
        step.GetAttributeValue<EntityReference?>("sdkmessageprocessingstepsecureconfigid") != null;

    static string StepCollisionMessage(PluginStepMetadata asmStep, PluginTypeMetadata asmPluginType, List<Entity> matches, string reason) =>
        $"Step '{asmStep.Name}' matches multiple existing steps " +
        $"({string.Join(", ", matches.Select(m => m.Id))}) for message '{asmStep.Message}' on '{asmPluginType.FullName}', and {reason}. " +
        "Resolve the duplicate registration manually via the Plugin Registration Tool before pushing again.";

    ActionPlan PlanImages(RegistrationSnapshot snapshot, Entity stepEntity, List<PluginImageMetadata> asmImages, string message, string stepName)
    {
        ActionPlan plan = new();

        var dvImagesForStep = snapshot.Images
            .Where(i => (i.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid")?.Id ?? Guid.Empty) == stepEntity.Id)
            .ToList();

        // Identity key: imagetype within the resolved step (R6). [PreImage]/[PostImage] cap at one
        // instance per class (R7), so the assembly side never presents two images of the same type
        // for one step. On the snapshot side, if organic accretion left 2+ images of the same type,
        // the lowest-id row is the match and the rest are treated as orphans (KTD4).
        var dvImageByType = dvImagesForStep
            .GroupBy(i => i.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? 0)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Id).First());

        var matchedIds = new HashSet<Guid>();

        foreach (var asmImage in asmImages)
        {
            if (dvImageByType.TryGetValue(asmImage.ImageType, out var dvImage))
            {
                matchedIds.Add(dvImage.Id);

                var changed =
                    dvImage.GetAttributeValue<string>("name") != asmImage.Name ||
                    dvImage.GetAttributeValue<string>("entityalias") != asmImage.Alias ||
                    dvImage.GetAttributeValue<string>("attributes") != asmImage.Attributes;

                if (!changed) continue;

                dvImage["name"]        = asmImage.Name;
                dvImage["entityalias"] = asmImage.Alias;
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

        foreach (var obsoleteImage in dvImagesForStep.Where(i => !matchedIds.Contains(i.Id)))
        {
            var imageName = obsoleteImage.GetAttributeValue<string>("name");
            plan.Deletes.Add(new DeleteAction($"{imageName}' on '{stepName}", "sdkmessageprocessingstepimage", obsoleteImage.Id));
        }

        return plan;
    }

    // Resolves every Custom API in the assembly to its final Dataverse uniquename before any planning
    // work runs — validates [CustomApi(UniqueName = "...")] overrides against the live publisher prefix
    // (throws on mismatch), warns on a redundant override, and throws on any two Custom APIs (derived or
    // explicit) resolving to the same final name. Keyed by plugin type FullName, since each plugin type
    // has at most one CustomApiMetadata.
    Dictionary<string, string> ResolveCustomApiNames(RegistrationSnapshot snapshot, PluginAssemblyMetadata metadata)
    {
        var prefix = snapshot.PublisherPrefix;
        var expectedPrefix = $"{prefix}_";
        var resolved = new Dictionary<string, string>();

        foreach (var pluginType in metadata.Plugins)
        {
            foreach (var asmApi in pluginType.CustomApis)
            {
                string fullApiName;
                if (asmApi.UniqueNameOverride != null)
                {
                    if (!asmApi.UniqueNameOverride.StartsWith(expectedPrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"{pluginType.FullName}: [CustomApi] UniqueName '{asmApi.UniqueNameOverride}' does not start with this " +
                            $"solution's publisher prefix '{expectedPrefix}' — Dataverse requires it. Set UniqueName to the complete " +
                            $"name, e.g. \"{expectedPrefix}{asmApi.BaseName}\".");

                    fullApiName = asmApi.UniqueNameOverride;

                    var derivedName = $"{prefix}_{asmApi.BaseName}";
                    if (fullApiName == derivedName)
                        console.Warning(
                            $"{pluginType.FullName}: [[CustomApi]] UniqueName is redundant — it matches what Flowline would have " +
                            $"derived automatically. Remove UniqueName and rely on the class-name convention.");
                }
                else
                {
                    fullApiName = $"{prefix}_{asmApi.BaseName}";
                }

                resolved[pluginType.FullName] = fullApiName;
            }
        }

        var duplicates = resolved
            .GroupBy(kv => kv.Value, kv => kv.Key)
            .Where(g => g.Count() > 1)
            .ToList();

        if (duplicates.Count > 0)
        {
            var details = string.Join("; ", duplicates.Select(g => $"'{g.Key}' used by {string.Join(", ", g)}"));
            throw new InvalidOperationException(
                $"Multiple Custom APIs resolve to the same unique name — Dataverse requires names to be unique: {details}. " +
                $"Rename the class or set a distinct [CustomApi(UniqueName = \"...\")] to disambiguate.");
        }

        return resolved;
    }

    (ActionPlan customApiPlan, ActionPlan requestParamPlan, ActionPlan responsePropPlan, List<CustomApiGroup> groups) PlanCustomApi(
        RegistrationSnapshot snapshot, Entity typeEntity, List<CustomApiMetadata> asmCustomApis, string solutionName,
        Dictionary<string, string> resolvedCustomApiNames, string? pluginTypeName = null)
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
            var fullApiName = resolvedCustomApiNames[asmApi.PluginTypeFullName];

            if (!dvApis.TryGetValue(fullApiName, out var dvApi))
            {
                var newApi = NewCustomApiEntity(fullApiName, asmApi, typeEntity);
                var upsert = new UpsertAction(asmApi.BaseName, newApi, IsCreate: true, SolutionName: solutionName);
                var pParam = PlanRequestParameters(snapshot, prefix, newApi.Id, asmApi.BaseName, asmApi.RequestParameters, solutionName);
                var pProp  = PlanResponseProperties(snapshot, prefix, newApi.Id, asmApi.BaseName, asmApi.ResponseProperties, solutionName);
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
                console.Warning($"Custom API '{fullApiName}' has immutable field changes — deleting and recreating.");

                var del = new DeleteAction(asmApi.BaseName, "customapi", dvApi.Id);
                var pParamDel = PlanRequestParameters(snapshot, prefix, dvApi.Id, fullApiName, [], solutionName);
                var pPropDel  = PlanResponseProperties(snapshot, prefix, dvApi.Id, fullApiName, [], solutionName);
                var newApi = NewCustomApiEntity(fullApiName, asmApi, typeEntity);
                var upsert = new UpsertAction(asmApi.BaseName, newApi, IsCreate: true, SolutionName: solutionName);
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
                var upsert = new UpsertAction(asmApi.BaseName, dvApi, IsCreate: false);
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

        // Compare against each declaration's *resolved* name (derived formula or validated UniqueName
        // override), not a re-derived formula — a class adopting an existing live record via UniqueName
        // has a resolved name that deliberately does NOT match "{prefix}_{BaseName}"; re-deriving here
        // would wrongly treat that live record as absent and delete the exact record this feature exists
        // to adopt.
        foreach (var obsoleteApi in dvApis.Where(a => asmCustomApis.All(c => resolvedCustomApiNames[c.PluginTypeFullName] != a.Key)))
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
        List<RequestParameterMetadata> asmRequestParams, string solutionName) =>
        PlanCustomApiChildComponents(
            snapshot, snapshot.RequestParams, customApiId, "customapirequestparameter",
            asmRequestParams, solutionName, warningLabel: "Request parameter",
            getUniqueName: p => p.UniqueName,
            getName: p => p.Name,
            getDisplayName: p => p.DisplayName,
            getDescription: p => p.Description,
            getType: p => p.Type,
            getEntityName: p => p.EntityName,
            newEntity: p => NewRequestParameterEntity(p, customApiId),
            // isoptional: immutable after creation despite IsValidForUpdate=true in entity metadata (see
            // shared type/logicalentityname immutability comment on PlanCustomApiChildComponents) — this
            // extra term only applies to request parameters, not response properties.
            extraImmutableChanged: (p, dvParam) => dvParam.GetAttributeValue<bool>("isoptional") != p.IsOptional);

    ActionPlan PlanResponseProperties(
        RegistrationSnapshot snapshot, string prefix, Guid customApiId, string customApiName,
        List<ResponsePropertyMetadata> asmResponseProps, string solutionName) =>
        PlanCustomApiChildComponents(
            snapshot, snapshot.ResponseProps, customApiId, "customapiresponseproperty",
            asmResponseProps, solutionName, warningLabel: "Response Property",
            getUniqueName: p => p.UniqueName,
            getName: p => p.Name,
            getDisplayName: p => p.DisplayName,
            getDescription: p => p.Description,
            getType: p => p.Type,
            getEntityName: p => p.EntityName,
            newEntity: p => NewResponsePropertyEntity(p, customApiId));

    // Shared by PlanRequestParameters and PlanResponseProperties — customapirequestparameter and
    // customapiresponseproperty are structurally identical child components (same
    // create/immutable-recreate/add-to-solution/mutable-update/delete-obsolete shape), differing only in
    // table name, an optional extra immutability term (request parameters' isoptional), and how each
    // asmItem's fields and replacement entity are read/built.
    //
    // type, logicalentityname: immutable after creation despite IsValidForUpdate=true in entity metadata.
    // Platform ignores updates to these fields. Must delete+recreate on change.
    // Source: https://learn.microsoft.com/power-apps/developer/data-platform/create-custom-api-solution#update-a-custom-api-in-a-solution
    ActionPlan PlanCustomApiChildComponents<T>(
        RegistrationSnapshot snapshot, IEnumerable<Entity> dvRows, Guid customApiId, string entityLogicalName,
        List<T> asmItems, string solutionName, string warningLabel,
        Func<T, string> getUniqueName, Func<T, string?> getName, Func<T, string?> getDisplayName,
        Func<T, string?> getDescription, Func<T, int> getType, Func<T, string?> getEntityName,
        Func<T, Entity> newEntity, Func<T, Entity, bool>? extraImmutableChanged = null)
    {
        ActionPlan plan = new();

        var dvItems = dvRows
            .Where(r => r.GetAttributeValue<EntityReference>("customapiid")?.Id == customApiId)
            .ToDictionary(r => r.GetAttributeValue<string>("uniquename"), r => r, StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();

        foreach (var asmItem in asmItems)
        {
            var uniqueName = getUniqueName(asmItem);

            if (!dvItems.TryGetValue(uniqueName, out var dvItem))
            {
                plan.Upserts.Add(new UpsertAction(uniqueName, newEntity(asmItem), IsCreate: true, SolutionName: solutionName));
                continue;
            }

            var immutableChanged =
                (dvItem.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0) != getType(asmItem) ||
                dvItem.GetAttributeValue<string>("logicalentityname") != getEntityName(asmItem) ||
                (extraImmutableChanged?.Invoke(asmItem, dvItem) ?? false);

            if (immutableChanged)
            {
                console.Warning($"{warningLabel} '{getDisplayName(asmItem)}' has immutable field changes — deleting and recreating.");
                plan.Deletes.Add(new DeleteAction(uniqueName, entityLogicalName, dvItem.Id));
                plan.Upserts.Add(new UpsertAction(uniqueName, newEntity(asmItem), IsCreate: true, SolutionName: solutionName));
                continue;
            }

            if (!IsInSolution(snapshot, dvItem.Id, solutionName))
            {
                plan.AddSolutionComponents.Add(
                    new AddToSolutionAction(uniqueName, entityLogicalName, dvItem.Id, solutionName,
                        snapshot.ComponentTypeById[dvItem.Id]));
            }

            var mutableChanged =
                dvItem.GetAttributeValue<string>("name") != getName(asmItem) ||
                dvItem.GetAttributeValue<string>("displayname") != getDisplayName(asmItem) ||
                dvItem.GetAttributeValue<string>("description") != getDescription(asmItem);

            if (!mutableChanged) continue;

            dvItem["name"]        = getName(asmItem);
            dvItem["displayname"] = getDisplayName(asmItem);
            dvItem["description"] = getDescription(asmItem);
            plan.Upserts.Add(new UpsertAction(uniqueName, dvItem, IsCreate: false));
        }

        foreach (var obsolete in dvItems.Where(r => asmItems.All(a => getUniqueName(a) != r.Key)))
        {
            var name = obsolete.Value.GetAttributeValue<string>("uniquename");
            plan.Deletes.Add(new DeleteAction(name, entityLogicalName, obsolete.Value.Id));
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
