namespace Flowline.Core.Models;

/// <summary>
/// Outcome of probing whether a PAC auth profile's already-cached credentials can reach a target
/// Dataverse environment — used by ProfileResolutionService to decide whether PAC's active profile
/// can serve a request before falling back to URL-based profile matching.
/// </summary>
public abstract record ReachabilityProbeResult;

/// <summary>The profile answered the target environment successfully.</summary>
public record ProbeReachable : ReachabilityProbeResult;

/// <summary>The target environment rejected or could not find the profile's credentials
/// (401/403/404, or any other non-success response) — another profile may still reach it, so
/// resolution should fall through to today's URL-matching chain.</summary>
public record ProbeUnauthorizedOrNotFound : ReachabilityProbeResult;

/// <summary>The probe couldn't complete — network failure or timeout, not a credentials problem.
/// Another profile would fail identically, so resolution should not keep trying other profiles.</summary>
public record ProbeTransportFailure(Exception Exception) : ReachabilityProbeResult;
