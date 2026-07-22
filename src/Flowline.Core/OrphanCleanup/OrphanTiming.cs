namespace Flowline.Core.OrphanCleanup;

// A component type can be known in advance to be unsafe or disallowed to delete before a successful
// import — a static, handler-level declaration, distinct from the existing *reactive*
// dependency-deferral that only fires when a delete attempt actually throws a dependency fault.
public enum OrphanTiming
{
    // Safe to attempt during the pre-import execution pass (still subject to reactive deferral on a
    // dependency fault).
    PreImportEligible,

    // Never attempted pre-import; only ever attempted after a confirmed-successful import.
    PostImportOnly,
}
