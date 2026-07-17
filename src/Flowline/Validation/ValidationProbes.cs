using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Validation;

public sealed class ValidationProbes
{
    // Default capture for the static FlowlineValidator.Default instance. Errors still print.
    static readonly SubprocessCapture s_defaultCapture = new SubprocessCapture(AnsiConsole.Console);

    // Default DataverseConnector for the profile-aware environment probe — same static-default-instance
    // pattern as s_defaultCapture, so FlowlineValidator.Default needs no DI wiring.
    static readonly DataverseConnector s_defaultDataverseConnector = new(AnsiConsole.Console, new HttpClient());

    public Func<bool, CancellationToken, Task<string>> CheckDotNetAsync { get; init; } =
        DotNetUtils.AssertDotNetInstalledAsync;

    public Func<bool, CancellationToken, Task<(string Version, string InstallType)>> CheckPacAsync { get; init; } =
        PacUtils.AssertPacCliInstalledAsync;

    public Func<bool, CancellationToken, Task<string>> CheckGitAsync { get; init; } =
        (verbose, ct) => GitUtils.AssertGitInstalledAsync(s_defaultCapture, verbose, ct);

    public Func<string, bool, CancellationToken, Task> CheckGitRepoAsync { get; init; } =
        (rootFolder, verbose, ct) => GitUtils.AssertGitRepoAsync(rootFolder, s_defaultCapture, verbose, ct);

    public Func<string, bool, CancellationToken, Task<EnvironmentInfo?>> GetEnvironmentAsync { get; init; } =
        (url, _, ct) => PacUtils.GetEnvironmentInfoByUrlAsync(url, s_defaultCapture, ct);

    // Profile-scoped, pac.exe-free environment lookup via a direct BAP admin API token read — used
    // wherever a PAC profile has already been resolved for the target URL. GetEnvironmentAsync above
    // stays pac.exe-backed for ProvisionCommand's target-environment-creation checks (KTD7), which
    // check a URL that intentionally has no matching local PAC profile yet.
    public Func<PacProfile, string, bool, CancellationToken, Task<EnvironmentInfo?>> GetEnvironmentByProfileAsync { get; init; } =
        (profile, url, _, ct) => s_defaultDataverseConnector.GetEnvironmentInfoAsync(profile, url, ct);

    public Func<string, bool, CancellationToken, Task<List<SolutionInfo>>> GetSolutionsAsync { get; init; } =
        (url, _, ct) => PacUtils.GetSolutionsAsync(url, s_defaultCapture, ct);

    public Func<string, string, bool, CancellationToken, Task<string?>> GetPublisherCustomizationPrefixAsync { get; init; } =
        (url, name, _, ct) => PacUtils.GetPublisherCustomizationPrefixAsync(url, name, s_defaultCapture, ct);
}
