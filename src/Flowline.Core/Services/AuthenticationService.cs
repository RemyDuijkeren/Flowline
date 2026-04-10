using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flowline.Core.Services;

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

public interface IAuthenticationService
{
    IOrganizationServiceAsync2 Connect(string connectionString);
    IOrganizationServiceAsync2 ConnectViaPac(PacProfile profile, string? environmentUrl = null);
    IEnumerable<PacProfile> GetPacProfiles();
}

public class AuthenticationService : IAuthenticationService
{
    private readonly ILogger<AuthenticationService> _logger;

    public AuthenticationService(ILogger<AuthenticationService> logger)
    {
        _logger = logger;
    }

    public IOrganizationServiceAsync2 Connect(string connectionString)
    {
        _logger.LogInformation("Connecting to Dataverse...");
        
        var client = new ServiceClient(connectionString);

        if (!client.IsReady)
        {
            _logger.LogError("Failed to connect to Dataverse: {LastError}", client.LastError);
            var lastError = client.LastError;
            var lastException = client.LastException;
            client.Dispose();
            throw new Exception($"Failed to connect to Dataverse: {lastError}", lastException);
        }
        
        _logger.LogInformation("Connected successfully to {EnvironmentUrl}", client.ConnectedOrgUriActual);
        
        return client;
    }

    public IOrganizationServiceAsync2 ConnectViaPac(PacProfile profile, string? environmentUrl = null)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        if (string.IsNullOrWhiteSpace(environmentUrl)) 
        {
            throw new ArgumentException("Environment URL is required for connecting via PAC profile.", nameof(environmentUrl));
        }

        var targetUrl = environmentUrl;

        _logger.LogInformation("Connecting to Dataverse using PAC profile for {User} at {EnvironmentUrl}...", profile.User, targetUrl);

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
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var authProfilesPath = Path.Combine(localAppData, "Microsoft", "PowerAppsCLI", "authprofiles_v2.json");

        if (!File.Exists(authProfilesPath))
        {
            _logger.LogWarning("PAC auth profiles file not found: {Path}", authProfilesPath);
            return Enumerable.Empty<PacProfile>();
        }

        try
        {
            var json = File.ReadAllText(authProfilesPath);
            var profiles = JsonSerializer.Deserialize<PacAuthProfiles>(json);
            return profiles?.Profiles ?? Enumerable.Empty<PacProfile>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read PAC auth profiles from {Path}", authProfilesPath);
            return Enumerable.Empty<PacProfile>();
        }
    }
}
