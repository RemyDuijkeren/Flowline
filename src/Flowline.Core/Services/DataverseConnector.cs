using Flowline;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.PowerPlatform.Dataverse.Client;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Spectre.Console;

namespace Flowline.Core.Services;

public class DataverseConnector(IAnsiConsole console, HttpClient httpClient)
{
    public const string PacCliAppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
    const string BapAdminResource = "https://api.bap.microsoft.com";
    const string BapAdminEnvironmentsUrl = "https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01";

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

        console.Verbose($"Connecting via PAC profile '{profile.Name ?? profile.User}' at {resourceUrl}...");

        var authority = string.IsNullOrWhiteSpace(profile.Authority)
            ? "https://login.microsoftonline.com/organizations"
            : profile.Authority.TrimEnd('/');

        var cacheHelper = await CreateMsalCacheHelperAsync();

        return profile.IsServicePrincipal
            ? await ConnectServicePrincipalAsync(profile, authority, resourceUrl, serviceUri, cacheHelper, cancellationToken)
            : await ConnectUserAsync(profile, authority, resourceUrl, serviceUri, cacheHelper, cancellationToken);
    }

    /// <summary>
    /// Platform storage backends:
    ///   Windows — DPAPI encryption, transparent via MsalCacheHelper
    ///   Linux/macOS — Secret Service / Keychain when available; unprotected file fallback for
    ///                 headless environments (CI, Docker, WSL) where no keyring is running
    /// Shared by ConnectViaPacAsync and GetEnvironmentInfoAsync — both read the same PAC CLI token cache.
    /// </summary>
    static async Task<MsalCacheHelper> CreateMsalCacheHelperAsync()
    {
        var storagePropsBuilder = new StorageCreationPropertiesBuilder(
            cacheFileName: "tokencache_msalv3.dat",
            cacheDirectory: GetPacCliDataDirectory());

        if (!OperatingSystem.IsWindows())
            storagePropsBuilder.WithLinuxUnprotectedFile();

        try { return await MsalCacheHelper.CreateAsync(storagePropsBuilder.Build()); }
        catch (MsalCachePersistenceException ex)
        {
            throw new InvalidOperationException(
                "Could not open the PAC CLI token cache. " +
                "Ensure 'pac auth create' has been run and the cache file is accessible. " +
                $"Detail: {ex.Message}", ex);
        }
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

        console.Verbose($"Token acquired for app '{appId}' (expires {initialToken.ExpiresOn:HH:mm})");

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
        const string redirectUri = "http://localhost";

        var scopes = new[] { $"{resourceUrl}/.default" };

        var app = PublicClientApplicationBuilder
            .Create(PacCliAppId)
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

        console.Verbose($"Token acquired silently for {initialToken.Account.Username} (expires {initialToken.ExpiresOn:HH:mm})");

        var tokenAccount = initialToken.Account;
        return new ServiceClient(serviceUri, async _ =>
        {
            var result = await app.AcquireTokenSilent(scopes, tokenAccount).ExecuteAsync(cancellationToken);
            return result.AccessToken;
        });
    }

    /// <summary>
    /// Resolves an environment's existence and Production/Sandbox Type via a direct token read against
    /// the resolved profile's cached PAC CLI credentials — no pac.exe subprocess, so the result is
    /// independent of which PAC auth profile is globally active. Mirrors ConnectViaPacAsync's token
    /// acquisition but scoped to the Power Platform BAP admin API instead of the Dataverse environment.
    /// </summary>
    public async Task<BapEnvironmentInfo?> GetEnvironmentInfoAsync(
        PacProfile profile,
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentException("Environment URL is required for looking up environment info.", nameof(environmentUrl));

        var authority = string.IsNullOrWhiteSpace(profile.Authority)
            ? "https://login.microsoftonline.com/organizations"
            : profile.Authority.TrimEnd('/');

        var cacheHelper = await CreateMsalCacheHelperAsync();

        var accessToken = profile.IsServicePrincipal
            ? await AcquireServicePrincipalTokenAsync(profile, authority, cacheHelper, BapAdminResource, cancellationToken)
            : await AcquireUserTokenAsync(profile, authority, cacheHelper, BapAdminResource, cancellationToken);

        // Per-request Authorization header, never HttpClient.DefaultRequestHeaders — httpClient is a
        // shared singleton also used for unrelated calls (e.g. XrmContextToolProvider's NuGet download),
        // and DefaultRequestHeaders would leak this admin-scoped bearer token onto those requests too.
        using var request = new HttpRequestMessage(HttpMethod.Get, BapAdminEnvironmentsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return MapBapEnvironmentsResponse(json, environmentUrl);
    }

    async Task<string> AcquireUserTokenAsync(
        PacProfile profile, string authority, MsalCacheHelper cacheHelper, string resourceUrl, CancellationToken cancellationToken)
    {
        const string redirectUri = "http://localhost";
        var scopes = new[] { $"{resourceUrl}/.default" };

        var app = PublicClientApplicationBuilder
            .Create(PacCliAppId)
            .WithAuthority(authority)
            .WithRedirectUri(redirectUri)
            .Build();

        cacheHelper.RegisterCache(app.UserTokenCache);

        var accounts = await app.GetAccountsAsync();
        var account  = (!string.IsNullOrWhiteSpace(profile.User)
                ? accounts.FirstOrDefault(a => string.Equals(a.Username, profile.User, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? accounts.FirstOrDefault();

        try
        {
            var result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            var user = profile.User ?? "unknown";
            throw new InvalidOperationException(
                $"Session expired for '{user}' at {resourceUrl}. " +
                $"Run 'pac auth create --url {resourceUrl}' to re-authenticate.", ex);
        }
    }

    async Task<string> AcquireServicePrincipalTokenAsync(
        PacProfile profile, string authority, MsalCacheHelper cacheHelper, string resourceUrl, CancellationToken cancellationToken)
    {
        var appId = profile.ApplicationId
            ?? throw new InvalidOperationException(
                $"Service principal profile '{profile.Name}' is missing ApplicationId.");

        var tenantAuthority = !string.IsNullOrWhiteSpace(profile.TenantId)
            ? $"https://login.microsoftonline.com/{profile.TenantId}"
            : authority;

        var scopes = new[] { $"{resourceUrl}/.default" };

        var app = ConfidentialClientApplicationBuilder
            .Create(appId)
            .WithAuthority(tenantAuthority)
            .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult("cache-only"))
            .Build();

        cacheHelper.RegisterCache(app.AppTokenCache);

        try
        {
            var result = await app.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            var tenantArg = !string.IsNullOrWhiteSpace(profile.TenantId) ? $" --tenant {profile.TenantId}" : "";
            throw new InvalidOperationException(
                $"No cached token for service principal '{appId}' at {resourceUrl}. " +
                $"Run 'pac auth create --kind ServicePrincipal --applicationId {appId} --clientSecret <secret>{tenantArg}' to authenticate.", ex);
        }
    }

    // BAP's admin/environments response shape (verified live for `properties.environmentSku` only —
    // https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments):
    //   { "value": [ { "name": "<env-guid>", "properties": { "displayName", "environmentSku",
    //       "linkedEnvironmentMetadata": { "resourceId", "friendlyName", "domainName", "instanceUrl", "version" } } } ] }
    // Pure and internal so it's unit-testable without a token or network call.
    internal static BapEnvironmentInfo? MapBapEnvironmentsResponse(string bapResponseJson, string environmentUrl)
    {
        var normalizedTarget = environmentUrl.TrimEnd('/');
        using var doc = JsonDocument.Parse(bapResponseJson);

        if (!doc.RootElement.TryGetProperty("value", out var envs) || envs.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var env in envs.EnumerateArray())
        {
            var mapped = MapBapEnvironment(env);
            if (mapped?.EnvironmentUrl?.TrimEnd('/').Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase) == true)
                return mapped;
        }

        return null;
    }

    internal static BapEnvironmentInfo? MapBapEnvironment(JsonElement env)
    {
        if (!env.TryGetProperty("properties", out var props))
            return null;

        var linked = props.TryGetProperty("linkedEnvironmentMetadata", out var lm) ? lm : default;

        var instanceUrl = GetStringProperty(linked, "instanceUrl");
        if (string.IsNullOrWhiteSpace(instanceUrl))
            return null;

        Guid.TryParse(GetStringProperty(env, "name"), out var environmentId);
        Guid.TryParse(GetStringProperty(linked, "resourceId"), out var organizationId);

        return new BapEnvironmentInfo
        {
            EnvironmentId = environmentId,
            EnvironmentUrl = instanceUrl,
            OrganizationId = organizationId,
            DisplayName = GetStringProperty(props, "displayName") ?? GetStringProperty(linked, "friendlyName"),
            Type = GetStringProperty(props, "environmentSku"),
            DomainName = GetStringProperty(linked, "domainName"),
            Version = GetStringProperty(linked, "version")
        };
    }

    static string? GetStringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public string BuildXrmContextConnectionString(string environmentUrl, string? username = null, string? password = null, string? clientId = null)
    {
        var profiles = GetPacProfiles();
        return BuildXrmContextConnectionString(environmentUrl, profiles, username, password, clientId);
    }

    internal static string BuildXrmContextConnectionString(string environmentUrl, IEnumerable<PacProfile> profiles,
        string? username = null, string? password = null, string? clientId = null)
    {
        var normalizedUrl = environmentUrl.TrimEnd('/');
        var appId = clientId ?? PacCliAppId;
        var tokenCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flowline", "auth", "xrmcontext3");
        Directory.CreateDirectory(tokenCachePath);

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            // ROPC flow — no browser or PAC profile required
            if (username.IndexOfAny([';', '=']) >= 0)
                throw new FlowlineException(ExitCode.ValidationFailed, "XrmContext username must not contain ';' or '='. Remove those characters from --username.");
            if (password.IndexOfAny([';', '=']) >= 0)
                throw new FlowlineException(ExitCode.ValidationFailed, "XrmContext password must not contain ';' or '='. Remove those characters from --password.");
            return $"AuthType=OAuth;Username={username};Password={password};Url={normalizedUrl};AppId={appId};RedirectUri=http://localhost;LoginPrompt=Never;TokenCacheStorePath={tokenCachePath};";
        }

        // Browser OAuth — PAC profile validates the environment exists in auth store
        var profile = profiles
            .FirstOrDefault(p => p.Resource?.TrimEnd('/').Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase) == true)
            ?? profiles.FirstOrDefault(p => p.IsUniversal);

        if (profile is null)
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"No PAC profile found for {normalizedUrl}. Run 'pac auth create --environment {normalizedUrl}' first.");

        if (profile.IsServicePrincipal)
            throw new FlowlineException(ExitCode.NotAuthenticated,
                "XrmContext doesn't support service principal profiles — PAC doesn't store client secrets. Use '--generator pac' instead.");

        // Interactive browser OAuth — LoginPrompt=Auto opens browser only when no cached token
        return $"AuthType=OAuth;Url={normalizedUrl};AppId={appId};RedirectUri=http://localhost;LoginPrompt=Auto;TokenCacheStorePath={tokenCachePath};";
    }

    public IEnumerable<PacProfile> GetPacProfiles()
    {
        var profiles = LoadPacAuthProfiles();
        return profiles.Profiles ?? Enumerable.Empty<PacProfile>();
    }

    public ProfileResolutionResult FindBestProfile(string environmentUrl)
        => FindBestProfile(environmentUrl, LoadPacAuthProfiles());

    internal static ProfileResolutionResult FindBestProfile(string environmentUrl, PacAuthProfiles? profiles)
    {
        var normalizedUrl = environmentUrl.TrimEnd('/');
        var candidates = profiles?.Profiles?
            .Where(p => p.Resource?.TrimEnd('/').Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase) == true)
            .ToList() ?? [];

        if (candidates.Count == 1)
            return new ProfileFound(candidates[0]);

        if (candidates.Count > 1)
        {
            // Prefer the profile that is currently active for its Kind
            foreach (var candidate in candidates)
            {
                if (candidate.Kind is null) continue;
                if (profiles?.Current?.TryGetValue(candidate.Kind, out var activeProfile) == true
                    && activeProfile == candidate)
                    return new ProfileFound(activeProfile);
            }
            return new ProfileAmbiguous(candidates);
        }

        // No URL match — check Current for a UNIVERSAL fallback
        var universal = profiles?.Current?.Values.FirstOrDefault(p => p.IsUniversal);
        if (universal is not null)
            return new ProfileFound(universal);

        return new ProfileNotFound(environmentUrl);
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

    PacAuthProfiles LoadPacAuthProfiles() =>
        LoadPacAuthProfiles(Path.Combine(GetPacCliDataDirectory(), "authprofiles_v2.json"));

    internal PacAuthProfiles LoadPacAuthProfiles(string authProfilesPath)
    {
        if (!File.Exists(authProfilesPath))
        {
            var version = GetPacCliVersion();
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"PAC auth profile file not found: {authProfilesPath}\n" +
                $"PAC CLI version: {version}\n" +
                "Run: pac auth create --environment <url> --name \"<Name>\" to create a profile.\n" +
                "If this error persists, update PAC CLI or file a bug.");
        }

        try
        {
            var json = File.ReadAllText(authProfilesPath);
            var profiles = JsonSerializer.Deserialize<PacAuthProfiles>(json);
            if (profiles == null)
                throw new JsonException("Profile file deserialized to null.");

            console.Verbose($"Loaded {profiles.Profiles?.Count ?? 0} PAC auth profile(s) from {authProfilesPath}");

            return profiles;
        }
        catch (JsonException ex)
        {
            var version = GetPacCliVersion();
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"Failed to parse PAC auth profiles ({authProfilesPath}): {ex.Message}\n" +
                $"PAC CLI version: {version}\n" +
                "Update PAC CLI or run: pac auth create --environment <url> to reinitialize.");
        }
        catch (IOException ex)
        {
            var version = GetPacCliVersion();
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"Failed to read PAC auth profiles ({authProfilesPath}): {ex.Message}\n" +
                $"PAC CLI version: {version}\n" +
                "Check file permissions or run: pac auth create --environment <url>.");
        }
        // Other exceptions bubble up
    }

    string GetPacCliVersion()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pac",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2000))
            {
                process.Kill();
                return "unknown (timeout)";
            }
            var outputText = outputTask.GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(outputText) ? "unknown" : outputText.Trim();
        }
        catch (Exception)
        {
            return "unknown (pac CLI not found)";
        }
    }
}