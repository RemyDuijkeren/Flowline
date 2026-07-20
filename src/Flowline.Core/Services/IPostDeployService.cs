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

// PackageSrcRoot is an unpacked copy of whatever zip DeployCommand actually imported (freshly packed,
// reused from the artifact cache, or supplied via --path) — not necessarily the committed package
// source folder itself; DeployCommand always unpacks PackagePath into a temp directory before building this
// context, so PackageSrcRoot reflects the real imported content even when it wasn't packed just now
// from the current checkout. OrphanCleanupService parses it itself (ComponentClassifier.ParseLocalSource)
// rather than receiving pre-parsed LocalComponents/EntityLogicalNames/NamedComponents fields, since it's
// the only IPostDeployService implementer that ever reads them.
public sealed record PostDeployContext(
    IOrganizationServiceAsync2 Service,
    DeploySolutionInfo Solution,
    RunMode Mode,
    string PackagePath,
    string PackageSrcRoot);

public interface IPostDeployService
{
    Task RunPreImportAsync(PostDeployContext context, CancellationToken ct);
    Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct);
}
