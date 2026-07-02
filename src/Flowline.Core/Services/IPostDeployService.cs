using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Services;

public sealed record PostDeployContext(
    IOrganizationServiceAsync2 Service,
    string SolutionName,
    IReadOnlyList<(Guid ObjectId, int ComponentType)> LocalComponents,
    RunMode Mode,
    string? WebResourceRoot);

public interface IPostDeployService
{
    Task RunPreImportAsync(PostDeployContext context, CancellationToken ct);
    Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct);
}
