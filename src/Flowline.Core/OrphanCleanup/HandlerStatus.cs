namespace Flowline.Core.OrphanCleanup;

// Self-declared per handler — the handler is the one place that knows its own confidence level, not a
// config file a user could misconfigure.
public enum HandlerStatus
{
    // Full detection, real Prio1/2/3 classification, included in the actionable report, eligible for
    // auto-delete when Auto.
    Active,

    // Full detection, verbose findings printed, but excluded from the Prio1/2/3 report and never acts —
    // lets a handler ship and be field-tested with zero action risk before promotion to Active.
    Preview,
}
