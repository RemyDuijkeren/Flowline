using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Services;

// PackageSrcRoot is the only source-of-truth for committed source — OrphanCleanupService parses it
// itself (ComponentClassifier.ParseLocalSource) rather than receiving pre-parsed LocalComponents/
// EntityLogicalNames/NamedComponents fields, since it's the only IPostDeployService implementer that
// ever reads them.
public sealed record PostDeployContext(
    IOrganizationServiceAsync2 Service,
    string SolutionName,
    RunMode Mode,
    string PackagePath,
    string EnvironmentUrl,
    string PackageSrcRoot);

public interface IPostDeployService
{
    Task RunPreImportAsync(PostDeployContext context, CancellationToken ct);
    Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct);
}
