using Flowline;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.PowerPlatform.Dataverse.Client;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Spectre.Console;

namespace Flowline.Core.Services;

public class DataverseConnector(IAnsiConsole console, HttpClient httpClient)
{
    // Power Platform CLI's own registered app id (Microsoft-documented as "Power Platform CLI - pac":
    // https://github.com/MicrosoftDocs/power-platform/blob/main/power-platform/admin/apps-to-allow.md),
    // reused verbatim so Flowline's own MSAL client can silently read tokens pac.exe already cached
    // under this identity, and so first-run users don't need a separate app registration/consent.
    // Previously hardcoded 51f81489-12ee-4a9e-aaae-a2591f45987d, which the SAME Microsoft doc lists as
    // "Dynamics 365 Example Client Application" -- a sample/tutorial app id (also explicitly labeled
    // "Sample / stand-in appID" in Microsoft's own PowerPlatform-DataverseServiceClient source), not
    // pac.exe's real identity. Using it caused AADSTS90072-shaped failures specifically for BAP admin
    // API calls (GetEnvironmentInfoAsync) in tenants where the sample app had incidental Dataverse
    // consent but was never granted BAP admin scope -- while pac.exe's own subprocess calls (PacUtils)
    // worked fine, since those use pac.exe's real, already-consented app id internally.
    public const string PacCliAppId = "9cee029c-6210-4654-90bb-17e6e9d36617";

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
            throw new ArgumentException("Environment URL is required for connecting via PAC auth profile.", nameof(environmentUrl));

        var resourceUrl = environmentUrl.TrimEnd('/');
        var serviceUri  = new Uri(resourceUrl + "/");

        console.Verbose($"Connecting via PAC auth profile '{ResolveProfileLabel(profile)}' at {resourceUrl}...");

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

    // PAC's authprofiles_v2.json gives an unnamed profile an empty-string Name, not null — a bare
    // ?? chain never falls through to User for that shape (see StatusCommand.FormatProfileNote's
    // identical fix).
    internal static string? ResolveProfileLabel(PacProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Name) ? profile.User : profile.Name;

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
    /// acquisition, scoped to the target Dataverse environment itself (not an admin API), and calls its
    /// RetrieveCurrentOrganization Web API function for the Production/Sandbox Type instead of relying
    /// on the BAP admin API — see PacCliAppId's comment for why a BAP admin dependency was fragile.
    /// </summary>
    public async Task<EnvironmentInfo?> GetEnvironmentInfoAsync(
        PacProfile profile,
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentException("Environment URL is required for looking up environment info.", nameof(environmentUrl));

        var resourceUrl = environmentUrl.TrimEnd('/');
        var authority = ResolveAuthority(profile);
        var cacheHelper = await GetOrCreateMsalCacheHelperAsync();

        var accessToken = profile.IsServicePrincipal
            ? (await AcquireServicePrincipalTokenAsync(profile, authority, cacheHelper, resourceUrl, cancellationToken)).Token.AccessToken
            : (await AcquireUserTokenAsync(profile, authority, cacheHelper, resourceUrl, cancellationToken)).Token.AccessToken;

        var requestUrl = $"{resourceUrl}/api/data/v9.2/RetrieveCurrentOrganization(AccessType=Microsoft.Dynamics.CRM.EndpointAccessType'Default')";

        // Per-request Authorization header, never HttpClient.DefaultRequestHeaders — httpClient is a
        // shared singleton also used for unrelated calls (e.g. XrmContextToolProvider's NuGet download),
        // and DefaultRequestHeaders would leak this bearer token onto those requests too.
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            // A non-existent environment URL, or one this profile can't access, fails here (DNS/connection
            // failure, or a non-2xx from the host) -- callers already treat a null result as "not found".
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return MapRetrieveCurrentOrganizationResponse(json, resourceUrl);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // Shared by ConnectUserAsync (which additionally wraps the result in a ServiceClient renewal
    // callback) and GetEnvironmentInfoAsync (which only needs Token.AccessToken).
    //
    // PAC 2.9+ defaults new user profiles to OS/WAM token storage (shown as Type=OperatingSystem in
    // `pac auth list`): the refresh token lives in the Windows broker, not tokencache_msalv3.dat, so a
    // file-cache-only silent read finds no account and fails with "Session expired" even while `pac`
    // itself still connects. So on Windows we try the WAM broker first, then fall back to the file MSAL
    // cache (File-type profiles, older PAC, and every non-Windows host). Silent only -- never
    // interactive; PAC still owns `pac auth create`. See
    // docs/test-findings/pac-2.9-user-token-not-in-shared-msal-cache-blocks-flowline.md.
    async Task<(IPublicClientApplication App, AuthenticationResult Token)> AcquireUserTokenAsync(
        PacProfile profile, string authority, MsalCacheHelper cacheHelper, string resourceUrl, CancellationToken cancellationToken)
    {
        var scopes = new[] { $"{resourceUrl}/.default" };

        if (OperatingSystem.IsWindows())
        {
            var viaBroker = await TryAcquireUserTokenViaBrokerAsync(profile, authority, scopes, cancellationToken);
            if (viaBroker is { } acquired)
                return acquired;
        }

        return await AcquireUserTokenViaFileCacheAsync(profile, authority, cacheHelper, scopes, resourceUrl, cancellationToken);
    }

    /// <summary>
    /// Silent acquisition through the Windows WAM broker, where PAC 2.9+ stores OS-type profile tokens.
    /// Returns <c>null</c> -- signalling the caller to fall back to the file MSAL cache -- when the broker
    /// is unavailable or holds no usable token for this account. Only a genuine tenant mismatch
    /// (AADSTS90072/50020), which a file-cache retry can't fix either, surfaces as an error here.
    /// </summary>
    [SupportedOSPlatform("windows")]
    async Task<(IPublicClientApplication App, AuthenticationResult Token)?> TryAcquireUserTokenViaBrokerAsync(
        PacProfile profile, string authority, string[] scopes, CancellationToken cancellationToken)
    {
        IPublicClientApplication app;
        try
        {
            app = PublicClientApplicationBuilder
                .Create(PacCliAppId)
                .WithAuthority(authority)
                .WithDefaultRedirectUri()
                .WithParentActivityOrWindow(GetConsoleOrTerminalWindow)
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
                .Build();
        }
        catch (Exception ex) when (ex is not FlowlineException)
        {
            console.Verbose($"WAM broker unavailable ({ex.GetType().Name}) -- using the file token cache.");
            return null;
        }

        IAccount account;
        try
        {
            var accounts = await app.GetAccountsAsync();
            // With the broker, GetAccountsAsync surfaces WAM accounts; fall back to the signed-in
            // Windows account when the profile's user isn't among them.
            account = SelectCachedAccount(accounts, profile.User, profile.TenantId)
                      ?? PublicClientApplication.OperatingSystemAccount;
        }
        catch (Exception ex) when (ex is not FlowlineException)
        {
            console.Verbose($"WAM account lookup failed ({ex.GetType().Name}) -- using the file token cache.");
            return null;
        }

        try
        {
            var token = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken);
            return (app, token);
        }
        catch (MsalUiRequiredException ex) when (ex.Message.Contains("AADSTS90072") || ex.Message.Contains("AADSTS50020"))
        {
            throw TenantMismatchException(profile, ex);
        }
        catch (MsalUiRequiredException)
        {
            return null;   // no usable WAM token -- fall back to the file cache
        }
        catch (MsalServiceException ex)
        {
            console.Verbose($"WAM broker error ({ex.ErrorCode}) -- using the file token cache.");
            return null;
        }
    }

