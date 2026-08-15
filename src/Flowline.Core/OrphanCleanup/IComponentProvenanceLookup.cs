namespace Flowline.Core.OrphanCleanup;

// KTD1: the engine declares the lookup, the CLI supplies it. Core has no subprocess library and the
// report renders from inside Core, so the verdict has to be resolvable here without Core reaching
// across the one-way project boundary. Flowline.Services.GitComponentProvenanceLookup is the
// implementation; engine tests fake this instead of building git repositories.
//
// Deliberately free of Dataverse types — it takes what identifies a component locally and returns a
// verdict.
public interface IComponentProvenanceLookup
{
    // KTD2: checkoutSolutionSrcRoot is the checkout's own solution source folder, supplied per call —
    // on deploy the compare's own source root is a temp extraction with no history at all, so the caller
    // tells the lookup where the equivalent folder lives in the checkout instead. Null (no mapping
    // available, e.g. a stand-alone artifact) is a valid input, not an error.
    //
    // Never throws for a lookup that cannot answer: an implementation that fails, cannot run, or finds
    // no affirmative evidence returns ComponentProvenance.Undetermined (R8). Callers still guard, since
    // a faulting lookup must not fail the compare.
    Task<ComponentProvenance> ResolveAsync(string? checkoutSolutionSrcRoot, ComponentSourceLocation location, CancellationToken ct);
}
