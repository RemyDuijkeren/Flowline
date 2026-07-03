using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

public class SolutionCheckService(IAnsiConsole console, SubprocessCapture capture) : IPostDeployService
{
    public async Task RunPreImportAsync(PostDeployContext context, CancellationToken ct)
    {
        var outputDirectory = Path.Combine(
            Path.GetDirectoryName(context.PackagePath) ?? Path.GetTempPath(), "checker-output");

        var result = await PacUtils.CheckSolutionAsync(context.PackagePath, context.EnvironmentUrl, outputDirectory, capture, ct);

        if (ShouldAbort(result.CriticalCount))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Solution checker found {result.CriticalCount} Critical finding{(result.CriticalCount == 1 ? "" : "s")} — see {result.OutputDirectory} for the full report.");

        console.Ok($"Solution checker: {result.TotalCount} finding{(result.TotalCount == 1 ? "" : "s")}, 0 Critical.");
    }

    public Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct) => Task.FromResult(0);

    // Static, tested directly — the throw itself happens inline in RunPreImportAsync (KTD1).
    internal static bool ShouldAbort(int criticalCount) => criticalCount > 0;
}
