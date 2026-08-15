namespace Flowline.Core.OrphanCleanup;

// Mirrors OrphanEntry's ObjectId/ComponentType/EntityName/DisplayName/Action shape (see
// OrphanCleanupService.cs) — reuses the existing OrphanAction enum for the handler's static Auto/Manual
// axis rather than inventing a parallel one — plus Priority (per-instance) and SequenceHint/Timing
// (ordering and timing), which each handler owns for its own findings.
public sealed record HandlerFinding(
    Guid ObjectId,
    int ComponentType,
    string DisplayName,
    OrphanAction Action,
    OrphanPriority Priority,

    // Small non-negative int scoped to the handler's own family only (0 = executes first, i.e. deepest
    // child in that family) — the centralized executor sorts entries within a family by ascending
    // SequenceHint. Not a global position; cross-family order is a separate, explicit list owned by the
    // orchestrator.
    int SequenceHint,

    OrphanTiming Timing,

    // Non-null only for entity-detected findings (CustomApi family, Bot, ConnectionReference), same as
    // OrphanEntry.EntityName today.
    string? EntityName = null)
{
    // R12/KTD4: where this component's identity lives in local source, declared by the handler that
    // matched it rather than re-derived downstream from its component type. Left at None by a handler
    // with no mapping, which resolves to Undetermined (R8). Init property so existing positional call
    // sites keep compiling.
    public LocalSourceIdentity Identity { get; init; } = LocalSourceIdentity.None;
}
