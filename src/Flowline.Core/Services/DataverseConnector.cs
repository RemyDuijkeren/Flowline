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
    const string BapAdminEnvironmentsUrl = BapAdminResource + "/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01";

    // Deterministic for the whole process (same cache file/directory regardless of which
    // DataverseConnector instance asks), so one MsalCacheHelper is reused rather than reopening
    // the on-disk token cache on every call.
    static Task<MsalCacheHelper>? s_cacheHelperTask;

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

        var authority = ResolveAuthority(profile);
        var cacheHelper = await GetOrCreateMsalCacheHelperAsync();

        return profile.IsServicePrincipal
            ? await ConnectServicePrincipalAsync(profile, authority, resourceUrl, serviceUri, cacheHelper, cancellationToken)
            : await ConnectUserAsync(profile, authority, resourceUrl, serviceUri, cacheHelper, cancellationToken);
    }

    static string ResolveAuthority(PacProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Authority)
            ? "https://login.microsoftonline.com/organizations"
            : profile.Authority.TrimEnd('/');

    /// <summary>
    /// Platform storage backends:
    ///   Windows — DPAPI encryption, transparent via MsalCacheHelper
    ///   Linux/macOS — Secret Service / Keychain when available; unprotected file fallback for
    ///                 headless environments (CI, Docker, WSL) where no keyring is running
    /// Shared by ConnectViaPacAsync and GetEnvironmentInfoAsync — both read the same PAC CLI token cache.
    /// Only a successfully-completed task is memoized — a transient failure (e.g. a momentary file
    /// lock) must not permanently poison every later call in the process with the same faulted Task.
    /// </summary>
    static Task<MsalCacheHelper> GetOrCreateMsalCacheHelperAsync()
    {
        var existing = s_cacheHelperTask;
        if (existing is { IsFaulted: false, IsCanceled: false })
            return existing;

        var created = CreateMsalCacheHelperAsync();
        s_cacheHelperTask = created;
        return created;
    }

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
            throw new FlowlineException(ExitCode.NotAuthenticated,
                "Could not open the PAC CLI token cache. " +
                "Ensure 'pac auth create' has been run and the cache file is accessible. " +
                $"Detail: {ex.Message}", ex);
        }
    }

    async Task<IOrganizationServiceAsync2> ConnectServicePrincipalAsync(
        PacProfile profile, string authority, string resourceUrl, Uri serviceUri,
        MsalCacheHelper cacheHelper, CancellationToken cancellationToken)
    {
        var scopes = new[] { $"{resourceUrl}/.default" };
        var (app, initialToken) = await AcquireServicePrincipalTokenAsync(profile, authority, cacheHelper, resourceUrl, cancellationToken);

        console.Verbose($"Token acquired for app '{profile.ApplicationId}' (expires {initialToken.ExpiresOn:HH:mm})");

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
        var scopes = new[] { $"{resourceUrl}/.default" };
        var (app, initialToken) = await AcquireUserTokenAsync(profile, authority, cacheHelper, resourceUrl, cancellationToken);

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
    public async Task<EnvironmentInfo?> GetEnvironmentInfoAsync(
        PacProfile profile,
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentException("Environment URL is required for looking up environment info.", nameof(environmentUrl));

        var authority = ResolveAuthority(profile);
        var cacheHelper = await GetOrCreateMsalCacheHelperAsync();

        var accessToken = profile.IsServicePrincipal
            ? (await AcquireServicePrincipalTokenAsync(profile, authority, cacheHelper, BapAdminResource, cancellationToken)).Token.AccessToken
            : (await AcquireUserTokenAsync(profile, authority, cacheHelper, BapAdminResource, cancellationToken, isInternalResource: true)).Token.AccessToken;

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

    // Shared by ConnectUserAsync (which additionally wraps the result in a ServiceClient renewal
    // callback) and GetEnvironmentInfoAsync (which only needs Token.AccessToken).
    async Task<(IPublicClientApplication App, AuthenticationResult Token)> AcquireUserTokenAsync(
        PacProfile profile, string authority, MsalCacheHelper cacheHelper, string resourceUrl, CancellationToken cancellationToken,
        bool isInternalResource = false)
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
        var account = SelectCachedAccount(accounts, profile.User, profile.TenantId);

        // throws a clear error instead of opening the browser unexpectedly
        try
        {
            var token = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken);
            return (app, token);
        }
        // AADSTS90072/AADSTS50020: the resolved account isn't valid in this tenant. Either it
        // genuinely lacks access, or (if the same account works via 'pac' directly) the cached
        // MSAL entry picked above belongs to a different tenant this username is also known to
        // -- re-running 'pac auth create' for the same account/profile won't fix either case on
        // its own. Distinguish this from a plain expired token, which the catch below still
        // handles correctly.
        catch (MsalUiRequiredException ex) when (ex.Message.Contains("AADSTS90072") || ex.Message.Contains("AADSTS50020"))
        {
            var user = profile.User ?? "unknown";
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"'{user}' isn't valid in tenant '{profile.TenantId ?? "unknown"}' for profile '{profile.Name ?? user}'. " +
                "If this account should have access, the cached PAC credential may be stale or bound " +
                "to a different tenant -- run 'pac auth clear' then 'pac auth create' to re-authenticate " +
                "from scratch. If it genuinely lacks access, ask a tenant admin to add it as a guest, " +
                "or sign in with a different account.", ex);
        }
        catch (MsalUiRequiredException ex)
        {
            var user = profile.User ?? "unknown";
            // resourceUrl is Flowline's internal BAP admin endpoint here, not a Dataverse
            // environment URL a user would ever pass to 'pac auth create --url' -- point them
            // at re-authenticating the PAC profile itself instead of that internal resource.
            var remediation = isInternalResource
                ? $"Run 'pac auth create' to refresh your PAC session for profile '{profile.Name ?? user}'."
                : $"Run 'pac auth create --url {resourceUrl}' to re-authenticate.";
            throw new FlowlineException(ExitCode.NotAuthenticated, $"Session expired for '{user}'. {remediation}", ex);
        }
    }

    /// <summary>
    /// Picks the cached MSAL account to acquire a token with. The same username can be cached
    /// under multiple home tenants (e.g. a stale guest/B2B entry from an unrelated org alongside
    /// the real member account for this profile's tenant) -- picking by username alone can
    /// silently select the wrong one, which <c>AcquireTokenSilent</c> then fails against with a
    /// confusing "account doesn't exist in tenant" error instead of a clean re-authentication
    /// prompt. Prefers the entry whose home tenant matches <paramref name="tenantId"/> when one
    /// is recorded; falls back to the first username match, then to any cached account.
    /// </summary>
    internal static IAccount? SelectCachedAccount(IEnumerable<IAccount> accounts, string? username, string? tenantId)
    {
        var usernameMatches = string.IsNullOrWhiteSpace(username)
            ? accounts.ToList()
            : accounts.Where(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)).ToList();

        return (!string.IsNullOrWhiteSpace(tenantId)
                ? usernameMatches.FirstOrDefault(a => string.Equals(a.HomeAccountId?.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? usernameMatches.FirstOrDefault()
            ?? accounts.FirstOrDefault();
    }

    // Shared by ConnectServicePrincipalAsync (which additionally wraps the result in a ServiceClient
    // renewal callback) and GetEnvironmentInfoAsync (which only needs Token.AccessToken).
    async Task<(IConfidentialClientApplication App, AuthenticationResult Token)> AcquireServicePrincipalTokenAsync(
        PacProfile profile, string authority, MsalCacheHelper cacheHelper, string resourceUrl, CancellationToken cancellationToken)
    {
        var appId = profile.ApplicationId
            ?? throw new FlowlineException(ExitCode.NotAuthenticated,
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

        try
        {
            var token = await app.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken);
            return (app, token);
        }
        catch (MsalException ex)
        {
            var tenantArg = !string.IsNullOrWhiteSpace(profile.TenantId) ? $" --tenant {profile.TenantId}" : "";
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"No cached token for service principal '{appId}' at {resourceUrl}. " +
                $"Run 'pac auth create --kind ServicePrincipal --applicationId {appId} --clientSecret <secret>{tenantArg}' to authenticate.", ex);
        }
    }

    // BAP's admin/environments response shape (verified live for `properties.environmentSku` only —
    // https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments):
    //   { "value": [ { "name": "<env-guid>", "properties": { "displayName", "environmentSku",
    //       "linkedEnvironmentMetadata": { "resourceId", "friendlyName", "domainName", "instanceUrl", "version" } } } ] }
    // Pure and internal so it's unit-testable without a token or network call.
    internal static EnvironmentInfo? MapBapEnvironmentsResponse(string bapResponseJson, string environmentUrl)
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

    internal static EnvironmentInfo? MapBapEnvironment(JsonElement env)
    {
        if (!env.TryGetProperty("properties", out var props))
            return null;

        var linked = props.TryGetProperty("linkedEnvironmentMetadata", out var lm) ? lm : default;

        var instanceUrl = GetStringProperty(linked, "instanceUrl");
        if (string.IsNullOrWhiteSpace(instanceUrl))
            return null;

        Guid.TryParse(GetStringProperty(env, "name"), out var environmentId);
        Guid.TryParse(GetStringProperty(linked, "resourceId"), out var organizationId);

        return new EnvironmentInfo
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