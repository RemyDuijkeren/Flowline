using Microsoft.Xrm.Sdk;

namespace Flowline.Core.Models;

public enum WebResourceType
{
    Unknown = 0,

    // Cloud-relevant model-driven app web resources.
    Html = 1,
    Htm = 1,
    Css = 2,
    Js = 3,
    Xml = 4,

    // Same Dataverse type as XML; supported but uncommon for modern cloud customizations.
    Xaml = 4,
    Xsd = 4,

    // Cloud-relevant image web resources.
    Png = 5,
    Jpg = 6,
    Jpeg = 6,
    Gif = 7,

    // Legacy Silverlight web resource. Silverlight was deprecated in Dynamics 365 v9
    // and does not work in Unified Interface.
    Xap = 8,

    // Supported Dataverse type but niche/legacy for modern model-driven apps.
    Xsl = 9,
    Xslt = 9,

    // Cloud-relevant image/localization web resources.
    Ico = 10,
    Svg = 11,
    Resx = 12
}

public enum WebResourceAction
{
    Create,
    Update,
    Delete,
    RemoveFromSolution,
    Skip
}

public record LocalWebResource(string Name, string RelativePath, string Path, string DisplayName, WebResourceType Type, string Content);

public record DataverseWebResource(
    Guid Id,
    string Name,
    string? DisplayName,
    WebResourceType Type,
    string? Content,
    Entity Entity,
    WebResourceOwnership Ownership);

public record WebResourceOwnership(int NonDefaultUnmanagedSolutionCount, bool IsInCurrentUnmanagedSolution);

public record WebResourceSyncSnapshot(
    DataverseSolutionInfo Solution,
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
    public List<WebResourcePlanAction> Creates { get; } = [];
    public List<WebResourcePlanAction> Updates { get; } = [];
    public List<WebResourcePlanAction> Deletes { get; } = [];
    public List<WebResourcePlanAction> RemovesFromSolution { get; } = [];
    public List<WebResourcePlanAction> Skips { get; } = [];

    public int TotalDeletes => Deletes.Count + RemovesFromSolution.Count;
    public int TotalUpserts => Creates.Count + Updates.Count;
    public int TotalChanges => TotalDeletes + TotalUpserts;
    public int PublishCount => Creates.Count + Updates.Count;
}
