using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.PowerPlatform.Dataverse.Client;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Flowline.Core.Services;

public class DataverseConnector(IAnsiConsole output, FlowlineRuntimeOptions opt)
{
    /// <summary>
    /// Connects to Dataverse by re-using the token that PAC CLI already acquired.
    /// PAC CLI writes its token cache in MSAL Extensions v3 (DPAPI-encrypted) format,
    /// which is incompatible with ServiceClient's own TokenCacheStorePath serialisation.
    /// We read that cache via MsalCacheHelper, acquire the token silently (no browser),
    /// then hand it to ServiceClient through its token-provider callback constructor.
    /// Supports both user (UNIVERSAL/DATAVERSE) and service principal profiles.
    /// </summary>
    public async Task<IOrganizationServiceAsync2> ConnectViaPacAsync(
        PacProfile profile,
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentException("Environment URL is required for connecting via PAC profile.", nameof(environmentUrl));

        var resourceUrl = environmentUrl.TrimEnd('/');
        var serviceUri  = new Uri(resourceUrl + "/");

        output.Verbose($"Connecting via PAC profile '{profile.Name ?? profile.User}' at {resourceUrl}...", opt.IsVerbose);

        var authority = string.IsNullOrWhiteSpace(profile.Authority)
            ? "https://login.microsoftonline.com/organizations"
            : profile.Authority.TrimEnd('/');

        // Platform storage backends:
        //   Windows — DPAPI encryption, transparent via MsalCacheHelper
        //   Linux/macOS — Secret Service / Keychain when available; unprotected file fallback for
        //                 headless environments (CI, Docker, WSL) where no keyring is running
        var storagePropsBuilder = new StorageCreationPropertiesBuilder(
            cacheFileName: "tokencache_msalv3.dat",
            cacheDirectory: GetPacCliDataDirectory());

        if (!OperatingSystem.IsWindows())
            storagePropsBuilder.WithLinuxUnprotectedFile();

        MsalCacheHelper cacheHelper;
        try { cacheHelper = await MsalCacheHelper.CreateAsync(storagePropsBuilder.Build()); }
        catch (MsalCachePersistenceException ex)
        {
            throw new InvalidOperationException(
                "Could not open the PAC CLI token cache. " +
                "Ensure 'pac auth create' has been run and the cache file is accessible. " +
                $"Detail: {ex.Message}", ex);
        }

        return profile.IsServicePrincipal
            ? await ConnectServicePrincipalAsync(profile, authority, resourceUrl, serviceUri, cacheHelper, cancellationToken)
            : await ConnectUserAsync(profile, authority, resourceUrl, serviceUri, cacheHelper, cancellationToken);
    }

    async Task<IOrganizationServiceAsync2> ConnectServicePrincipalAsync(
        PacProfile profile, string authority, string resourceUrl, Uri serviceUri,
        MsalCacheHelper cacheHelper, CancellationToken cancellationToken)
    {
        var appId = profile.ApplicationId
            ?? throw new InvalidOperationException(
                $"Service principal profile '{profile.Name}' is missing ApplicationId.");

        // Service principal authority must be tenant-specific, not 'organizations'
        var tenantAuthority = !string.IsNullOrWhiteSpace(profile.TenantId)
            ? $"https://login.microsoftonline.com/{profile.TenantId}"
            : authority;

        var scopes = new[] { $"{resourceUrl}/.default" };

        // The assertion is only sent to AAD on cache miss. On a cache hit (< 1 hour since
        // 'pac auth create --kind ServicePrincipal'), MSAL returns the stored app token directly.
        var app = ConfidentialClientApplicationBuilder
            .Create(appId)
            .WithAuthority(tenantAuthority)
            .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult("cache-only"))
            .Build();

        cacheHelper.RegisterCache(app.AppTokenCache);

