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
    // OrganizationServiceFault.ErrorCode for a RowVersion mismatch under ConcurrencyBehavior.IfRowVersionMatches.
    const int ConcurrencyVersionMismatchErrorCode = -2147088254;

    enum ChangeKind { Added, Updated, Removed }

    // Attribute is non-null only for OnChange — carried through so the change report can distinguish two
    // onchange attributes that happen to share a FunctionName+LibraryName (FormEventHandler's identity key).
    readonly record struct HandlerChange(string Entity, string Form, FormEventType Event, string? Attribute, ChangeKind Kind, FormEventHandler Handler);
    readonly record struct LibraryChange(string Entity, string Form, ChangeKind Kind, FormLibrary Library);

    public async Task ExecuteAsync(
        IOrganizationServiceAsync2 service,
        FormEventSnapshot snapshot,
        FormEventSyncPlan plan,
        bool force,
        bool dryRun,
        bool cleanupOnly,
        bool publishAfterSync = true,
        CancellationToken cancellationToken = default)
    {
        if (plan.Forms.Count == 0)
            return;

        // R18b: preview everything the confirmation gate below would ask about — computed, never applied.
        // No UpdateAsync/PublishXml, no prompt, regardless of interactivity. Reachable only for the
        // registration pass — FormEventService.SyncAsync short-circuits before this point when
        // dryRun && cleanupOnly — so BuildFormXml is always called unnarrowed (cleanupOnly: false) here,
        // reflecting the eventual full outcome rather than a phase-specific slice.
        if (dryRun)
        {
            PrintDryRunPreview(snapshot, plan, force);
            return;
        }

        var removeUnrecognized = ResolveUnrecognizedHandling(plan, force);

        // Single source of truth for both what gets written and what gets reported — computed once, up
        // front. This lets a phase with nothing to actually do (most commonly cleanup, which is
        // removals-only — a form can sit in plan.Forms purely for a registration-only change, e.g. a
        // brand-new handler) skip the progress bar entirely, instead of rendering an empty 0->100% bar
        // for zero real work (confirmed live).
        var formBuilds = plan.Forms.GroupBy(f => f.FormId)
            .ToDictionary(g => g.Key, g => BuildFormXml(snapshot, g, removeUnrecognized, cleanupOnly));

        if (formBuilds.Values.All(b => b.HandlerChanges.Count == 0 && b.LibraryChanges.Count == 0))
            return;

        var failures = new List<(string Name, Exception Error)>();
        var appliedHandlerChanges = new List<HandlerChange>();
        var appliedLibraryChanges = new List<LibraryChange>();
        var changesLock = new object();

        var formCount = plan.DistinctFormCount;
        // Progress previously only counted per-form UpdateAsync completions, so it sat frozen at 100% while
        // the (serialized, sometimes multi-second) per-entity PublishXml calls still ran afterward —
        // reported live as an apparent hang with no spinner. One extra unit per distinct entity accounts
        // for that publish step too.
        var entityCount = plan.Forms.Select(f => f.EntityLogicalName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // Phase-aware label, mirroring WebResourceExecutor's per-operation naming ("Creating web
        // resources", "Deleting web resources", ...) rather than one generic combined label: cleanup is
        // removals-only, registration is everything else.
        var progressLabel = cleanupOnly ? "Cleaning forms" : "Updating forms";
        await console.Progress().StartAsync(ctx =>
            ExecuteByEntityAsync(service, plan, formBuilds, publishAfterSync, failures,
                appliedHandlerChanges, appliedLibraryChanges, changesLock,
                ctx.AddTask(progressLabel, maxValue: formCount + entityCount), cancellationToken)).ConfigureAwait(false);

        if (failures.Count > 0)
        {
            foreach (var (name, ex) in failures)
                console.Error($"'{name}' — {Markup.Escape(ex.Message)}");
            throw new InvalidOperationException($"{failures.Count} form event operation(s) failed.");
        }

        var counts = WriteChangeReport(appliedHandlerChanges, appliedLibraryChanges, PlanReportMode.Verbose);
        console.Ok(counts);
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
        $"'{form.FormName}' ({form.EntityLogicalName}): {unrecognized.Handler.FunctionName} ({unrecognized.Handler.LibraryName})"
        + $"{Environment.NewLine}      — to keep this, add to the file:"
        + $"{Environment.NewLine}        {unrecognized.ProposedAnnotation}";

    // R18b: same per-handler detail as ResolveUnrecognizedHandling's prompt, plus the full change report —
    // computed against the pristine current formxml via the same BuildFormXml the real write uses, so the
    // preview can never drift from what applying it would actually do. Nothing written.
    void PrintDryRunPreview(FormEventSnapshot snapshot, FormEventSyncPlan plan, bool force)
    {
        var unrecognized = CollectUnrecognized(plan);
        if (unrecognized.Count > 0)
        {
            console.Warning($"{unrecognized.Count} unrecognized handler(s) found on tracked forms:");
            foreach (var u in unrecognized)
                console.Info($"  {FormatUnrecognizedHandlerLine(u.Form, u.Unrecognized)}");
        }

        // Mirrors ResolveUnrecognizedHandling: --force always removes them (no prompt), so the preview
        // must reflect that deterministically. Without --force, a real interactive run's outcome depends
        // on the user's live answer, which dry-run can't know in advance — showing them as kept is the
        // same conservative default the non-force case already used.
        var handlerChanges = new List<HandlerChange>();
        var libraryChanges = new List<LibraryChange>();
        foreach (var formGroup in plan.Forms.GroupBy(f => f.FormId))
        {
            var (_, _, hc, lc) = BuildFormXml(snapshot, formGroup, removeUnrecognized: force, cleanupOnly: false);
            handlerChanges.AddRange(hc);
            libraryChanges.AddRange(lc);
        }

        var counts = WriteChangeReport(handlerChanges, libraryChanges, PlanReportMode.DryRun);
        console.Ok($"Dry run: {plan.DistinctFormCount} form(s) with pending changes — {counts}. Run without --dry-run to apply.");
    }

    // Mirrors WebResourceService.WritePlanReport's shape: a summary line plus one section per change kind,
    // each listing the specific forms/handlers/libraries affected — not just aggregate counts. Verbose mode
    // prints via console.Verbose (only visible under -v); DryRun mode always prints (via console.Info).
    // Returns the joined counts string so callers can reuse it for their own always-visible summary line.
    string WriteChangeReport(List<HandlerChange> handlerChanges, List<LibraryChange> libraryChanges, PlanReportMode mode)
    {
        // console.Verbose escapes markup internally (VerboseRenderable); console.Info does not — escaping
        // here too would double-escape under Verbose but is required under DryRun, since form/function/
        // library names come from Dataverse-controlled data and can contain markup-special characters.
        Action<string> line = mode == PlanReportMode.Verbose
            ? msg => console.Verbose(msg)
            : msg => console.Info(Markup.Escape(msg));

        var handlerAdded = handlerChanges.Where(c => c.Kind == ChangeKind.Added).ToList();
        var handlerUpdated = handlerChanges.Where(c => c.Kind == ChangeKind.Updated).ToList();
        var handlerRemoved = handlerChanges.Where(c => c.Kind == ChangeKind.Removed).ToList();
        var libraryAdded = libraryChanges.Where(c => c.Kind == ChangeKind.Added).ToList();
        var libraryRemoved = libraryChanges.Where(c => c.Kind == ChangeKind.Removed).ToList();

        var counts = PlanReportFormatting.JoinCounts(
            (handlerAdded.Count, "handler(s) added"), (handlerUpdated.Count, "handler(s) updated"), (handlerRemoved.Count, "handler(s) removed"),
            (libraryAdded.Count, "library(ies) added"), (libraryRemoved.Count, "library(ies) removed"));
        line($"  Summary: {counts}");

        WriteHandlerSection(line, "Handlers added", handlerAdded);
        WriteHandlerSection(line, "Handlers updated", handlerUpdated);
        WriteHandlerSection(line, "Handlers removed", handlerRemoved);
        WriteLibrarySection(line, "Libraries added", libraryAdded);
        WriteLibrarySection(line, "Libraries removed", libraryRemoved);

        return counts;
    }

    static void WriteHandlerSection(Action<string> line, string label, List<HandlerChange> items)
    {
        if (items.Count == 0)
            return;

        line($"  {label} ({items.Count})");
        foreach (var c in items.OrderBy(i => i.Entity).ThenBy(i => i.Form, StringComparer.OrdinalIgnoreCase))
        {
            var eventLabel = c.Attribute is null ? c.Event.ToString() : $"{c.Event}:{c.Attribute}";
            line($"    - {c.Entity}/{c.Form} ({eventLabel}): {c.Handler.FunctionName} ({c.Handler.LibraryName})");
        }
    }

    static void WriteLibrarySection(Action<string> line, string label, List<LibraryChange> items)
    {
        if (items.Count == 0)
            return;

        line($"  {label} ({items.Count})");
        foreach (var c in items.OrderBy(i => i.Entity).ThenBy(i => i.Form, StringComparer.OrdinalIgnoreCase))
            line($"    - {c.Entity}/{c.Form}: {c.Library.Name}");
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
                "Unrecognized form event handler(s) found — confirmation required but not in interactive mode. Use --force delete-form-handlers to proceed.\n"
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
        FormEventSyncPlan plan,
        Dictionary<Guid, (string FormXml, string? RowVersion, List<HandlerChange> HandlerChanges, List<LibraryChange> LibraryChanges)> formBuilds,
        bool publishAfterSync,
        List<(string Name, Exception Error)> failures,
        List<HandlerChange> appliedHandlerChanges,
        List<LibraryChange> appliedLibraryChanges,
        object changesLock,
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
                var anyFormChanged = false;
                var updateTasks = entityGroup.GroupBy(f => f.FormId).Select(async formGroup =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        try
                        {
                            var (formXml, rowVersion, handlerChanges, libraryChanges) = formBuilds[formGroup.Key];
                            // cleanupOnly's narrowing (BuildFormXml) can leave nothing to write for this
                            // particular form even when the phase overall has work elsewhere — e.g. a form
                            // whose only pending change is a brand-new handler/library, deferred to
                            // registration. Skipping the write here is what stops the cleanup pass from
                            // publishing that entity for no reason (confirmed live: reported as an
                            // apparent double-publish).
                            if (handlerChanges.Count > 0 || libraryChanges.Count > 0)
                            {
                                var entity = new Entity(SystemFormEntity, formGroup.Key) { ["formxml"] = formXml };
                                // Optimistic concurrency (confirmed live: systemform has
                                // IsOptimisticConcurrencyEnabled=true) - catches a concurrent second push,
                                // or a maker hand-editing the form in the classic designer, racing this
                                // read-modify-write. RowVersion is only absent for test-built snapshots;
                                // the platform always returns it on a real retrieve, so this is a genuine
                                // fallback, not the common path.
                                if (rowVersion is not null)
                                {
                                    entity.RowVersion = rowVersion;
                                    await service.ExecuteAsync(
                                        new UpdateRequest { Target = entity, ConcurrencyBehavior = ConcurrencyBehavior.IfRowVersionMatches },
                                        cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    await service.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
                                }
                                // Plain bool write, no lock: only ever set to true, and Task.WhenAll below
                                // establishes happens-before for every updateTasks write before it's read.
                                anyFormChanged = true;
                                lock (changesLock)
                                {
                                    appliedHandlerChanges.AddRange(handlerChanges);
                                    appliedLibraryChanges.AddRange(libraryChanges);
                                }
                            }
                        }
                        catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail.ErrorCode == ConcurrencyVersionMismatchErrorCode)
                        {
                            lock (failures) failures.Add((formGroup.First().FormName, new InvalidOperationException(
                                "form was modified since Flowline last read it (a concurrent push, or a manual edit in the maker portal) — re-run push to apply your changes against the latest state.", ex)));
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

                if (anyFormChanged && publishAfterSync)
                {
                    await publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await PublishAsync(service, entityGroup.Key, cancellationToken).ConfigureAwait(false);
                    }
                    catch (FaultException<OrganizationServiceFault> ex)
                    {
                        // Distinct from an update failure: the formxml write already succeeded and is
                        // saved, only the publish that makes it live failed - the form is not in an error
                        // state, just not yet reflecting the saved change for end users. Dataverse exposes
                        // no queryable "needs publish" signal for systemform (no modifiedon column to
                        // compare against publishedon - confirmed live), so this in-run warning is the
                        // only point this can be surfaced; a later push with no further diff won't retry it.
                        lock (failures) failures.Add((entityGroup.Key, new InvalidOperationException(
                            $"form(s) were updated and saved, but the publish that makes them live failed - changes are saved, not yet published: {ex.Message}", ex)));
                    }
                    finally { publishGate.Release(); }
                }
                progressTask.Increment(1);
            });

        await Task.WhenAll(entityTasks).ConfigureAwait(false);
    }

    // formGroup holds every FormEventFormPlan for one FormId (one entry per touched event, or — for
    // onchange — per touched (event, attribute) pair) — each entry's desired handlers are applied to the
    // same xdoc, and libraries are unioned across entries and applied
    // once (SetLibraries replaces the full <formLibraries> section, so applying it per-plan in sequence
    // would drop an earlier plan's libraries). The returned change lists are the single source of truth for
    // both what gets written (empty means nothing changed) and what gets reported (verbose detail, dry-run
    // preview, and the always-visible summary) — computing them once here means the report can never drift
    // from what applying it actually does.
    static (string FormXml, string? RowVersion, List<HandlerChange> HandlerChanges, List<LibraryChange> LibraryChanges) BuildFormXml(
        FormEventSnapshot snapshot, IGrouping<Guid, FormEventFormPlan> formGroup, bool removeUnrecognized, bool cleanupOnly)
    {
        var first = formGroup.First();
        var dataverseForm = snapshot.Forms[(first.EntityLogicalName, first.FormName)];
        var xdoc = XDocument.Parse(dataverseForm.FormXml);

        var currentLibraries = FormXmlEventSerializer.GetLibraries(xdoc);

        var handlerChanges = new List<HandlerChange>();
        var unionLibraries = new HashSet<FormLibrary>();
        foreach (var formPlan in formGroup)
        {
            // Planner already folded UnrecognizedHandlers into DesiredHandlers (kept by default, R18) —
            // removing them here is the only place that decision is undone, once confirmed.
            var unrecognizedHandlers = formPlan.UnrecognizedHandlers.Select(u => u.Handler).ToHashSet();
            IEnumerable<FormEventHandler> desired = removeUnrecognized
                ? formPlan.DesiredHandlers.Where(h => !unrecognizedHandlers.Contains(h))
                : formPlan.DesiredHandlers;

            // Read before this iteration's SetHandlers call below — each event's Handlers live in their own
            // <event> element, so an earlier iteration in this loop (a different event, or a different
            // onchange attribute) never touches this one. formPlan.Attribute is non-null only for OnChange.
            var currentHandlers = FormXmlEventSerializer.GetHandlers(xdoc, formPlan.Event, formPlan.Attribute);

            // cleanupOnly (KTD12's phase-1 cleanup pass, U7): removals only, never additions or in-place
            // updates — a clean separation of concerns where cleanup only ever shrinks the current set, and
            // registration is the only phase that adds or modifies (parameter-only changes included).
            // Sourced from currentHandlers (not desired): a wanted-but-not-yet-updatable handler survives
            // with its exact current value untouched, rather than being excluded — excluding it here would
            // make SetHandlers (which replaces the whole set) drop it entirely instead of leaving it as-is.
            // Only an identity genuinely absent from desired altogether (truly orphaned, or never wanted)
            // is left out, which is what actually removes it.
            if (cleanupOnly)
            {
                var desiredIdentities = desired.ToHashSet();
                desired = currentHandlers.Where(desiredIdentities.Contains);
            }

            // Order-preserving de-duplication (KTD2/KTD8's fix): the planner's computed handler order must
            // reach SetHandlers below, not get discarded by a set conversion first. Distinct() keeps each
            // identity's first occurrence and its position — unlike ToHashSet(), which has no ordering
            // contract at all. FormEventHandlerDiffer stays scoped to content-equality (KTD8), so it gets
            // its own throwaway set conversion here — a pure reorder never confuses it into reporting a
            // false "updated" handler.
            var desiredOrdered = desired.Distinct().ToList();
            var (added, updated, removed) = FormEventHandlerDiffer.DiffDetailed(desiredOrdered.ToHashSet(), currentHandlers.ToHashSet());
            handlerChanges.AddRange(added.Select(h => new HandlerChange(formPlan.EntityLogicalName, formPlan.FormName, formPlan.Event, formPlan.Attribute, ChangeKind.Added, h)));
            handlerChanges.AddRange(updated.Select(h => new HandlerChange(formPlan.EntityLogicalName, formPlan.FormName, formPlan.Event, formPlan.Attribute, ChangeKind.Updated, h)));
            handlerChanges.AddRange(removed.Select(h => new HandlerChange(formPlan.EntityLogicalName, formPlan.FormName, formPlan.Event, formPlan.Attribute, ChangeKind.Removed, h)));

            FormXmlEventSerializer.SetHandlers(xdoc, formPlan.Event, desiredOrdered, formPlan.Attribute, formPlan.BulkEditEnabled);

            var desiredLibraries = cleanupOnly
                ? formPlan.DesiredLibraries.Where(currentLibraries.Contains)
                : formPlan.DesiredLibraries;
            unionLibraries.UnionWith(desiredLibraries);
        }

        var libraryChanges = new List<LibraryChange>();
        libraryChanges.AddRange(unionLibraries.Where(l => !currentLibraries.Contains(l))
            .Select(l => new LibraryChange(first.EntityLogicalName, first.FormName, ChangeKind.Added, l)));
        libraryChanges.AddRange(currentLibraries.Where(l => !unionLibraries.Contains(l))
            .Select(l => new LibraryChange(first.EntityLogicalName, first.FormName, ChangeKind.Removed, l)));

        FormXmlEventSerializer.SetLibraries(xdoc, unionLibraries);

        return (xdoc.ToString(), dataverseForm.RowVersion, handlerChanges, libraryChanges);
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
