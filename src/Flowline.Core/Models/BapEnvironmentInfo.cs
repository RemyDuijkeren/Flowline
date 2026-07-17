namespace Flowline.Core.Models;

/// <summary>
/// Environment existence/Type data resolved via a direct BAP admin API token read
/// (<see cref="Services.DataverseConnector.GetEnvironmentInfoAsync"/>). The Flowline CLI project's
/// own EnvironmentInfo (src/Flowline/Utils/PacUtils.cs) is the caller-facing shape this maps into —
/// this record exists only so Flowline.Core, which doesn't reference the Flowline project, can
/// return BAP data without depending on that type.
/// </summary>
public record BapEnvironmentInfo
{
    public Guid EnvironmentId { get; init; }
    public string? EnvironmentUrl { get; init; }
    public Guid OrganizationId { get; init; }
    public string? DisplayName { get; init; }
    public string? Type { get; init; }
    public string? DomainName { get; init; }
    public string? Version { get; init; }
}
