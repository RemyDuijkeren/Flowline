using System.Text.RegularExpressions;
using System.Xml.Linq;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class FormEventPlanner(IAnsiConsole console)
{
    // suppressWarnings: KTD12 runs Plan twice per push (cleanup pass, then registration pass) on an
    // identical snapshot for warning purposes — only the registration (second, fuller) pass shows these,
    // matching FormEventReader.LoadSnapshotAsync's same dedup convention.
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

        // R14: iterate every solution-scoped form (not just annotation-referenced ones), so a form whose
        // last annotation was removed is still evaluated and its now-orphaned Handler gets cleaned up.
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
            // then emit plan entries using that shared result (KTD12: a library whose last handler was
            // just auto-removed as stale must actually leave <formLibraries>, or the web resource it
            // points at still looks referenced and its delete still faults — fixing only the <Handler>
            // side left this gap open).
            var perEventResults = new List<(FormEventType Event, string? Attribute, IReadOnlySet<FormEventHandler> DesiredHandlers, IReadOnlySet<UnrecognizedHandler> Unrecognized, IReadOnlySet<FormEventHandler> CurrentHandlers, IReadOnlySet<string> ManagedLibraryNames)>();

            // KTD4: onload/onsave each plan once (Attribute: null); onchange plans once per attribute in
            // the union of (a) attributes with a live <event name="onchange" attribute="..."> element and
            // (b) attributes referenced by a current annotation — either alone would miss orphan cleanup
            // for the other side (R13).
            var onChangeAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            onChangeAttributes.UnionWith(FormXmlEventSerializer.GetOnChangeAttributes(xdoc));
            onChangeAttributes.UnionWith(formAnnotations
                .Where(a => a.Annotation.Event == FormEventType.OnChange)
                .Select(a => a.Annotation.Attribute!));

            var planningKeys = new List<(FormEventType Event, string? Attribute)> { (FormEventType.OnLoad, null), (FormEventType.OnSave, null) };
            planningKeys.AddRange(onChangeAttributes.Select(attr => (FormEventType.OnChange, (string?)attr)));

            foreach (var (evt, attribute) in planningKeys)
            {
                // Desired handlers as derived purely from this push's annotations. Always computes a fresh
                // deterministic HandlerUniqueId, so a matched-by-identity entry with changed Parameters
                // naturally lands here with the new Parameters value (no separate "update" bucket needed).
                var annotationDesired = new HashSet<FormEventHandler>();
                var relevantAnnotations = attribute is null
                    ? formAnnotations.Where(a => a.Annotation.Event == evt)
                    : formAnnotations.Where(a => a.Annotation.Event == evt && string.Equals(a.Annotation.Attribute, attribute, StringComparison.OrdinalIgnoreCase));
                foreach (var resolved in relevantAnnotations)
                {
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
                        // R7 (defaulted, always hard-fails) and R7a outcome 2 (explicit, confirmed absent)
                        // share the same per-declaration hard-fail path — other declarations still apply.
                        errors.Add($"{resolved.SourceFile}: function '{requestedFunctionName}' not found in library '{resolved.LibraryName}'.");
                        continue;
                    }
                    else
                    {
                        // R7a outcome 3: explicit but inconclusive — warn and register verbatim, don't fail.
                        if (!suppressWarnings)
                            console.Warning($"{resolved.SourceFile}: function '{requestedFunctionName}' could not be confirmed in library '{resolved.LibraryName}' — registering as written.");
                        finalFunctionName = requestedFunctionName;
                    }

                    annotationDesired.Add(new FormEventHandler(
                        finalFunctionName,
                        resolved.LibraryName,
                        FormEventDeterministicId.ForHandler(entity, form, evt, finalFunctionName, resolved.LibraryName, attribute),
                        resolved.Annotation.Parameters ?? ""));
                }

                var currentHandlers = FormXmlEventSerializer.GetHandlers(xdoc, evt, attribute);

                // R15: a Handler on a library this project doesn't track, and whose ID Flowline didn't
                // derive, is never evaluated for staleness or unrecognized status — it's simply carried
                // through untouched so the write-back doesn't drop it (SetHandlers replaces the whole
                // event's Handlers set on write).
                var foreignHandlers = new HashSet<FormEventHandler>();

                // Current entries with no matching annotation: safe to drop if Flowline's own deterministic
                // derivation still matches the stored id (nothing else could have produced that exact id —
                // the hash is derived from this handler's own libraryName, so a match is proof of Flowline
                // ownership REGARDLESS of whether that library is still locally tracked right now), otherwise
                // it's unrecognized (R18) — kept by default, with a proposed adoption annotation (R18a),
                // until the executor confirms removal.
                //
                // R14: the ID check runs BEFORE the tracked-library gate, not after. A library this project
                // created a handler for can legitimately fall out of TrackedLibraryNames (built from
                // currently-existing local files) once its source file is deleted entirely — that's exactly
                // the "delete the JS file entirely" case R14 exists to close. Gating ownership recognition
                // behind current tracked-status would silently reclassify that handler as "foreign" and
                // never clean it up, defeating R14/KTD12 for the one scenario they were built to fix.
                var unrecognized = new HashSet<UnrecognizedHandler>();
                foreach (var current in currentHandlers)
                {
                    if (annotationDesired.Contains(current))
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
                }

                var desiredHandlers = new HashSet<FormEventHandler>(annotationDesired);
                desiredHandlers.UnionWith(foreignHandlers);
                foreach (var u in unrecognized)
                    desiredHandlers.Add(u.Handler);

                // Library reconciliation only covers handlers Flowline actually manages (annotationDesired +
                // unrecognized) — not foreignHandlers. A foreign handler can reference a library that was
                // never declared in <formLibraries> to begin with (observed live: a Dataverse OOB Handler
                // whose library is registered only via <internaljscriptfile>, not <formLibraries><Library>).
                // Folding foreignHandlers' library names into neededLibraryNames would "fix" that as if it
                // were Flowline's job, fabricating a Library entry on a form this project never touches —
                // exactly what R15's "carried through untouched" is supposed to prevent.
                var managedLibraryNames = annotationDesired.Select(h => h.LibraryName)
                    .Concat(unrecognized.Select(u => u.Handler.LibraryName))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                perEventResults.Add((evt, attribute, desiredHandlers, unrecognized, currentHandlers, managedLibraryNames));
            }

            // Form-wide library decision, using both events' desired-handler results together.
            var neededLibraryNames = perEventResults
                .SelectMany(r => r.ManagedLibraryNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A currentLibrary is kept if the FINAL desired handler set (either event, managed or foreign
            // pass-through) still references it, OR it's outside this project's own tracked-library
            // boundary (R15) — Flowline only ever cleans up a library entry that corresponds to one of ITS
            // OWN local JS files. A Microsoft/foreign library with zero current handler references is left
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

            foreach (var (evt, attribute, desiredHandlers, unrecognized, currentHandlers, _) in perEventResults)
            {
                var handlersChanged = !HandlerSetsFullyEqual(desiredHandlers, currentHandlers);
                if (!handlersChanged && unrecognized.Count == 0)
                    continue;

                anyEntryEmitted = true;
                plan.Forms.Add(new FormEventFormPlan(
                    dataverseForm.Id,
                    entity,
                    form,
                    evt,
                    desiredHandlers,
                    unrecognized,
                    desiredLibraries,
                    attribute));
            }

            // Narrow fallback: a form-wide library change with no individual event flagged — e.g. a library
            // whose last handler was removed outside Flowline entirely (never appeared as a plan handler
            // change on either event), so nothing above set handlersChanged, yet the library is still now
            // orphaned. Attach it to the first event so the executor's per-form library union still sees it.
            if (!anyEntryEmitted && librariesChangedForForm)
            {
                var (evt, attribute, desiredHandlers, unrecognized, _, _) = perEventResults[0];
                plan.Forms.Add(new FormEventFormPlan(
                    dataverseForm.Id,
                    entity,
                    form,
                    evt,
                    desiredHandlers,
                    unrecognized,
                    desiredLibraries,
                    attribute));
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

    // HashSet.SetEquals can't detect a Parameters-only change (FormEventHandler's equality is identity-only) —
    // FormEventHandlerDiffer compares full record state on identity-matched pairs instead.
    static bool HandlerSetsFullyEqual(IReadOnlySet<FormEventHandler> a, IReadOnlySet<FormEventHandler> b) =>
        FormEventHandlerDiffer.Diff(a, b) is (0, 0, 0);

    // R18a: proposed annotation text built from the unrecognized handler's own stored state, in the same
    // `// flowline:onload/onsave <entity> "<form>" Function[(params)]` (or, for onchange, the R1
    // `// flowline:onchange <entity> "<form>" <attribute> Function[(params)]`) shape R1 defines.
    // FunctionName is used verbatim — including a dotted namespace (R6a's escape hatch), since a dotted
    // name round-trips through the annotation grammar without re-derivation.
    static string BuildProposedAnnotation(string entity, string form, FormEventType evt, FormEventHandler handler, string? attribute)
    {
        var parameters = string.IsNullOrEmpty(handler.Parameters) ? "" : $"({handler.Parameters})";
        return evt switch
        {
            FormEventType.OnLoad => $"// flowline:onload {entity} \"{form}\" {handler.FunctionName}{parameters}",
            FormEventType.OnSave => $"// flowline:onsave {entity} \"{form}\" {handler.FunctionName}{parameters}",
            FormEventType.OnChange => $"// flowline:onchange {entity} \"{form}\" {attribute} {handler.FunctionName}{parameters}",
            _ => throw new ArgumentOutOfRangeException(nameof(evt))
        };
    }

    // Shared with FormEventRenameAdvisor (U3), which needs the same inputs to recompute a self-tag
    // candidate's deterministic handler id. Only the resolution *inputs* are shared — each caller still
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

    // R5: default onChange function name is "on" + PascalCased attribute (publisher prefix stripped) +
    // "Change" — e.g. "creditlimit" -> "onCreditlimitChange", "new_credit_limit" -> "onCreditLimitChange".
    static string DeriveDefaultFunctionName(FormEventType evt, string? attribute) => evt switch
    {
        FormEventType.OnLoad => "onLoad",
        FormEventType.OnSave => "onSave",
        FormEventType.OnChange => "on" + ToPascalCase(StripPublisherPrefix(attribute!)) + "Change",
        _ => throw new ArgumentOutOfRangeException(nameof(evt))
    };

    // Publisher prefix is everything up to and including the first '_' (e.g. "new_creditlimit" ->
    // "creditlimit"); a name with no underscore is returned unchanged.
    internal static string StripPublisherPrefix(string name)
    {
        var index = name.IndexOf('_');
        return index < 0 ? name : name[(index + 1)..];
    }

    // Internal (not private): FormEventRenameAdvisor (U3) needs to derive the same auto-namespace when
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
