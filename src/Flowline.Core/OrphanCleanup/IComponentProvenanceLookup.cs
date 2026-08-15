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
    // Never throws for a lookup that cannot answer: an implementation that fails, cannot run, or finds
    // no affirmative evidence returns ComponentProvenance.Undetermined (R8). Callers still guard, since
    // a faulting lookup must not fail the compare.
    Task<ComponentProvenance> ResolveAsync(ComponentSourceLocation location, CancellationToken ct);
}
