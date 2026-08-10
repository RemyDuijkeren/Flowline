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
    AddToSolution,
    Skip
}

public record DependencyLibrary(string Name, string DisplayName, Guid LibraryUniqueId)
{
    public virtual bool Equals(DependencyLibrary? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}

// AnnotatedDependsOn is the raw `// flowline:depends` lines exactly as written in source. DependsOn
// starts as the same set but is enriched downstream — AutoMatchResxDependencies adds RESX names by
// base-name match with no annotation behind them, and ExpandLcidDependencies rewrites bare RESX
// references into LCID variants. Anything that must answer "did the author actually declare this?"
// has to read AnnotatedDependsOn; DependsOn answers "what load-order dependencies get registered".
public record LocalWebResource(
    string Name,
    string RelativePath,
    string Path,
    string DisplayName,
    WebResourceType Type,
    string? Content,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string>? AnnotatedDependsOn = null)
{
    public IReadOnlyList<string> AnnotatedDependsOn { get; init; } = AnnotatedDependsOn ?? DependsOn;
}

public record DataverseWebResource(
    Guid Id,
    string Name,
    string? DisplayName,
    WebResourceType Type,
    string? Content,
    Entity Entity,
    WebResourceOwnership Ownership,
    string? DependencyXml = null);

public record WebResourceOwnership(
    int NonDefaultUnmanagedSolutionCount,
    bool IsInCurrentUnmanagedSolution,
    bool HasManagedSolutionReference = false,
    IReadOnlyList<string>? OwningSolutionNames = null)
{
    public IReadOnlyList<string> OwningSolutionNames { get; init; } = OwningSolutionNames ?? [];
}

public record WebResourceSyncSnapshot(
    DataverseSolutionInfo Solution,
    IReadOnlyDictionary<string, LocalWebResource> LocalResources,
    IReadOnlyDictionary<string, DataverseWebResource> DataverseResources,
    IReadOnlyDictionary<string, DataverseWebResource> GlobalOrphans);

public record WebResourcePlanAction(
    string Name,
    WebResourceAction Action,
    Entity? Entity = null,
    Guid? Id = null,
    string? SolutionName = null,
    string? Reason = null,
    // Solutions that own the resource — distinct from SolutionName, which is the solution an action
    // targets. Only set on a reference-only Skip, where naming the owner is the point of the warning.
    string? OwningSolutions = null);

public class WebResourceSyncPlan
{
    public List<WebResourcePlanAction> Creates { get; } = [];
    public List<WebResourcePlanAction> Updates { get; } = [];
    public List<WebResourcePlanAction> Deletes { get; } = [];
    public List<WebResourcePlanAction> RemovesFromSolution { get; } = [];
    public List<WebResourcePlanAction> AddsToSolution { get; } = [];
    public List<WebResourcePlanAction> Skips { get; } = [];

    public int TotalDeletes => Deletes.Count + RemovesFromSolution.Count;
    public int TotalUpserts => Creates.Count + Updates.Count;
    public int TotalChanges => TotalDeletes + TotalUpserts + AddsToSolution.Count;
    public int PublishCount => Creates.Count + Updates.Count;
}
