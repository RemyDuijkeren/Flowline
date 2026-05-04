using Microsoft.PowerPlatform.Dataverse.Client;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Flowline.Core.Services;

public class DataverseConnector(IAnsiConsole output, FlowlineRuntimeOptions opt)
{
    public IOrganizationServiceAsync2 Connect(string connectionString)
    {
        output.Verbose("Connecting to Dataverse...", opt);

        var client = new ServiceClient(connectionString);

        if (!client.IsReady)
        {
            var lastError = client.LastError;
            var lastException = client.LastException;
            client.Dispose();
            throw new Exception($"Failed to connect to Dataverse: {lastError}", lastException);
        }

        output.Verbose($"Connected to {client.ConnectedOrgUriActual}", opt);

        return client;
    }

    public IOrganizationServiceAsync2 ConnectViaPac(PacProfile profile, string? environmentUrl = null)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentException("Environment URL is required for connecting via PAC profile.", nameof(environmentUrl));

        var targetUrl = environmentUrl;

        output.Verbose($"Connecting via PAC profile for {profile.User} at {targetUrl}...", opt);

        // PAC CLI Client ID
        const string pacClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        const string redirectUri = "http://localhost";

        // Construct connection string for silent auth via PAC shared cache
        // We point TokenCacheStorePath to the .IdentityService folder where PAC stores its MSAL cache
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tokenCachePath = Path.Combine(localAppData, ".IdentityService");

        var authority = string.IsNullOrWhiteSpace(profile.Authority)
            ? "https://login.microsoftonline.com/organizations"
            : profile.Authority;

        // Ensure authority doesn't end with a slash for the connection string if it's explicitly provided
        authority = authority.TrimEnd('/');

        var connectionString = $"AuthType=OAuth;" +
                               $"Url={targetUrl};" +
                               $"ClientId={pacClientId};" +
                               $"RedirectUri={redirectUri};" +
                               $"TokenCacheStorePath={tokenCachePath};" +
                               $"Authority={authority};" +
                               $"LoginPrompt=Never;" +
                               $"RequireNewInstance=True";

        return Connect(connectionString);
    }

    public IEnumerable<PacProfile> GetPacProfiles()
    {
        var profiles = LoadPacAuthProfiles();
        return profiles?.Profiles ?? Enumerable.Empty<PacProfile>();
    }

    public PacProfile? GetCurrentResourceSpecificPacProfile()
    {
        var profiles = LoadPacAuthProfiles();
        return GetCurrentResourceSpecificPacProfile(profiles);
    }

    internal static PacProfile? GetCurrentResourceSpecificPacProfile(PacAuthProfiles? profiles)
    {
        var currentProfiles = profiles?.Current?.Values
            .Where(p => !p.IsUniversal && !string.IsNullOrWhiteSpace(p.Resource))
            .DistinctBy(p => p.Resource?.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return currentProfiles is { Count: 1 } ? currentProfiles[0] : null;
    }

    PacAuthProfiles? LoadPacAuthProfiles()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var authProfilesPath = Path.Combine(localAppData, "Microsoft", "PowerAppsCLI", "authprofiles_v2.json");

        if (!File.Exists(authProfilesPath))
        {
            output.Verbose($"PAC auth profiles file not found: {authProfilesPath}", opt);
            return null;
        }

        try
        {
            var json = File.ReadAllText(authProfilesPath);
            return JsonSerializer.Deserialize<PacAuthProfiles>(json);
        }
        catch (Exception ex)
        {
            output.Verbose($"Failed to read PAC auth profiles: {ex.Message}", opt);
            return null;
        }
    }
}

public record PacProfile
{
    [JsonPropertyName("Kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("User")]
    public string? User { get; init; }

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
}

public record PacAuthProfiles
{
    [JsonPropertyName("Profiles")]
    public List<PacProfile>? Profiles { get; init; }

    [JsonPropertyName("Current")]
    public Dictionary<string, PacProfile>? Current { get; init; }
}
