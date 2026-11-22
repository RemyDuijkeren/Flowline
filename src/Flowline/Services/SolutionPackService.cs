using CliWrap;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Config;
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

    /// <summary>
    /// Copies the files git tracks under <c>&lt;dataverseSolutionFolder&gt;/src</c> into a new temp folder and returns
    /// it, so a pack reads the commit rather than whatever sits on disk: an ignored binary or stray file never
    /// ships. The clean-tree check has already run, so the tracked files on disk equal HEAD in the checked-out
    /// form pac reads today (line endings, filters and LFS behave as they always have). The caller deletes it.
    /// </summary>
    internal static async Task<string> StageTrackedSolutionAsync(string dataverseSolutionFolder, string rootFolder, SubprocessCapture capture, CancellationToken ct)
    {
        var src = Path.Combine(dataverseSolutionFolder, "src");
        var staged = Directory.CreateTempSubdirectory("flowline-pack-").FullName;
        try
        {
            // Copied in git's index order, the order a fresh checkout writes them: pac lists components in the
            // order it finds folders, so this matches a fresh clone of this commit (same files and contents; byte
            // order inside customizations.xml can still follow how a filesystem lists folders).
            foreach (var tracked in await GitUtils.GetTrackedFilesAsync(src, rootFolder, capture, ct))
            {
                var file = Path.GetFullPath(Path.Combine(rootFolder, tracked));
                var target = Path.Combine(staged, Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            return staged;
        }
        catch
        {
            DeleteStagedFolder(staged, logger: null);
            throw;
        }
    }

    /// <summary>
    /// Without fresh build output, every plugin, plugin package and web resource slot must have its binary in the
    /// commit. pac skips a missing one with only a warning, so this fails first and names the way out.
    /// </summary>
    internal static void EnsureSlotsHaveBinaries(string stagedSrc)
    {
        var missing = BuildOutputSlotMatcher.FindSlots(stagedSrc)
            .Where(slot => !File.Exists(Path.Combine(stagedSrc, slot.BinaryRelativePath)))
            .Select(slot => slot.Key)
            .ToList();
        if (missing.Count == 0) return;

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"No committed binary for {Shown(missing)} — commit it, or pass --binaries-from-build to pack it from the build.");
    }

    /// <summary>
    /// Builds every plugin project and the WebResources project the way push does, and matches what they produced
    /// to the slots of the committed solution. Fails before anything is staged when a build output has no slot or a
    /// slot has no build output, so every binary in the zip comes from the build.
    /// </summary>
    internal static async Task<IReadOnlyList<SlotCopy>> BuildForPackAsync(IAnsiConsole console, SubprocessCapture capture,
        SolutionFileLayout layout, string solutionUniqueName, PluginPackageMode pluginPackageMode, bool noBuild, string rootFolder, CancellationToken ct)
    {
        var plugins = new List<PluginBuildOutput>();
        foreach (var candidate in layout.PluginProjects)
            if (await PushSupport.BuildPluginProjectAsync(console, capture, candidate, noBuild, pluginPackageMode, ct) is { } built)
                plugins.Add(new PluginBuildOutput(built.AssemblyName, built.PushPath,
                    IsPackage: built.PushPath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)));

        var webResources = new List<WebResourceBuildOutput>();
        if (layout.WebResourcesProjectPath is { } webResourcesProject)
        {
            var dist = await PushSupport.BuildWebResourcesAsync(console, capture, webResourcesProject, noBuild, ct);
            webResources.AddRange(Directory.EnumerateFiles(dist, "*", SearchOption.AllDirectories)
                .Select(file => new WebResourceBuildOutput(Path.GetRelativePath(dist, file).Replace('\\', '/'), file)));
        }

        var src = Path.Combine(layout.DataverseSolutionFolder, "src");
        var match = BuildOutputSlotMatcher.Match(BuildOutputSlotMatcher.FindSlots(src), plugins, webResources, solutionUniqueName);
        EnsureEveryOutputHasASlot(match);

        var tracked = (await GitUtils.GetTrackedFilesAsync(src, rootFolder, capture, ct))
            .Select(file => Path.GetRelativePath(src, Path.GetFullPath(Path.Combine(rootFolder, file))).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (match.Copies.Any(copy => tracked.Contains(copy.SlotRelativePath)))
        {
            console.Warning("Plugin or web resource binaries are committed in git, and the build replaces them in the zip. To keep them out of source control, add this to .gitignore:");
            console.WriteLine(GitignoreAdvice);
        }

        console.Warning("Packing fresh build output — DEV hasn't confirmed these binaries yet.");
        return match.Copies;
    }

    // --path supplies a finished zip, so there is nothing to build; --no-build only qualifies the build flag.
    internal static void ValidateBuildFlags(bool usingExplicitArtifact, bool binariesFromBuild, bool noBuild)
    {
        if (usingExplicitArtifact && binariesFromBuild)
            throw new FlowlineException(ExitCode.ValidationFailed, "--path imports a finished zip, so --binaries-from-build has nothing to build — drop one of them.");
        if (noBuild && !binariesFromBuild)
            throw new FlowlineException(ExitCode.ValidationFailed, "--no-build only applies with --binaries-from-build — add it, or drop --no-build.");
    }

    internal static void EnsureEveryOutputHasASlot(SlotMatchResult match)
    {
        if (match.DuplicateOutputs.Count > 0)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Two build outputs map to the same place in the solution: {Shown(match.DuplicateOutputs)} — rename or remove one, then pack again.");
        if (match.UnmatchedOutputs.Count > 0)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"No place in the solution for {Shown(match.UnmatchedOutputs)} — run 'push' then 'pull' to add it, then pack again.");
        if (match.SlotsWithoutOutput.Count > 0)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"No build output for {Shown(match.SlotsWithoutOutput.Select(slot => slot.Key).ToList())} — add it to the plugin or WebResources project, or remove it from the solution.");
    }

    /// <summary>Lays the build output over a staged copy of the solution; the working tree is never written.</summary>
    internal static void OverlayBuildOutput(string staged, IReadOnlyList<SlotCopy> copies)
    {
        foreach (var copy in copies)
        {
            var target = Path.Combine(staged, copy.SlotRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(copy.SourcePath, target, overwrite: true);
        }
    }

    internal const string GitignoreAdvice = """
          # Plugin assembly / package output, packed from the build with --binaries-from-build
          Solution/src/PluginAssemblies/**/*.dll
          Solution/src/pluginpackages/**/*.nupkg

          # Web resource content, keep the .data.xml metadata tracked
          Solution/src/WebResources/**
          !Solution/src/WebResources/**/
          !Solution/src/WebResources/**/*.data.xml

        After that, every pack and deploy needs --binaries-from-build, because the commit no longer holds the binaries.
        """;

    static string Shown(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" (+{names.Count - 3} more)" : "");

    /// <summary>
    /// The pack both commands run: stage the committed solution, lay any build output over it (otherwise check that
    /// every slot has a committed binary), pack, and delete the staged folder.
    /// </summary>
    internal static async Task PackFromCommitAsync(IAnsiConsole console, SubprocessCapture capture, ILogger logger, string uniqueName,
        string dataverseSolutionFolder, string rootFolder, string zipPath, bool managed, IReadOnlyList<SlotCopy>? buildOutput, CancellationToken ct)
    {
        var staged = await StageTrackedSolutionAsync(dataverseSolutionFolder, rootFolder, capture, ct);
        try
        {
            if (buildOutput is null)
                EnsureSlotsHaveBinaries(staged);
            else
                OverlayBuildOutput(staged, buildOutput);
            await PackAsync(console, capture, uniqueName, staged, zipPath, managed, ct);
        }
        finally
        {
            DeleteStagedFolder(staged, logger);
        }
    }

    /// <summary>Best effort: a staged folder left behind sits in the system temp folder, never in the repo.</summary>
    internal static void DeleteStagedFolder(string staged, ILogger? logger)
    {
        try
        {
            Directory.Delete(staged, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "Could not delete staged pack folder {Folder}", staged);
        }
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
    //
    // Under --binaries-from-build the callers check the whole repository instead: the build also reads a root
    // Directory.Build.props, referenced projects and signing keys, and none of that may be uncommitted.
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
    internal static string BuildDirtyTreeMessage(IReadOnlyList<string> changes, string command) =>
        $"Uncommitted changes in {Shown(changes)} — commit or stash first, then {command}.";

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
