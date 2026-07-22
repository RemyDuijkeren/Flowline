using System.Text.RegularExpressions;
using System.Xml.Linq;
using Flowline.Core.Models;
using Flowline.Core.FormEvents.Support;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.FormEvents;

public class FormEventPlanner(IAnsiConsole console)
{
    // Per-(event,scope) intermediate result, computed before the form-wide library decision. A record
    // (not a positional tuple) so adding a future field can't silently shift an existing destructuring
    // site's positions — call sites read named properties instead.
    record PerEventPlanResult(
        FormEventType Event,
        string? Attribute,
        IReadOnlyList<FormEventHandler> DesiredHandlers,
        IReadOnlySet<UnrecognizedHandler> Unrecognized,
        IReadOnlyList<FormEventHandler> CurrentHandlers,
        IReadOnlySet<string> ManagedLibraryNames,
        bool BulkEditEnabled,
        bool BulkEditChanged);


    // suppressWarnings: Plan runs twice per push (cleanup pass, then registration pass) on an identical
    // snapshot for warning purposes — only the registration (second, fuller) pass shows these, matching
    // FormEventReader.LoadSnapshotAsync's same dedup convention.
    public FormEventSyncPlan Plan(FormEventSnapshot snapshot, bool suppressWarnings = false)
    {
        var plan = new FormEventSyncPlan();
        var errors = new List<string>();

        // Case-insensitive lookup from (Entity, Form) to its current annotations, keyed the same way
        // snapshot.Forms itself is keyed (FormEventReader.FormKeyComparer) — annotation casing isn't
        // guaranteed to match the form's own EntityLogicalName/Name casing exactly.
        var annotationsByForm = snapshot.Annotations
            .GroupBy(a => (a.Annotation.Entity, a.Annotation.Form), FormEventReader.FormKeyComparer.Instance)
            .ToDictionary(g => g.Key, g => g.ToList(), FormEventReader.FormKeyComparer.Instance);

        // Iterate every solution-scoped form (not just annotation-referenced ones), so a form whose last
        // annotation was removed is still evaluated and its now-orphaned Handler gets cleaned up.
        foreach (var dataverseForm in snapshot.Forms.Values)
        {
            var entity = dataverseForm.EntityLogicalName;
            var form = dataverseForm.Name;
            var xdoc = XDocument.Parse(dataverseForm.FormXml);
            var currentLibraries = FormXmlEventSerializer.GetLibraries(xdoc);

            var formAnnotations = annotationsByForm.TryGetValue((entity, form), out var list)
                ? list
                : [];

            // Libraries live at the form level, not per-event (<formLibraries> is one list shared by both
            // onLoad and onSave) — so whether a library is still needed can only be decided once BOTH
            // events' handler decisions are known. Compute each event's handler-level results first,
            // without touching libraries yet, then decide library removal once across the whole form,
            // then emit plan entries using that shared result — a library whose last handler was just
            // auto-removed as stale must actually leave <formLibraries>, or the web resource it points at
            // still looks referenced and its delete still faults.
            var perEventResults = new List<PerEventPlanResult>();

            // onload/onsave each plan once (Attribute: null); onchange plans once per attribute in the
            // union of (a) attributes with a live <event name="onchange" attribute="..."> element and (b)
            // attributes referenced by a current annotation — either alone would miss orphan cleanup for
            // the other side.
            var onChangeAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            onChangeAttributes.UnionWith(FormXmlEventSerializer.GetOnChangeAttributes(xdoc));
            onChangeAttributes.UnionWith(formAnnotations
                .Where(a => a.Annotation.Event == FormEventType.OnChange)
                .Select(a => a.Annotation.Attribute!));

            // Same union pattern as onChangeAttributes, extended to Tab/IFRAME scopes: live
            // container-scoped events with at least one Handler, plus scopes referenced by a current
            // annotation — either alone would miss orphan cleanup for the other side. Unlike
            // onChangeAttributes, an annotation-referenced Tab/IFRAME scope must actually resolve to
            // a live container before being added — GetOrAddEventsContainer throws if it doesn't (the
            // container can't be synthesized), so an unresolvable scope is validated and reported here as a
            // clean per-annotation FlowlineException, not left to surface as an unhandled exception deep in
            // the executor and abort the entire push.
            var tabStateChangeScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            tabStateChangeScopes.UnionWith(FormXmlEventSerializer.GetTabNamesWithStateChangeHandlers(xdoc));
            foreach (var a in formAnnotations.Where(a => a.Annotation.Event == FormEventType.TabStateChange))
            {
                var tabName = a.Annotation.Attribute!;
                if (FormXmlEventSerializer.TabExists(xdoc, tabName))
                    tabStateChangeScopes.Add(tabName);
                else
                    errors.Add($"{a.SourceFile}: no tab named '{tabName}' found on form '{form}' (entity '{entity}').");
            }

            var readyStateScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readyStateScopes.UnionWith(FormXmlEventSerializer.GetIframeControlIdsWithReadyStateHandlers(xdoc));
            foreach (var a in formAnnotations.Where(a => a.Annotation.Event == FormEventType.OnReadyStateComplete))
            {
                var controlId = a.Annotation.Attribute!;
                if (FormXmlEventSerializer.IframeControlExists(xdoc, controlId))
                    // Normalized so an annotation written with or without the "IFRAME_" prefix collapses
                    // onto the same scope key as the live-scan result above — otherwise the same physical
                    // control could plan as two distinct scopes and its <events> element would be written
                    // twice, with the second write clobbering the first.
                    readyStateScopes.Add(FormXmlEventSerializer.NormalizeIframeControlId(controlId));
                else
                    errors.Add($"{a.SourceFile}: no IFRAME control '{controlId}' found on form '{form}' (entity '{entity}').");
            }

            var planningKeys = new List<(FormEventType Event, string? Attribute)> { (FormEventType.OnLoad, null), (FormEventType.OnSave, null) };
            planningKeys.AddRange(onChangeAttributes.Select(attr => (FormEventType.OnChange, (string?)attr)));
            planningKeys.AddRange(tabStateChangeScopes.Select(attr => (FormEventType.TabStateChange, (string?)attr)));
            planningKeys.AddRange(readyStateScopes.Select(attr => (FormEventType.OnReadyStateComplete, (string?)attr)));

            foreach (var (evt, attribute) in planningKeys)
                perEventResults.Add(PlanEvent(evt, attribute, entity, form, xdoc, formAnnotations, snapshot, suppressWarnings, errors));

            // Form-wide library decision, using both events' desired-handler results together.
            var neededLibraryNames = perEventResults
                .SelectMany(r => r.ManagedLibraryNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A currentLibrary is kept if the FINAL desired handler set (either event, managed or foreign
            // pass-through) still references it, OR it's outside this project's own tracked-library
            // boundary — Flowline only ever cleans up a library entry that corresponds to one of ITS OWN
            // local JS files. A Microsoft/foreign library with zero current handler references is left
            // alone regardless: Flowline can't be certain nothing else on the platform depends on it, so it
            // only takes responsibility for tidying up libraries it actually manages.
            var allReferencedLibraryNames = perEventResults
                .SelectMany(r => r.DesiredHandlers.Select(h => h.LibraryName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var desiredLibraries = new HashSet<FormLibrary>(currentLibraries
                .Where(l => allReferencedLibraryNames.Contains(l.Name) || !snapshot.TrackedLibraryNames.Contains(l.Name)));
            // Only libraries Flowline actually manages (annotationDesired + unrecognized) can be newly added
            // — never fabricate a <Library> entry for a foreign handler's library (see managedLibraryNames).
            foreach (var libraryName in neededLibraryNames)
            {
                if (desiredLibraries.Contains(new FormLibrary(libraryName, Guid.Empty)))
                    continue;
                desiredLibraries.Add(new FormLibrary(libraryName, FormEventDeterministicId.ForLibrary(libraryName)));
            }

            // desiredLibraries is now identical for every event of this form (computed once, above) — the
            // executor unions DesiredLibraries across whichever plan entries exist for a FormId, so only
            // ONE entry needs to carry it; an event with nothing of its own to do doesn't need its own
            // entry just because a DIFFERENT event on the same form needed a new/retired library.
            var librariesChangedForForm = !desiredLibraries.SetEquals(currentLibraries);
            var anyEntryEmitted = false;

            foreach (var result in perEventResults)
            {
                // FormEventHandlerDiffer.Diff only detects added/updated/removed by identity and parameter
                // state — a pure [order:N] reorder with no membership/parameter change diffs to (0,0,0)
                // and would otherwise be skipped entirely, silently discarding the corrected order.
                // orderChanged is a separate, independent check (not folded into Diff's own semantics) so a
                // plan entry still emits whenever either the handler set OR its order changed.
                var orderChanged = !result.DesiredHandlers.SequenceEqual(result.CurrentHandlers);
                var handlersChanged = !HandlerSetsFullyEqual(result.DesiredHandlers, result.CurrentHandlers) || orderChanged;
                // A bulk-edit-only change touches neither DesiredHandlers nor its order, so it must
                // independently force this entry to emit — otherwise BehaviorInBulkEditForm would never
                // actually get written or cleared when it's the only thing that changed.
                if (!handlersChanged && result.Unrecognized.Count == 0 && !result.BulkEditChanged)
                    continue;

                anyEntryEmitted = true;
                plan.Forms.Add(new FormEventFormPlan(
                    dataverseForm.Id,
                    entity,
                    form,
                    result.Event,
                    result.DesiredHandlers,
                    result.Unrecognized,
                    desiredLibraries,
                    result.Attribute,
                    result.BulkEditEnabled));
            }

            // Narrow fallback: a form-wide library change with no individual event flagged — e.g. a library
            // whose last handler was removed outside Flowline entirely (never appeared as a plan handler
            // change on either event), so nothing above set handlersChanged, yet the library is still now
            // orphaned. Attach it to the first event so the executor's per-form library union still sees it.
            if (!anyEntryEmitted && librariesChangedForForm)
            {
                var result = perEventResults[0];
                plan.Forms.Add(new FormEventFormPlan(
                    dataverseForm.Id,
                    entity,
                    form,
                    result.Event,
                    result.DesiredHandlers,
                    result.Unrecognized,
                    desiredLibraries,
                    result.Attribute,
                    result.BulkEditEnabled));
            }
        }

        // Failures are isolated per-annotation above — every annotation gets a chance to resolve before
        // this throws, so a single unresolvable function never prevents the others from being planned.
        // FlowlineException, not a raw exception: this is a user-actionable annotation problem (a typo'd
        // or genuinely missing function name), not an internal bug — Program.cs prints it as a clean
        // "Error: ..." line instead of dumping a full stack trace for something the user needs to fix.
        if (errors.Count > 0)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Form event annotations failed to resolve required functions:\n" + string.Join("\n", errors));

        return plan;
    }

    // Per-(event,scope) planning: resolves this event's annotations into desired handlers, diffs them
    // against the live FormXml, and classifies anything left over as foreign or unrecognized. Errors are
    // appended to the caller's shared `errors` list rather than thrown, so one bad annotation on this
    // event/scope doesn't stop the rest of the form (or other forms) from being planned.
    PerEventPlanResult PlanEvent(
        FormEventType evt,
        string? attribute,
        string entity,
        string form,
        XDocument xdoc,
        List<ResolvedFormEventAnnotation> formAnnotations,
        FormEventSnapshot snapshot,
        bool suppressWarnings,
        List<string> errors)
    {
        // attribute is already the canonical (bare, prefix-stripped) IFRAME control id for
        // OnReadyStateComplete (see readyStateScopes above) — an annotation's own Attribute must be
        // normalized the same way before comparing, or a user-written "IFRAME_"-prefixed token would
        // never match its own scope key.
        var relevantAnnotations = (attribute is null
            ? formAnnotations.Where(a => a.Annotation.Event == evt)
            : formAnnotations.Where(a => a.Annotation.Event == evt && string.Equals(
                evt == FormEventType.OnReadyStateComplete
                    ? FormXmlEventSerializer.NormalizeIframeControlId(a.Annotation.Attribute!)
                    : a.Annotation.Attribute,
                attribute, StringComparison.OrdinalIgnoreCase))).ToList();

        // Shared by the [order:N] conflict and 50-handler-limit error messages below.
        var scopeDescription = attribute is null ? $"{entity}.{form}.{evt}" : $"{entity}.{form}.{evt}.{attribute}";

        // BehaviorInBulkEditForm is OnLoad-only — the union is recomputed from scratch on every push, not
        // merged with the form's prior FormXml state, so a form whose last [bulkEdit] annotation was
        // removed correctly clears the attribute.
        var bulkEditEnabled = evt == FormEventType.OnLoad && relevantAnnotations.Any(a => a.Annotation.BulkEdit);

        // A bulk-edit-only change (e.g. adding/removing [bulkEdit] with no other handler/order
        // change) doesn't touch DesiredHandlers or its order at all, so the handlersChanged gate
        // below would otherwise skip emitting a plan entry entirely and the attribute would never
        // actually get written or cleared. Compared against live FormXml, not the prior push's
        // computed value, for the same reason bulkEditEnabled itself is recomputed fresh.
        var bulkEditChanged = evt == FormEventType.OnLoad && bulkEditEnabled != FormXmlEventSerializer.IsBulkEditEnabled(xdoc);

        // Desired handlers as derived purely from this push's annotations, kept in an intermediate
        // ordered list (not a set) so each entry's [order:N]/encounter-position survives to the sort step
        // below. Always computes a fresh deterministic HandlerUniqueId, so a matched-by-identity entry
        // with changed Parameters naturally lands here with the new Parameters value (no separate
        // "update" bucket needed).
        var resolvedEntries = new List<(FormEventHandler Handler, int? Order, string SourceFile)>();
        foreach (var resolved in relevantAnnotations)
        {
            if (resolved.Annotation.BulkEdit && evt != FormEventType.OnLoad)
            {
                // [bulkEdit] is only meaningful on onload — reject rather than silently ignore, same
                // per-annotation isolation as the function-not-found failure below.
                errors.Add($"{resolved.SourceFile}: [bulkEdit] is only valid on flowline:onload annotations.");
                continue;
            }

            var (requestedFunctionName, autoNamespace, isExplicit) = DeriveHandlerResolutionInputs(resolved, evt);

            var (resolvedFunctionName, found, confident) = FormEventFunctionResolver.Resolve(
                resolved.Content, requestedFunctionName, autoNamespace, isExplicit);

            string finalFunctionName;
            if (found)
            {
                finalFunctionName = resolvedFunctionName!;
            }
            else if (!isExplicit || confident)
            {
                // Both the defaulted case (always hard-fails) and the explicit-but-confirmed-absent case
                // share the same per-declaration hard-fail path — other declarations still apply.
                errors.Add($"{resolved.SourceFile}: function '{requestedFunctionName}' not found in library '{resolved.LibraryName}'.");
                continue;
            }
            else
            {
                // Explicit but inconclusive — warn and register verbatim, don't fail.
                if (!suppressWarnings)
                    console.Warning($"{resolved.SourceFile}: function '{requestedFunctionName}' could not be confirmed in library '{resolved.LibraryName}' — registering as written.");
                finalFunctionName = requestedFunctionName;
            }

            resolvedEntries.Add((
                new FormEventHandler(
                    finalFunctionName,
                    resolved.LibraryName,
                    FormEventDeterministicId.ForHandler(entity, form, evt, finalFunctionName, resolved.LibraryName, attribute),
                    resolved.Annotation.Parameters ?? ""),
                resolved.Annotation.Order,
                resolved.SourceFile));
        }

        // Two annotations sharing this event/scope that both specify the same [order:N] value is a
        // conflict regardless of whether they resolve to the same handler identity — name every
        // conflicting source file so the developer can find and fix all of them at once.
        foreach (var group in resolvedEntries.Where(e => e.Order.HasValue).GroupBy(e => e.Order!.Value))
        {
            if (group.Count() <= 1)
                continue;
            errors.Add($"{string.Join(", ", group.Select(g => g.SourceFile))}: annotations for {scopeDescription} share [order:{group.Key}].");
        }

        // Identity collisions (two annotations resolving to the same FunctionName+LibraryName) keep
        // first-encountered-wins behavior — not a conflict class this plan validates; DistinctBy already
        // keeps the first occurrence per key.
        // Explicit [order:N] entries sort ascending first; unordered entries (Order: null) keep their
        // annotation-encounter order and append after — OrderBy's documented stability preserves
        // relative order among the equal (null) keys.
        var annotationDesired = resolvedEntries
            .DistinctBy(e => e.Handler)
            .OrderBy(e => e.Order ?? int.MaxValue)
            .Select(e => e.Handler)
            .ToList();
        var annotationDesiredIdentities = annotationDesired.ToHashSet();

        var currentHandlers = FormXmlEventSerializer.GetHandlers(xdoc, evt, attribute);

        // A Handler on a library this project doesn't track, and whose ID Flowline didn't derive, is
        // never evaluated for staleness or unrecognized status — it's simply carried through untouched
        // so the write-back doesn't drop it (SetHandlers replaces the whole event's Handlers set on
        // write). Kept in FormXml document order (not annotation-ordered — these have no [order:N] of
        // their own), same as unrecognized below.
        var foreignHandlers = new List<FormEventHandler>();

        // Current entries with no matching annotation: safe to drop if Flowline's own deterministic
        // derivation still matches the stored id (nothing else could have produced that exact id — the
        // hash is derived from this handler's own libraryName, so a match is proof of Flowline ownership
        // REGARDLESS of whether that library is still locally tracked right now), otherwise it's
        // unrecognized — kept by default, with a proposed adoption annotation, until the executor
        // confirms removal.
        //
        // The ID check runs BEFORE the tracked-library gate, not after. A library this project created a
        // handler for can legitimately fall out of TrackedLibraryNames (built from currently-existing
        // local files) once its source file is deleted entirely — gating ownership recognition behind
        // current tracked-status would silently reclassify that handler as "foreign" and never clean it
        // up, defeating cleanup for exactly the "delete the JS file entirely" case.
        var unrecognized = new HashSet<UnrecognizedHandler>();
        // Parallel ordered list of the same handlers, sourced from the same currentHandlers iteration as
        // `unrecognized` above — needed because HashSet<UnrecognizedHandler>'s enumeration order depends
        // on string.GetHashCode, which .NET randomizes per process. Reading order back out of
        // `unrecognized` itself would make desiredHandlers' order (and therefore the orderChanged check
        // below) vary across separate `flowline push` runs even with byte-identical input.
        var unrecognizedOrdered = new List<FormEventHandler>();
        foreach (var current in currentHandlers)
        {
            if (annotationDesiredIdentities.Contains(current))
                continue;

            var expectedId = FormEventDeterministicId.ForHandler(entity, form, evt, current.FunctionName, current.LibraryName, attribute);
            if (current.HandlerUniqueId == expectedId)
            {
                // stale, Flowline-owned — drop automatically, not added to desiredHandlers below.
                // Its library is dropped too, below, as long as nothing else on this form (either
                // event) still references it.
                continue;
            }

            if (!snapshot.TrackedLibraryNames.Contains(current.LibraryName))
            {
                foreignHandlers.Add(current);
                continue;
            }

            unrecognized.Add(new UnrecognizedHandler(current, BuildProposedAnnotation(entity, form, evt, current, attribute)));
            unrecognizedOrdered.Add(current);
        }

        // Interleave-preserving merge: a foreign or unrecognized handler — most importantly a Microsoft
        // OOB script — must keep its exact original position in the live FormXml, never getting silently
        // relocated to run after a Flowline-managed handler it used to run before. Walk currentHandlers
        // in its original document order: a foreign/unrecognized entry is an "anchor" and is placed
        // exactly where it already was; every other position (a Flowline-managed handler being
        // kept/updated, or a stale one being dropped) is a "Flowline slot", filled from annotationDesired
        // in its own newly-computed order. Only brand-new Flowline handlers with no prior slot — and any
        // left over because more slots were freed by removed/stale handlers than annotations remain —
        // append at the end, where there's no original position to preserve anyway.
        var anchorIdentities = new HashSet<FormEventHandler>(foreignHandlers);
        anchorIdentities.UnionWith(unrecognizedOrdered);

        var desiredHandlers = new List<FormEventHandler>();
        var flowlineQueue = new Queue<FormEventHandler>(annotationDesired);
        foreach (var current in currentHandlers)
        {
            if (anchorIdentities.Contains(current))
                desiredHandlers.Add(current);
            else if (flowlineQueue.Count > 0)
                desiredHandlers.Add(flowlineQueue.Dequeue());
            // else: this slot held a Flowline handler that's now gone (removed/stale) with nothing
            // left to fill it — the slot simply disappears rather than leaving a placeholder.
        }
        desiredHandlers.AddRange(flowlineQueue);

        // The full set this event/scope will actually write — Flowline-managed plus foreign/unrecognized
        // pass-through — must not exceed Dataverse's 50-handler-per-event cap. Checked before any
        // Dataverse write (the whole Plan() call fails before FormEventExecutor ever runs, per the
        // accumulate-then-throw pattern below).
        if (desiredHandlers.Count > 50)
            errors.Add($"{scopeDescription}: would write {desiredHandlers.Count} handlers, exceeding Dataverse's 50-handler-per-event limit.");

        // Library reconciliation only covers handlers Flowline actually manages (annotationDesired +
        // unrecognized) — not foreignHandlers. A foreign handler can reference a library that was never
        // declared in <formLibraries> to begin with (observed live: a Dataverse OOB Handler whose
        // library is registered only via <internaljscriptfile>, not <formLibraries><Library>). Folding
        // foreignHandlers' library names into neededLibraryNames would "fix" that as if it were
        // Flowline's job, fabricating a Library entry on a form this project never touches.
        var managedLibraryNames = annotationDesired.Select(h => h.LibraryName)
            .Concat(unrecognized.Select(u => u.Handler.LibraryName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new PerEventPlanResult(evt, attribute, desiredHandlers, unrecognized, currentHandlers, managedLibraryNames, bulkEditEnabled, bulkEditChanged);
    }

    // HashSet.SetEquals can't detect a Parameters-only change (FormEventHandler's equality is identity-only) —
    // FormEventHandlerDiffer compares full record state on identity-matched pairs instead. Converts the
    // ordered lists to sets here: Diff stays scoped to content-equality only, so a pure reorder never
    // shows up as an "updated" handler in its added/updated/removed result — order-changed is its own,
    // separate check at the call site.
    static bool HandlerSetsFullyEqual(IReadOnlyList<FormEventHandler> a, IReadOnlyList<FormEventHandler> b) =>
        FormEventHandlerDiffer.Diff(a.ToHashSet(), b.ToHashSet()) is (0, 0, 0);

    // Proposed annotation text built from the unrecognized handler's own stored state, in the same
    // `// flowline:onload/onsave <entity> "<form>" Function[(params)]` shape, or — for any
    // attribute-scoped directive (onchange/tabstatechange/onreadystatecomplete) — the
    // `// flowline:<directive> <entity> "<form>" <scope> Function[(params)]` shape. attribute is
    // non-null for exactly those three directives, so a null check already distinguishes them from
    // onload/onsave without listing each attribute-scoped FormEventType explicitly.
    // FunctionName is used verbatim — including a dotted namespace, since a dotted name round-trips
    // through the annotation grammar without re-derivation.
    static string BuildProposedAnnotation(string entity, string form, FormEventType evt, FormEventHandler handler, string? attribute)
    {
        var directive = evt.ToString().ToLowerInvariant();
        var attributeToken = attribute is not null ? $" {attribute}" : "";
        var parameters = string.IsNullOrEmpty(handler.Parameters) ? "" : $"({handler.Parameters})";
        return $"// flowline:{directive} {entity} \"{form}\"{attributeToken} {handler.FunctionName}{parameters}";
    }

    // Shared with FormEventRenameAdvisor, which needs the same inputs to recompute a self-tag candidate's
    // deterministic handler id. Only the resolution *inputs* are shared — each caller still
    // calls FormEventFunctionResolver.Resolve and FormEventDeterministicId.ForHandler itself, since what
    // they do with the result (and which form name they derive the id against) differs per caller.
    internal static (string RequestedFunctionName, string AutoNamespace, bool IsExplicit) DeriveHandlerResolutionInputs(
        ResolvedFormEventAnnotation resolved, FormEventType evt)
    {
        var isExplicit = resolved.Annotation.FunctionName is not null;
        var requestedFunctionName = resolved.Annotation.FunctionName
            ?? DeriveDefaultFunctionName(evt, resolved.Annotation.Attribute);
        return (requestedFunctionName, DeriveAutoNamespace(resolved.LibraryName), isExplicit);
    }

    // Default onChange function name is "on" + PascalCased attribute (publisher prefix stripped) +
    // "Change" — e.g. "creditlimit" -> "onCreditlimitChange", "new_credit_limit" -> "onCreditLimitChange".
    // Tab/IFRAME default names never strip a publisher prefix — tab and IFRAME control names are
    // maker-assigned form-design names, not Dataverse schema attribute names, so there's no prefix
    // convention to strip.
    static string DeriveDefaultFunctionName(FormEventType evt, string? attribute) => evt switch
    {
        FormEventType.OnLoad => "onLoad",
        FormEventType.OnSave => "onSave",
        FormEventType.OnChange => "on" + ToPascalCase(StripPublisherPrefix(attribute!)) + "Change",
        FormEventType.TabStateChange => "on" + ToPascalCase(attribute!) + "TabStateChange",
        // Normalized so the default name is stable regardless of whether the annotation (or a prior
        // FormXml scan) spelled the control id with or without the maker-assigned "IFRAME_" prefix.
        FormEventType.OnReadyStateComplete => "on" + ToPascalCase(FormXmlEventSerializer.NormalizeIframeControlId(attribute!)) + "ReadyStateComplete",
        _ => throw new ArgumentOutOfRangeException(nameof(evt))
    };

    // Publisher prefix is everything up to and including the first '_' (e.g. "new_creditlimit" ->
    // "creditlimit"); a name with no underscore is returned unchanged.
    internal static string StripPublisherPrefix(string name)
    {
        var index = name.IndexOf('_');
        return index < 0 ? name : name[(index + 1)..];
    }

    // Internal (not private): FormEventRenameAdvisor needs to derive the same auto-namespace when
    // recomputing a self-tag candidate's deterministic handler id — shares this rather than duplicating it.
    internal static string DeriveAutoNamespace(string libraryName) =>
        ToPascalCase(Path.GetFileNameWithoutExtension(libraryName));

    // Mirrors src/Flowline/Templates/WebResources/rollup.config.mjs's toPascalCase so the auto-derived
    // function namespace matches what the Rollup-built bundle actually exports.
    internal static string ToPascalCase(string name) =>
        string.Concat(Regex.Split(name, "[^a-zA-Z0-9]+")
            .Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
