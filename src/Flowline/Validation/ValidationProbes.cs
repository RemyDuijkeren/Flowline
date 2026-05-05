using Flowline.Utils;

namespace Flowline.Validation;

public sealed class ValidationProbes
{
    public Func<bool, CancellationToken, Task<string>> CheckDotNetAsync { get; init; } =
        DotNetUtils.AssertDotNetInstalledAsync;

    public Func<bool, CancellationToken, Task<(string Version, string InstallType)>> CheckPacAsync { get; init; } =
        PacUtils.AssertPacCliInstalledAsync;

    public Func<bool, CancellationToken, Task<string>> CheckGitAsync { get; init; } =
        GitUtils.AssertGitInstalledAsync;

    public Func<string, bool, CancellationToken, Task> CheckGitRepoAsync { get; init; } =
        GitUtils.AssertGitRepoAsync;

    public Func<string, bool, CancellationToken, Task<EnvironmentInfo?>> GetEnvironmentAsync { get; init; } =
        PacUtils.GetEnvironmentInfoByUrlAsync;

    public Func<string, bool, CancellationToken, Task<List<SolutionInfo>>> GetSolutionsAsync { get; init; } =
        PacUtils.GetSolutionsAsync;
}
