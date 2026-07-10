using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

public class SolutionCheckService(IAnsiConsole console, SubprocessCapture capture) : IPostDeployService
{
    public async Task RunPreImportAsync(PostDeployContext context, CancellationToken ct)
    {
        var outputDirectory = Path.Combine(
            Path.GetDirectoryName(context.PackagePath) ?? Path.GetTempPath(), "solution-check");

        var result = await PacUtils.CheckSolutionAsync(context.PackagePath, context.Solution.EnvironmentUrl, outputDirectory, capture, ct);

        if (ShouldAbort(result.CriticalCount))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Solution checker found {result.CriticalCount} Critical finding{Plural(result.CriticalCount)} — see {result.OutputDirectory} for the full report.");

        console.Ok($"Solution checker: {result.TotalCount} finding{Plural(result.TotalCount)}, 0 Critical.");
    }

    public Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct) => Task.FromResult(0);

    internal static bool ShouldAbort(int criticalCount) => criticalCount > 0;

    static string Plural(int count) => count == 1 ? "" : "s";
}
