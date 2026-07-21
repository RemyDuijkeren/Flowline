using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public enum BumpComponent { Patch, Minor, Major, None }

public class SyncCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) :
    FlowlineCommand<SyncCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandOption("--dev <URL>")]
        [Description("Development environment URL")]
        public string? DevUrl { get; set; }

        [CommandOption("--managed [false]")]
        [Description("Include managed artifacts (--managed false resets to default)")]
        [DefaultValue(true)]
        public FlagValue<bool> IncludeManaged { get; set; } = null!;

        [CommandOption("--bump")]
        [Description("Version component to increment: patch, minor, major, or none to skip bumping (default: patch)")]
        [DefaultValue(BumpComponent.Patch)]
        public BumpComponent Bump { get; set; } = BumpComponent.Patch;

        [CommandOption("--no-build")]
        [Description("Skip the 'dotnet build' validation step")]
        [DefaultValue(false)]
        public bool NoBuild { get; set; } = false;
    }

    internal static readonly string[] ValidSpecifiers = ["dirty", "config", "all"];
    protected override string[] ValidForceSpecifiers => ValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Dev URL is required
        var (devEnv, _) = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, settings.DevUrl, settings, cancellationToken);

        // Solution is the single one configured in .flowline — sync is project-mode only
        var (projectSln, slnInfo) = await GetAndCheckSolutionAsync(null, devEnv.EnvironmentUrl!, settings.IncludeManaged.IsSet ? settings.IncludeManaged.Value : (bool?)null, settings, cancellationToken);
        if (slnInfo.IsManaged)
            throw new FlowlineException(ExitCode.ValidationFailed, "Managed solutions are not supported for sync — use an unmanaged solution.");

        Logger.LogInformation("target={EnvironmentUrl} solution={SolutionName} bump={Bump}", devEnv.EnvironmentUrl, projectSln.UniqueName, settings.Bump);

        Config!.Save();
        Console.Verbose($"Project configuration saved to {ProjectConfig.s_configFileName}");

        // Validate that we have an initialized project
        var slnFolder = RootFolder;

        // The solution file says which project packs the solution, and its folder is where the unpacked
        // source lives — sync never composes either. Resolution throws with the fix in it when there's no
        // solution file, no .cdsproj entry, or the entry points at nothing. Loaded once and threaded through
        // the drift check below, so one sync never parses the solution file twice and acts on two answers.
        var layout = await SolutionFileLayout.LoadAsync(slnFolder, cancellationToken);
        var dataverseSolutionFolder = layout.DataverseSolutionFolder;

        // Resolve WebResources (and, through its exclusion set, the plugin projects) up front — BEFORE any
        // mutating call (SetSolutionVersionAsync bumps the live Dev version, SyncSolutionFromDataverseAsync
        // overwrites local source). A bad WebResources layout then fails as a clean precondition instead of
        // aborting mid-sync with Dev already mutated. The Lazy caches the result for the drift check below.
        _ = layout.WebResourcesProjectPath;

        // Check for uncommitted changes
        var srcPath = Path.Combine(dataverseSolutionFolder, "src");
        // Rendered from the resolved path, never spelled out: a user who has relocated the Dataverse solution
        // folder must not be told to look in 'Solution/src' when the check ran somewhere else entirely.
        var srcDisplay = ConsolePath.FormatRelativePath(srcPath, RootFolder);
        var preSyncSummary = await SolutionChangeSummary.ComputeAsync(srcPath, RootFolder, _capture, cancellationToken);
        Logger.LogInformation("Diff: {TotalFiles} files changed", preSyncSummary.TotalFiles);
        if (preSyncSummary.TotalFiles > 0)
        {
            if (settings.HasForce("dirty"))
            {
                Console.Warning($"Uncommitted changes in '{srcDisplay}' — overwriting.");
                preSyncSummary.WriteFlat(Console, RuntimeOptions, "[dim]  ");
            }
            else
            {
                Console.Warning($"Found uncommitted changes in '{srcDisplay}'.");
                preSyncSummary.WriteFlat(Console, RuntimeOptions, "[dim]  ");
                var srcDisplayPlain = ConsolePath.FormatRelativePath(srcPath, RootFolder, markup: false);
                throw new FlowlineException(ExitCode.DirtyWorkingDirectory, $"Uncommitted changes in '{srcDisplayPlain}' — Commit or stash changes first, or re-run with --force dirty.");
            }
        }

        // Bump version in Dataverse before sync so the downloaded XML reflects the new version
        var skipBump = settings.Bump == BumpComponent.None;
        var tagVersion = await Console.Status().FlowlineSpinner().StartAsync(
            skipBump ? $"Reading version [bold]{projectSln.UniqueName}[/]..." : $"Bump {settings.Bump} version [bold]{projectSln.UniqueName}[/]...",
            async ctx =>
            {
                var currentVersion = await PacUtils.GetSolutionVersionAsync(slnInfo.SolutionUniqueName!, devEnv.EnvironmentUrl!, _capture, cancellationToken);
                Console.Verbose($"Current version: {currentVersion}");
                if (skipBump)
                    return ToTagVersion(currentVersion);

                var newVersion = BumpVersion(currentVersion, settings.Bump);
                await PacUtils.SetSolutionVersionAsync(slnInfo.SolutionUniqueName!, newVersion, devEnv.EnvironmentUrl!, _capture, cancellationToken);
                Console.Verbose($"New version: {newVersion}");
                return ToTagVersion(newVersion);
            });

        if (skipBump)
            Console.Skip($"Version bump — skipping (--bump none active), current version {tagVersion}");
        else
            Console.Ok($"Version bumped: {tagVersion}");

        // Sync solution from Dataverse
        await PacUtils.SyncSolutionFromDataverseAsync(projectSln.UniqueName, dataverseSolutionFolder, devEnv.EnvironmentUrl!, projectSln.IncludeManaged, _capture, cancellationToken);

        // Pack the solution in pac to validate it
        Logger.LogInformation("Validating pack: {SolutionName}", projectSln.UniqueName);
        var artifactsFolder = Path.Combine(slnFolder, "artifacts");
        if (await PacUtils.PackSolutionAsync(projectSln, dataverseSolutionFolder, artifactsFolder, false, _capture, cancellationToken) != 0) return (int)ExitCode.BuildFailed;
        if (projectSln.IncludeManaged &&
            await PacUtils.PackSolutionAsync(projectSln, dataverseSolutionFolder, artifactsFolder, true, _capture, cancellationToken) != 0)
        {
            return (int)ExitCode.BuildFailed;
        }

        // Build the solution in dotnet to validate it (Debug = unmanaged, Release = managed!)
        Logger.LogInformation("Validating build: {SlnFolder}", slnFolder);
        if (settings.NoBuild)
            Console.Skip("Build validation — skipping (--no-build active)");
        else if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, _capture, cancellationToken) != 0)
            return (int)ExitCode.BuildFailed;

        // Check for drift between local solution (Plugins/WebResources) and Dataverse (/src). A null
        // WebResources project is a legitimate state — warn loudly and let the checker skip that half.
        if (layout.WebResourcesProjectPath is null)
            Console.Warning("No WebResources project — skipping web-resource drift check.");
        var driftWarnings = await PluginWebResourceDriftChecker.CheckAsync(slnFolder, layout, dataverseSolutionFolder, slnInfo.PublisherPrefix, cancellationToken);
        Logger.LogInformation("Drift: {DriftCount} warnings", driftWarnings.Count);
        if (driftWarnings.Count == 0)
        {
            Console.Ok("Plugins / WebResources match Dataverse");
        }
        else
        {
            Console.Warning("Dataverse doesn't match local Plugins / WebResources:");
            foreach (var w in driftWarnings)
            {
                var hint = w.Category switch
                {
                    DriftCategory.ContentDiffers => $"- '{w.RelativePath}' changed in Dataverse — push to overwrite with local",
                    DriftCategory.NewInDataverse => $"- '{w.RelativePath}' added in Dataverse — add to local WebResources, or push to remove",
                    DriftCategory.OnlyLocal => $"- '{w.RelativePath}' local only, not in Dataverse — push to upload",
                    DriftCategory.PluginSizeMismatch => $"- '{w.RelativePath}' plugin size differs — rebuild and push if local is current",
                    DriftCategory.OrphanAssembly => $"- '{w.RelativePath}' in Dataverse — no local plugin source, won't manage",
                    _ => $"- {w.RelativePath}"
                };
                Console.MarkupLine($"  {hint}");
            }
        }

        // Summary of changes
        var summary = await SolutionChangeSummary.ComputeAsync(srcPath, RootFolder, _capture, cancellationToken);
        Logger.LogInformation("Diff: {TotalFiles} files changed", summary.TotalFiles);
        summary.WriteTree(Console, devEnv.DisplayName, settings.Verbose);
        await summary.WriteChangesFileAsync(slnFolder, projectSln.UniqueName, devEnv.DisplayName, cancellationToken);
        await new DataverseContextGenerator(Console).GenerateAsync(
            srcPath, projectSln.UniqueName, RootFolder, cancellationToken);

        Console.Done(summary.TotalFiles == 0
            ? $"Synced {tagVersion} — no component changes, nothing to deploy."
            : $"Synced {tagVersion}. Commit, then 'git tag {tagVersion}' when ready to deploy. ◝(ᵔᵕᵔ)◜");

        return 0;
    }

    internal static string BumpVersion(string version, BumpComponent component)
    {
        var parts = version.Split('.');
        var nums = parts.Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();

        switch (component)
        {
            case BumpComponent.Major:
                nums[0]++;
                for (var i = 1; i < nums.Length; i++) nums[i] = 0;
                break;
            case BumpComponent.Minor:
                nums[1]++;
                for (var i = 2; i < nums.Length; i++) nums[i] = 0;
                break;
            case BumpComponent.Patch:
                nums[2]++;
                for (var i = 3; i < nums.Length; i++) nums[i] = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(component), component, "BumpVersion does not accept BumpComponent.None — callers must skip the bump entirely instead.");
        }

        return string.Join(".", nums);
    }

    internal static string ToTagVersion(string version) =>
        string.Join(".", version.Split('.').Take(3));

}
