using Flowline.Core.WebResources;
using Flowline.Core.Console;
using Spectre.Console;

namespace Flowline.Core.OrphanCleanup.Handlers;

// Handles WebResource (61) detection and the // flowline:depends annotation exemption. Auto/Manual is
// static Auto; the orchestrator still owns the cross-solution Delete-vs-RemoveFromSolution override.
// Prio is a constant Prio3 — a WebResource never executes business logic.
//
// Name resolution is caught — a failed lookup degrades to the bare-id display fallback below.
public sealed class WebResourceHandler(IAnsiConsole console) : IOrphanHandler
{
    const int WebResourceComponentType = 61;

    public HandlerStatus Status => HandlerStatus.Active;

    public Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct)
    {
        // Scans the package source under WebResources — the content this deploy is actually packing and
        // importing — never WebResources/dist. Deploy promotes whatever's committed there; reading a
        // separate local build artifact here would check content that may not match what's shipping.
        var annotationRefs = WebResourceAnnotationParser.CollectAllReferences(Path.Combine(context.DataverseSolutionSrcRoot, "WebResources"));

        return NameLookupDetectionHelper.DetectByComponentTypeAsync(
            context, candidates, console, ct,
            componentType: WebResourceComponentType,
            entityLogicalName: "webresource",
            idAttribute: "webresourceid",
            nameAttribute: "name",
            label: "WebResource",
            action: OrphanAction.Delete,
            isExempt: (_, name) =>
            {
                // Still referenced via // flowline:depends elsewhere in the committed WebResources —
                // exempt it from the orphan report.
                if (name is null || !annotationRefs.Contains(name)) return false;
                console.Skip($"'{name}' preserved — referenced in // flowline:depends annotation.");
                return true;
            });
    }
}
