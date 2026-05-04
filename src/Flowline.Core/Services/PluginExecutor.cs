using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class PluginExecutor(IAnsiConsole output, FlowlineRuntimeOptions opt)
{
    const int MaxParallelism = 8;
    const string DefaultSolutionUniqueName = "Default";
    const int PluginAssemblyComponentType = 91;
    const int SdkMessageProcessingStepComponentType = 92;

    readonly Dictionary<string, int> _componentTypeByEntityLogicalName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pluginassembly"] = PluginAssemblyComponentType,
        ["sdkmessageprocessingstep"] = SdkMessageProcessingStepComponentType
    };

    public Task ExecuteDeletesAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, string solutionName, bool save, CancellationToken cancellationToken, ProgressTask? progressTask = null) =>
        RunDeletesAsync(service, plan, solutionName, save, cancellationToken, progressTask);

    public Task ExecuteUpsertsAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, string solutionName, CancellationToken cancellationToken, ProgressTask? progressTask = null) =>
        RunUpsertsAsync(service, plan, solutionName, cancellationToken, progressTask);

    public Task ExecuteAddToSolutionAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, CancellationToken cancellationToken, ProgressTask? progressTask = null) =>
        RunAddSolutionComponentsAsync(service, plan, cancellationToken, progressTask);

    async Task RunDeletesAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, string solutionName, bool save, CancellationToken cancellationToken, ProgressTask? progressTask)
    {
        var progressLock = new object();

        // Level 3 — delete leaf items first
        if (save)
        {
            foreach (var s in plan.Images.Deletes.Keys)        output.Skip($"Image '{s}' not in source — kept (--save)");
            foreach (var s in plan.ResponseProps.Deletes.Keys) output.Skip($"Response Property '{s}' not in source — kept (--save)");
            foreach (var s in plan.RequestParams.Deletes.Keys) output.Skip($"Request Parameter '{s}' not in source — kept (--save)");
        }
        else
        {
            await Task.WhenAll(
                ExecuteBoundedParallelAsync(plan.Images.Deletes.Values,        MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken, progressTask, progressLock),
                ExecuteBoundedParallelAsync(plan.ResponseProps.Deletes.Values, MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken, progressTask, progressLock),
                ExecuteBoundedParallelAsync(plan.RequestParams.Deletes.Values, MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken, progressTask, progressLock)).ConfigureAwait(false);

            foreach (var s in plan.Images.Deletes.Keys)         output.Verbose($"Image '{s}' not in source — deleted", opt);
            if (plan.Images.Deletes.Count > 0)         output.Info($"[green]{plan.Images.Deletes.Count} image(s) deleted[/]");

            foreach (var s in plan.ResponseProps.Deletes.Keys)  output.Verbose($"Response Property '{s}' not in source — deleted", opt);
            if (plan.ResponseProps.Deletes.Count > 0)  output.Info($"[green]{plan.ResponseProps.Deletes.Count} response property record(s) deleted[/]");

            foreach (var s in plan.RequestParams.Deletes.Keys)  output.Verbose($"Request Parameter '{s}' not in source — deleted", opt);
            if (plan.RequestParams.Deletes.Count > 0)  output.Info($"[green]{plan.RequestParams.Deletes.Count} request parameter(s) deleted[/]");
        }

        // Level 2 — steps and custom APIs
        if (save)
        {
            foreach (var s in plan.Steps.Deletes.Keys)      output.Skip($"Step '{s}' not in source — kept (--save)");
            foreach (var s in plan.CustomApis.Deletes.Keys) output.Skip($"Custom API '{s}' not in source — kept (--save)");
        }
        else
        {
            await Task.WhenAll(
                ExecuteBoundedParallelAsync(plan.Steps.Deletes.Values,      MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken, progressTask, progressLock),
                ExecuteBoundedParallelAsync(plan.CustomApis.Deletes.Values, MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken, progressTask, progressLock)).ConfigureAwait(false);

            foreach (var s in plan.Steps.Deletes.Keys)       output.Verbose($"Step '{s}' not in source — deleted", opt);
            if (plan.Steps.Deletes.Count > 0)      output.Info($"[green]{plan.Steps.Deletes.Count} plugin step(s) deleted[/]");

            foreach (var s in plan.CustomApis.Deletes.Keys)  output.Verbose($"Custom API '{s}' not in source — deleted", opt);
            if (plan.CustomApis.Deletes.Count > 0) output.Info($"[green]{plan.CustomApis.Deletes.Count} Custom API(s) deleted[/]");
        }

        // Level 1 — plugin types
        if (save)
        {
            foreach (var s in plan.PluginTypes.Deletes.Keys) output.Skip($"Plugin Type '{s}' not in source — kept (--save)");
        }
        else
        {
            await ExecuteBoundedParallelAsync(plan.PluginTypes.Deletes.Values, MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken, progressTask, progressLock).ConfigureAwait(false);
            foreach (var s in plan.PluginTypes.Deletes.Keys) output.Verbose($"Plugin Type '{s}' not in source — deleted", opt);
            if (plan.PluginTypes.Deletes.Count > 0) output.Info($"[green]{plan.PluginTypes.Deletes.Count} plugin type(s) deleted[/]");
        }
    }

    async Task RunUpsertsAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, string solutionName, CancellationToken cancellationToken, ProgressTask? progressTask)
    {
        var progressLock = new object();

        // Level 1 — plugin types
        await ExecuteBoundedParallelAsync(plan.PluginTypes.Upserts.Values, MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken, progressTask, progressLock).ConfigureAwait(false);
        foreach (var s in plan.PluginTypes.Upserts.Keys) output.Verbose($"Plugin type '{s}' upserted", opt);
        if (plan.PluginTypes.Upserts.Count > 0) output.Info($"[green]{plan.PluginTypes.Upserts.Count} plugin type(s) synced[/]");

        // Level 2 — steps and custom APIs
        await Task.WhenAll(
            ExecuteBoundedParallelAsync(plan.Steps.Upserts.Values,      MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken, progressTask, progressLock),
            ExecuteBoundedParallelAsync(plan.CustomApis.Upserts.Values, MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken, progressTask, progressLock)).ConfigureAwait(false);

        foreach (var s in plan.Steps.Upserts.Keys)      output.Verbose($"Step '{s}' upserted", opt);
        if (plan.Steps.Upserts.Count > 0)      output.Info($"[green]{plan.Steps.Upserts.Count} plugin step(s) synced[/]");

        foreach (var s in plan.CustomApis.Upserts.Keys) output.Verbose($"Custom API '{s}' upserted", opt);
        if (plan.CustomApis.Upserts.Count > 0) output.Info($"[green]{plan.CustomApis.Upserts.Count} Custom API(s) synced[/]");

        // Level 3 — leaf items
        await Task.WhenAll(
            ExecuteBoundedParallelAsync(plan.Images.Upserts.Values,       MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken, progressTask, progressLock),
            ExecuteBoundedParallelAsync(plan.ResponseProps.Upserts.Values, MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken, progressTask, progressLock),
            ExecuteBoundedParallelAsync(plan.RequestParams.Upserts.Values, MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken, progressTask, progressLock)).ConfigureAwait(false);

        foreach (var s in plan.Images.Upserts.Keys)        output.Verbose($"Image '{s}' upserted", opt);
        if (plan.Images.Upserts.Count > 0)        output.Info($"[green]{plan.Images.Upserts.Count} image(s) synced[/]");

        foreach (var s in plan.ResponseProps.Upserts.Keys)  output.Verbose($"Response property '{s}' upserted", opt);
        if (plan.ResponseProps.Upserts.Count > 0)  output.Info($"[green]{plan.ResponseProps.Upserts.Count} response property record(s) synced[/]");

        foreach (var s in plan.RequestParams.Upserts.Keys)  output.Verbose($"Request Parameter '{s}' upserted", opt);
        if (plan.RequestParams.Upserts.Count > 0)  output.Info($"[green]{plan.RequestParams.Upserts.Count} request parameter(s) synced[/]");
    }

    async Task RunAddSolutionComponentsAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, CancellationToken cancellationToken, ProgressTask? progressTask)
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

        var progressLock = new object();
        await ExecuteBoundedParallelAsync(all, MaxParallelism, a => AddSolutionComponentAsync(service, a.EntityLogicalName, a.Id, a.SolutionName, cancellationToken), cancellationToken, progressTask, progressLock).ConfigureAwait(false);

        foreach (var s in plan.Steps.AddSolutionComponents.Keys)       output.Verbose($"Step '{s}' added to solution", opt);
        if (plan.Steps.AddSolutionComponents.Count > 0)        output.Info($"[green]{plan.Steps.AddSolutionComponents.Count} plugin step(s) added to solution[/]");

        foreach (var s in plan.CustomApis.AddSolutionComponents.Keys)  output.Verbose($"Custom API '{s}' added to solution", opt);
        if (plan.CustomApis.AddSolutionComponents.Count > 0)   output.Info($"[green]{plan.CustomApis.AddSolutionComponents.Count} Custom API(s) added to solution[/]");

        foreach (var s in plan.ResponseProps.AddSolutionComponents.Keys) output.Verbose($"Response property '{s}' added to solution", opt);
        if (plan.ResponseProps.AddSolutionComponents.Count > 0) output.Info($"[green]{plan.ResponseProps.AddSolutionComponents.Count} response property record(s) added to solution[/]");

        foreach (var s in plan.RequestParams.AddSolutionComponents.Keys) output.Verbose($"Request Parameter '{s}' added to solution", opt);
        if (plan.RequestParams.AddSolutionComponents.Count > 0) output.Info($"[green]{plan.RequestParams.AddSolutionComponents.Count} request parameter(s) added to solution[/]");
    }

    async Task UpsertAsync(IOrganizationServiceAsync2 service, UpsertAction action, string solutionName, CancellationToken cancellationToken)
    {
        if (!action.IsCreate)
        {
            await WarnIfComponentExistsInOtherSolutionsAsync(service, action.Entity.Id, solutionName, action.Entity.LogicalName, action.Name, cancellationToken).ConfigureAwait(false);
            await service.UpdateAsync(action.Entity, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (action.SolutionName != null)
            await service.ExecuteAsync(new CreateRequest { Target = action.Entity, ["SolutionUniqueName"] = action.SolutionName }, cancellationToken).ConfigureAwait(false);
        else
            await service.CreateAsync(action.Entity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Idempotent — safe to call multiple times for the same component.</summary>
    async Task AddSolutionComponentAsync(IOrganizationServiceAsync2 service, string entityLogicalName, Guid componentId, string solutionName, CancellationToken cancellationToken = default)
    {
        var componentType = await GetComponentTypeAsync(service, entityLogicalName, componentId, cancellationToken).ConfigureAwait(false);

        var request = new OrganizationRequest("AddSolutionComponent")
        {
            ["ComponentId"]              = componentId,
            ["ComponentType"]            = componentType,
            ["SolutionUniqueName"]       = solutionName,
            ["AddRequiredComponents"]    = false,
            ["DoNotIncludeSubcomponents"] = false
        };

        await service.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        output.Verbose($"Added {entityLogicalName} '{componentId}' to solution '{solutionName}'.", opt);
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

    /// <summary>Logs a warning when a component being updated is also part of other solutions.</summary>
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

        output.Warning($"Updating {entityLogicalName} '{componentDisplayName}' which also exists in other solutions: {string.Join(", ", otherSolutions)}.");
    }

    static async Task ExecuteBoundedParallelAsync<T>(
        IEnumerable<T> items,
        int maxParallelism,
        Func<T, Task> action,
        CancellationToken cancellationToken,
        ProgressTask? progressTask = null,
        object? progressLock = null)
    {
        var list = items as ICollection<T> ?? items.ToList();
        if (list.Count == 0) return;

        using var gate = new SemaphoreSlim(maxParallelism);
        var tasks = list.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action(item).ConfigureAwait(false);
                IncrementProgress(progressTask, progressLock);
            }
            finally { gate.Release(); }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    static void IncrementProgress(ProgressTask? progressTask, object? progressLock)
    {
        if (progressTask == null)
            return;

        if (progressLock == null)
        {
            progressTask.Increment(1);
            return;
        }

        lock (progressLock)
            progressTask.Increment(1);
    }
}
