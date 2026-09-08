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

public class SyncCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture, NuGetVersionClient nuGetVersionClient, EnvironmentTargetResolver environmentTargetResolver) :
    FlowlineCommand<SyncCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : EnvironmentSettings
    {
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
        // R3: DEV-only — refused before any PAC profile resolve, connect, or .flowline write.
        var target = await environmentTargetResolver.ResolveAsync(settings.Env, Config!, devOnly: true, IsInteractive(), settings,
            (url, ct) => Validator.GetEnvironmentInfoByUrlAsync(url, settings, settings.NoCache, ct), cancellationToken);
        var (devEnv, _) = await GetAndCheckEnvironmentAsync(target.Url, target.Role, settings, cancellationToken);

        // Solution is the single one configured in .flowline — sync is project-mode only
        var (projectSln, slnInfo) = await GetAndCheckSolutionAsync(null, devEnv.EnvironmentUrl!, settings.IncludeManaged.IsSet ? settings.IncludeManaged.Value : (bool?)null, settings, cancellationToken);
        if (slnInfo.IsManaged)
            throw new FlowlineException(ExitCode.ValidationFailed, "Managed solutions are not supported for sync — use an unmanaged solution.");

        Logger.LogInformation("target={EnvironmentUrl} solution={SolutionName} bump={Bump}", devEnv.EnvironmentUrl, projectSln.UniqueName, settings.Bump);

        Config!.Save();
        // KTD6: the resolver's own setter already prints "Saved to .flowline: DevUrl" when it actually
        // saved something — this line only adds noise when nothing changed.
        if (target.Saved)
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

        if (await ValidatePackAndBuildAsync(projectSln, dataverseSolutionFolder, slnFolder,
                buildRelease: false, skipBuild: settings.NoBuild, cancellationToken) is { } exitCode)
        {
            return exitCode;
        }

        // Check for drift between local solution (Plugins/WebResources) and Dataverse (/src). A null
        // WebResources project is a legitimate state — warn loudly and let the checker skip that half.
        if (layout.WebResourcesProjectPath is null)
            Console.Warning("No WebResources project — skipping web-resource drift check.");
        var driftWarnings = await PluginWebResourceDriftChecker.CheckAsync(projectSln.UniqueName, layout, dataverseSolutionFolder, slnInfo.PublisherPrefix, cancellationToken);
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
        await WriteSyncReportAsync(summary, Console, slnFolder, srcPath, projectSln.UniqueName,
            devEnv.DisplayName, settings.Verbose, cancellationToken);

        Console.Done(summary.TotalFiles == 0
            ? $"Synced {tagVersion} — no component changes, nothing to deploy."
            : $"Synced {tagVersion}. Commit, then 'git tag {tagVersion}' when ready to deploy. ◝(ᵔᵕᵔ)◜");

        return 0;
    }

    bool IsInteractive() => Console.Profile.Capabilities.Interactive;

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

    /// <summary>Everything sync reports once the summary exists: the terminal tree, CHANGES.md, and the
    /// regenerated schema context. Extracted whole so it can be tested against a temp folder — reaching it
    /// through <c>ExecuteFlowlineAsync</c> would need a live Dataverse environment. The context generator
    /// belongs here rather than at the call site: it needs no environment either, and keeping it in makes
    /// this the one place that decides what a sync leaves behind on disk.</summary>
    internal static async Task WriteSyncReportAsync(SolutionChangeSummary summary, IAnsiConsole console,
        string slnFolder, string srcPath, string solutionUniqueName, string? envDisplayName, bool verbose,
        CancellationToken ct = default)
    {
        summary.WriteTree(console, NoChangesLine(envDisplayName), verbose, "see CHANGES.md");
        await summary.WriteChangesFileAsync(ChangesFilePath(slnFolder), solutionUniqueName,
            ProvenanceLine(envDisplayName), writeWhenEmpty: false, ct);
        await new DataverseContextGenerator(console).GenerateAsync(srcPath, solutionUniqueName, slnFolder, ct);
    }

    // The writer takes these three from its caller, so sync is the only thing holding its own output shape
    // in place. They live here as named seams rather than inline in the report method above, so a test can
    // catch the wording, the fallback, or the file location drifting on its own.

    /// <summary>Where sync writes its change summary: the project root, not the solution source folder.</summary>
    internal static string ChangesFilePath(string slnFolder) => Path.Combine(slnFolder, "CHANGES.md");

    /// <summary>The written file's provenance line. No environment name means no line at all.</summary>
    internal static string? ProvenanceLine(string? envDisplayName) =>
        envDisplayName is null ? null : $"Synced from: {envDisplayName}";

    /// <summary>The terminal line for a sync that pulled nothing. Escaped: an environment name is user data.</summary>
    internal static string NoChangesLine(string? envDisplayName) =>
        $"No changes pulled from {Markup.Escape(envDisplayName ?? "DEV")}.";
}
