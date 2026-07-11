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
        bool dryRun,
        bool cleanupOnly,
        CancellationToken cancellationToken = default)
    {
        if (plan.Forms.Count == 0)
            return;

        // R18b: preview everything the confirmation gate below would ask about — computed, never applied.
        // No UpdateAsync/PublishXml, no prompt, regardless of interactivity. PrintDryRunPreview's own
        // WritePlanReport(..., DryRun) call already shows this detail unconditionally (via console.Info),
        // so the dry-run path skips the Verbose-mode report below entirely rather than printing it twice.
        if (dryRun)
        {
            PrintDryRunPreview(snapshot, plan);
            return;
        }

        // Same per-form/per-handler/per-library detail WebResourceService's WritePlanReport shows for web
        // resources — printed under -v when actually applying (dry-run already showed it above).
        WritePlanReport(snapshot, plan, PlanReportMode.Verbose);

        var removeUnrecognized = ResolveUnrecognizedHandling(plan, force);

        var failures = new List<(string Name, Exception Error)>();
        var formCount = plan.DistinctFormCount;

        await console.Progress().StartAsync(ctx =>
            ExecuteByEntityAsync(service, snapshot, plan, removeUnrecognized, cleanupOnly, failures,
                ctx.AddTask("Updating forms", maxValue: formCount), cancellationToken)).ConfigureAwait(false);

        if (failures.Count > 0)
        {
            foreach (var (name, ex) in failures)
                console.Error($"'{name}' — {Markup.Escape(ex.Message)}");
            throw new InvalidOperationException($"{failures.Count} form event operation(s) failed.");
        }

        console.Ok($"{formCount} form(s) updated");
    }

    // Shared by the confirmation gate (R18) and the dry-run preview (R18b) so both list exactly the same
    // unrecognized handlers, in the same shape, and never drift apart.
    static List<(FormEventFormPlan Form, UnrecognizedHandler Unrecognized)> CollectUnrecognized(FormEventSyncPlan plan) =>
        plan.Forms.SelectMany(f => f.UnrecognizedHandlers.Select(u => (Form: f, Unrecognized: u))).ToList();

    // R18a: every surfaced handler shows the annotation the user could add instead of removing it. No
    // mention of "confirming below" here — this line also renders in the non-interactive throw message
    // (nothing "below" it — it's failing, not prompting) and the dry-run preview (which never prompts at
    // all); callers that DO follow with a confirm prompt add that context themselves.
    static string FormatUnrecognizedHandlerLine(FormEventFormPlan form, UnrecognizedHandler unrecognized) =>
        $"{form.EntityLogicalName}/{form.FormName}: {unrecognized.Handler.FunctionName} ({unrecognized.Handler.LibraryName})"
        + $" — to keep this, add {unrecognized.ProposedAnnotation} to {unrecognized.Handler.LibraryName}.";

    // R18b: same per-handler detail as ResolveUnrecognizedHandling's prompt, plus the full change report
    // (WritePlanReport) — computed against the pristine current formxml, nothing written.
    void PrintDryRunPreview(FormEventSnapshot snapshot, FormEventSyncPlan plan)
    {
        var unrecognized = CollectUnrecognized(plan);
        if (unrecognized.Count > 0)
        {
            console.Warning($"{unrecognized.Count} unrecognized handler(s) found on tracked forms:");
            foreach (var u in unrecognized)
                console.Info($"  {FormatUnrecognizedHandlerLine(u.Form, u.Unrecognized)}");
        }

        WritePlanReport(snapshot, plan, PlanReportMode.DryRun);
    }

    enum PlanReportMode { Verbose, DryRun }

    // Mirrors WebResourceService.WritePlanReport's shape: a summary line plus one section per change kind,
    // each listing the specific forms/handlers/libraries affected — not just aggregate counts. Verbose mode
    // prints via console.Verbose (only visible under -v); DryRun mode always prints (via console.Info) and
    // additionally emits the final "Dry run: ... Run without --dry-run to apply." line.
    void WritePlanReport(FormEventSnapshot snapshot, FormEventSyncPlan plan, PlanReportMode mode)
    {
        // console.Verbose escapes markup internally (VerboseRenderable); console.Info does not — escaping
        // here too would double-escape under Verbose but is required under DryRun, since form/function/
        // library names come from Dataverse-controlled data and can contain markup-special characters.
        Action<string> line = mode == PlanReportMode.Verbose
            ? msg => console.Verbose(msg)
            : msg => console.Info(Markup.Escape(msg));

        var handlerAdded = new List<(string Entity, string Form, FormEventType Event, FormHandler Handler)>();
        var handlerUpdated = new List<(string Entity, string Form, FormEventType Event, FormHandler Handler)>();
        var handlerRemoved = new List<(string Entity, string Form, FormEventType Event, FormHandler Handler)>();
        var libraryAdded = new List<(string Entity, string Form, FormLibraryEntry Library)>();
        var libraryRemoved = new List<(string Entity, string Form, FormLibraryEntry Library)>();

        // Groups by FormId so a form with both onLoad/onSave changes parses its formxml once, and so the
        // form-wide library diff (identical across every plan entry for the same form, per the planner) is
        // computed once rather than once per event — mirrors BuildFormXml's grouping.
        foreach (var formGroup in plan.Forms.GroupBy(f => f.FormId))
        {
            var first = formGroup.First();
            var dataverseForm = snapshot.Forms[(first.EntityLogicalName, first.FormName)];
            var xdoc = XDocument.Parse(dataverseForm.FormXml);

            foreach (var formPlan in formGroup)
            {
                var current = FormXmlEventSerializer.GetHandlers(xdoc, formPlan.Event);
                var (added, updated, removed) = FormHandlerDiffer.DiffDetailed(formPlan.DesiredHandlers, current);
                handlerAdded.AddRange(added.Select(h => (formPlan.EntityLogicalName, formPlan.FormName, formPlan.Event, h)));
                handlerUpdated.AddRange(updated.Select(h => (formPlan.EntityLogicalName, formPlan.FormName, formPlan.Event, h)));
                handlerRemoved.AddRange(removed.Select(h => (formPlan.EntityLogicalName, formPlan.FormName, formPlan.Event, h)));
            }

            var currentLibraries = FormXmlEventSerializer.GetLibraries(xdoc);
            var desiredLibraries = first.DesiredLibraries;
            libraryAdded.AddRange(desiredLibraries.Where(l => !currentLibraries.Contains(l))
                .Select(l => (first.EntityLogicalName, first.FormName, l)));
            libraryRemoved.AddRange(currentLibraries.Where(l => !desiredLibraries.Contains(l))
                .Select(l => (first.EntityLogicalName, first.FormName, l)));
        }

        var counts = JoinCounts(
            (handlerAdded.Count, "handler(s) added"), (handlerUpdated.Count, "handler(s) updated"), (handlerRemoved.Count, "handler(s) removed"),
            (libraryAdded.Count, "library(ies) added"), (libraryRemoved.Count, "library(ies) removed"));
        line($"  Summary: {counts}");

        WriteHandlerSection(line, "Handlers added", handlerAdded);
        WriteHandlerSection(line, "Handlers updated", handlerUpdated);
        WriteHandlerSection(line, "Handlers removed", handlerRemoved);
        WriteLibrarySection(line, "Libraries added", libraryAdded);
        WriteLibrarySection(line, "Libraries removed", libraryRemoved);

        if (mode != PlanReportMode.DryRun)
            return;

        console.Ok($"Dry run: {plan.DistinctFormCount} form(s) with pending changes — {counts}. Run without --dry-run to apply.");
    }

    static string JoinCounts(params (int Count, string Label)[] parts)
    {
        var nonZero = parts.Where(p => p.Count > 0).Select(p => $"{p.Count} {p.Label}").ToList();
        return nonZero.Count > 0 ? string.Join(", ", nonZero) : "no changes";
    }

    static void WriteHandlerSection(Action<string> line, string label, List<(string Entity, string Form, FormEventType Event, FormHandler Handler)> items)
    {
        if (items.Count == 0)
            return;

        line($"  {label} ({items.Count})");
        foreach (var (entity, form, evt, handler) in items.OrderBy(i => i.Entity).ThenBy(i => i.Form, StringComparer.OrdinalIgnoreCase))
            line($"    - {entity}/{form} ({evt}): {handler.FunctionName} ({handler.LibraryName})");
    }

    static void WriteLibrarySection(Action<string> line, string label, List<(string Entity, string Form, FormLibraryEntry Library)> items)
    {
        if (items.Count == 0)
            return;

        line($"  {label} ({items.Count})");
        foreach (var (entity, form, library) in items.OrderBy(i => i.Entity).ThenBy(i => i.Form, StringComparer.OrdinalIgnoreCase))
            line($"    - {entity}/{form}: {library.Name}");
    }

    // Single confirmation for the whole push (not one per handler, per KTD6) — the result gates whether
    // every unrecognized handler across every form is removed (confirmed/forced) or kept (declined/no
    // unrecognized handlers at all). Everything else in the plan (new handlers, recognized-handler
    // updates, library changes, other forms) is unaffected by this decision.
    bool ResolveUnrecognizedHandling(FormEventSyncPlan plan, bool force)
    {
        var unrecognized = CollectUnrecognized(plan);

        if (unrecognized.Count == 0)
            return true; // nothing to remove either way — Except() against an empty set is a no-op

        if (force)
            return true;

        if (!IsInteractive())
        {
            var lines = unrecognized.Select(u => FormatUnrecognizedHandlerLine(u.Form, u.Unrecognized));
            throw new FlowlineException(ExitCode.ForceRequired,
                "Unrecognized form event handler(s) found — confirmation required but not in interactive mode. Use --force to proceed.\n"
                + string.Join("\n", lines));
        }

        console.Warning($"{unrecognized.Count} unrecognized handler(s) found on tracked forms — confirming below removes them:");
        foreach (var u in unrecognized)
            console.Info($"  {FormatUnrecognizedHandlerLine(u.Form, u.Unrecognized)}");

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
        bool cleanupOnly,
        List<(string Name, Exception Error)> failures,
        ProgressTask progressTask,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxParallelism);
        // Dataverse serializes PublishXml org-wide — a second concurrent publish fails with "Cannot start
        // another [Publish] because there is a previous [Publish] running at this moment" (confirmed live).
        // Updates stay parallel across entities (KTD9); only the publish call itself is serialized.
        using var publishGate = new SemaphoreSlim(1);

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
                            var formXml = BuildFormXml(snapshot, formGroup, removeUnrecognized, cleanupOnly);
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

                await publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await PublishAsync(service, entityGroup.Key, cancellationToken).ConfigureAwait(false);
                }
                catch (FaultException<OrganizationServiceFault> ex)
                {
                    lock (failures) failures.Add((entityGroup.Key, ex));
                }
                finally { publishGate.Release(); }
            });

        await Task.WhenAll(entityTasks).ConfigureAwait(false);
    }

    // formGroup holds every FormEventFormPlan for one FormId (one entry per touched event) — each event's
    // desired handlers are applied to the same xdoc, and libraries are unioned across events and applied
    // once (SetLibraries replaces the full <formLibraries> section, so applying it per-plan in sequence
    // would drop an earlier plan's libraries).
    static string BuildFormXml(FormEventSnapshot snapshot, IGrouping<Guid, FormEventFormPlan> formGroup, bool removeUnrecognized, bool cleanupOnly)
    {
        var first = formGroup.First();
        var dataverseForm = snapshot.Forms[(first.EntityLogicalName, first.FormName)];
        var xdoc = XDocument.Parse(dataverseForm.FormXml);

        // cleanupOnly (KTD12's phase-1 cleanup pass, U7): only write removals that are already safe — never
        // a brand-new library reference, since at this point in the three-phase sequence the web resource
        // it points at may not exist in Dataverse yet. Intersecting desired against the form's pristine
        // (pre-mutation) current state achieves that with no extra Dataverse lookup: anything
        // desired-but-not-current necessarily needs a library that isn't on the form yet, so it's excluded
        // here and deferred to the later registration-phase call. Read once, before any SetHandlers call
        // below mutates the doc.
        var currentLibraries = cleanupOnly ? FormXmlEventSerializer.GetLibraries(xdoc) : null;

        var unionLibraries = new HashSet<FormLibraryEntry>();
        foreach (var formPlan in formGroup)
        {
            // Planner already folded UnrecognizedHandlers into DesiredHandlers (kept by default, R18) —
            // removing them here is the only place that decision is undone, once confirmed.
            var unrecognizedHandlers = formPlan.UnrecognizedHandlers.Select(u => u.Handler).ToHashSet();
            IEnumerable<FormHandler> desired = removeUnrecognized
                ? formPlan.DesiredHandlers.Where(h => !unrecognizedHandlers.Contains(h))
                : formPlan.DesiredHandlers;

            if (cleanupOnly)
            {
                // Read before this iteration's SetHandlers call below — each event's Handlers live in their
                // own <event> element, so an earlier iteration in this loop (a different event) never
                // touches this one.
                var currentHandlers = FormXmlEventSerializer.GetHandlers(xdoc, formPlan.Event);
                desired = desired.Where(currentHandlers.Contains);
            }

            FormXmlEventSerializer.SetHandlers(xdoc, formPlan.Event, desired.ToHashSet());

            var desiredLibraries = cleanupOnly
                ? formPlan.DesiredLibraries.Where(currentLibraries!.Contains)
                : formPlan.DesiredLibraries;
            unionLibraries.UnionWith(desiredLibraries);
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
