using System.ComponentModel;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public enum BumpComponent { Patch, Minor, Major }

public class SyncCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions) :
    FlowlineCommand<SyncCommand.Settings>(console, runtimeOptions)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to sync")]
        public string? Solution { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Development environment URL")]
        public string? DevUrl { get; set; }

        [CommandOption("--managed")]
        [Description("Include managed artifacts")]
        [DefaultValue(false)]
        public bool IncludeManaged { get; set; } = false;

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
        var (projectSln, slnInfo) = await GetAndCheckSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, settings.IncludeManaged, settings, cancellationToken);
        if (slnInfo.IsManaged)
            throw new FlowlineException("Managed solutions are not supported for sync");

        // Validate that we have an initialized project
        var slnFolder = Path.Combine(RootFolder, "solutions", projectSln.Name);
        var cdsprojPath = Path.Combine(PackageFolder(slnFolder), $"{PackageName}.cdsproj");
        if (!File.Exists(cdsprojPath))
            throw new FlowlineException($"No solution found at '{cdsprojPath}' — run 'clone' first");

        // Check for uncommitted changes
        var srcPath = Path.Combine(PackageFolder(slnFolder), "src");
        var preSyncSummary = await SolutionChangeSummary.ComputeAsync(srcPath, RootFolder, settings.Verbose, cancellationToken);
        if (preSyncSummary.TotalFiles > 0)
        {
            if (!settings.Force)
            {
                throw new FlowlineException($"Uncommitted changes in '{projectSln.Name}/{PackageName}/src/' — git commit first, or re-run with --force.")
                    .WithDetail(c => preSyncSummary.WriteFlat(c, settings.Verbose, "[dim]  "));
            }

            Console.Warning($"Uncommitted changes in '{projectSln.Name}/{PackageName}/src/' — overwriting.");
            preSyncSummary.WriteFlat(Console, settings.Verbose, "[dim]  ");
        }

        // Bump version in Dataverse before sync so the downloaded XML reflects the new version
        var tagVersion = await Console.Status().FlowlineSpinner().StartAsync(
            $"Bump {settings.Bump} version [bold]{projectSln.Name}[/]...",
            async ctx =>
            {
                var currentVersion = await PacUtils.GetSolutionVersionAsync(slnInfo.SolutionUniqueName!, devEnv.EnvironmentUrl!, settings.Verbose, cancellationToken);
                Console.Verbose($"[dim]Current version: {currentVersion}[/]", settings.Verbose);
                var newVersion = BumpVersion(currentVersion, settings.Bump);
                await PacUtils.SetSolutionVersionAsync(slnInfo.SolutionUniqueName!, newVersion, devEnv.EnvironmentUrl!, settings.Verbose, cancellationToken);
                Console.Verbose($"[dim]New version: {newVersion}[/]", settings.Verbose);
                var tagVersion = ToTagVersion(newVersion);
                return tagVersion;
            });
        Console.Ok($"Version bumped: {tagVersion}");

        // Sync solution from Dataverse
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
                      .WithToolExecutionLog(settings.Verbose, ctx)
                      .ExecuteAsync(cancellationToken)
                      .Task);

        if (!result.IsSuccess)
            throw new FlowlineException("Sync failed — check the environment and your PAC login. Use --verbose for more details.");

        Console.Ok($"Solution synced from Dataverse in {FormatDuration(result.RunTime)}");

        // Pack the solution in pac to validate it
        var artifactsFolder = Path.Combine(slnFolder, "artifacts");
        if (await PacUtils.PackSolutionAsync(projectSln, PackageFolder(slnFolder), artifactsFolder, false, settings.Verbose, cancellationToken) != 0) return 1;
        if (settings.IncludeManaged &&
            await PacUtils.PackSolutionAsync(projectSln, PackageFolder(slnFolder), artifactsFolder, true, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        // Build the solution in dotnet to validate it (Debug = unmanaged, Release = managed!)
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        // Check for drift between local solution (Plugins/WebResources) and Dataverse (/src)
        var driftWarnings = DriftChecker.Check(slnFolder, PackageFolder(slnFolder), slnInfo.PublisherPrefix, cancellationToken);
        if (driftWarnings.Count == 0)
        {
            Console.Ok("Local solution matches Dataverse");
        }
        else
        {
            Console.Warning("Drift detected between local solution and Dataverse:");
            foreach (var w in driftWarnings)
            {
                var hint = w.Category switch
                {
                    DriftCategory.ContentDiffers => $"- Check who changed '{w.RelativePath}' in Dataverse — run 'flowline push' to re-sync",
                    DriftCategory.NewInDataverse => $"- Check who added '{w.RelativePath}' in Dataverse — add to local WebResources and run 'flowline push' to re-sync",
                    DriftCategory.OnlyLocal => $"- Local file '{w.RelativePath}' not in Dataverse — run 'flowline push' to re-sync",
                    DriftCategory.PluginSizeMismatch => $"- Local plugin build may not match Dataverse — rebuild and push if intentional ({w.RelativePath})",
                    DriftCategory.OrphanAssembly => $"- '{w.RelativePath}' in Dataverse — no local source. Flowline won't manage it.",
                    _ => $"- {w.RelativePath}"
                };
                Console.MarkupLine($"  {hint}");
            }
        }

        // Summary of changes
        var summary = await SolutionChangeSummary.ComputeAsync(srcPath, RootFolder, settings.Verbose, cancellationToken);
        summary.WriteTree(Console, devEnv.DisplayName, settings.Verbose);

        Console.Done($"Synced {tagVersion}. Run 'git commit' to save a checkpoint and 'git tag {tagVersion}' to tag it.");

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

    static async Task GitCommitChanges(IAnsiConsole console, CancellationToken cancellationToken)
    {
        // Add all files to the git staging area
        await Cli.Wrap("git")
                 .WithArguments("add -A")
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => console.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(System.Console.Error.WriteLine))
                 .WithToolExecutionLog()
                 .ExecuteAsync(cancellationToken);

        // Check if there are changes to commit
        var statusResult = await Cli.Wrap("git")
                                    .WithArguments("status --porcelain")
                                    .WithToolExecutionLog()
                                    .ExecuteBufferedAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(statusResult.StandardOutput))
        {
            console.Skip("No changes — skipping commit");
            return;
        }

        // Commit the changes
        await Cli.Wrap("git")
                 .WithArguments(args => args
                                        .Add("commit")
                                        .Add("-m").Add("flowline: sync solution"))
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => console.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(System.Console.Error.WriteLine))
                 .WithToolExecutionLog()
                 .ExecuteAsync(cancellationToken);
    }
}
