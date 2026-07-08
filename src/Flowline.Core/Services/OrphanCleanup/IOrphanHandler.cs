namespace Flowline.Core.Services.OrphanCleanup;

// Mirrors IPostDeployService's shape (see IPostDeployService.cs) — a small interface, DI-resolved as
// IEnumerable<IOrphanHandler> (KTD7), same fan-out convention documented in
// docs/solutions/architecture-patterns/post-deploy-service-di-fanout-protocol.md.
//
// "Match" is not a cheap synchronous predicate for every handler (see the Planning Contract's HTD
// note): componenttype-gated handlers (PluginAssembly family, WebResource, Workflow, Role, Entity
// family) can match by componenttype alone, but the three entity-detected handlers (CustomApi family,
// Bot, ConnectionReference) can only tell whether a candidate is theirs by querying their own backing
// table — for those, match and detect are the same batched async call. DetectAsync accommodates both
// shapes uniformly: the orchestrator (U9) hands every still-unclaimed candidate to each handler once,
// and the handler returns the findings it claims (filtering by componenttype then verifying, or
// batch-querying then matching) alongside every candidate it recognized as its own (ClaimedIds), even
// when a recognized candidate produced no finding (see HandlerDetectionResult).
public interface IOrphanHandler
{
    HandlerStatus Status { get; }

    Task<HandlerDetectionResult> DetectAsync(
        DetectionContext context,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
        CancellationToken ct);
}
