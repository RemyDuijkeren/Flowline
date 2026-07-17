namespace Flowline.Core.OrphanCleanup;

// Mirrors OrphanEntry's ObjectId/ComponentType/EntityName/DisplayName/Action shape (see
// OrphanCleanupService.cs) — reuses the existing OrphanAction enum for the handler's static Auto/Manual
// axis rather than inventing a parallel one — plus the two new fields each handler now owns: Priority
// (R3, per-instance) and SequenceHint/Timing (R11/R12, ordering and timing).
public sealed record HandlerFinding(
    Guid ObjectId,
    int ComponentType,
    string DisplayName,
    OrphanAction Action,
    OrphanPriority Priority,

    // KTD1: small non-negative int scoped to the handler's own family only (0 = executes first, i.e.
    // deepest child in that family) — the centralized executor sorts entries within a family by
    // ascending SequenceHint. Not a global position; cross-family order is a separate, explicit list
    // owned by the orchestrator (U9).
    int SequenceHint,

    OrphanTiming Timing,

    // Non-null only for entity-detected findings (CustomApi family, Bot, ConnectionReference), same as
    // OrphanEntry.EntityName today.
    string? EntityName = null);
