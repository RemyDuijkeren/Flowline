using CliWrap;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Deploy;
using Flowline.Core.Environments;
using Flowline.Core.Solutions;
using Flowline.Diagnostics;
using Flowline.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Flowline.Services;

/// <summary>
/// The one pack path `pack` and `deploy` share: the versioned zip name, the `pac solution pack` call, the
/// clean-tree and drift guards, and the CI hand-off.
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

    // The SAME path list scopes both the clean-check and the commit-sha lookup behind deploy's provenance note
    // and tag hint, so the two can never diverge.
    //
    // Deliberately narrow to what actually affects the packed artifact: Solution/, every plugin project the
    // solution file references, and the WebResources project file. The plugin pre-filter is what keeps this
    // narrow — an unrelated csproj in the solution stays out of scope, so it can't block a pack or deploy.
    //
    // Takes no solution name: all three project paths come out of the already-loaded layout, so a relocated
    // or renamed project stays in scope without knowing what it is called.
    internal static IReadOnlyList<string> GetPackInputPaths(SolutionFileLayout layout, string dataverseSolutionFolder) =>
    [
        dataverseSolutionFolder,
        ..layout.PluginProjects.Select(c => c.ProjectPath),
        // Omitted when null: no WebResources project is a real absence, not a scope gap.
        ..layout.WebResourcesProjectPath is { } wr ? new[] { wr } : Array.Empty<string>()
    ];

    internal static async Task ValidateGitCleanAsync(IReadOnlyList<string> inputPaths, string rootFolder, SubprocessCapture capture, string command, CancellationToken ct)
    {
        var changes = await GitUtils.GetUncommittedChangesInPathAsync(inputPaths, rootFolder, capture, ct);
        if (changes.Count == 0) return;

        throw new FlowlineException(ExitCode.DirtyWorkingDirectory, BuildDirtyTreeMessage(changes, command));
    }

    // Names the files rather than the folders it looked in — the scope is discovered, so restating it would
    // mean restating a list the user can't predict from the message.
    internal static string BuildDirtyTreeMessage(IReadOnlyList<string> changes, string command)
    {
        var shown = string.Join(", ", changes.Take(3));
        var more = changes.Count > 3 ? $" (+{changes.Count - 3} more)" : "";
        return $"Uncommitted changes in {shown}{more} — commit or stash first, then {command}.";
    }

    /// <summary>
    /// Fails when the local build output differs from the binaries committed in the solution, so neither command
    /// ships or imports binaries older than the source. Compares only output that already exists; it never builds.
    /// </summary>
    internal static async Task ValidateDriftAsync(IAnsiConsole console, bool forceDrift, string solutionUniqueName,
        SolutionFileLayout layout, string dataverseSolutionFolder, string command, CancellationToken ct)
    {
        // Safety-critical: a null WebResources project means the web-resource half of drift is skipped. Say
        // so loudly — a solution that does have web resources would ship without them being validated.
        if (layout.WebResourcesProjectPath is null)
            console.Warning($"No WebResources project resolved — skipping the web-resource drift check. If this solution has web resources, {command} will not validate them.");

        var drift = (await PluginWebResourceDriftChecker.CheckAsync(solutionUniqueName, layout, dataverseSolutionFolder, cancellationToken: ct))
            .Where(w => w.Category is DriftCategory.OnlyLocal or DriftCategory.PluginSizeMismatch)
            .ToList();

        if (drift.Count == 0) return;

        foreach (var w in drift)
            console.Warning(w.Category == DriftCategory.OnlyLocal
                ? $"Only local: {w.RelativePath}"
                : $"Plugin size mismatch: {w.RelativePath}");

        if (!forceDrift)
            throw new FlowlineException(ExitCode.ValidationFailed, BuildDriftMessage(command));
    }

    internal static string BuildDriftMessage(string command) =>
        $"Local changes not in Dataverse — {(command == "deploy" ? "deploy would revert them" : "the zip would hold the older committed copies")}. Run 'push' then 'pull' to capture them, or use --force drift to skip.";

    // Azure Pipelines' documented stdout logging-command protocol for any process — no SDK, no opt-in flag.
    // The artifact name carries the version too, so it's visible at a glance in the pipeline's Artifacts tab.
    internal static string BuildAzureDevOpsArtifactUploadLine(string packagePath, string solutionName, string version) =>
        $"##vso[artifact.upload artifactname={solutionName}-{version}]{packagePath}";

    // Qualified by solution name so looping over sibling solutions in one workflow step doesn't have each
    // write silently clobber the previous solution's $GITHUB_OUTPUT key.
    internal static string BuildGitHubActionsOutputLine(string packagePath, string solutionName) =>
        $"artifact-path-{solutionName}={packagePath}";

    /// <summary>Hands the zip to the CI pipeline. Never fails the run: a CI side effect is not worth a failed pack or deploy.</summary>
    internal static void PublishArtifactForCi(ILogger logger, string packagePath, string solutionName, string version)
    {
        try
        {
            // Resolved once, absolute for both platforms — packagePath can be relative (a --path deploy
            // takes settings.Path verbatim), and neither CI consumer should have to guess it's relative
            // to whatever directory the agent happened to run the command from.
            var fullPackagePath = Path.GetFullPath(packagePath);

            switch (CiPlatform.Detect())
            {
                case "azuredevops":
                    // Raw System.Console, never the injected IAnsiConsole — Console.MarkupLine would parse the
                    // vso line's literal "[artifact.upload ...]" as a Spectre style tag and throw, and even a
                    // plain IAnsiConsole write can word-wrap this line (it contains a space) across physical
                    // lines once redirected stdout falls back to an 80-column profile width on a real agent,
                    // which silently breaks the agent's single-line ##vso parse.
                    System.Console.WriteLine(BuildAzureDevOpsArtifactUploadLine(fullPackagePath, solutionName, version));
                    break;
                case "github":
                    var githubOutputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
                    if (!string.IsNullOrEmpty(githubOutputPath))
                        File.AppendAllText(githubOutputPath, BuildGitHubActionsOutputLine(fullPackagePath, solutionName) + Environment.NewLine);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish CI artifact signal for {SolutionName}", solutionName);
        }
    }
}
