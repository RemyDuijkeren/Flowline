using Flowline.Utils;
using Microsoft.Extensions.Logging;

namespace Flowline.Services;

public sealed class SolutionDiffService(ILogger<SolutionDiffService> logger)
{
    public async Task<SolutionChangeSummary> ComputeAsync(string srcFolder, string workingDirectory, bool verbose, CancellationToken cancellationToken)
    {
        var result = await SolutionChangeSummary.ComputeAsync(srcFolder, workingDirectory, verbose, cancellationToken);
        logger.LogInformation("Diff: {TotalFiles} files changed", result.TotalFiles);
        return result;
    }
}
