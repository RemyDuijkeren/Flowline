using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class PluginService(IAnsiConsole output, FlowlineRuntimeOptions opt)
{
    const string FlowlineMarker = "[flowline]";

    readonly PluginReader _reader   = new();
    readonly PluginPlanner      _planner  = new(output, opt.IsVerbose);
    readonly PluginExecutor _executor = new(output, opt.IsVerbose);
    readonly SolutionReader  _solutionReader = new();
    readonly PluginAssemblyReader _assemblyReader = new(output, opt.IsVerbose);

    public async Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        string dllPath,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dllPath))
            throw new ArgumentException("dllPath is required and cannot be empty.", nameof(dllPath));

        var metadata = output.Status().Start("Analyzing plugin assembly...", ctx => _assemblyReader.Analyze(dllPath));
        await SyncSolutionAsync(service, metadata, solutionName, runMode, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SyncSolutionAsync(
        IOrganizationServiceAsync2 service,
        PluginAssemblyMetadata metadata,
        string solutionName,
        RunMode runMode = RunMode.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new ArgumentException("solutionName is required and cannot be empty.", nameof(solutionName));

        // Phase 0: Check if solution exists and is supported
        await output.Status()
                    .StartAsync($"Looking up solution [bold]{solutionName}[/]...",
                        _ => _solutionReader.GetSupportedSolutionInfoAsync(service, solutionName, cancellationToken))
                    .ConfigureAwait(false);
        output.Info("Solution found and supported");

        // Phase 1: Get or register assembly
        var (assembly, needsUpdate) = await GetOrRegisterAssemblyAsync(service, metadata, solutionName, runMode, cancellationToken).ConfigureAwait(false);
        output.Ok($"Assembly registered [bold]{metadata.Name}[/] ({metadata.Version})");

        await WarnOrphanAssembliesAsync(service, metadata.Name, solutionName, cancellationToken).ConfigureAwait(false);

        // Phase 2: Load snapshot (all Dataverse state in parallel)
        var snapshot = await output.Status()
            .StartAsync("Loading plugin registration snapshot...", _ => _reader.LoadSnapshotAsync(service, assembly.Id, metadata, solutionName, cancellationToken))
            .ConfigureAwait(false);
        WriteSnapshotVerbose(snapshot);
        output.Info("Snapshot plugins loaded");

        // Phase 3: Plan registration (pure, synchronous)
        var plan = _planner.Plan(snapshot, metadata, assembly, solutionName);
        WritePlanVerbose(plan);
        output.Info("Registration plan ready");

        // Output cross-solution warnings before execution
        foreach (var warning in plan.Warnings)
            output.Warning(warning);

        if (needsUpdate && snapshot.ComponentSolutionMembership.TryGetValue(assembly.Id, out var assemblyMembership))
        {
            var otherSolutions = assemblyMembership
                .Where(s => !string.Equals(s, solutionName, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(s, "Default", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (otherSolutions.Count > 0)
                output.Warning($"Updating assembly '{metadata.Name}' which also exists in other solutions: {string.Join(", ", otherSolutions)}.");
        }

        // Dry-run: print preview and return without making any changes
        if (runMode == RunMode.DryRun)
        {
            WriteDryRunSummary(metadata, needsUpdate, plan);
            return;
        }

        if (!needsUpdate && plan.TotalChanges == 0)
        {
            output.Ok("Plugins already up to date — skipping");
            return;
        }

        // Phase 4: Execute the deletes first — must precede assembly update and upserts
        if (runMode == RunMode.Save || plan.TotalDeletes == 0)
        {
            await _executor.ExecuteDeletesAsync(service, plan, solutionName, runMode == RunMode.Save, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await output.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Deleting stale plugin components", maxValue: plan.TotalDeletes);
                    await _executor.ExecuteDeletesAsync(service, plan, solutionName, false, cancellationToken, task).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        if (plan.TotalDeletes > 0) output.Ok($"{plan.TotalDeletes} stale component(s) deleted");

        // Phase 5: Update assembly content — must happen before new plugin types are registered
        if (needsUpdate)
        {
            await output.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Updating plugin assembly", maxValue: 1);
                    assembly["content"]     = Convert.ToBase64String(metadata.Content);
                    assembly["version"]     = metadata.Version;
                    assembly["description"] = $"{FlowlineMarker} sha256={metadata.Hash}";
                    await service.UpdateAsync(assembly, cancellationToken).ConfigureAwait(false);
                    task.Increment(1);
                })
                .ConfigureAwait(false);
            output.Ok($"Updated assembly content for [bold]{metadata.Name}[/]");
        }

        // Phase 6: Execute upserts and add to solution
        if (plan.TotalUpserts > 0)
        {
            await output.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Syncing plugin components", maxValue: plan.TotalUpserts);
                    await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken, task).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        else
        {
            await _executor.ExecuteUpsertsAsync(service, plan, solutionName, cancellationToken).ConfigureAwait(false);
        }
        if (plan.TotalUpserts > 0) output.Info($"[green]{plan.TotalUpserts} component(s) synced[/]");

        var addToSolutionCount = CountAddToSolutionComponents(plan);
        if (addToSolutionCount > 0)
        {
            await output.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Adding plugin components to solution", maxValue: addToSolutionCount);
                    await _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken, task).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        else
        {
            await _executor.ExecuteAddToSolutionAsync(service, plan, cancellationToken).ConfigureAwait(false);
        }
    }

    async Task WarnOrphanAssembliesAsync(
        IOrganizationServiceAsync2 service,
        string managedAssemblyName,
        string solutionName,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("name"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.NotEqual, managedAssemblyName) } }
        };
        var componentLink = query.AddLink("solutioncomponent", "pluginassemblyid", "objectid", JoinOperator.Inner);
        componentLink.LinkCriteria.AddCondition("componenttype", ConditionOperator.Equal, 91); // 91 = PluginAssembly
        var solutionLink = componentLink.AddLink("solution", "solutionid", "solutionid", JoinOperator.Inner);
        solutionLink.LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionName);

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        foreach (var entity in result.Entities)
        {
            var name = entity.GetAttributeValue<string>("name");
            output.Warning($"[bold]{Safe(name)}.dll[/] in environment — no local source. Flowline won't touch it.");
        }
    }

    async Task<(Entity entity, bool needsUpdate)> GetOrRegisterAssemblyAsync(
        IOrganizationServiceAsync2 service, PluginAssemblyMetadata metadata, string solutionName, RunMode runMode, CancellationToken cancellationToken = default)
    {
        var query = new Microsoft.Xrm.Sdk.Query.QueryExpression("pluginassembly")
        {
            TopCount = 1,
            ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("pluginassemblyid", "name", "version", "publickeytoken", "culture", "description"),
            Criteria =
            {
                Conditions = { new Microsoft.Xrm.Sdk.Query.ConditionExpression("name", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, metadata.Name) }
            }
        };

        var result = await service.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
        var existing = result.Entities.FirstOrDefault();

        if (existing == null)
        {
            if (runMode == RunMode.DryRun)
            {
                output.Skip($"Assembly '{metadata.Name}' — would create");
                // Return a dummy entity so that the caller can continue with the dry-run
                return (new Entity("pluginassembly") { Id = Guid.NewGuid() }, false);
            }

            var entity = new Entity("pluginassembly")
            {
                ["name"]          = metadata.Name,
                ["content"]       = Convert.ToBase64String(metadata.Content),
                ["version"]       = metadata.Version,
                ["isolationmode"] = new OptionSetValue(2), // 2 = Sandbox (cloud only)
                ["description"]   = $"{FlowlineMarker} sha256={metadata.Hash}"
            };

            var response = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest { Target = entity, ["SolutionUniqueName"] = solutionName }, cancellationToken).ConfigureAwait(false);

            output.Ok($"Assembly [bold]{metadata.Name}[/] added");

            entity.Id = response.id;
            return (entity, false);
        }

        var registeredPkt     = existing.GetAttributeValue<string>("publickeytoken");
        var registeredCulture = existing.GetAttributeValue<string>("culture") ?? "neutral";
        var registeredVersion = existing.GetAttributeValue<string>("version");

        bool pktChanged        = !string.Equals(registeredPkt, metadata.PublicKeyToken, StringComparison.OrdinalIgnoreCase);
        bool cultureChanged    = !string.Equals(registeredCulture, metadata.Culture, StringComparison.OrdinalIgnoreCase);
        bool majorMinorChanged = HasMajorOrMinorVersionChange(registeredVersion, metadata.Version);

        if (pktChanged || cultureChanged || majorMinorChanged)
        {
            var reasons = new List<string>();
            if (pktChanged)        reasons.Add($"public key token ({registeredPkt ?? "null"} -> {metadata.PublicKeyToken ?? "null"})");
            if (cultureChanged)    reasons.Add($"culture ({registeredCulture} -> {metadata.Culture})");
            if (majorMinorChanged) reasons.Add($"major/minor version ({registeredVersion} -> {metadata.Version})");
            var reason = string.Join(", ", reasons);

            switch (runMode)
            {
                case RunMode.Save:
                    output.Error($"Assembly '{metadata.Name}' identity changed ({reason}) — Dataverse needs a delete and recreate. Re-run without --save to apply it, or use --dry-run to preview.");
                    throw new InvalidOperationException($"Assembly '{metadata.Name}' identity changed ({reason}). Cannot continue in save mode — re-run without --save to apply or use --dry-run to preview changes.");
                case RunMode.DryRun:
                    output.Skip($"Assembly '{metadata.Name}' identity changed ({reason}) — would delete and recreate");
                    // Return a dummy entity so that the caller can continue with the dry-run
                    return (new Entity("pluginassembly") { Id = Guid.NewGuid() }, false);
                case RunMode.Normal:
                    output.Warning($"Assembly '{metadata.Name}' identity changed ({reason}) — deleting and recreating plugin registrations.");
                    await service.DeleteAsync("pluginassembly", existing.Id, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(runMode), runMode, null);
            }

            var freshEntity = new Entity("pluginassembly")
            {
                ["name"]          = metadata.Name,
                ["content"]       = Convert.ToBase64String(metadata.Content),
                ["version"]       = metadata.Version,
                ["isolationmode"] = new OptionSetValue(2),
                ["description"]   = $"{FlowlineMarker} sha256={metadata.Hash}"
            };

            var response = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest { Target = freshEntity, ["SolutionUniqueName"] = solutionName },
                cancellationToken).ConfigureAwait(false);

            freshEntity.Id = response.id;
            output.Ok($"Assembly [bold]{metadata.Name}[/] recreated");
            return (freshEntity, false);
        }

        await AddSolutionComponentAsync(service, existing.Id, solutionName, cancellationToken).ConfigureAwait(false);
        var storedHash = ParseStoredHash(existing.GetAttributeValue<string>("description"));
        return (existing, storedHash != metadata.Hash);
    }

    void WriteDryRunSummary(PluginAssemblyMetadata metadata, bool needsUpdate, RegistrationPlan plan)
    {
        if (needsUpdate)
            output.Info($"Assembly '{metadata.Name} ({metadata.Version})' — would update content");

        foreach (var a in plan.PluginTypes.Deletes)   output.Info($"Plugin type '{a.Name}' — would delete");
        foreach (var a in plan.Steps.Deletes)         output.Info($"Step '{a.Name}' — would delete");
        foreach (var a in plan.Images.Deletes)        output.Info($"Image '{a.Name}' — would delete");
        foreach (var a in plan.CustomApis.Deletes)    output.Info($"Custom API '{a.Name}' — would delete");
        foreach (var a in plan.RequestParams.Deletes) output.Info($"Request parameter '{a.Name}' — would delete");
        foreach (var a in plan.ResponseProps.Deletes) output.Info($"Response property '{a.Name}' — would delete");

        foreach (var ups in plan.PluginTypes.Upserts)   output.Info($"Plugin type '{ups.Name}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var ups in plan.Steps.Upserts)         output.Info($"Step '{ups.Name}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var ups in plan.Images.Upserts)        output.Info($"Image '{ups.Name}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var ups in plan.CustomApis.Upserts)    output.Info($"Custom API '{ups.Name}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var ups in plan.RequestParams.Upserts) output.Info($"Request parameter '{ups.Name}' — would {(ups.IsCreate ? "create" : "update")}");
        foreach (var ups in plan.ResponseProps.Upserts) output.Info($"Response property '{ups.Name}' — would {(ups.IsCreate ? "create" : "update")}");

        var creates = plan.PluginTypes.Upserts.Count(u => u.IsCreate)
                      + plan.Steps.Upserts.Count(u => u.IsCreate)
                      + plan.CustomApis.Upserts.Count(u => u.IsCreate)
                      + plan.Images.Upserts.Count(u => u.IsCreate)
                      + plan.RequestParams.Upserts.Count(u => u.IsCreate)
                      + plan.ResponseProps.Upserts.Count(u => u.IsCreate);
        var updates = plan.TotalUpserts - creates;

        output.Ok($"Dry run: {plan.TotalDeletes} delete(s), {creates} create(s), {updates} update(s). Run without --dry-run to apply.");
    }

    void WriteSnapshotVerbose(RegistrationSnapshot snapshot)
    {
        if (!opt.IsVerbose) return;

        var tree = new Tree("[dim]Dataverse snapshot[/]") { Style = Style.Parse("dim") };
        tree.AddNode($"[dim]Publisher prefix: {Safe(snapshot.PublisherPrefix)}[/]");

        var pluginTypesNode = tree.AddNode($"[dim]Plugin types ({snapshot.PluginTypes.Count})[/]");
        foreach (var pluginType in snapshot.PluginTypes.Values.OrderBy(NameForPluginType, StringComparer.OrdinalIgnoreCase))
        {
            var pluginTypeId = pluginType.Id;
            var isWorkflow = BoolValue(pluginType, "isworkflowactivity");
            var pluginTypeNode = pluginTypesNode.AddNode(
                $"[dim]{Safe(NameForPluginType(pluginType))} ({pluginTypeId}){(isWorkflow ? " [[workflow]]" : "")}[/]");

            var steps = snapshot.Steps
                .Where(step => SameReference(step.GetAttributeValue<EntityReference>("plugintypeid"), pluginTypeId))
                .OrderBy(step => step.GetAttributeValue<string>("name"), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (steps.Count > 0)
            {
                var stepsNode = pluginTypeNode.AddNode($"[dim]Steps ({steps.Count})[/]");
                foreach (var step in steps)
                {
                    var stepId = step.Id;
                    var stepNode = stepsNode.AddNode(
                        $"[dim]{Safe(step.GetAttributeValue<string>("name") ?? stepId.ToString())} " +
                        $"stage={OptionValue(step, "stage")} mode={OptionValue(step, "mode")} rank={OptionValue(step, "rank")}[/]");

                    var description = step.GetAttributeValue<string>("description");
                    if (!string.IsNullOrWhiteSpace(description))
                        stepNode.AddNode($"[dim]Description: {Safe(description)}[/]");

                    var filteringAttributes = step.GetAttributeValue<string>("filteringattributes");
                    if (!string.IsNullOrWhiteSpace(filteringAttributes))
                        stepNode.AddNode($"[dim]Filtering attributes: {Safe(filteringAttributes)}[/]");

                    var impersonatingUser = step.GetAttributeValue<EntityReference>("impersonatinguserid");
                    if (impersonatingUser != null)
                        stepNode.AddNode($"[dim]Run as: {impersonatingUser.Id}[/]");

                    var images = snapshot.Images
                        .Where(image => SameReference(image.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid"), stepId))
                        .OrderBy(image => image.GetAttributeValue<string>("name"), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (images.Count > 0)
                    {
                        var imagesNode = stepNode.AddNode($"[dim]Images ({images.Count})[/]");
                        foreach (var image in images)
                            imagesNode.AddNode(
                                $"[dim]{Safe(image.GetAttributeValue<string>("name") ?? image.Id.ToString())} " +
                                $"alias={Safe(image.GetAttributeValue<string>("entityalias") ?? "(none)")} " +
                                $"type={OptionValue(image, "imagetype")} " +
                                $"attributes={Safe(image.GetAttributeValue<string>("attributes") ?? "(all)")}[/]");
                    }
                }
            }

            var customApis = snapshot.CustomApis
                .Where(api => SameReference(api.GetAttributeValue<EntityReference>("plugintypeid"), pluginTypeId))
                .OrderBy(api => api.GetAttributeValue<string>("uniquename"), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (customApis.Count > 0)
            {
                var apisNode = pluginTypeNode.AddNode($"[dim]Custom APIs ({customApis.Count})[/]");
                foreach (var api in customApis)
                {
                    var apiId = api.Id;
                    var apiNode = apisNode.AddNode(
                        $"[dim]{Safe(api.GetAttributeValue<string>("uniquename") ?? apiId.ToString())} " +
                        $"binding={OptionValue(api, "bindingtype")} function={BoolValue(api, "isfunction")} private={BoolValue(api, "isprivate")}[/]");

                    var boundEntity = api.GetAttributeValue<string>("boundentitylogicalname");
                    if (!string.IsNullOrWhiteSpace(boundEntity))
                        apiNode.AddNode($"[dim]Bound entity: {Safe(boundEntity)}[/]");

                    var requestParams = snapshot.RequestParams
                        .Where(param => SameReference(param.GetAttributeValue<EntityReference>("customapiid"), apiId))
                        .OrderBy(param => param.GetAttributeValue<string>("uniquename"), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (requestParams.Count > 0)
                    {
                        var paramsNode = apiNode.AddNode($"[dim]Request parameters ({requestParams.Count})[/]");
                        foreach (var param in requestParams)
                            paramsNode.AddNode(
                                $"[dim]{Safe(param.GetAttributeValue<string>("uniquename") ?? param.Id.ToString())} " +
                                $"type={OptionValue(param, "type")} optional={BoolValue(param, "isoptional")} " +
                                $"entity={Safe(param.GetAttributeValue<string>("logicalentityname") ?? "(none)")}[/]");
                    }

                    var responseProps = snapshot.ResponseProps
                        .Where(prop => SameReference(prop.GetAttributeValue<EntityReference>("customapiid"), apiId))
                        .OrderBy(prop => prop.GetAttributeValue<string>("uniquename"), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (responseProps.Count > 0)
                    {
                        var propsNode = apiNode.AddNode($"[dim]Response properties ({responseProps.Count})[/]");
                        foreach (var prop in responseProps)
                            propsNode.AddNode(
                                $"[dim]{Safe(prop.GetAttributeValue<string>("uniquename") ?? prop.Id.ToString())} " +
                                $"type={OptionValue(prop, "type")} entity={Safe(prop.GetAttributeValue<string>("logicalentityname") ?? "(none)")}[/]");
                    }
                }
            }
        }

        AddUnlinkedNodes(tree, "Unlinked steps", snapshot.Steps,
            e => e.GetAttributeValue<EntityReference>("plugintypeid"),
            snapshot.PluginTypes.Values.Select(e => e.Id).ToHashSet());
        AddUnlinkedNodes(tree, "Unlinked images", snapshot.Images,
            e => e.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid"),
            snapshot.Steps.Select(e => e.Id).ToHashSet());
        AddUnlinkedNodes(tree, "Unlinked Custom APIs", snapshot.CustomApis,
            e => e.GetAttributeValue<EntityReference>("plugintypeid"),
            snapshot.PluginTypes.Values.Select(e => e.Id).ToHashSet());

        if (snapshot.SdkMessageIds.Count > 0)
        {
            var messagesNode = tree.AddNode($"[dim]SDK messages ({snapshot.SdkMessageIds.Count})[/]");
            foreach (var (name, id) in snapshot.SdkMessageIds.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                messagesNode.AddNode($"[dim]{Safe(name)}: {id}[/]");
        }

        if (snapshot.FilterIds.Count > 0)
        {
            var filtersNode = tree.AddNode($"[dim]SDK message filters ({snapshot.FilterIds.Count})[/]");
            foreach (var (key, id) in snapshot.FilterIds.OrderBy(kvp => $"{kvp.Key.MessageId}:{kvp.Key.EntityName}:{kvp.Key.SecondaryEntity}", StringComparer.OrdinalIgnoreCase))
                filtersNode.AddNode($"[dim]message={key.MessageId} entity={Safe(key.EntityName ?? "(any)")} secondary={Safe(key.SecondaryEntity ?? "(none)")}: {id?.ToString() ?? "(none)"}[/]");
        }

        if (snapshot.SystemUserIds.Count > 0)
        {
            var usersNode = tree.AddNode($"[dim]System users ({snapshot.SystemUserIds.Count})[/]");
            foreach (var id in snapshot.SystemUserIds.OrderBy(id => id))
                usersNode.AddNode($"[dim]{id}[/]");
        }

        output.Write(tree);
    }

    void AddUnlinkedNodes(Tree tree, string title, IReadOnlyList<Entity> items,
        Func<Entity, EntityReference?> parentSelector, IReadOnlySet<Guid> knownParentIds)
    {
        var unlinked = items
            .Where(item =>
            {
                var parent = parentSelector(item);
                return parent == null || parent.Id == Guid.Empty || !knownParentIds.Contains(parent.Id);
            })
            .OrderBy(NameForEntity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unlinked.Count == 0) return;

        var section = tree.AddNode($"[dim]{title} ({unlinked.Count})[/]");
        foreach (var item in unlinked)
            section.AddNode($"[dim]{Safe(NameForEntity(item))} ({item.Id})[/]");
    }

    void WritePlanVerbose(RegistrationPlan plan)
    {
        if (!opt.IsVerbose)
            return;

        var addToSolutionCount = CountAddToSolutionComponents(plan);
        var tree = new Tree("[dim]Registration plan[/]") { Style = Style.Parse("dim") };
        tree.AddNode($"[dim]Summary: {plan.TotalDeletes} delete(s), {plan.TotalUpserts} upsert(s), {addToSolutionCount} add-to-solution action(s)[/]");

        AddActionPlanNode(tree, "Plugin types", plan.PluginTypes,
            e => $"workflow={BoolValue(e, "isworkflowactivity")}");
        AddActionPlanNode(tree, "Steps", plan.Steps,
            e => $"stage={OptionValue(e, "stage")} mode={OptionValue(e, "mode")} rank={OptionValue(e, "rank")}");
        AddActionPlanNode(tree, "Images", plan.Images,
            e => $"alias={Safe(e.GetAttributeValue<string>("entityalias") ?? "(none)")} type={OptionValue(e, "imagetype")} attributes={Safe(e.GetAttributeValue<string>("attributes") ?? "(all)")}");
        AddActionPlanNode(tree, "Custom APIs", plan.CustomApis,
            e => $"binding={OptionValue(e, "bindingtype")} function={BoolValue(e, "isfunction")} private={BoolValue(e, "isprivate")}");
        AddActionPlanNode(tree, "Request parameters", plan.RequestParams,
            e => $"type={OptionValue(e, "type")} optional={BoolValue(e, "isoptional")} entity={Safe(e.GetAttributeValue<string>("logicalentityname") ?? "(none)")}");
        AddActionPlanNode(tree, "Response properties", plan.ResponseProps,
            e => $"type={OptionValue(e, "type")} entity={Safe(e.GetAttributeValue<string>("logicalentityname") ?? "(none)")}");

        output.Write(tree);
    }

    void AddActionPlanNode(IHasTreeNodes parent, string title, ActionPlan actionPlan, Func<Entity, string> entityDetail)
    {
        if (actionPlan.Deletes.Count == 0 && actionPlan.Upserts.Count == 0 && actionPlan.AddSolutionComponents.Count == 0)
            return;

        var section = parent.AddNode($"[dim]{title}[/]");

        if (actionPlan.Deletes.Count > 0)
        {
            var deletesNode = section.AddNode($"[dim]Deletes ({actionPlan.Deletes.Count})[/]");
            foreach (var action in actionPlan.Deletes.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                deletesNode.AddNode($"[dim]{Safe(action.Name)}[/]");
        }

        if (actionPlan.Upserts.Count > 0)
        {
            var upsertsNode = section.AddNode($"[dim]Upserts ({actionPlan.Upserts.Count})[/]");
            foreach (var action in actionPlan.Upserts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var detail = entityDetail(action.Entity);
                var solution = string.IsNullOrWhiteSpace(action.SolutionName) ? "" : $" solution={Safe(action.SolutionName)}";
                upsertsNode.AddNode($"[dim]{Safe(action.Name)} [[{(action.IsCreate ? "create" : "update")}]] {detail}{solution}[/]");
            }
        }

        if (actionPlan.AddSolutionComponents.Count > 0)
        {
            var addNode = section.AddNode($"[dim]Add to solution ({actionPlan.AddSolutionComponents.Count})[/]");
            foreach (var action in actionPlan.AddSolutionComponents.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                addNode.AddNode($"[dim]{Safe(action.Name)} solution={Safe(action.SolutionName)} componenttype={action.ComponentType}[/]");
        }
    }

    async Task AddSolutionComponentAsync(IOrganizationServiceAsync2 service, Guid assemblyId, string solutionName, CancellationToken cancellationToken)
    {
        var request = new OrganizationRequest("AddSolutionComponent")
        {
            ["ComponentId"]               = assemblyId,
            ["ComponentType"]             = 91, // PluginAssembly
            ["SolutionUniqueName"]        = solutionName,
            ["AddRequiredComponents"]     = false,
            ["DoNotIncludeSubcomponents"] = false
        };
        await service.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    static string? ParseStoredHash(string? description)
    {
        if (description == null) return null;
        var idx = description.IndexOf("sha256=", StringComparison.Ordinal);
        return idx < 0 ? null : description[(idx + 7)..].Split(' ')[0].Trim();
    }

    internal static bool HasMajorOrMinorVersionChange(string? registered, string local)
    {
        if (string.IsNullOrWhiteSpace(registered)) return false;
        if (!Version.TryParse(registered, out var reg)) return false;
        if (!Version.TryParse(local, out var loc))      return false;
        return reg.Major != loc.Major || reg.Minor != loc.Minor;
    }

    static int CountAddToSolutionComponents(RegistrationPlan plan) =>
        plan.PluginTypes.AddSolutionComponents.Count
        + plan.Steps.AddSolutionComponents.Count
        + plan.Images.AddSolutionComponents.Count
        + plan.CustomApis.AddSolutionComponents.Count
        + plan.RequestParams.AddSolutionComponents.Count
        + plan.ResponseProps.AddSolutionComponents.Count;

    static bool SameReference(EntityReference? reference, Guid id) =>
        reference != null && reference.Id == id;

    static string NameForPluginType(Entity entity) =>
        entity.GetAttributeValue<string>("typename")
        ?? entity.GetAttributeValue<string>("name")
        ?? entity.Id.ToString();

    static string NameForEntity(Entity entity) =>
        entity.GetAttributeValue<string>("uniquename")
        ?? entity.GetAttributeValue<string>("name")
        ?? entity.Id.ToString();

    static string OptionValue(Entity entity, string attribute) =>
        entity.Attributes.TryGetValue(attribute, out var value)
            ? value switch
            {
                OptionSetValue option => option.Value.ToString(),
                int integer => integer.ToString(),
                null => "(none)",
                _ => value.ToString() ?? "(none)"
            }
            : "(none)";

    static bool BoolValue(Entity entity, string attribute) =>
        entity.Attributes.TryGetValue(attribute, out var value) && value is bool boolean && boolean;

    static string Safe(string value) => Markup.Escape(value);
}
