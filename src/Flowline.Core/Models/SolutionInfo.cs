namespace Flowline.Core.Models;

public class SolutionInfo
{
    public Guid Id { get; set; }
    public string? SolutionUniqueName { get; set; }
    public string? FriendlyName { get; set; }
    public string? PublisherUniqueName { get; set; }
    public string? PublisherPrefix { get; set; }
    public string? VersionNumber { get; set; }
    public bool IsManaged { get; set; }
}