    async Task<(IPublicClientApplication App, AuthenticationResult Token)> AcquireUserTokenViaFileCacheAsync(
        PacProfile profile, string authority, MsalCacheHelper cacheHelper, string[] scopes, string resourceUrl,
        CancellationToken cancellationToken)
    {
        var app = PublicClientApplicationBuilder
            .Create(PacCliAppId)
            .WithAuthority(authority)
            .WithRedirectUri("http://localhost")
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
        catch (MsalUiRequiredException ex) when (ex.Message.Contains("AADSTS90072") || ex.Message.Contains("AADSTS50020"))
        {
            throw TenantMismatchException(profile, ex);
        }
        catch (MsalUiRequiredException ex)
        {
            var user = profile.User ?? "unknown";
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"Session expired for '{user}'. Run 'pac auth create --url {resourceUrl}' to re-authenticate.", ex);
        }
    }

    // AADSTS90072/AADSTS50020: the resolved account isn't valid in this tenant. Either it genuinely
    // lacks access, or (if the same account works via 'pac' directly) the cached MSAL entry picked
    // above belongs to a different tenant this username is also known to -- re-running 'pac auth create'
    // for the same account/profile won't fix either case on its own. Distinguished from a plain expired
    // token, which the file-cache path reports as "Session expired".
    static FlowlineException TenantMismatchException(PacProfile profile, MsalUiRequiredException ex)
    {
        var user = profile.User ?? "unknown";
        return new FlowlineException(ExitCode.NotAuthenticated,
            $"'{user}' isn't valid in tenant '{profile.TenantId ?? "unknown"}' for profile '{ResolveProfileLabel(profile) ?? user}'. " +
            "If this account should have access, the cached PAC credential may be stale or bound " +
            "to a different tenant -- run 'pac auth clear' then 'pac auth create' to re-authenticate " +
            "from scratch. If it genuinely lacks access, ask a tenant admin to add it as a guest, " +
            "or sign in with a different account.", ex);
    }

    // WAM parents its dialogs to a window handle (unused on the silent path, but required to configure
    // the broker); a console app supplies its terminal window.
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", ExactSpelling = true)]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [SupportedOSPlatform("windows")]
    static IntPtr GetConsoleOrTerminalWindow()
    {
        const uint GA_ROOTOWNER = 3;
        var hConsole = GetConsoleWindow();
        var owner = GetAncestor(hConsole, GA_ROOTOWNER);
        return owner == IntPtr.Zero ? hConsole : owner;
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

    // RetrieveCurrentOrganization's Web API JSON shape (verified live against a real Dev and a real
    // Production environment — https://<org>.crm.dynamics.com/api/data/v9.2/RetrieveCurrentOrganization(...)):
    //   { "Detail": { "OrganizationId", "EnvironmentId", "FriendlyName", "UniqueName", "UrlName",
    //       "State", "OrganizationType", "OrganizationVersion", ... } }
    // OrganizationType is a string enum name (Microsoft.Xrm.Sdk.Organization.OrganizationType), not the
    // BAP admin API's "environmentSku" — mapped to Flowline's "Production"/"Sandbox"/etc via MapOrganizationType.
    // Pure and internal so it's unit-testable without a token or network call.
    internal static EnvironmentInfo? MapRetrieveCurrentOrganizationResponse(string responseJson, string environmentUrl)
    {
        using var doc = JsonDocument.Parse(responseJson);

        if (!doc.RootElement.TryGetProperty("Detail", out var detail) || detail.ValueKind != JsonValueKind.Object)
            return null;

        Guid.TryParse(GetStringProperty(detail, "EnvironmentId"), out var environmentId);
        Guid.TryParse(GetStringProperty(detail, "OrganizationId"), out var organizationId);

        return new EnvironmentInfo
        {
            EnvironmentId = environmentId,
            EnvironmentUrl = environmentUrl.TrimEnd('/'),
            OrganizationId = organizationId,
            DisplayName = GetStringProperty(detail, "FriendlyName"),
            Type = MapOrganizationType(GetStringProperty(detail, "OrganizationType")),
            DomainName = GetStringProperty(detail, "UrlName"),
            Version = GetStringProperty(detail, "OrganizationVersion")
        };
    }

    // Microsoft.Xrm.Sdk.Organization.OrganizationType enum names -> Flowline's Type string. Unrecognized
    // values pass through as-is rather than defaulting to a guess -- only "Production" is compared
    // against elsewhere (FlowlineCommand's role/Type guard), so an unmapped value simply won't match it.
    internal static string? MapOrganizationType(string? organizationType) => organizationType switch
    {
        "Secondary" or "Customer" => "Production",
        "CustomerTest" or "CustomerFreeTest" => "Sandbox",
        _ => organizationType
    };

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
            // ROPC flow — no browser or PAC auth profile required
            if (username.IndexOfAny([';', '=']) >= 0)
                throw new FlowlineException(ExitCode.ValidationFailed, "XrmContext username must not contain ';' or '='. Remove those characters from --username.");
            if (password.IndexOfAny([';', '=']) >= 0)
                throw new FlowlineException(ExitCode.ValidationFailed, "XrmContext password must not contain ';' or '='. Remove those characters from --password.");
            return $"AuthType=OAuth;Username={username};Password={password};Url={normalizedUrl};AppId={appId};RedirectUri=http://localhost;LoginPrompt=Never;TokenCacheStorePath={tokenCachePath};";
        }

        // Browser OAuth — PAC auth profile validates the environment exists in auth store
        var profile = profiles
            .FirstOrDefault(p => p.Resource?.TrimEnd('/').Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase) == true)
            ?? profiles.FirstOrDefault(p => p.IsUniversal);

        if (profile is null)
            throw new FlowlineException(ExitCode.NotAuthenticated,
                $"No PAC auth profile found for {normalizedUrl}. Run 'pac auth create --environment {normalizedUrl}' first.");

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

    public bool IsProfileActive(PacProfile profile) => IsResolvedProfileActive(profile, LoadPacAuthProfiles());

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

    // Standalone check, not the same as FindBestProfile's ambiguous-candidate active-preference
    // tiebreak: does profiles.Current[resolved.Kind] equal resolved? False when Current has no
    // entry for that Kind (nothing confirmed active) or the resolved profile has no matching entry.
    internal static bool IsResolvedProfileActive(PacProfile resolved, PacAuthProfiles? profiles)
    {
        if (resolved.Kind is null) return false;
        return profiles?.Current?.TryGetValue(resolved.Kind, out var activeProfile) == true
            && activeProfile == resolved;
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

    PacAuthProfiles? _cachedAuthProfiles;

    // Cached for the process lifetime (DataverseConnector is a singleton — one CLI invocation).
    // FindBestProfile/IsProfileActive/GetPacProfiles all funnel through here and were each re-reading
    // + re-parsing the file from disk, so a single profile resolution could hit it 7+ times. Must be
    // invalidated after anything that rewrites the file out from under us — see InvalidateAuthProfilesCache.
    PacAuthProfiles LoadPacAuthProfiles() =>
        _cachedAuthProfiles ??= LoadPacAuthProfiles(Path.Combine(GetPacCliDataDirectory(), "authprofiles_v2.json"));

    // Call after anything that changes PAC CLI's active profile on disk (e.g. 'pac auth select')
    // so the next read reflects the change instead of the cached snapshot.
    public void InvalidateAuthProfilesCache() => _cachedAuthProfiles = null;

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

            console.Verbose($"Loaded {profiles.Profiles?.Count ?? 0} PAC auth profile(s) from {ConsolePath.ShortenPath(authProfilesPath, markup: false)}");

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