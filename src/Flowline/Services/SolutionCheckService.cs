using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Deploy;
using Flowline.Diagnostics;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

public class SolutionCheckService(IAnsiConsole console, SubprocessCapture capture) : IPostDeployService
{
    public Task RunPreImportAsync(PostDeployContext context, CancellationToken ct) =>
        RunAsync(console, capture, context.PackagePath, context.Solution.EnvironmentUrl, ct);

    /// <summary>
    /// Runs the solution checker on a zip and fails on any Critical finding. Shared by deploy (against its
    /// target) and pack (no environment: pac then checks with the active auth profile only).
    /// </summary>
    internal static async Task RunAsync(IAnsiConsole console, SubprocessCapture capture, string packagePath, string? environmentUrl, CancellationToken ct)
    {
        var outputDirectory = Path.Combine(
            Path.GetDirectoryName(packagePath) ?? Path.GetTempPath(), "solution-check");

        var result = await PacUtils.CheckSolutionAsync(packagePath, environmentUrl, outputDirectory, capture, ct);

        if (ShouldAbort(result.CriticalCount))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Solution checker found {result.CriticalCount} Critical finding{Plural(result.CriticalCount)} — see {result.OutputDirectory} for the full report.");

        console.Ok($"Solution checker: {result.TotalCount} finding{Plural(result.TotalCount)}, 0 Critical.");
    }

    public Task<PostDeployOutcome> RunPostImportAsync(PostDeployContext context, CancellationToken ct) => Task.FromResult(PostDeployOutcome.Clean);

    internal static bool ShouldAbort(int criticalCount) => criticalCount > 0;

    static string Plural(int count) => count == 1 ? "" : "s";
}
