using System.Text.RegularExpressions;
using System.Xml.Linq;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Services;

public class FormEventPlanner(IAnsiConsole console)
{
    public FormEventSyncPlan Plan(FormEventSnapshot snapshot)
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
            var perEventResults = new List<(FormEventType Event, IReadOnlySet<FormHandler> DesiredHandlers, IReadOnlySet<UnrecognizedHandler> Unrecognized, IReadOnlySet<FormHandler> CurrentHandlers, IReadOnlySet<string> ManagedLibraryNames)>();

            // Libraries whose only reference on this form was a Handler we just cleanly auto-removed as
            // stale (Flowline-owned, no confirmation needed) — the only case safe to also drop the Library
            // entry for. A library with no handler referencing it at all, ever, or one still carrying a
            // foreign/unrecognized handler, is never touched here — this feature only retires a Library
            // entry it can attribute to its own cleanup, never one it has no attributable reason to remove.
            var staleRemovedLibraryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var evt in Enum.GetValues<FormEventType>())
            {
                // Desired handlers as derived purely from this push's annotations. Always computes a fresh
                // deterministic HandlerUniqueId, so a matched-by-identity entry with changed Parameters
                // naturally lands here with the new Parameters value (no separate "update" bucket needed).
                var annotationDesired = new HashSet<FormHandler>();
                foreach (var resolved in formAnnotations.Where(a => a.Annotation.Event == evt))
                {
                    var isExplicit = resolved.Annotation.FunctionName is not null;
                    var requestedFunctionName = resolved.Annotation.FunctionName
                        ?? (evt == FormEventType.OnLoad ? "onLoad" : "onSave");
                    var autoNamespace = DeriveAutoNamespace(resolved.LibraryName);

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
                        console.Warning($"{resolved.SourceFile}: function '{requestedFunctionName}' could not be confirmed in library '{resolved.LibraryName}' — registering as written.");
                        finalFunctionName = requestedFunctionName;
                    }

                    annotationDesired.Add(new FormHandler(
                        finalFunctionName,
                        resolved.LibraryName,
                        FormEventDeterministicId.ForHandler(entity, form, evt, finalFunctionName, resolved.LibraryName),
                        resolved.Annotation.Parameters ?? ""));
                }

                var currentHandlers = FormXmlEventSerializer.GetHandlers(xdoc, evt);

                // R15: a Handler on a library this project doesn't track, and whose ID Flowline didn't
                // derive, is never evaluated for staleness or unrecognized status — it's simply carried
                // through untouched so the write-back doesn't drop it (SetHandlers replaces the whole
                // event's Handlers set on write).
                var foreignHandlers = new HashSet<FormHandler>();

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

                    var expectedId = FormEventDeterministicId.ForHandler(entity, form, evt, current.FunctionName, current.LibraryName);
                    if (current.HandlerUniqueId == expectedId)
                    {
                        // stale, Flowline-owned — drop automatically, not added to desiredHandlers below.
                        // Record its library as a removal CANDIDATE — only actually dropped after the form's
                        // other event confirms nothing still needs it (see the form-wide library pass below).
                        staleRemovedLibraryNames.Add(current.LibraryName);
                        continue;
                    }

                    if (!snapshot.TrackedLibraryNames.Contains(current.LibraryName))
                    {
                        foreignHandlers.Add(current);
                        continue;
                    }

                    unrecognized.Add(new UnrecognizedHandler(current, BuildProposedAnnotation(entity, form, evt, current)));
                }

                var desiredHandlers = new HashSet<FormHandler>(annotationDesired);
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

                perEventResults.Add((evt, desiredHandlers, unrecognized, currentHandlers, managedLibraryNames));
            }

            // Form-wide library decision, using both events' desired-handler results together.
            var neededLibraryNames = perEventResults
                .SelectMany(r => r.ManagedLibraryNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var desiredLibraries = new HashSet<FormLibraryEntry>(currentLibraries
                .Where(l => neededLibraryNames.Contains(l.Name) || !staleRemovedLibraryNames.Contains(l.Name)));
            foreach (var libraryName in neededLibraryNames)
            {
                if (desiredLibraries.Contains(new FormLibraryEntry(libraryName, Guid.Empty)))
                    continue;
                desiredLibraries.Add(new FormLibraryEntry(libraryName, FormEventDeterministicId.ForLibrary(libraryName)));
            }

            // desiredLibraries is now identical for every event of this form (computed once, above) — the
            // executor unions DesiredLibraries across whichever plan entries exist for a FormId, so only
            // ONE entry needs to carry it; an event with nothing of its own to do doesn't need its own
            // entry just because a DIFFERENT event on the same form needed a new/retired library.
            var librariesChangedForForm = !desiredLibraries.SetEquals(currentLibraries);
            var anyEntryEmitted = false;

            foreach (var (evt, desiredHandlers, unrecognized, currentHandlers, _) in perEventResults)
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
                    desiredLibraries));
            }

            // Narrow fallback: a form-wide library change with no individual event flagged (only reachable
            // if a library's last reference was removed via a path that doesn't itself flip an event's
            // handlersChanged — not expected given how staleRemovedLibraryNames is populated above, but
            // kept so a library-only change is never silently dropped). Attach it to the first event so the
            // executor's per-form library union still sees it.
            if (!anyEntryEmitted && librariesChangedForForm)
            {
                var (evt, desiredHandlers, unrecognized, _, _) = perEventResults[0];
                plan.Forms.Add(new FormEventFormPlan(
                    dataverseForm.Id,
                    entity,
                    form,
                    evt,
                    desiredHandlers,
                    unrecognized,
                    desiredLibraries));
            }
        }

        // Failures are isolated per-annotation above — every annotation gets a chance to resolve before
        // this throws, so a single unresolvable function never prevents the others from being planned.
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Form event annotations failed to resolve required functions:\n" + string.Join("\n", errors));

        return plan;
    }

    // HashSet.SetEquals can't detect a Parameters-only change (FormHandler's equality is identity-only) —
    // FormHandlerDiffer compares full record state on identity-matched pairs instead.
    static bool HandlerSetsFullyEqual(IReadOnlySet<FormHandler> a, IReadOnlySet<FormHandler> b) =>
        FormHandlerDiffer.Diff(a, b) is (0, 0, 0);

    // R18a: proposed annotation text built from the unrecognized handler's own stored state, in the same
    // `// flowline:onload/onsave <entity> "<form>" Function[(params)]` shape R1 defines. FunctionName is
    // used verbatim — including a dotted namespace (R6a's escape hatch), since a dotted name round-trips
    // through the annotation grammar without re-derivation.
    static string BuildProposedAnnotation(string entity, string form, FormEventType evt, FormHandler handler)
    {
        var directive = evt == FormEventType.OnLoad ? "onload" : "onsave";
        var parameters = string.IsNullOrEmpty(handler.Parameters) ? "" : $"({handler.Parameters})";
        return $"// flowline:{directive} {entity} \"{form}\" {handler.FunctionName}{parameters}";
    }

    static string DeriveAutoNamespace(string libraryName) =>
        ToPascalCase(Path.GetFileNameWithoutExtension(libraryName));

    // Mirrors src/Flowline/Templates/WebResources/rollup.config.mjs's toPascalCase so the auto-derived
    // function namespace matches what the Rollup-built bundle actually exports.
    static string ToPascalCase(string name) =>
        string.Concat(Regex.Split(name, "[^a-zA-Z0-9]+")
            .Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
