using Flowline.Core;
using Flowline.Diagnostics;
using Flowline.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Flowline.Validation;

public sealed class ValidationProbes
{
    // Default capture for the static FlowlineValidator.Default instance.
    // Uses NullLogger (no Serilog) + terminal console, non-verbose. Errors still print.
    static readonly SubprocessCapture s_defaultCapture = new SubprocessCapture(
        NullLogger<SubprocessCapture>.Instance,
        new FlowlineRuntimeOptions(),
        AnsiConsole.Console);

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

    public Func<string, bool, CancellationToken, Task<List<SolutionInfo>>> GetSolutionsAsync { get; init; } =
        (url, _, ct) => PacUtils.GetSolutionsAsync(url, s_defaultCapture, ct);

    public Func<string, string, bool, CancellationToken, Task<string?>> GetPublisherCustomizationPrefixAsync { get; init; } =
        (url, name, _, ct) => PacUtils.GetPublisherCustomizationPrefixAsync(url, name, s_defaultCapture, ct);
}
