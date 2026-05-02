using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Models;

public enum WebResourceType
{
    HTML = 1,
    HTM = 1,
    CSS = 2,
    JS = 3,
    XML = 4,
    XAML = 4,
    XSD = 4,
    PNG = 5,
    JPG = 6,
    JPEG = 6,
    GIF = 7,
    XAP = 8,
    XSL = 9,
    XSLT = 9,
    ICO = 10,
    SVG = 11, // D365 only
    RESX = 12 // D365 only
}

public enum WebResourceAction
{
    Create,
    Update,
    UpdateAndAddToPatchSolution,
    Delete,
    RemoveFromSolution,
    Skip
}

public record WebResourceSyncResult(bool Success, string Message);

public record WebResourceSolutionInfo(Guid Id, string UniqueName, string PublisherPrefix, bool IsManaged);

public record LocalWebResource(
    string Name,
    string Path,
    string DisplayName,
    int Type,
    string Content,
    string? SilverlightVersion = null);

public record DataverseWebResource(
    Guid Id,
    string Name,
    string? DisplayName,
    int Type,
    string? Content,
    bool IsInPatch,
    Entity Entity,
    WebResourceOwnership Ownership);

public record WebResourceOwnership(
    int NonDefaultUnmanagedSolutionCount,
    bool IsInCurrentUnmanagedSolution);

public record WebResourceSyncSnapshot(
    WebResourceSolutionInfo BaseSolution,
    WebResourceSolutionInfo? PatchSolution,
    IReadOnlyDictionary<string, LocalWebResource> LocalResources,
    IReadOnlyDictionary<string, DataverseWebResource> DataverseResources);

public record WebResourcePlanAction(
    string Name,
    WebResourceAction Action,
    Entity? Entity = null,
    Guid? Id = null,
    string? SolutionName = null,
    string? Reason = null);

public class WebResourceSyncPlan
{
    public Dictionary<string, WebResourcePlanAction> Creates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WebResourcePlanAction> Updates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WebResourcePlanAction> UpdatesAndAddsToPatch { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WebResourcePlanAction> Deletes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WebResourcePlanAction> RemovesFromSolution { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WebResourcePlanAction> Skips { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int TotalChanges => Creates.Count + Updates.Count + UpdatesAndAddsToPatch.Count + Deletes.Count + RemovesFromSolution.Count;
    public int PublishCount => Creates.Count + Updates.Count + UpdatesAndAddsToPatch.Count;
}
