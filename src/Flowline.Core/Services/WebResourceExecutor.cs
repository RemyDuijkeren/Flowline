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
        var failures = new List<(string Name, Exception Error)>();
        var progressLock = new object();

        // Create web resources — sequential, so no lock needed for progress
        var createdIds = await ExecuteCreatesAsync(service, plan.Creates, failures, cancellationToken, createsTask, progressLock).ConfigureAwait(false);
        publishIds.AddRange(createdIds);

        await Task.WhenAll(
            ExecuteBoundedParallelAsync(plan.Updates, MaxParallelism, async action =>
            {
                try
                {
                    await service.UpdateAsync(action.Entity!, cancellationToken).ConfigureAwait(false);
                    lock (publishIds) publishIds.Add(action.Entity!.Id);
                }
                catch (Exception ex) { lock (failures) failures.Add((action.Name, ex)); }
            }, cancellationToken, updatesTask, progressLock)).ConfigureAwait(false);

        if (!save)
        {
            await Task.WhenAll(
                ExecuteBoundedParallelAsync(plan.RemovesFromSolution, MaxParallelism, async action =>
                {
                    try { await RemoveFromSolutionAsync(service, action.Id!.Value, action.SolutionName!, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) { lock (failures) failures.Add((action.Name, ex)); }
                }, cancellationToken, removesTask, progressLock),
                ExecuteBoundedParallelAsync(plan.Deletes, MaxParallelism, async action =>
                {
                    try { await service.DeleteAsync("webresource", action.Id!.Value, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) { lock (failures) failures.Add((action.Name, ex)); }
                }, cancellationToken, deletesTask, progressLock)).ConfigureAwait(false);
        }

        WriteSummary(plan, save);

        if (publishAfterSync && publishIds.Count > 0)
        {
            await PublishAsync(service, publishIds.Distinct().ToList(), cancellationToken).ConfigureAwait(false);
            IncrementProgress(publishTask, progressLock, publishIds.Count);
            output.Info($"[green]{publishIds.Count} web resource(s) published[/]");
        }

        if (failures.Count > 0)
        {
            foreach (var (name, ex) in failures)
                output.Error($"'{name}' — {ex.Message}");
            throw new InvalidOperationException($"{failures.Count} web resource operation(s) failed.");
        }
    }

    async Task<List<Guid>> ExecuteCreatesAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<WebResourcePlanAction> creates,
        List<(string Name, Exception Error)> failures,
        CancellationToken cancellationToken,
        ProgressTask? progressTask,
        object? progressLock)
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
            catch (Exception ex) { failures.Add((action.Name, ex)); }
            IncrementProgress(progressTask, progressLock);
        }

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
        foreach (var a in plan.Skips) output.Skip($"Web resource '{a.Name}' kept ({a.Reason})");

        foreach (var a in plan.Creates) output.Verbose($"Web resource '{a.Name}' created", opt);
        if (plan.Creates.Count > 0) output.Info($"[green]{plan.Creates.Count} web resource(s) created[/]");

        foreach (var a in plan.Updates) output.Verbose($"Web resource '{a.Name}' updated", opt);
        if (plan.Updates.Count > 0) output.Info($"[green]{plan.Updates.Count} web resource(s) updated[/]");

        if (!save)
        {
            foreach (var a in plan.Deletes) output.Verbose($"Web resource '{a.Name}' deleted", opt);
            if (plan.Deletes.Count > 0) output.Info($"[green]{plan.Deletes.Count} web resource(s) deleted[/]");

            foreach (var a in plan.RemovesFromSolution) output.Verbose($"Web resource '{a.Name}' removed from solution", opt);
            if (plan.RemovesFromSolution.Count > 0) output.Info($"[green]{plan.RemovesFromSolution.Count} web resource(s) removed from solution[/]");
        }
        else
        {
            foreach (var a in plan.Deletes) output.Skip($"Web resource '{a.Name}' not in source — kept (--save)");
            if (plan.Deletes.Count > 0) output.Skip($"{plan.Deletes.Count} web resource(s) not in source — kept (--save)");
            foreach (var a in plan.RemovesFromSolution) output.Skip($"Web resource '{a.Name}' still in other solution — kept (--save)");
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
