using System.Security;
using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class WebResourceExecutor(IAnsiConsole console, FlowlineRuntimeOptions options)
{
    const int MaxParallelism = 8;
    const int WebResourceComponentType = 61;

    public async Task ExecuteAsync(
        IOrganizationServiceAsync2 service,
        WebResourceSyncPlan plan,
        bool publishAfterSync,
        bool save,
        CancellationToken cancellationToken = default)
    {
        var publishIds = new List<Guid>();
        var failures = new List<(string Name, Exception Error)>();

        foreach (var a in plan.Skips) console.Skip($"Web resource '{a.Name}' kept ({a.Reason})");

        // Create web resources — sequential, so no lock needed for progress
        if (plan.Creates.Count > 0)
        {
            publishIds.AddRange(await console.Progress().StartAsync(ctx =>
                ExecuteCreatesAsync(service, plan.Creates, failures,
                    ctx.AddTask("Creating web resources", maxValue: plan.Creates.Count), cancellationToken)).ConfigureAwait(false));
            foreach (var a in plan.Creates) console.Verbose($"Web resource '{a.Name}' created");
            console.Ok($"{plan.Creates.Count} web resource(s) created");
        }

        // Update web resources — parallel, so lock needed for progress
        if (plan.Updates.Count > 0)
        {
            await console.Progress().StartAsync(ctx =>
                ExecuteBoundedParallelAsync(plan.Updates, MaxParallelism, async action =>
                {
                    try
                    {
                        await service.UpdateAsync(action.Entity!, cancellationToken).ConfigureAwait(false);
                        lock (publishIds) publishIds.Add(action.Entity!.Id);
                    }
                    catch (FaultException<OrganizationServiceFault> ex) { lock (failures) failures.Add((action.Name, ex)); }
                }, ctx.AddTask("Updating web resources", maxValue: plan.Updates.Count), cancellationToken)).ConfigureAwait(false);
            foreach (var a in plan.Updates) console.Verbose($"Web resource '{a.Name}' updated");
            console.Ok($"{plan.Updates.Count} web resource(s) updated");
        }

        // Add web resources to solution — parallel, so lock needed for progress
        if (plan.AddsToSolution.Count > 0)
        {
            await console.Progress().StartAsync(ctx =>
                ExecuteBoundedParallelAsync(plan.AddsToSolution, MaxParallelism, async action =>
                {
                    try { await AddToSolutionAsync(service, action.Id!.Value, action.SolutionName!, cancellationToken).ConfigureAwait(false); }
                    catch (FaultException<OrganizationServiceFault> ex) { lock (failures) failures.Add((action.Name, ex)); }
                }, ctx.AddTask("Adding web resources to solution", maxValue: plan.AddsToSolution.Count), cancellationToken)).ConfigureAwait(false);
            foreach (var a in plan.AddsToSolution) console.Verbose($"Web resource '{a.Name}' added to solution");
            console.Ok($"{plan.AddsToSolution.Count} web resource(s) added to solution");
        }

        if (!save)
        {
            // Delete web resources — parallel, so lock needed for progress
            if (plan.Deletes.Count > 0)
            {
                await console.Progress().StartAsync(ctx =>
                    ExecuteBoundedParallelAsync(plan.Deletes, MaxParallelism, async action =>
                    {
                        try { await service.DeleteAsync("webresource", action.Id!.Value, cancellationToken).ConfigureAwait(false); }
                        catch (FaultException<OrganizationServiceFault> ex) { lock (failures) failures.Add((action.Name, ex)); }
                    }, ctx.AddTask("Deleting web resources", maxValue: plan.Deletes.Count), cancellationToken)).ConfigureAwait(false);
                foreach (var a in plan.Deletes) console.Verbose($"Web resource '{a.Name}' deleted");
                console.Ok($"{plan.Deletes.Count} web resource(s) deleted");
            }

            // Remove web resources from solution — parallel, so lock needed for progress
            if (plan.RemovesFromSolution.Count > 0)
            {
                await console.Progress().StartAsync(ctx =>
                    ExecuteBoundedParallelAsync(plan.RemovesFromSolution, MaxParallelism, async action =>
                    {
                        try { await RemoveFromSolutionAsync(service, action.Id!.Value, action.SolutionName!, cancellationToken).ConfigureAwait(false); }
                        catch (FaultException<OrganizationServiceFault> ex) { lock (failures) failures.Add((action.Name, ex)); }
                    }, ctx.AddTask("Removing web resources from solution", maxValue: plan.RemovesFromSolution.Count), cancellationToken)).ConfigureAwait(false);
                foreach (var a in plan.RemovesFromSolution) console.Verbose($"Web resource '{a.Name}' removed from solution");
                console.Ok($"{plan.RemovesFromSolution.Count} web resource(s) removed from solution");
            }
        }
        else
        {
            foreach (var a in plan.Deletes) console.Skip($"Web resource '{a.Name}' not in source — kept (--no-delete)");
            if (plan.Deletes.Count > 0) console.Skip($"{plan.Deletes.Count} web resource(s) not in source — kept (--no-delete)");
            foreach (var a in plan.RemovesFromSolution) console.Skip($"Web resource '{a.Name}' still in other solution — kept (--no-delete)");
            if (plan.RemovesFromSolution.Count > 0) console.Skip($"{plan.RemovesFromSolution.Count} web resource(s) still in other solution — kept (--no-delete)");
        }

        if (publishAfterSync && publishIds.Count > 0)
        {
            var distinctIds = publishIds.Distinct().ToList();
            await console.Status().FlowlineSpinner()
                        .StartAsync("Publishing web resources", ctx => PublishAsync(service, distinctIds, cancellationToken))
                        .ConfigureAwait(false);
            console.Ok($"{distinctIds.Count} web resource(s) published");
        }

        if (failures.Count > 0)
        {
            foreach (var (name, ex) in failures)
                console.Error($"'{name}' — {ex.Message}");
            throw new InvalidOperationException($"{failures.Count} web resource operation(s) failed.");
        }
    }

    async Task<List<Guid>> ExecuteCreatesAsync(IOrganizationServiceAsync2 service,
        IEnumerable<WebResourcePlanAction> creates,
        List<(string Name, Exception Error)> failures,
        ProgressTask progressTask,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();

        // Sequential — CreateRequest+SolutionUniqueName triggers GrantInheritedAccess collisions
        // in Dataverse when multiple creates run in parallel. Web resource creates are rare (0-5
        // per warm run); sequential execution has no observable performance impact. Do not change
        // back to parallel without addressing the Dataverse collision first.
        foreach (var action in creates)
        {
            try
            {
                var response = (CreateResponse)await service.ExecuteAsync(
                    new CreateRequest { Target = action.Entity!, ["SolutionUniqueName"] = action.SolutionName },
                    cancellationToken).ConfigureAwait(false);
                ids.Add(response.id);
            }
            catch (FaultException<OrganizationServiceFault> ex) { failures.Add((action.Name, ex)); }
            progressTask.Increment(1);
        }

        return ids;
    }

    static Task AddToSolutionAsync(
        IOrganizationServiceAsync2 service,
        Guid webResourceId,
        string solutionName,
        CancellationToken cancellationToken)
    {
        var request = new OrganizationRequest("AddSolutionComponent")
        {
            ["ComponentId"] = webResourceId,
            ["ComponentType"] = WebResourceComponentType,
            ["SolutionUniqueName"] = solutionName,
            ["AddRequiredComponents"] = false
        };
        return service.ExecuteAsync(request, cancellationToken);
    }

    static Task RemoveFromSolutionAsync(
        IOrganizationServiceAsync2 service,
        Guid webResourceId,
        string solutionName,
        CancellationToken cancellationToken)
    {
        var request = new OrganizationRequest("RemoveSolutionComponent")
        {
            ["ComponentId"] = webResourceId,
            ["ComponentType"] = WebResourceComponentType,
            ["SolutionUniqueName"] = solutionName
        };
        return service.ExecuteAsync(request, cancellationToken);
    }

    static Task PublishAsync(IOrganizationServiceAsync2 service, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var webresources = string.Concat(ids.Select(id => $"<webresource>{SecurityElement.Escape(id.ToString())}</webresource>"));
        var request = new OrganizationRequest("PublishXml")
        {
            ["ParameterXml"] = $"<importexportxml><webresources>{webresources}</webresources></importexportxml>"
        };
        return service.ExecuteAsync(request, cancellationToken);
    }

    static async Task ExecuteBoundedParallelAsync<T>(IEnumerable<T> items,
        int maxParallelism,
        Func<T, Task> action,
        ProgressTask progressTask,
        CancellationToken cancellationToken)
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
                progressTask.Increment(1);
            }
            finally { gate.Release(); }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
