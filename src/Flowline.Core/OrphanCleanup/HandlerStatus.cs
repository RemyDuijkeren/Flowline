namespace Flowline.Core.OrphanCleanup;

// Self-declared per handler — the handler is the one place that knows its own confidence level, not a
// config file a user could misconfigure. An escalating ladder of autonomy: each rung acts more than the
// last, and a handler is promoted up it as live integration evidence accumulates. The axis is *action*,
// not maturity — maturity is just the policy for which rung to assign.
public enum HandlerStatus
{
    // Detect, print to the verbose log only. Never surfaces in the actionable report, never acts.
    // For a brand-new handler whose detection may still give false-positive — collect field data quietly
    // before showing anything to the user.
    Silent,

    // Detect and surface in the report, but never delete. Trusted to *find* orphans, not yet trusted
    // (or not yet permitted) to *remove* them — the user cleans them up manually. This is the safe
    // default for a handler whose detection isn't live-proven against real Dataverse yet.
    Report,

    // Detect, surface, and delete — but only with explicit consent (`--force delete-orphans`; a TTY
    // prompt is a later refinement). A maturity waystation: tested enough to act, not yet trusted for
    // unattended auto-delete. Without the consent it behaves exactly like Report (surfaces, never deletes it),
    // so a non-interactive run never blocks and never deletes by surprise.
    Guarded,

    // Detect, surface, and auto-delete with no extra gate (subject only to the run mode — dry-run and
    // managed still force report-only). Fully trusted after live integration verification.
    Auto,
}
