using Flowline.Core.Dataverse;
using Flowline.Core.Models;
using Spectre.Console;

namespace Flowline.Core.Validation;

// KTD3: every probe that shells out (pac, git, dotnet) defaults to a stub that throws, so a caller that
// forgot to bind it fails loudly instead of silently launching a process. The real bindings are supplied
// by the composition root (Flowline's PacValidationProbes).
public sealed class ValidationProbes
{
    // Default DataverseConnector for the profile-aware environment probe. It is pac.exe-free (a direct BAP
    // admin API token read), so it stays a real default rather than a stub.
    static readonly DataverseConnector s_defaultDataverseConnector = new(AnsiConsole.Console, new HttpClient());

    public Func<bool, CancellationToken, Task<string>> CheckDotNetAsync { get; init; } =
        (_, _) => throw NotBound(nameof(CheckDotNetAsync));

    public Func<bool, CancellationToken, Task<(string Version, string InstallType)>> CheckPacAsync { get; init; } =
        (_, _) => throw NotBound(nameof(CheckPacAsync));

    public Func<bool, CancellationToken, Task<string>> CheckGitAsync { get; init; } =
        (_, _) => throw NotBound(nameof(CheckGitAsync));

    public Func<string, bool, CancellationToken, Task> CheckGitRepoAsync { get; init; } =
        (_, _, _) => throw NotBound(nameof(CheckGitRepoAsync));

    public Func<string, CancellationToken, Task<EnvironmentInfo?>> GetEnvironmentAsync { get; init; } =
        (_, _) => throw NotBound(nameof(GetEnvironmentAsync));

    // Profile-scoped, pac.exe-free environment lookup via a direct BAP admin API token read — used
    // wherever a PAC auth profile has already been resolved for the target URL. GetEnvironmentAsync above
    // stays pac.exe-backed for ProvisionCommand's target-environment-creation checks (KTD7), which
    // check a URL that intentionally has no matching local PAC auth profile yet.
    public Func<PacProfile, string, CancellationToken, Task<EnvironmentInfo?>> GetEnvironmentByProfileAsync { get; init; } =
        (profile, url, ct) => s_defaultDataverseConnector.GetEnvironmentInfoAsync(profile, url, ct);

    public Func<string, CancellationToken, Task<List<SolutionInfo>>> GetSolutionsAsync { get; init; } =
        (_, _) => throw NotBound(nameof(GetSolutionsAsync));

    public Func<string, string, CancellationToken, Task<string?>> GetPublisherCustomizationPrefixAsync { get; init; } =
        (_, _, _) => throw NotBound(nameof(GetPublisherCustomizationPrefixAsync));

    static InvalidOperationException NotBound(string probe) =>
        new($"Validation probe '{probe}' has no binding. The composition root must supply it.");
}
