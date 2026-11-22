using CliWrap;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Diagnostics;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

/// <summary>
/// The one pack path `pack` and `deploy` share: the versioned zip name and the `pac solution pack` call.
/// </summary>
internal static class SolutionPackService
{
    /// <summary>
    /// <c>&lt;UniqueName&gt;_&lt;major&gt;_&lt;minor&gt;_&lt;build&gt;_&lt;revision&gt;_managed.zip</c> (or <c>_unmanaged</c>):
    /// the version is in the name so a zip handed to a customer or attached to a release says what it is.
    /// </summary>
    internal static string BuildZipFileName(string uniqueName, string version, bool managed) =>
        $"{uniqueName}_{version.Replace('.', '_')}_{(managed ? "managed" : "unmanaged")}.zip";

    /// <summary>
    /// <paramref name="output"/> when given (relative to the current directory, like any CLI path),
    /// otherwise <c>artifacts/</c> next to the solution.
    /// </summary>
    internal static string ResolveZipPath(string solutionRoot, string? output, string uniqueName, string version, bool managed)
    {
        var folder = string.IsNullOrWhiteSpace(output)
            ? Path.Combine(solutionRoot, "artifacts")
            : Path.GetFullPath(output);
        return Path.Combine(folder, BuildZipFileName(uniqueName, version, managed));
    }

    /// <summary>Packs an unpacked solution folder into <paramref name="zipPath"/>; throws when pac fails.</summary>
    internal static async Task PackAsync(IAnsiConsole console, SubprocessCapture capture, string uniqueName,
        string sourceFolder, string zipPath, bool managed, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(ct);
        var result = await console.Status().FlowlineSpinner().StartAsync(
            $"Packing [bold]{uniqueName}[/]...",
            _ => Cli.Wrap(cmdName)
                    .WithArguments(args => args
                        .AddIfNotNull(prefixArgs)
                        .Add("solution").Add("pack")
                        .Add("--folder").Add(sourceFolder)
                        .Add("--zipFile").Add(zipPath)
                        .Add("--packageType").Add(managed ? "Managed" : "Unmanaged"))
                    .WithValidation(CommandResultValidation.None)
                    .WithCapture(capture)
                    .ExecuteAsync(ct)
                    .Task);

        if (result.ExitCode != 0)
            throw new FlowlineException(ExitCode.BuildFailed, "Pack failed — check your solution source.");
    }
}
