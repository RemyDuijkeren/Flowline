using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Services;

public sealed record PostDeployContext(
    IOrganizationServiceAsync2 Service,
    string SolutionName,
    IReadOnlyList<(Guid ObjectId, int ComponentType)> LocalComponents,
    RunMode Mode,
    string PackagePath,
    string EnvironmentUrl,
    IReadOnlyList<string> EntityLogicalNames,
    string PackageSrcRoot,
    IReadOnlyList<(int ComponentType, string SchemaName)> NamedComponents);

public interface IPostDeployService
{
    Task RunPreImportAsync(PostDeployContext context, CancellationToken ct);
    Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct);
}
