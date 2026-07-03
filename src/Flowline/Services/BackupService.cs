using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

public class BackupService(IAnsiConsole console, SubprocessCapture capture) : IPostDeployService
{
    public async Task RunPreImportAsync(PostDeployContext context, CancellationToken ct)
    {
        var label = PacUtils.BuildBackupLabel(context.SolutionName, DateTime.UtcNow);
        await PacUtils.BackupEnvironmentAsync(context.EnvironmentUrl, label, capture, ct);
        console.Ok($"Environment backed up ({label}).");
    }

    public Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct) => Task.FromResult(0);
}
