using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Flowline.Core.WebResources;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Migrates WebResource (61) detection and the // flowline:depends annotation exemption (U3) out of
// OrphanCleanupService's old dedicated exemption step (removed during U9's orchestrator rewrite).
// Auto/Manual is static Auto — every finding carries OrphanAction.Delete (R2); the orchestrator (U9)
// still owns the cross-solution Delete-vs-RemoveFromSolution override, same as it does for every other
// Auto handler's findings, since that check spans handlers and isn't this family's concern. Prio is a
// constant Prio3 (KTD8) — a WebResource never executes business logic, so it can never be Prio1/Prio2.
//
// Code-review fault-isolation fix: name resolution is now caught the same way the entity-detected
// handlers already catch their queries (KTD6) — a failed lookup degrades to the same bare-id display the
// unresolved-name path below already produces, rather than propagating uncaught.
public sealed class WebResourceHandler(IAnsiConsole console) : IOrphanHandler
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

        Dictionary<Guid, string> names;
        try
        {
            names = await EntityNameLookup.GetEntityNamesAsync(
                context.Service, "webresource", "webresourceid", "name", webResourceCandidates.Select(c => c.ObjectId), ct).ConfigureAwait(false);
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            names = [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.Warning($"WebResource name resolution failed ({Markup.Escape(ex.Message)}) — display falls back to bare id this run.");
            names = [];
        }

        // Scans the package source under WebResources — the content this deploy is actually packing and
        // importing — never WebResources/dist. Deploy promotes whatever's committed there; reading a
        // separate local build artifact here would check content that may not match what's shipping.
        var annotationRefs = WebResourceAnnotationParser.CollectAllReferences(Path.Combine(context.DataverseSolutionSrcRoot, "WebResources"));

        var findings = new List<HandlerFinding>();
        foreach (var candidate in webResourceCandidates)
        {
            var hasName = names.TryGetValue(candidate.ObjectId, out var name);

            // Still referenced via // flowline:depends elsewhere in the committed WebResources — exempt
            // it from the orphan report, announcing the suppression here since this handler is the only
            // place that resolves the name needed to print it (previously re-queried by the orchestrator
            // for the same purpose — see U9/this-pass cleanup).
            if (hasName && annotationRefs.Contains(name!))
            {
                console.Skip($"'{name}' preserved — referenced in // flowline:depends annotation.");
                continue;
            }

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
}
