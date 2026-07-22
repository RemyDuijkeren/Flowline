namespace Flowline.Core.OrphanCleanup;

// The risk-priority axis, orthogonal to OrphanAction's Auto/Manual axis. Decided per instance inside
// the handler that owns the type — a type may be capable of more than one Prio outcome. None is first
// (default(OrphanPriority) == None) so an accidentally-unset value never silently reads as a real
// priority — only Active-handler findings ever carry Prio1/2/3; the generic fallback gets no Prio.
public enum OrphanPriority
{
    None,

    // Blocks deployment.
    Prio1,

    // Silently still running deleted logic.
    Prio2,

    // Safe to clean up.
    Prio3,
}
