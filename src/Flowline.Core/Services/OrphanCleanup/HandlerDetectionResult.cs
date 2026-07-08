namespace Flowline.Core.Services.OrphanCleanup;

// Findings alone can't distinguish "not this handler's type" from "this handler's type,
// confirmed not orphaned" — both are simply absent from Findings. ClaimedIds closes that
// gap: it's every candidate this handler positively recognized as belonging to its
// family, whether or not it ended up in Findings. The orchestrator (U9) computes the
// generic-fallback set as candidates outside the union of every handler's ClaimedIds —
// never by checking Findings alone — so a recognized-but-clean candidate is silently
// excluded from both the report and fallback, matching today's behavior exactly.
public sealed record HandlerDetectionResult(
    IReadOnlyList<HandlerFinding> Findings,
    IReadOnlySet<Guid> ClaimedIds);
