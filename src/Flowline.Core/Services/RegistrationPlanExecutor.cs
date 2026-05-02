using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

public class RegistrationPlanExecutor(IFlowlineOutput output)
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
        IOrganizationServiceAsync2 service, RegistrationPlan plan, string solutionName, bool save, CancellationToken cancellationToken) =>
        RunDeletesAsync(service, plan, solutionName, save, cancellationToken);

    public Task ExecuteUpsertsAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, string solutionName, CancellationToken cancellationToken) =>
        RunUpsertsAsync(service, plan, solutionName, cancellationToken);

    public Task ExecuteAddToSolutionAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, CancellationToken cancellationToken) =>
        RunAddSolutionComponentsAsync(service, plan, cancellationToken);

    async Task RunDeletesAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, string solutionName, bool save, CancellationToken cancellationToken)
    {
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
                ExecuteBoundedParallelAsync(plan.Images.Deletes.Values,        MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken),
                ExecuteBoundedParallelAsync(plan.ResponseProps.Deletes.Values, MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken),
                ExecuteBoundedParallelAsync(plan.RequestParams.Deletes.Values, MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken)).ConfigureAwait(false);

            foreach (var s in plan.Images.Deletes.Keys)         output.Verbose($"Image '{s}' not in source — deleted");
            if (plan.Images.Deletes.Count > 0)         output.Info($"Deleted {plan.Images.Deletes.Count} Images");

            foreach (var s in plan.ResponseProps.Deletes.Keys)  output.Verbose($"Response Property '{s}' not in source — deleted");
            if (plan.ResponseProps.Deletes.Count > 0)  output.Info($"Deleted {plan.ResponseProps.Deletes.Count} Response Properties");

            foreach (var s in plan.RequestParams.Deletes.Keys)  output.Verbose($"Request Parameter '{s}' not in source — deleted");
            if (plan.RequestParams.Deletes.Count > 0)  output.Info($"Deleted {plan.RequestParams.Deletes.Count} Request Parameters");
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
                ExecuteBoundedParallelAsync(plan.Steps.Deletes.Values,      MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken),
                ExecuteBoundedParallelAsync(plan.CustomApis.Deletes.Values, MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken)).ConfigureAwait(false);

            foreach (var s in plan.Steps.Deletes.Keys)       output.Verbose($"Step '{s}' not in source — deleted");
            if (plan.Steps.Deletes.Count > 0)      output.Info($"Deleted {plan.Steps.Deletes.Count} Plugin Steps");

            foreach (var s in plan.CustomApis.Deletes.Keys)  output.Verbose($"Custom API '{s}' not in source — deleted");
            if (plan.CustomApis.Deletes.Count > 0) output.Info($"Deleted {plan.CustomApis.Deletes.Count} Custom APIs");
        }

        // Level 1 — plugin types
        if (save)
        {
            foreach (var s in plan.PluginTypes.Deletes.Keys) output.Skip($"Plugin Type '{s}' not in source — kept (--save)");
        }
        else
        {
            await ExecuteBoundedParallelAsync(plan.PluginTypes.Deletes.Values, MaxParallelism, a => service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken), cancellationToken).ConfigureAwait(false);
            foreach (var s in plan.PluginTypes.Deletes.Keys) output.Verbose($"Plugin Type '{s}' not in source — deleted");
            if (plan.PluginTypes.Deletes.Count > 0) output.Info($"Deleted {plan.PluginTypes.Deletes.Count} PluginTypes");
        }
    }

    async Task RunUpsertsAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, string solutionName, CancellationToken cancellationToken)
    {
        // Level 1 — plugin types
        await ExecuteBoundedParallelAsync(plan.PluginTypes.Upserts.Values, 1, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var s in plan.PluginTypes.Upserts.Keys) output.Verbose($"Plugin type '{s}' upserted");
        if (plan.PluginTypes.Upserts.Count > 0) output.Info($"Upserted {plan.PluginTypes.Upserts.Count} PluginTypes");

        // Level 2 — steps and custom APIs
        await Task.WhenAll(
            ExecuteBoundedParallelAsync(plan.Steps.Upserts.Values,      MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken),
            ExecuteBoundedParallelAsync(plan.CustomApis.Upserts.Values, MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken)).ConfigureAwait(false);

        foreach (var s in plan.Steps.Upserts.Keys)      output.Verbose($"Step '{s}' upserted");
        if (plan.Steps.Upserts.Count > 0)      output.Info($"Upserted {plan.Steps.Upserts.Count} Plugin Steps");

        foreach (var s in plan.CustomApis.Upserts.Keys) output.Verbose($"Custom API '{s}' upserted");
        if (plan.CustomApis.Upserts.Count > 0) output.Info($"Upserted {plan.CustomApis.Upserts.Count} Custom APIs");

        // Level 3 — leaf items
        await Task.WhenAll(
            ExecuteBoundedParallelAsync(plan.Images.Upserts.Values,       MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken),
            ExecuteBoundedParallelAsync(plan.ResponseProps.Upserts.Values, MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken),
            ExecuteBoundedParallelAsync(plan.RequestParams.Upserts.Values, MaxParallelism, a => UpsertAsync(service, a, solutionName, cancellationToken), cancellationToken)).ConfigureAwait(false);

        foreach (var s in plan.Images.Upserts.Keys)        output.Verbose($"Image '{s}' upserted");
        if (plan.Images.Upserts.Count > 0)        output.Info($"Upserted {plan.Images.Upserts.Count} Images");

        foreach (var s in plan.ResponseProps.Upserts.Keys)  output.Verbose($"Response property '{s}' upserted");
        if (plan.ResponseProps.Upserts.Count > 0)  output.Info($"Upserted {plan.ResponseProps.Upserts.Count} Response Properties");

        foreach (var s in plan.RequestParams.Upserts.Keys)  output.Verbose($"Request Parameter '{s}' upserted");
        if (plan.RequestParams.Upserts.Count > 0)  output.Info($"Upserted {plan.RequestParams.Upserts.Count} Request Parameters");
    }

    async Task RunAddSolutionComponentsAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, CancellationToken cancellationToken)
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

        await ExecuteBoundedParallelAsync(all, MaxParallelism, a => AddSolutionComponentAsync(service, a.EntityLogicalName, a.Id, a.SolutionName, cancellationToken), cancellationToken).ConfigureAwait(false);

        foreach (var s in plan.Steps.AddSolutionComponents.Keys)       output.Verbose($"Step '{s}' added to solution");
        if (plan.Steps.AddSolutionComponents.Count > 0)        output.Info($"Added {plan.Steps.AddSolutionComponents.Count} Plugin Steps to solution");

        foreach (var s in plan.CustomApis.AddSolutionComponents.Keys)  output.Verbose($"Custom API '{s}' added to solution");
        if (plan.CustomApis.AddSolutionComponents.Count > 0)   output.Info($"Added {plan.CustomApis.AddSolutionComponents.Count} Custom APIs to solution");

        foreach (var s in plan.ResponseProps.AddSolutionComponents.Keys) output.Verbose($"Response property '{s}' added to solution");
        if (plan.ResponseProps.AddSolutionComponents.Count > 0) output.Info($"Added {plan.ResponseProps.AddSolutionComponents.Count} Response Properties to solution");

        foreach (var s in plan.RequestParams.AddSolutionComponents.Keys) output.Verbose($"Request Parameter '{s}' added to solution");
        if (plan.RequestParams.AddSolutionComponents.Count > 0) output.Info($"Added {plan.RequestParams.AddSolutionComponents.Count} Request Parameters to solution");
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

    static async Task ExecuteBoundedParallelAsync<T>(IEnumerable<T> items, int maxParallelism, Func<T, Task> action, CancellationToken cancellationToken)
    {
        var list = items as ICollection<T> ?? items.ToList();
        if (list.Count == 0) return;

        using var gate = new SemaphoreSlim(maxParallelism);
        var tasks = list.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try   { await action(item).ConfigureAwait(false); }
            finally { gate.Release(); }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