        AuthenticationResult initialToken;
        try
        {
            initialToken = await app.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken);
        }
        catch (MsalException ex)
        {
            var tenantArg = !string.IsNullOrWhiteSpace(profile.TenantId) ? $" --tenant {profile.TenantId}" : "";
            throw new InvalidOperationException(
                $"No cached token for service principal '{appId}' at {resourceUrl}. " +
                $"Run 'pac auth create --kind ServicePrincipal --applicationId {appId} --clientSecret <secret>{tenantArg}' to authenticate.", ex);
        }

        output.Verbose($"Token acquired for app '{appId}' (expires {initialToken.ExpiresOn:HH:mm})", opt.IsVerbose);

        return new ServiceClient(serviceUri, async _ =>
        {
            var result = await app.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken);
            return result.AccessToken;
        });
    }

    async Task<IOrganizationServiceAsync2> ConnectUserAsync(
        PacProfile profile, string authority, string resourceUrl, Uri serviceUri,
        MsalCacheHelper cacheHelper, CancellationToken cancellationToken)
    {
        // must match PAC CLI's client registration
        const string pacClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        const string redirectUri = "http://localhost";

        var scopes = new[] { $"{resourceUrl}/.default" };

        var app = PublicClientApplicationBuilder
            .Create(pacClientId)
            .WithAuthority(authority)
            .WithRedirectUri(redirectUri)
            .Build();

        cacheHelper.RegisterCache(app.UserTokenCache);

        var accounts = await app.GetAccountsAsync();
        var account  = (!string.IsNullOrWhiteSpace(profile.User)
                ? accounts.FirstOrDefault(a => string.Equals(a.Username, profile.User, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? accounts.FirstOrDefault();

        // throws a clear error instead of opening the browser unexpectedly
        AuthenticationResult initialToken;
        try
        {
            initialToken = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken);
        }
        catch (MsalUiRequiredException ex)
        {
            var user = profile.User ?? "unknown";
            throw new InvalidOperationException(
                $"Session expired for '{user}' at {resourceUrl}. " +
                $"Run 'pac auth create --url {resourceUrl}' to re-authenticate.", ex);
        }

        output.Verbose($"Token acquired silently for {initialToken.Account.Username} (expires {initialToken.ExpiresOn:HH:mm})", opt.IsVerbose);

        var tokenAccount = initialToken.Account;
        return new ServiceClient(serviceUri, async _ =>
        {
            var result = await app.AcquireTokenSilent(scopes, tokenAccount).ExecuteAsync(cancellationToken);
            return result.AccessToken;
        });
    }

    public string BuildXrmContextConnectionString(string environmentUrl)
    {
        var profiles = GetPacProfiles();
        return BuildXrmContextConnectionString(environmentUrl, profiles);
    }

    internal static string BuildXrmContextConnectionString(string environmentUrl, IEnumerable<PacProfile> profiles)
    {
        var normalizedUrl = environmentUrl.TrimEnd('/');
        var profile = profiles
            .FirstOrDefault(p => p.Resource?.TrimEnd('/').Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase) == true)
            ?? profiles.FirstOrDefault(p => p.IsUniversal);

        if (profile is null)
            throw new InvalidOperationException(
                $"No PAC profile found for {normalizedUrl}. Run 'pac auth create --environment {normalizedUrl}' first.");

        if (profile.IsServicePrincipal)
            // PAC CLI stores MSAL tokens, not the raw client secret — XrmContext ClientSecret auth is not possible.
            throw new InvalidOperationException(
                "XrmContext doesn't support service principal profiles — PAC doesn't store client secrets. Use '--generator pac' instead.");

        // UNIVERSAL or other interactive profile — use OAuth connection string
        var tokenCachePath = Path.Combine(GetPacCliDataDirectory(), "auth");
        return $"AuthType=OAuth;Url={normalizedUrl};AppId=1950a258-227b-4e31-a9cf-717495945fc2;RedirectUri=http://localhost;TokenCacheStorePath={tokenCachePath};";
    }

    public IEnumerable<PacProfile> GetPacProfiles()
    {
        var profiles = LoadPacAuthProfiles();
        return profiles?.Profiles ?? Enumerable.Empty<PacProfile>();
    }

    public PacProfile? FindBestProfile(string environmentUrl)
    {
        var profiles = GetPacProfiles().ToList();
        return profiles.FirstOrDefault(p => p.Resource?.TrimEnd('/').Equals(environmentUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) == true)
            ?? profiles.FirstOrDefault(p => p.IsUniversal);
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

    internal static string GetPacCliDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Microsoft", "PowerAppsCLI");
    }

    PacAuthProfiles? LoadPacAuthProfiles()
    {
        var authProfilesPath = Path.Combine(GetPacCliDataDirectory(), "authprofiles_v2.json");

        if (!File.Exists(authProfilesPath))
        {
            output.Verbose($"PAC auth profiles file not found: {authProfilesPath}", opt.IsVerbose);
            return null;
        }

        try
        {
            var json = File.ReadAllText(authProfilesPath);
            return JsonSerializer.Deserialize<PacAuthProfiles>(json);
        }
        catch (Exception ex)
        {
            output.Verbose($"Failed to read PAC auth profiles: {ex.Message}", opt.IsVerbose);
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
}

public record PacAuthProfiles
{
    [JsonPropertyName("Profiles")]
    public List<PacProfile>? Profiles { get; init; }

    [JsonPropertyName("Current")]
    public Dictionary<string, PacProfile>? Current { get; init; }
}