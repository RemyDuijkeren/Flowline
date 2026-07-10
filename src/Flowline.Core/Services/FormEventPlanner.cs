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

        // Group by (Entity, Form) first, not by (Entity, Form, Event) — a form with both onLoad and
        // onSave annotations shares one formxml, so parse it once per form instead of once per event.
        var formGroups = snapshot.Annotations
            .GroupBy(a => (a.Annotation.Entity, a.Annotation.Form));

        foreach (var formGroup in formGroups)
        {
            var (entity, form) = formGroup.Key;
            var dataverseForm = snapshot.Forms[(entity, form)];
            var xdoc = XDocument.Parse(dataverseForm.FormXml);
            var currentLibraries = FormXmlEventSerializer.GetLibraries(xdoc);

            foreach (var group in formGroup.GroupBy(a => a.Annotation.Event))
            {
                var evt = group.Key;

                // Desired handlers as derived purely from this push's annotations. Always computes a fresh
                // deterministic HandlerUniqueId, so a matched-by-identity entry with changed Parameters
                // naturally lands here with the new Parameters value (no separate "update" bucket needed).
                var annotationDesired = new HashSet<FormHandler>();
                foreach (var resolved in group)
                {
                    var requestedFunctionName = resolved.Annotation.FunctionName
                        ?? (evt == FormEventType.OnLoad ? "onLoad" : "onSave");
                    var autoNamespace = DeriveAutoNamespace(resolved.LibraryName);

                    var (resolvedFunctionName, found) = FormXmlEventSerializer.ResolveFunction(
                        resolved.Content, requestedFunctionName, autoNamespace);

                    if (!found)
                    {
                        errors.Add($"{resolved.SourceFile}: function '{requestedFunctionName}' not found in library '{resolved.LibraryName}'.");
                        continue;
                    }

                    annotationDesired.Add(new FormHandler(
                        resolvedFunctionName!,
                        resolved.LibraryName,
                        FormEventDeterministicId.ForHandler(entity, form, evt, resolvedFunctionName!, resolved.LibraryName),
                        resolved.Annotation.Parameters ?? ""));
                }

                var currentHandlers = FormXmlEventSerializer.GetHandlers(xdoc, evt);

                // Current entries with no matching annotation: safe to drop if Flowline's own deterministic
                // derivation still matches the stored id (nothing else could have produced that exact id),
                // otherwise it's unrecognized — keep by default (R18), executor decides removal after confirmation.
                var unrecognized = new HashSet<FormHandler>();
                foreach (var current in currentHandlers)
                {
                    if (annotationDesired.Contains(current))
                        continue;

                    var expectedId = FormEventDeterministicId.ForHandler(entity, form, evt, current.FunctionName, current.LibraryName);
                    if (current.HandlerUniqueId == expectedId)
                        continue;

                    unrecognized.Add(current);
                }

                var desiredHandlers = new HashSet<FormHandler>(annotationDesired);
                foreach (var u in unrecognized)
                    desiredHandlers.Add(u);

                // Libraries are add-only (never removed here) — start from everything currently on the form,
                // then add any library a desired handler needs that isn't already present.
                var desiredLibraries = new HashSet<FormLibraryEntry>(currentLibraries);
                foreach (var libraryName in desiredHandlers.Select(h => h.LibraryName).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (desiredLibraries.Any(l => string.Equals(l.Name, libraryName, StringComparison.OrdinalIgnoreCase)))
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
                    dataverseForm.Name,
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

    // FormHandler's Equals/GetHashCode are identity-only (FunctionName+LibraryName, R12 dedup key) so
    // HashSet.SetEquals can't detect a Parameters-only change — compare full record equality here instead.
    static bool HandlerSetsFullyEqual(IReadOnlySet<FormHandler> a, IReadOnlySet<FormHandler> b)
    {
        if (a.Count != b.Count) return false;

        var byIdentity = b.ToDictionary(h => (h.FunctionName.ToLowerInvariant(), h.LibraryName.ToLowerInvariant()));

        foreach (var handler in a)
        {
            var key = (handler.FunctionName.ToLowerInvariant(), handler.LibraryName.ToLowerInvariant());
            if (!byIdentity.TryGetValue(key, out var match))
                return false;
            if (match.HandlerUniqueId != handler.HandlerUniqueId || match.Parameters != handler.Parameters)
                return false;
        }

        return true;
    }

    static string DeriveAutoNamespace(string libraryName)
    {
        var lastSlash = libraryName.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? libraryName[(lastSlash + 1)..] : libraryName;
        var lastDot = fileName.LastIndexOf('.');
        var baseName = lastDot >= 0 ? fileName[..lastDot] : fileName;
        return ToPascalCase(baseName);
    }

    // Mirrors src/Flowline/Templates/WebResources/rollup.config.mjs's toPascalCase so the auto-derived
    // function namespace matches what the Rollup-built bundle actually exports.
    static string ToPascalCase(string name) =>
        string.Concat(Regex.Split(name, "[^a-zA-Z0-9]+")
            .Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
