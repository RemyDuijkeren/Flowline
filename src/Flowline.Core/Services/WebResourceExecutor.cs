using System.Security;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class WebResourceExecutor(IAnsiConsole output, FlowlineRuntimeOptions opt)
{
    const int MaxParallelism = 8;
    const int WebResourceComponentType = 61;

    public async Task ExecuteAsync(
        IOrganizationServiceAsync2 service,
        WebResourceSyncPlan plan,
        bool publishAfterSync,
        bool save,
        CancellationToken cancellationToken = default,
        ProgressTask? createsTask = null,
        ProgressTask? updatesTask = null,
        ProgressTask? removesTask = null,
        ProgressTask? deletesTask = null,
        ProgressTask? publishTask = null)
    {
        var publishIds = new List<Guid>();
        var progressLock = new object();

        // Create web resources
        var createdIds = await ExecuteCreatesAsync(service, plan.Creates.Values, cancellationToken, createsTask, progressLock).ConfigureAwait(false);
        publishIds.AddRange(createdIds);

        await Task.WhenAll(
            ExecuteBoundedParallelAsync(plan.Updates.Values, MaxParallelism, async action =>
            {
                await service.UpdateAsync(action.Entity!, cancellationToken).ConfigureAwait(false);
                lock (publishIds) publishIds.Add(action.Entity!.Id);
            }, cancellationToken, updatesTask, progressLock)).ConfigureAwait(false);

        if (!save)
        {
            await Task.WhenAll(
                ExecuteBoundedParallelAsync(plan.RemovesFromSolution.Values, MaxParallelism,
                    action => RemoveFromSolutionAsync(service, action.Id!.Value, action.SolutionName!, cancellationToken), cancellationToken, removesTask, progressLock),
                ExecuteBoundedParallelAsync(plan.Deletes.Values, MaxParallelism,
                    action => service.DeleteAsync("webresource", action.Id!.Value, cancellationToken), cancellationToken, deletesTask, progressLock)).ConfigureAwait(false);
        }

        WriteSummary(plan, save);

        if (publishAfterSync && publishIds.Count > 0)
        {
            await PublishAsync(service, publishIds.Distinct().ToList(), cancellationToken).ConfigureAwait(false);
            IncrementProgress(publishTask, progressLock, publishIds.Count);
            output.Info($"[green]{publishIds.Count} web resource(s) published[/]");
        }
    }

    async Task<List<Guid>> ExecuteCreatesAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<WebResourcePlanAction> creates,
        CancellationToken cancellationToken,
        ProgressTask? progressTask,
        object progressLock)
    {
        var ids = new List<Guid>();
        await ExecuteBoundedParallelAsync(creates, MaxParallelism, async action =>
        {
            var response = (CreateResponse)await service.ExecuteAsync(
                new CreateRequest { Target = action.Entity!, ["SolutionUniqueName"] = action.SolutionName },
                cancellationToken).ConfigureAwait(false);

            lock (ids) ids.Add(response.id);
        }, cancellationToken, progressTask, progressLock).ConfigureAwait(false);

        return ids;
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

    void WriteSummary(WebResourceSyncPlan plan, bool save)
    {
        foreach (var s in plan.Skips.Values) output.Skip($"Web resource '{s.Name}' kept ({s.Reason})");

        foreach (var s in plan.Creates.Keys) output.Verbose($"Web resource '{s}' created", opt);
        if (plan.Creates.Count > 0) output.Info($"[green]{plan.Creates.Count} web resource(s) created[/]");

        foreach (var s in plan.Updates.Keys) output.Verbose($"Web resource '{s}' updated", opt);
        if (plan.Updates.Count > 0) output.Info($"[green]{plan.Updates.Count} web resource(s) updated[/]");

        if (!save)
        {
            foreach (var s in plan.Deletes.Keys) output.Verbose($"Web resource '{s}' deleted", opt);
            if (plan.Deletes.Count > 0) output.Info($"[green]{plan.Deletes.Count} web resource(s) deleted[/]");

            foreach (var s in plan.RemovesFromSolution.Keys) output.Verbose($"Web resource '{s}' removed from solution", opt);
            if (plan.RemovesFromSolution.Count > 0) output.Info($"[green]{plan.RemovesFromSolution.Count} web resource(s) removed from solution[/]");
        }
        else
        {
            foreach (var name in plan.Deletes.Keys) output.Skip($"Web resource '{name}' not in source — kept (--save)");
            if (plan.Deletes.Count > 0)output.Skip($"{plan.Deletes.Count} web resource(s) not in source — kept (--save)");
            foreach (var name in plan.RemovesFromSolution.Keys) output.Skip($"Web resource '{name}' still in other solution — kept (--save)");
            if (plan.RemovesFromSolution.Count > 0) output.Skip($"{plan.RemovesFromSolution.Count} web resource(s) still in other solution — kept (--save)");
        }
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

    static void IncrementProgress(ProgressTask? progressTask, object? progressLock, int value = 1)
    {
        if (progressTask == null)
            return;

        if (progressLock == null)
        {
            progressTask.Increment(value);
            return;
        }

        lock (progressLock)
            progressTask.Increment(value);
    }
}
