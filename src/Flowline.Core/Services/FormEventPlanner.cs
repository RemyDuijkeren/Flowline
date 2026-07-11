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
                        continue; // stale, Flowline-owned — drop automatically, not added to desiredHandlers below

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

                // Libraries are add-only (never removed here) — start from everything currently on the form,
                // then add any library a desired handler needs that isn't already present.
                var desiredLibraries = new HashSet<FormLibraryEntry>(currentLibraries);
                foreach (var libraryName in desiredHandlers.Select(h => h.LibraryName).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (desiredLibraries.Contains(new FormLibraryEntry(libraryName, Guid.Empty)))
                        continue;
                    desiredLibraries.Add(new FormLibraryEntry(libraryName, FormEventDeterministicId.ForLibrary(libraryName)));
                }

                var handlersChanged = !HandlerSetsFullyEqual(desiredHandlers, currentHandlers);
                var librariesChanged = !desiredLibraries.SetEquals(currentLibraries);
                if (!handlersChanged && !librariesChanged && unrecognized.Count == 0)
                    continue;

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
