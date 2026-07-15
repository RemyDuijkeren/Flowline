using System.Xml.Linq;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

// U3: on a name-lookup miss (FormEventReader's globalMatches.Count == 0 branch), combines three advisory
// signals into a single tiered suggestion appended to the existing failure message — self-tag match
// (strongest: the form's own previously-written handler fingerprint), rename-cache lookup (probable), and
// sole-surviving-form-on-entity (weakest, clearly hedged). R5: this NEVER resolves a form or lets
// registration succeed — FormEventReader still throws regardless of what (if anything) is returned here.
public static class FormEventRenameAdvisor
{
    enum Confidence { Strong, Probable, Hedged }

    public static string? Suggest(
        string entity,
        string requestedName,
        IReadOnlyList<ResolvedFormEventAnnotation> sharingAnnotations,
        IReadOnlyList<DataverseForm> solutionForms,
        FormEventIdentityCache? cache)
    {
        var candidatesForEntity = solutionForms
            .Where(f => string.Equals(f.EntityLogicalName, entity, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 1. Self-tag (strongest): does a live candidate's own formxml still carry a Handler whose
        // deterministic id matches what one of the sharing annotations would have produced when it was
        // originally registered against requestedName? Only Flowline's own derivation could have produced
        // that exact id, so a match is proof this candidate WAS the form the annotation used to target.
        var selfTagName = FindSelfTagMatch(entity, requestedName, sharingAnnotations, candidatesForEntity);
        if (selfTagName is not null)
            return BuildSuggestion(entity, selfTagName, sharingAnnotations, Confidence.Strong);

        // 2. Rename cache: a previous push resolved (entity, requestedName) to a formId that still
        // exists live (in this solution), just possibly under a different name now.
        var cachedFormId = cache?.TryGet(entity, requestedName);
        if (cachedFormId is { } formId)
        {
            var cacheMatches = solutionForms.Where(f => f.Id == formId).ToList();
            if (cacheMatches.Count == 1)
                return BuildSuggestion(entity, cacheMatches[0].Name, sharingAnnotations, Confidence.Probable);
        }

        // 3. Sole survivor (weakest): exactly one live form remains on this entity — Main and Quick
        // Create both count, no type-scoping (they already share one (Entity, Name) pool upstream).
        if (candidatesForEntity.Count == 1)
            return BuildSuggestion(entity, candidatesForEntity[0].Name, sharingAnnotations, Confidence.Hedged);

        return null;
    }

    static string? FindSelfTagMatch(
        string entity, string requestedName,
        IReadOnlyList<ResolvedFormEventAnnotation> sharingAnnotations,
        List<DataverseForm> candidatesForEntity)
    {
        foreach (var resolved in sharingAnnotations)
        {
            var evt = resolved.Annotation.Event;
            var (requestedFunctionName, autoNamespace, isExplicit) = FormEventPlanner.DeriveHandlerResolutionInputs(resolved, evt);

            var (finalFunctionName, found, _) = FormEventFunctionResolver.Resolve(
                resolved.Content, requestedFunctionName, autoNamespace, isExplicit);
            if (!found)
                continue; // no evidence from this annotation — other signals (or another sharing annotation) may still fire.

            // Recomputed with requestedName (the annotation's still-unrenamed target) — that's what was
            // true when the handler was originally registered, not any candidate's current live name.
            // attribute is non-null for OnChange/TabStateChange/OnReadyStateComplete — the deterministic id
            // and the XML scan both still need it to target the right event element. Normalized for
            // OnReadyStateComplete to match FormEventPlanner's canonical (bare, prefix-stripped) hashing
            // convention — otherwise a "IFRAME_"-prefixed annotation would never self-tag-match the handler
            // the planner actually wrote, which hashed against the stripped form.
            var attribute = evt == FormEventType.OnReadyStateComplete && resolved.Annotation.Attribute is not null
                ? FormXmlEventSerializer.NormalizeIframeControlId(resolved.Annotation.Attribute)
                : resolved.Annotation.Attribute;
            var expectedId = FormEventDeterministicId.ForHandler(entity, requestedName, evt, finalFunctionName!, resolved.LibraryName, attribute);

            foreach (var candidate in candidatesForEntity)
            {
                // Unlike FormEventPlanner's XDocument.Parse (only ever run on cleanly, uniquely resolved
                // forms), this runs over every live candidate on the entity — including ones this feature
                // never validated formxml for. A malformed/empty formxml here must degrade this candidate
                // out of self-tag consideration, not crash the whole advisory pass (R5: never worsen the
                // outcome of an already-failing push).
                XDocument xdoc;
                try { xdoc = XDocument.Parse(candidate.FormXml); }
                catch (Exception) { continue; }

                if (FormXmlEventSerializer.GetHandlers(xdoc, evt, attribute).Any(h => h.HandlerUniqueId == expectedId))
                    return candidate.Name;
            }
        }

        return null;
    }

    // R6: names the candidate and shows the exact annotation text to change — one line per sharing
    // annotation, since each event (onLoad/onSave) sharing this (entity, form) pair carries its own
    // function/library. Mirrors FormEventExecutor.FormatUnrecognizedHandlerLine's "state the problem,
    // then show the exact fix" two-line shape.
    static string BuildSuggestion(
        string entity, string candidateName, IReadOnlyList<ResolvedFormEventAnnotation> sharingAnnotations, Confidence confidence)
    {
        var wording = confidence switch
        {
            Confidence.Strong => $"this form was renamed to '{candidateName}' — same handler signature",
            Confidence.Probable => $"a previous push resolved this to a form now named '{candidateName}' — may have been renamed",
            Confidence.Hedged => $"'{candidateName}' is the only form left on entity '{entity}' — check if this is what you meant",
            _ => throw new ArgumentOutOfRangeException(nameof(confidence))
        };

        var annotationLines = sharingAnnotations
            .Select(a => BuildSuggestedAnnotation(entity, candidateName, a))
            .Distinct()
            .Select(line => $"{Environment.NewLine}        {line}");

        return $"{Environment.NewLine}      — {wording}"
            + $"{Environment.NewLine}      — update your annotation to:"
            + string.Concat(annotationLines);
    }

    // Mirrors FormEventPlanner.BuildProposedAnnotation's shape, but reconstructed from the annotation as
    // originally written (FunctionName/Parameters may be null/defaulted) rather than a resolved Handler.
    // KTD6: Attribute is non-null for onchange/tabstatechange/onreadystatecomplete alike, so a null check
    // already distinguishes them from onload/onsave without listing each attribute-scoped FormEventType —
    // a rename suggestion for a Tab/IFRAME-scoped annotation now correctly includes its scope token instead
    // of rendering text that wouldn't parse back through the mandatory-scope-token regex (R2).
    static string BuildSuggestedAnnotation(string entity, string candidateName, ResolvedFormEventAnnotation resolved)
    {
        var directive = resolved.Annotation.Event.ToString().ToLowerInvariant();
        var attribute = resolved.Annotation.Attribute is not null ? $" {resolved.Annotation.Attribute}" : "";
        var function = resolved.Annotation.FunctionName is null ? "" : $" {resolved.Annotation.FunctionName}";
        var parameters = string.IsNullOrEmpty(resolved.Annotation.Parameters) ? "" : $"({resolved.Annotation.Parameters})";
        return $"// flowline:{directive} {entity} \"{candidateName}\"{attribute}{function}{parameters}";
    }
}
