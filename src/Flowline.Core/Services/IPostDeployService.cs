using Microsoft.PowerPlatform.Dataverse.Client;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

// ExistsInTarget is the solution's presence in the target environment as of the pre-deploy check —
// true whenever the target already had a prior version installed, which is also exactly the condition
// under which a managed deploy imports as a Dataverse Upgrade (see DeployCommand.useStageAndUpgrade).
// Consumers derive their own presentation from IncludeManaged/ExistsInTarget rather than being handed a
// pre-rendered message — see OrphanCleanupService.BuildNoDeleteHint.
public sealed record DeploySolutionInfo(
    string Name,
    string EnvironmentUrl,
    bool IncludeManaged,
    bool ExistsInTarget);

// DataverseSolutionSrcRoot is an unpacked copy of whatever zip DeployCommand actually imported (freshly packed,
// reused from the artifact cache, or supplied via --path) — not necessarily the committed Dataverse solution
// source folder itself; DeployCommand always unpacks PackagePath into a temp directory before building this
// context, so DataverseSolutionSrcRoot reflects the real imported content even when it wasn't packed just now
// from the current checkout. OrphanCleanupService parses it itself (ComponentClassifier.ParseLocalSource)
// rather than receiving pre-parsed LocalComponents/EntityLogicalNames/NamedComponents fields, since it's
// the only IPostDeployService implementer that ever reads them.
public sealed record PostDeployContext(
    IOrganizationServiceAsync2 Service,
    DeploySolutionInfo Solution,
    RunMode Mode,
    string PackagePath,
    string DataverseSolutionSrcRoot,
    // `--force delete-orphans` consent — lets Guarded handlers delete on this deploy. Defaults false so
    // Guarded stays report-only unless the user explicitly opted in. Ignored in report-only run modes.
    bool DeleteOrphansConsent = false,
    // KTD2: the checkout's own solution source folder (Solution/src, or wherever the solution file
    // resolves it) — distinct from DataverseSolutionSrcRoot, which on the packed/cached deploy route is a
    // temp extraction with no git history. Null on the --path route, where DeployCommand has no checkout
    // folder to offer (a solution file isn't required there); the provenance lookup then reads every
    // entry as Undetermined rather than guessing a path.
    string? CheckoutSolutionSrcRoot = null);

// FIX A: replaces the plain finding count so a service can also say "I couldn't verify" (Inconclusive)
// without that being read as a clean pass, and so DeployCommand can resolve the right ExitCode without
// sniffing which concrete service produced it (KTD5). PreferredExitCode is null for a clean outcome and
// for the four services that only ever no-op post-import.
public readonly record struct PostDeployOutcome(int Findings, bool Inconclusive, ExitCode? PreferredExitCode)
{
    public static PostDeployOutcome Clean { get; } = new(0, false, null);
}

public interface IPostDeployService
{
    Task RunPreImportAsync(PostDeployContext context, CancellationToken ct);
    Task<PostDeployOutcome> RunPostImportAsync(PostDeployContext context, CancellationToken ct);
}
