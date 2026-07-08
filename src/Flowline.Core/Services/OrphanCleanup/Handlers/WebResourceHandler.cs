using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Flowline.Core.Services.OrphanCleanup.Handlers;

// Migrates WebResource (61) detection and the // flowline:depends annotation exemption (U3) out of
// OrphanCleanupService.ExemptAnnotationReferencedWebResourcesAsync. Auto/Manual is static Auto — every
// finding carries OrphanAction.Delete (R2); the orchestrator (U9) still owns the cross-solution
// Delete-vs-RemoveFromSolution override, same as it does for every other Auto handler's findings, since
// that check spans handlers and isn't this family's concern. Prio is a constant Prio3 (KTD8) — a
// WebResource never executes business logic, so it can never be Prio1/Prio2.
public sealed class WebResourceHandler : IOrphanHandler
{
    const int WebResourceComponentType = 61;

    public HandlerStatus Status => HandlerStatus.Active;

    public async Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        var webResourceCandidates = candidates.Where(c => c.ComponentType == WebResourceComponentType).ToList();
        if (webResourceCandidates.Count == 0) return new HandlerDetectionResult([], new HashSet<Guid>());

        // Every componenttype-61 candidate is claimed, even one the annotation exemption below
        // suppresses out of Findings — it's still a recognized WebResource, just not orphaned.
        var claimedIds = webResourceCandidates.Select(c => c.ObjectId).ToHashSet();

        var names = await GetWebResourceNamesAsync(context.Service, webResourceCandidates.Select(c => c.ObjectId), ct).ConfigureAwait(false);

        // Scans Package/src/WebResources — the content this deploy is actually packing and importing —
        // never WebResources/dist. Deploy promotes whatever's committed in Package/src; reading a
        // separate local build artifact here would check content that may not match what's shipping.
        var annotationRefs = WebResourceAnnotationParser.CollectAllReferences(Path.Combine(context.PackageSrcRoot, "WebResources"));

        var findings = new List<HandlerFinding>();
        foreach (var candidate in webResourceCandidates)
        {
            var hasName = names.TryGetValue(candidate.ObjectId, out var name);

            // Still referenced via // flowline:depends elsewhere in the committed WebResources — exempt
            // it, matching today's ExemptAnnotationReferencedWebResourcesAsync behavior exactly.
            if (hasName && annotationRefs.Contains(name!))
                continue;

            var displayName = hasName ? $"WebResource '{name}' ({candidate.ObjectId})" : $"WebResource {candidate.ObjectId}";

            findings.Add(new HandlerFinding(
                candidate.ObjectId,
                WebResourceComponentType,
                displayName,
                OrphanAction.Delete,
                OrphanPriority.Prio3,
                SequenceHint: 0, // WebResource is the only type in this family — no ordering to express
                OrphanTiming.PreImportEligible));
        }

        return new HandlerDetectionResult(findings, claimedIds);
    }

    static async Task<Dictionary<Guid, string>> GetWebResourceNamesAsync(
        IOrganizationServiceAsync2 service,
        IEnumerable<Guid> ids,
        CancellationToken ct)
    {
        var idList = ids.Distinct().Where(id => id != Guid.Empty).ToList();
        if (idList.Count == 0) return [];

        var query = new QueryExpression("webresource")
        {
            ColumnSet = new ColumnSet("name"),
            Criteria  = { Conditions = { new ConditionExpression("webresourceid", ConditionOperator.In, idList.Select(id => (object)id).ToArray()) } }
        };

        var entities = await service.RetrieveAllAsync(query, ct).ConfigureAwait(false);
        return entities
            .Where(e => !string.IsNullOrEmpty(e.GetAttributeValue<string>("name")))
            .ToDictionary(e => e.Id, e => e.GetAttributeValue<string>("name")!);
    }
}
