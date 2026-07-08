using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Services.OrphanCleanup;

// Primitives, not PostDeployContext (matching the comparison engine's own established shape — see
// OrphanCleanupService.CompareAsync's doc comment on KTD12) — a handler's dependencies should be
// exactly what it needs, not a deploy-pipeline type carrying fields (like PackagePath) it never reads.
// EntityLogicalNames is required by U8's ResolveAttributeInfoAsync-driven attribute check, which today
// takes it as an explicit parameter rather than deriving it from PackageSrcRoot alone.
public sealed record DetectionContext(
    string PackageSrcRoot,
    IOrganizationServiceAsync2 Service,
    string SolutionName,
    string EnvironmentUrl,
    RunMode Mode,
    IReadOnlyList<string> EntityLogicalNames);
