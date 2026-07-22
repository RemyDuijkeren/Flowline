using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.Plugins;

public class PluginExecutor(IAnsiConsole console)
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
        await RunDeleteLevelAsync(service, plan.Images.Deletes, "Image", "image(s)", cancellationToken, progressTask).ConfigureAwait(false);
        await RunDeleteLevelAsync(service, plan.ResponseProps.Deletes, "Response Property", "response property record(s)", cancellationToken, progressTask).ConfigureAwait(false);
        await RunDeleteLevelAsync(service, plan.RequestParams.Deletes, "Request Parameter", "request parameter(s)", cancellationToken, progressTask).ConfigureAwait(false);

        // Level 2 — steps and custom APIs
        await RunDeleteLevelAsync(service, plan.Steps.Deletes, "Step", "plugin step(s)", cancellationToken, progressTask).ConfigureAwait(false);
        await RunDeleteLevelAsync(service, plan.CustomApis.Deletes, "Custom API", "Custom API(s)", cancellationToken, progressTask).ConfigureAwait(false);

        // Level 1 — plugin types
        await RunDeleteLevelAsync(service, plan.PluginTypes.Deletes, "Plugin Type", "plugin type(s)", cancellationToken, progressTask).ConfigureAwait(false);
    }

    async Task RunDeleteLevelAsync(
        IOrganizationServiceAsync2 service, IReadOnlyList<DeleteAction> items, string label, string plural, CancellationToken cancellationToken, ProgressTask? progressTask)
    {
        foreach (var a in items)
        {
            await service.DeleteAsync(a.EntityLogicalName, a.Id, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"{label} '{a.Name}' not in source — deleted");
        }
        if (items.Count > 0) console.Info($"[green]{items.Count} {plural} deleted[/]");
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
        await RunUpsertLevelAsync(service, plan.PluginTypes.Upserts, "Plugin type", "plugin type(s)", solutionName, cancellationToken, progressTask).ConfigureAwait(false);

        // Level 2 — steps and custom APIs
        await RunUpsertLevelAsync(service, plan.Steps.Upserts, "Step", "plugin step(s)", solutionName, cancellationToken, progressTask).ConfigureAwait(false);
        await RunUpsertLevelAsync(service, plan.CustomApis.Upserts, "Custom API", "Custom API(s)", solutionName, cancellationToken, progressTask).ConfigureAwait(false);

        // Level 3 — leaf items
        await RunUpsertLevelAsync(service, plan.Images.Upserts, "Image", "image(s)", solutionName, cancellationToken, progressTask).ConfigureAwait(false);
        await RunUpsertLevelAsync(service, plan.ResponseProps.Upserts, "Response property", "response property record(s)", solutionName, cancellationToken, progressTask).ConfigureAwait(false);
        await RunUpsertLevelAsync(service, plan.RequestParams.Upserts, "Request Parameter", "request parameter(s)", solutionName, cancellationToken, progressTask).ConfigureAwait(false);
    }

    async Task RunUpsertLevelAsync(
        IOrganizationServiceAsync2 service, IReadOnlyList<UpsertAction> items, string label, string plural, string solutionName, CancellationToken cancellationToken, ProgressTask? progressTask)
    {
        foreach (var a in items)
        {
            await UpsertAsync(service, a, solutionName, cancellationToken).ConfigureAwait(false);
            progressTask?.Increment(1);
            console.Info($"{label} '{a.Name}' upserted");
        }
        if (items.Count > 0) console.Info($"[green]{items.Count} {plural} synced[/]");
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
