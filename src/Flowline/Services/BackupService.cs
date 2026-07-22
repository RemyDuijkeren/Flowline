using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Spectre.Console;

namespace Flowline.Services;

public class BackupService(IAnsiConsole console, SubprocessCapture capture) : IPostDeployService
{
    public async Task RunPreImportAsync(PostDeployContext context, CancellationToken ct)
    {
        var label = PacUtils.BuildBackupLabel(context.Solution.Name, DateTime.UtcNow, context.Mode == RunMode.DryRun);
        await PacUtils.BackupEnvironmentAsync(context.Solution.EnvironmentUrl, label, capture, ct);
        console.Ok($"Environment backed up ({label}).");
    }

    public Task<int> RunPostImportAsync(PostDeployContext context, CancellationToken ct) => Task.FromResult(0);
}
