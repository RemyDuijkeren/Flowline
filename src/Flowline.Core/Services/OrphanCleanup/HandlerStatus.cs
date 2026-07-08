namespace Flowline.Core.Services.OrphanCleanup;

// Self-declared per handler (KTD2/R5) — the handler is the one place that knows its own confidence
// level, not a config file a user could misconfigure.
public enum HandlerStatus
{
    // Full detection, real Prio1/2/3 classification, included in the actionable report, eligible for
    // auto-delete when Auto (R6).
    Active,

    // Full detection, verbose findings printed, but excluded from the Prio1/2/3 report and never acts
    // (R7) — lets a handler ship and be field-tested with zero action risk before promotion to Active.
    Preview,
}
