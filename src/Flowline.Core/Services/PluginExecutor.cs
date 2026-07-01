using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class PluginExecutor(IAnsiConsole console, bool isVerbose)
{

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
        if (save)
        {
            foreach (var a in plan.Images.Deletes)        console.Skip($"Image '{a.Name}' not in source — kept (--no-delete)");
            foreach (var a in plan.ResponseProps.Deletes) console.Skip($"Response Property '{a.Name}' not in source — kept (--no-delete)");
            foreach (var a in plan.RequestParams.Deletes) console.Skip($"Request Parameter '{a.Name}' not in source — kept (--no-delete)");
            foreach (var a in plan.Steps.Deletes)         console.Skip($"Step '{a.Name}' not in source — kept (--no-delete)");
            foreach (var a in plan.CustomApis.Deletes)    console.Skip($"Custom API '{a.Name}' not in source — kept (--no-delete)");
            foreach (var a in plan.PluginTypes.Deletes)   console.Skip($"Plugin Type '{a.Name}' not in source — kept (--no-delete)");
            return;
        }

        // Sequential: Leveled execution (types → steps → images) already forces sequential between levels. Within a level
        // the count is always small. Drop parallelism entirely — simpler, no collision risk, no observable perf regression.
        // Do not change back to parallel without good reason and testing.

        // Level 3 — delete leaf items first
        foreach (var a in plan.Images.Deletes)
        {
            await service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Image '{a.Name}' not in source — deleted");
        }
        if (plan.Images.Deletes.Count > 0) console.Info($"[green]{plan.Images.Deletes.Count} image(s) deleted[/]");

        foreach (var a in plan.ResponseProps.Deletes)
        {
            await service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Response Property '{a.Name}' not in source — deleted");
        }
        if (plan.ResponseProps.Deletes.Count > 0) console.Info($"[green]{plan.ResponseProps.Deletes.Count} response property record(s) deleted[/]");

        foreach (var a in plan.RequestParams.Deletes)
        {
            await service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Request Parameter '{a.Name}' not in source — deleted");
        }
        if (plan.RequestParams.Deletes.Count > 0) console.Info($"[green]{plan.RequestParams.Deletes.Count} request parameter(s) deleted[/]");

        // Level 2 — steps and custom APIs
        foreach (var a in plan.Steps.Deletes)
        {
            await service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Step '{a.Name}' not in source — deleted");
        }
        if (plan.Steps.Deletes.Count > 0) console.Info($"[green]{plan.Steps.Deletes.Count} plugin step(s) deleted[/]");

        foreach (var a in plan.CustomApis.Deletes)
        {
            await service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Custom API '{a.Name}' not in source — deleted");
        }
        if (plan.CustomApis.Deletes.Count > 0) console.Info($"[green]{plan.CustomApis.Deletes.Count} Custom API(s) deleted[/]");

        // Level 1 — plugin types
        foreach (var a in plan.PluginTypes.Deletes)
        {
            await service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Plugin Type '{a.Name}' not in source — deleted");
        }
        if (plan.PluginTypes.Deletes.Count > 0) console.Info($"[green]{plan.PluginTypes.Deletes.Count} plugin type(s) deleted[/]");
    }

    async Task RunUpsertsAsync(
        IOrganizationServiceAsync2 service, RegistrationPlan plan, string solutionName, CancellationToken cancellationToken, ProgressTask? progressTask)
    {
        // Sequential: Typical warm run = 0–3 creates, mostly updates. Leveled execution (types → steps → images) already
        // forces sequential between levels. Within a level the count is always small. Drop parallelism entirely — simpler,
        // no collision risk, no observable perf regression. Do not change back to parallel without good reason and testing.

        // CreateRequest+SolutionUniqueName for create triggers GrantInheritedAccess collisions in Dataverse under
        // parallel load. Keeping everything sequential avoids that class of bug. You can split the create into two
        // separate requests (Create+AddSolutionComponent) but it is not worth the extra complexity for these low numbers.

        // Level 1 — plugin types
        foreach (var a in plan.PluginTypes.Upserts)
        {
            await UpsertAsync(service, a, solutionName, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Plugin type '{a.Name}' upserted");
        }
        if (plan.PluginTypes.Upserts.Count > 0) console.Info($"[green]{plan.PluginTypes.Upserts.Count} plugin type(s) synced[/]");

        // Level 2 — steps and custom APIs
        foreach (var a in plan.Steps.Upserts)
        {
            await UpsertAsync(service, a, solutionName, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Step '{a.Name}' upserted");
        }
        if (plan.Steps.Upserts.Count > 0) console.Info($"[green]{plan.Steps.Upserts.Count} plugin step(s) synced[/]");

        foreach (var a in plan.CustomApis.Upserts)
        {
            await UpsertAsync(service, a, solutionName, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Custom API '{a.Name}' upserted");
        }
        if (plan.CustomApis.Upserts.Count > 0) console.Info($"[green]{plan.CustomApis.Upserts.Count} Custom API(s) synced[/]");

        // Level 3 — leaf items
        foreach (var a in plan.Images.Upserts)
        {
            await UpsertAsync(service, a, solutionName, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Image '{a.Name}' upserted");
        }
        if (plan.Images.Upserts.Count > 0) console.Info($"[green]{plan.Images.Upserts.Count} image(s) synced[/]");

        foreach (var a in plan.ResponseProps.Upserts)
        {
            await UpsertAsync(service, a, solutionName, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Response property '{a.Name}' upserted");
        }
        if (plan.ResponseProps.Upserts.Count > 0) console.Info($"[green]{plan.ResponseProps.Upserts.Count} response property record(s) synced[/]");

        foreach (var a in plan.RequestParams.Upserts)
        {
            await UpsertAsync(service, a, solutionName, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"Request Parameter '{a.Name}' upserted");
        }
        if (plan.RequestParams.Upserts.Count > 0) console.Info($"[green]{plan.RequestParams.Upserts.Count} request parameter(s) synced[/]");
    }

    async Task RunAddSolutionComponentsAsync(IOrganizationServiceAsync2 service, RegistrationPlan plan, CancellationToken cancellationToken, ProgressTask? progressTask)
    {
        List<AddToSolutionAction> all =
        [
            ..plan.PluginTypes.AddSolutionComponents,
            ..plan.Steps.AddSolutionComponents,
            ..plan.CustomApis.AddSolutionComponents,
            ..plan.RequestParams.AddSolutionComponents,
            ..plan.ResponseProps.AddSolutionComponents,
            ..plan.Images.AddSolutionComponents,
        ];

        // Sequential — see RunUpsertsAsync for rationale. Do not change back to parallel without
        // addressing the Dataverse GrantInheritedAccess collision on CreateRequest+SolutionUniqueName.
        foreach (var a in all)
        {
            await AddSolutionComponentAsync(service, a.EntityLogicalName, a.Id, a.ComponentType, a.SolutionName, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"{a.EntityLogicalName} '{a.Id}' added to solution");
        }
        if (all.Count > 0) console.Ok($"{all.Count} component(s) added to solution");
    }

    async Task UpsertAsync(IOrganizationServiceAsync2 service, UpsertAction action, string solutionName, CancellationToken cancellationToken)
    {
        if (!action.IsCreate)
        {
            await service.UpdateAsync(action.Entity, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (action.SolutionName != null)
            await service.ExecuteAsync(new CreateRequest { Target = action.Entity, ["SolutionUniqueName"] = action.SolutionName }, cancellationToken).ConfigureAwait(false);
        else
            await service.CreateAsync(action.Entity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Idempotent — safe to call multiple times for the same component.</summary>
    async Task AddSolutionComponentAsync(IOrganizationServiceAsync2 service, string entityLogicalName, Guid componentId, int componentType, string solutionName, CancellationToken cancellationToken = default)
    {
        var request = new OrganizationRequest("AddSolutionComponent")
        {
            ["ComponentId"]               = componentId,
            ["ComponentType"]             = componentType,
            ["SolutionUniqueName"]        = solutionName,
            ["AddRequiredComponents"]     = false,
            ["DoNotIncludeSubcomponents"] = false
        };

        await service.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        console.Info($"Added {entityLogicalName} '{componentId}' to solution '{solutionName}'.");
    }
}
