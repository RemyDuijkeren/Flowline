using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.OrphanCleanup;

// Primitives, not PostDeployContext (matching the comparison engine's own established shape — see
// OrphanCleanupService.CompareAsync's doc comment) — a handler's dependencies should be exactly what it
// needs, not a deploy-pipeline type carrying fields (like PackagePath) it never reads.
// EntityLogicalNames is required by EntityFamilyHandler's ResolveAttributeInfoAsync-driven attribute
// check, which takes it as an explicit parameter rather than deriving it from DataverseSolutionSrcRoot
// alone.
public sealed record DetectionContext(
    string DataverseSolutionSrcRoot,
    IOrganizationServiceAsync2 Service,
    string SolutionName,
    RunMode Mode,
    IReadOnlyList<string> EntityLogicalNames,
    // Explicit `--force delete-orphans` consent. Promotes Guarded handlers from report-only to
    // actionable; irrelevant to Auto (always acts), Report/Silent (never act), and any report-only run
    // mode (dry-run/managed). Defaults false so every existing call site stays report-safe.
    bool DeleteOrphansConsent = false);
