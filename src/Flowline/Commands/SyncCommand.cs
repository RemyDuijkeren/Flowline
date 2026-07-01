using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Services;
using Flowline.Utils;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public enum BumpComponent { Patch, Minor, Major }

public class SyncCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) :
    FlowlineCommand<SyncCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to sync (optional in project mode)")]
        public string? Solution { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Development environment URL")]
        public string? DevUrl { get; set; }

        [CommandOption("--managed")]
        [Description("Include managed artifacts (--managed false resets to default)")]
        public FlagValue<bool> IncludeManaged { get; set; } = null!;

        [CommandOption("--bump")]
        [Description("Version component to increment: patch, minor, or major (default: patch)")]
        [DefaultValue(BumpComponent.Patch)]
        public BumpComponent Bump { get; set; } = BumpComponent.Patch;
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Dev URL is required
        var devEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, settings.DevUrl, settings, cancellationToken);

        // Solution name is required
        var (projectSln, slnInfo) = await GetAndCheckSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, settings.IncludeManaged.IsSet ? settings.IncludeManaged.Value : (bool?)null, settings, cancellationToken);
        if (slnInfo.IsManaged)
            throw new FlowlineException(ExitCode.ValidationFailed, "Managed solutions are not supported for sync — use an unmanaged solution.");

        Logger.LogInformation("target={EnvironmentUrl} solution={SolutionName} bump={Bump}", devEnv.EnvironmentUrl, projectSln.Name, settings.Bump);

        // Validate that we have an initialized project
        var slnFolder = Path.Combine(RootFolder, "solutions", projectSln.Name);
        var cdsprojPath = Path.Combine(PackageFolder(slnFolder), $"{PackageName}.cdsproj");
        if (!File.Exists(cdsprojPath))
            throw new FlowlineException(ExitCode.NotFound, $"No solution found at '{cdsprojPath}' — run 'clone' first");

        // Check for uncommitted changes
        var srcPath = Path.Combine(PackageFolder(slnFolder), "src");
        var preSyncSummary = await SolutionChangeSummary.ComputeAsync(srcPath, RootFolder, _capture, cancellationToken);
        Logger.LogInformation("Diff: {TotalFiles} files changed", preSyncSummary.TotalFiles);
        if (preSyncSummary.TotalFiles > 0)
        {
            if (settings.Force)
            {
                Console.Warning($"Uncommitted changes in '{projectSln.Name}/{PackageName}/src/' — overwriting.");
                preSyncSummary.WriteFlat(Console, runtimeOptions, "[dim]  ");
            }
            else
            {
                Console.Warning($"Found uncommitted changes in '{projectSln.Name}/{PackageName}/src/'.");
                preSyncSummary.WriteFlat(Console, runtimeOptions, "[dim]  ");
                throw new FlowlineException(ExitCode.DirtyWorkingDirectory, $"Uncommitted changes in '{projectSln.Name}/{PackageName}/src/' — Commit or stash changes first, or re-run with --force.");
            }
        }

        // Bump version in Dataverse before sync so the downloaded XML reflects the new version
        var tagVersion = await Console.Status().FlowlineSpinner().StartAsync(
            $"Bump {settings.Bump} version [bold]{projectSln.Name}[/]...",
            async ctx =>
            {
                var currentVersion = await PacUtils.GetSolutionVersionAsync(slnInfo.SolutionUniqueName!, devEnv.EnvironmentUrl!, _capture, cancellationToken);
                Console.Verbose($"Current version: {currentVersion}");
                var newVersion = BumpVersion(currentVersion, settings.Bump);
                await PacUtils.SetSolutionVersionAsync(slnInfo.SolutionUniqueName!, newVersion, devEnv.EnvironmentUrl!, _capture, cancellationToken);
                Console.Verbose($"New version: {newVersion}");
                var tagVersion = ToTagVersion(newVersion);
                return tagVersion;
            });
        Console.Ok($"Version bumped: {tagVersion}");

        // Sync solution from Dataverse
        Logger.LogInformation("Syncing from Dataverse: {SolutionName}", projectSln.Name);
        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        CommandResult result = await Console.Status().FlowlineSpinner().StartAsync(
            $"Syncing solution [bold]{projectSln.Name}[/]...",
            ctx => Cli.Wrap(cmdName)
                      .WithArguments(args =>
                          args.AddIfNotNull(prefixArgs)
                              .Add("solution")
                              .Add("sync")
                              .Add("--solution-folder").Add(PackageFolder(slnFolder))
                              .Add("--environment").Add(devEnv.EnvironmentUrl!)
                              .Add("--packagetype").Add(projectSln.IncludeManaged ? "Both" : "Unmanaged")
                              .Add("--async"))
                      .WithValidation(CommandResultValidation.None)
                      .WithCapture(_capture, ctx)
                      .ExecuteAsync(cancellationToken)
                      .Task);

        if (!result.IsSuccess)
            throw new FlowlineException(ExitCode.GeneralError, "Sync failed — check the environment and your PAC login. Use --verbose for more details.");

        Console.Ok($"Solution synced from Dataverse in {FormatDuration(result.RunTime)}");

        // Pack the solution in pac to validate it
        Logger.LogInformation("Validating pack: {SolutionName}", projectSln.Name);
        var artifactsFolder = Path.Combine(slnFolder, "artifacts");
        if (await PacUtils.PackSolutionAsync(projectSln, PackageFolder(slnFolder), artifactsFolder, false, _capture, cancellationToken) != 0) return (int)ExitCode.BuildFailed;
        if (projectSln.IncludeManaged &&
            await PacUtils.PackSolutionAsync(projectSln, PackageFolder(slnFolder), artifactsFolder, true, _capture, cancellationToken) != 0)
        {
            return (int)ExitCode.BuildFailed;
        }

        // Build the solution in dotnet to validate it (Debug = unmanaged, Release = managed!)
        Logger.LogInformation("Validating build: {SlnFolder}", slnFolder);
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, _capture, cancellationToken) != 0)
        {
            return (int)ExitCode.BuildFailed;
        }

        // Check for drift between local solution (Plugins/WebResources) and Dataverse (/src)
        var driftWarnings = PluginWebResourceDriftChecker.Check(slnFolder, PackageFolder(slnFolder), slnInfo.PublisherPrefix, cancellationToken);
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
        await summary.WriteChangesFileAsync(slnFolder, projectSln.Name, devEnv.DisplayName, cancellationToken);
        await new DataverseContextGenerator(Console, runtimeOptions).GenerateAsync(
            srcPath, projectSln.Name, RootFolder, cancellationToken);

        Console.Done($"Synced {tagVersion}. Run 'git commit' to save a checkpoint and 'git tag {tagVersion}' to tag it. ◝(ᵔᵕᵔ)◜");

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
            default: // Patch
                nums[2]++;
                for (var i = 3; i < nums.Length; i++) nums[i] = 0;
                break;
        }

        return string.Join(".", nums);
    }

    internal static string ToTagVersion(string version) =>
        string.Join(".", version.Split('.').Take(3));

}
