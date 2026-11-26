using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Solutions;

/// <summary>Takes a component out of one solution without deleting the component itself.</summary>
public static class SolutionComponentRemoval
{
    public static Task RemoveAsync(
        IOrganizationServiceAsync2 service, string solutionUniqueName, Guid componentId, int componentType, CancellationToken ct) =>
        service.ExecuteAsync(new OrganizationRequest("RemoveSolutionComponent")
        {
            ["ComponentId"]        = componentId,
            ["ComponentType"]      = componentType,
            ["SolutionUniqueName"] = solutionUniqueName
        }, ct);
}
