using Spectre.Console;

namespace Flowline.Core.OrphanCleanup;

// Shared componenttype-filtered, name-lookup-only detection for handlers whose id is already declared
// in Solution.xml's RootComponents — no local-source scanner needed, only a display-name resolution
// (see RoleHandler/WebResourceHandler doc comments).
public static class NameLookupDetectionHelper
{
    public static async Task<HandlerDetectionResult> DetectByComponentTypeAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        IAnsiConsole console,
        CancellationToken ct,
        int componentType,
        string entityLogicalName,
        string idAttribute,
        string nameAttribute,
        string label,
        OrphanAction action,
        Func<Guid, string?, bool>? isExempt = null)
    {
        var typeCandidates = candidates.Where(c => c.ComponentType == componentType).ToList();
        if (typeCandidates.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        // Every candidate of this componenttype is claimed — this handler always recognizes it as its
        // own, even one an exemption suppresses out of Findings.
        var claimedIds = typeCandidates.Select(c => c.ObjectId).ToHashSet();

        var names = await DataverseFaultTolerance.TryQueryAsync(
            () => EntityNameLookup.GetEntityNamesAsync(context.Service, entityLogicalName, idAttribute, nameAttribute, typeCandidates.Select(c => c.ObjectId), ct),
            [], console, msg => $"{label} name resolution failed ({msg}) — display falls back to bare id this run.");

        var findings = new List<HandlerFinding>();
        foreach (var candidate in typeCandidates)
        {
            var hasName = names.TryGetValue(candidate.ObjectId, out var name);

            if (isExempt != null && isExempt(candidate.ObjectId, hasName ? name : null))
                continue;

            var displayName = hasName ? $"{label} '{name}' ({candidate.ObjectId})" : $"{label} {candidate.ObjectId}";

            findings.Add(new HandlerFinding(
                candidate.ObjectId,
                componentType,
                displayName,
                action,
                OrphanPriority.Prio3,
                SequenceHint: 0, // Only type in its family — no ordering to express
                OrphanTiming.PreImportEligible));
        }

        return new HandlerDetectionResult(findings, claimedIds);
    }
}
