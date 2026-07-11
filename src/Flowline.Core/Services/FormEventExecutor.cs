using System.Security;
using System.ServiceModel;
using System.Xml.Linq;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

// U6: applies a FormEventSyncPlan (U5) — confirms unrecognized handlers, writes formxml, publishes per entity.
//
// Deviation from the plan's literal `ExecuteAsync(service, plan, force, ct)` signature: FormEventFormPlan
// carries FormId/EntityLogicalName/FormName/Event/desired sets but not the raw current formxml string
// needed to mutate via FormXmlEventSerializer. Rather than extend FormEventFormPlan (already committed by
// U5, and depended on by FormEventPlannerTests), ExecuteAsync also takes the originating FormEventSnapshot
// so it can look up DataverseForm.FormXml by (EntityLogicalName, FormName) — the same dictionary key the
// planner itself uses to build the plan.
public class FormEventExecutor(IAnsiConsole console)
{
    const int MaxParallelism = 8;
    const string SystemFormEntity = "systemform";

    public async Task ExecuteAsync(
        IOrganizationServiceAsync2 service,
        FormEventSnapshot snapshot,
        FormEventSyncPlan plan,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (plan.Forms.Count == 0)
            return;

        var removeUnrecognized = ResolveUnrecognizedHandling(plan, force);

        var failures = new List<(string Name, Exception Error)>();
        var formCount = plan.DistinctFormCount;

        await console.Progress().StartAsync(ctx =>
            ExecuteByEntityAsync(service, snapshot, plan, removeUnrecognized, failures,
                ctx.AddTask("Updating forms", maxValue: formCount), cancellationToken)).ConfigureAwait(false);

        console.Ok($"{formCount} form(s) updated");

        if (failures.Count > 0)
        {
            foreach (var (name, ex) in failures)
                console.Error($"'{name}' — {ex.Message}");
            throw new InvalidOperationException($"{failures.Count} form event operation(s) failed.");
        }
    }

    // Single confirmation for the whole push (not one per handler, per KTD6) — the result gates whether
    // every unrecognized handler across every form is removed (confirmed/forced) or kept (declined/no
    // unrecognized handlers at all). Everything else in the plan (new handlers, recognized-handler
    // updates, library changes, other forms) is unaffected by this decision.
    bool ResolveUnrecognizedHandling(FormEventSyncPlan plan, bool force)
    {
        var unrecognized = plan.Forms
            .SelectMany(f => f.UnrecognizedHandlers.Select(u => (Form: f, Handler: u.Handler)))
            .ToList();

        if (unrecognized.Count == 0)
            return true; // nothing to remove either way — Except() against an empty set is a no-op

        if (force)
            return true;

        if (!IsInteractive())
        {
            var lines = unrecognized.Select(u =>
                $"{u.Form.EntityLogicalName}/{u.Form.FormName}: {u.Handler.FunctionName} ({u.Handler.LibraryName})");
            throw new FlowlineException(ExitCode.ForceRequired,
                "Unrecognized form event handler(s) found — confirmation required but not in interactive mode. Use --force to proceed.\n"
                + string.Join("\n", lines));
        }

        console.Warning($"{unrecognized.Count} unrecognized handler(s) found on tracked forms:");
        foreach (var (form, handler) in unrecognized)
            console.Info($"  {form.EntityLogicalName}/{form.FormName}: {handler.FunctionName} ({handler.LibraryName})");

        return console.Confirm("Remove unrecognized handler(s) from form(s)?", false);
    }

    // Shares CiEnvironment's CI-detection with ConsoleHelper.IsInteractive (Flowline.Core can't reference
    // the CLI project's ConsoleHelper/FlowlineSettings), but checks the injected console's own profile
    // rather than the static AnsiConsole ambient instance — keeps this testable via a TestConsole without
    // mutating global state.
    bool IsInteractive() => !CiEnvironment.IsCi() && console.Profile.Capabilities.Interactive;

