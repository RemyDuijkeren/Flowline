using System.Security;
using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.WebResources;

public class WebResourceExecutor(IAnsiConsole console)
{
    const int MaxParallelism = 8;
    const int WebResourceComponentType = 61;

    public async Task ExecuteAsync(
        IOrganizationServiceAsync2 service,
        WebResourceSyncPlan plan,
        string webresourceRoot,
        bool publishAfterSync,
        bool save,
        CancellationToken cancellationToken = default)
    {
        var publishIds = new List<Guid>();
        var failures = new List<(WebResourcePlanAction Action, Exception Error)>();

        RenderSkips(console, plan.Skips, webresourceRoot);

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
        await RunPhaseAsync(plan.Updates, "Updating web resources", "updated",
            a => $"Web resource '{a.Name}' updated ({a.Reason})", failures,
            async action =>
            {
                await service.UpdateAsync(action.Entity!, cancellationToken).ConfigureAwait(false);
                lock (publishIds) publishIds.Add(action.Entity!.Id);
            }, cancellationToken).ConfigureAwait(false);

        // Add web resources to solution — parallel, so lock needed for progress
        await RunPhaseAsync(plan.AddsToSolution, "Adding web resources to solution", "added to solution",
            a => $"Web resource '{a.Name}' added to solution", failures,
            action => AddToSolutionAsync(service, action.Id!.Value, action.SolutionName!, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (!save)
        {
            // R5/R7: warn before the delete/remove actually runs — finding a dependent never blocks
            // either action (KD3), it only tells the operator what still points at the resource.
            // Skipped entirely under --no-delete (the `else` branch below) — nothing is at risk there,
            // so a "still has dependents" warning would be reporting on an action that never happens.
            RenderDependentWarnings(console, plan.Deletes.Concat(plan.RemovesFromSolution));

            // Delete web resources — parallel, so lock needed for progress
            await RunPhaseAsync(plan.Deletes, "Deleting web resources", "deleted",
                a => $"Web resource '{a.Name}' deleted", failures,
                action => service.DeleteAsync("webresource", action.Id!.Value, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            // Remove web resources from solution — parallel, so lock needed for progress
            await RunPhaseAsync(plan.RemovesFromSolution, "Removing web resources from solution", "removed from solution",
                a => $"Web resource '{a.Name}' removed from solution ({a.Reason})", failures,
                action => RemoveFromSolutionAsync(service, action.Id!.Value, action.SolutionName!, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var a in plan.Deletes) console.Skip($"Web resource '{a.Name}' not in source — kept (--no-delete)");
            if (plan.Deletes.Count > 0) console.Skip($"{plan.Deletes.Count} web resource(s) not in source — kept (--no-delete)");
            foreach (var a in plan.RemovesFromSolution) console.Skip($"Web resource '{a.Name}' kept — {a.Reason} (--no-delete)");
            if (plan.RemovesFromSolution.Count > 0) console.Skip($"{plan.RemovesFromSolution.Count} web resource(s) kept (--no-delete)");
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
            // R8: reuse the dependents already resolved during planning (KD1) — a delete/remove that
            // still faults on Dataverse's dependency check gets its held dependents rendered alongside
            // the failure, no second RetrieveDependenciesForDelete request.
            foreach (var (action, ex) in failures)
            {
                console.Error($"'{action.Name}' — {ex.Message}");
                if (action.Dependents is { Count: > 0 } dependents)
                    RenderDependentLines(console, dependents);
            }
            throw new InvalidOperationException($"{failures.Count} web resource operation(s) failed.");
        }
    }

    // R8: a foreign-owned resource kept only because a local file references it via
    // // flowline:depends isn't a neutral skip — the file sitting in the web resource folder is
    // dead weight the user will keep re-creating on every push. Name the folder that was actually
    // resolved (webresourceRoot), never a hardcoded "dist/" — --webresources overrides it.
    // Every path that surfaces a plan's skips renders them through here. A reference-only skip is the
    // one case that warns rather than reporting neutrally, and the push that most needs that warning —
    // the file already synced, nothing else to do — never reaches the executor at all, because
    // TotalChanges excludes Skips and WebResourceService returns early. Two renderers drifted once
    // already; one shared method is what stops a third.
    internal static void RenderSkips(IAnsiConsole console, IEnumerable<WebResourcePlanAction> skips, string webresourceRoot)
    {
        foreach (var a in skips)
        {
            if (a.Reason == WebResourcePlanner.ReferencedNotOwnedReason)
                WarnReferencedNotOwned(console, a, webresourceRoot);
            else
                console.Skip($"Web resource '{a.Name}' kept ({a.Reason})");
        }
    }

    static void WarnReferencedNotOwned(IAnsiConsole console, WebResourcePlanAction a, string webresourceRoot)
    {
        // Escape every interpolated value: console.Warning/MarkupLine parse Spectre markup, and a
        // folder path is free-form user input (--webresources) that may legally contain '['.
        var folder = Markup.Escape(webresourceRoot.Replace('\\', '/').TrimEnd('/') + "/");
        console.Warning($"'{Markup.Escape(a.Name)}' is owned by '{Markup.Escape(a.OwningSolutions ?? "another solution")}' — not pushed.");
        console.MarkupLine($"  The dependency's declared, so the file isn't needed in {folder}. Remove it.");
    }

    // R4/R5/R7/R11: dependents were already looked up (WebResourceService.ApplyDependencyChecksAsync)
    // and hung off each Deletes/RemovesFromSolution entry before this ever runs — finding one never
    // blocks the delete or the removal, it only warns. Two call sites reuse this, mirroring RenderSkips:
    // here (the real run) and WebResourceService's dry-run preview, so both read the same warning.
    internal static void RenderDependentWarnings(IAnsiConsole console, IEnumerable<WebResourcePlanAction> actions)
    {
        foreach (var a in actions)
        {
            if (a.Dependents is { Count: > 0 } dependents)
                WarnDependents(console, a, dependents);
            else if (a.Dependents is null)
                // R11: a faulted lookup must read as "unverified", never silently as "no dependents".
                console.Warning($"Couldn't check '{Markup.Escape(a.Name)}' for dependents.");
        }
    }

    // KTD5: the two RemovesFromSolution reasons carry different risk, so each closes with its own
    // line — both keep the record, but only StillInOtherSolutionReason leaves it held by a solution
    // that might not ship downstream. The risk goes on its own line rather than into the header:
    // one thought per line, and a header carrying it wraps on an 80-column terminal.
    static void WarnDependents(IAnsiConsole console, WebResourcePlanAction a, IReadOnlyList<WebResourceDependent> dependents)
    {
        var verb = a.Action == WebResourceAction.Delete ? "deleting anyway" : "removing it anyway";
        console.Warning($"'{Markup.Escape(a.Name)}' still has dependents — {verb}:");

        RenderDependentLines(console, dependents);

        if (a.Action == WebResourceAction.Delete)
            return;

        console.MarkupLine(a.Reason == WebResourcePlanner.OwnedByManagedSolutionReason
            ? "  A managed solution holds it too, so it should ship downstream."
            : "  Only another unmanaged solution holds it. That may not ship downstream.");
    }

    // Shared by WarnDependents (U4) and the failure loop in ExecuteAsync (U5, R8) — same dependent
    // line shape either way, so one list-of-dependents renderer serves both.
    static void RenderDependentLines(IAnsiConsole console, IReadOnlyList<WebResourceDependent> dependents)
    {
        foreach (var d in dependents)
            console.MarkupLine(d.Name is not null
                ? $"  - {Markup.Escape(d.TypeLabel)} '{Markup.Escape(d.Name)}'"
                : $"  - {Markup.Escape(d.TypeLabel)} {d.ObjectId}");
    }

    // Shared by the Updates/AddsToSolution/Deletes/RemovesFromSolution phases — each is a
    // progress-tracked bounded-parallel run over a WebResourcePlanAction list, differing only in the
    // progress label, the summary verb, the per-item verbose message, and the operation itself.
    async Task RunPhaseAsync(
        List<WebResourcePlanAction> actions,
        string progressLabel,
        string verb,
        Func<WebResourcePlanAction, string> verboseMessage,
        List<(WebResourcePlanAction Action, Exception Error)> failures,
        Func<WebResourcePlanAction, Task> perform,
        CancellationToken cancellationToken)
    {
        if (actions.Count == 0) return;

        await console.Progress().StartAsync(ctx =>
            ExecuteBoundedParallelAsync(actions, MaxParallelism, async action =>
            {
                try { await perform(action).ConfigureAwait(false); }
                catch (FaultException<OrganizationServiceFault> ex) { lock (failures) failures.Add((action, ex)); }
            }, ctx.AddTask(progressLabel, maxValue: actions.Count), cancellationToken)).ConfigureAwait(false);

        foreach (var a in actions) console.Verbose(verboseMessage(a));
        console.Ok($"{actions.Count} web resource(s) {verb}");
    }

    async Task<List<Guid>> ExecuteCreatesAsync(IOrganizationServiceAsync2 service,
        IEnumerable<WebResourcePlanAction> creates,
        List<(WebResourcePlanAction Action, Exception Error)> failures,
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
            catch (FaultException<OrganizationServiceFault> ex) { failures.Add((action, ex)); }
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
