using System.Text.Json.Serialization;

namespace Flowline.Core.Models;

public record PacProfile
{
    [JsonPropertyName("Kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("User")]
    public string? User { get; init; }

    [JsonPropertyName("ApplicationId")]
    public string? ApplicationId { get; init; }

    [JsonPropertyName("AadObjectId")]
    public string? AadObjectId { get; init; }

    [JsonPropertyName("ExpiresOn")]
    public DateTime? ExpiresOn { get; init; }

    [JsonPropertyName("Authority")]
    public string? Authority { get; init; }

    [JsonPropertyName("Name")]
    public string? Name { get; init; }

    [JsonPropertyName("TenantId")]
    public string? TenantId { get; init; }

    [JsonPropertyName("Resource")]
    public string? Resource { get; init; }

    [JsonPropertyName("EnvironmentId")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("FriendlyName")]
    public string? FriendlyName { get; init; }

    [JsonIgnore]
    public bool IsUniversal => string.Equals(Kind, "UNIVERSAL", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsServicePrincipal => string.Equals(Kind, "ServicePrincipal", StringComparison.OrdinalIgnoreCase);

    // Bare text, not pre-quoted/parenthesized — profiles created without `--name` have an empty
    // Name, and callers format that fallback differently (quoted, parenthesized, combined with
    // Kind, ...), so this only owns the "what text represents this profile" decision, not its
    // surrounding punctuation.
    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Name) ? "unnamed" : Name;

    // "FriendlyName (Url)" when a display name is available, otherwise the bare URL.
    [JsonIgnore]
    public string EnvironmentLabel => string.IsNullOrEmpty(FriendlyName)
        ? Resource ?? ""
        : $"{FriendlyName} ({Resource})";
}

public record PacAuthProfiles
{
    [JsonPropertyName("Profiles")]
    public List<PacProfile>? Profiles { get; init; }

    [JsonPropertyName("Current")]
    public Dictionary<string, PacProfile>? Current { get; init; }
}