    async Task ExecuteByEntityAsync(
        IOrganizationServiceAsync2 service,
        FormEventSnapshot snapshot,
        FormEventSyncPlan plan,
        bool removeUnrecognized,
        List<(string Name, Exception Error)> failures,
        ProgressTask progressTask,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxParallelism);

        // One task per entity: update all of that entity's forms (bounded parallel, shared gate across
        // every entity), then publish that entity as soon as its own updates are done — not batched to the
        // end of the whole run (KTD9). Entities run independently, so a slow/failing entity never delays
        // another entity's publish.
        var entityTasks = plan.Forms
            .GroupBy(f => f.EntityLogicalName)
            .Select(async entityGroup =>
            {
                // A single form can appear as two plan entries (one per event — onLoad/onSave share the
                // same FormId, per the planner's (entity, form, event) grouping). Sub-group by FormId so
                // both events are folded onto one parse of the current formxml and written with a single
                // UpdateAsync — writing them separately would have each start from the pristine formxml
                // and the second write would clobber the first (last write wins).
                var updateTasks = entityGroup.GroupBy(f => f.FormId).Select(async formGroup =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        try
                        {
                            var formXml = BuildFormXml(snapshot, formGroup, removeUnrecognized);
                            var entity = new Entity(SystemFormEntity, formGroup.Key) { ["formxml"] = formXml };
                            await service.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
                        }
                        catch (FaultException<OrganizationServiceFault> ex)
                        {
                            lock (failures) failures.Add((formGroup.First().FormName, ex));
                        }
                        progressTask.Increment(1);
                    }
                    finally { gate.Release(); }
                });

                await Task.WhenAll(updateTasks).ConfigureAwait(false);

                try
                {
                    await PublishAsync(service, entityGroup.Key, cancellationToken).ConfigureAwait(false);
                }
                catch (FaultException<OrganizationServiceFault> ex)
                {
                    lock (failures) failures.Add((entityGroup.Key, ex));
                }
            });

        await Task.WhenAll(entityTasks).ConfigureAwait(false);
    }

    // formGroup holds every FormEventFormPlan for one FormId (one entry per touched event) — each event's
    // desired handlers are applied to the same xdoc, and libraries are unioned across events and applied
    // once (SetLibraries replaces the full <formLibraries> section, so applying it per-plan in sequence
    // would drop an earlier plan's libraries).
    static string BuildFormXml(FormEventSnapshot snapshot, IGrouping<Guid, FormEventFormPlan> formGroup, bool removeUnrecognized)
    {
        var first = formGroup.First();
        var dataverseForm = snapshot.Forms[(first.EntityLogicalName, first.FormName)];
        var xdoc = XDocument.Parse(dataverseForm.FormXml);

        var unionLibraries = new HashSet<FormLibraryEntry>();
        foreach (var formPlan in formGroup)
        {
            // Planner already folded UnrecognizedHandlers into DesiredHandlers (kept by default, R18) —
            // removing them here is the only place that decision is undone, once confirmed.
            var unrecognizedHandlers = formPlan.UnrecognizedHandlers.Select(u => u.Handler).ToHashSet();
            var desired = removeUnrecognized
                ? (IReadOnlySet<FormHandler>)formPlan.DesiredHandlers.Where(h => !unrecognizedHandlers.Contains(h)).ToHashSet()
                : formPlan.DesiredHandlers;

            FormXmlEventSerializer.SetHandlers(xdoc, formPlan.Event, desired);
            unionLibraries.UnionWith(formPlan.DesiredLibraries);
        }

        FormXmlEventSerializer.SetLibraries(xdoc, unionLibraries);

        return xdoc.ToString();
    }

    static Task PublishAsync(IOrganizationServiceAsync2 service, string entityLogicalName, CancellationToken cancellationToken)
    {
        var request = new OrganizationRequest("PublishXml")
        {
            ["ParameterXml"] = $"<importexportxml><entities><entity>{SecurityElement.Escape(entityLogicalName)}</entity></entities></importexportxml>"
        };
        return service.ExecuteAsync(request, cancellationToken);
    }
}
