using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>
/// <c>flowline diff</c> — reports which Dataverse solution components changed between two points in git
/// history, without contacting Dataverse.
/// </summary>
/// <remarks>
/// <c>sync</c> already computes this summary, but only as a side effect of an export it has just run, so
/// the answer is reachable exactly once and only for HEAD-vs-working-tree. This command exposes the same
/// summary over any two git points, at any time.
///
/// <b>Git is the only axis.</b> Nothing here connects, authenticates, or reads a solution from an
/// environment — <c>drift</c> is the command that compares against a live environment. That is why the
/// setup probe is skipped entirely below.
/// </remarks>
public class DiffCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture, NuGetVersionClient nuGetVersionClient)
    : FlowlineCommand<DiffCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandOption("--from <REF>")]
        [Description("Git ref to compare from — a commit, tag, or branch (default: HEAD)")]
        public string? From { get; set; }

        [CommandOption("--to <REF>")]
        [Description("Git ref to compare to (default: the working tree, so uncommitted and untracked files count). Needs --from")]
        public string? To { get; set; }

        [CommandOption("--write [FILE]")]
        [Description("Write the report to a file (default: CHANGES.md in the repo root)")]
        public FlagValue<string> Write { get; set; } = null!;
    }

    /// <summary>The unpacked solution source lives in <c>src/</c> beside the <c>.cdsproj</c>.</summary>
    const string SrcFolderName = "src";

    /// <summary>Default target of a bare <c>--write</c>, resolved against the repo root — never the source
    /// root, which is the folder the file listing scans.</summary>
    internal const string ChangesFileName = "CHANGES.md";

    // A repo cloned by Flowline has a .flowline, so project mode still resolves the root from any subfolder.
    // Without one, the working directory is used and the missing-solution-file error below is what the user
    // sees — the accurate problem for a folder that isn't a solution repo at all.
    protected override bool RequiresFlowlineProject => false;

    /// <summary>Skips the git/dotnet/pac probe and the update check.</summary>
    /// <remarks>
    /// The base probe requires the PAC CLI and calls NuGet before the command body runs. This command reads
    /// git and local files only, so both are false prerequisites. The one thing it does need — a git
    /// repository — is checked in <see cref="EnsureGitRepository"/> with a message about comparing history
    /// rather than about setup.
    /// </remarks>
    protected override Task CheckSetupAsync(Settings settings, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        DiffAsync(RootFolder, settings.From, settings.To,
            settings.Write.IsSet ? Path.GetFullPath(settings.Write.Value ?? ChangesFileName, RootFolder) : null,
            settings.Verbose, cancellationToken);

    /// <summary>Runs the comparison against <paramref name="rootFolder"/> and renders it.</summary>
    /// <remarks>
    /// Takes the root and the two refs rather than reading <c>Settings</c>, and is <c>internal</c>, so every
    /// path is exercisable against a temp repository without running the base command pipeline.
    /// </remarks>
    internal async Task<int> DiffAsync(string rootFolder, string? from, string? to, string? writeTo, bool verbose, CancellationToken cancellationToken)
    {
        var (fromSide, toSide) = ResolveSides(from, to);
        EnsureGitRepository(rootFolder);

        var (srcFolder, solutionName) = await ResolveSourceFolderAsync(rootFolder, cancellationToken);

        var summary = await SolutionChangeSummary.ComputeAsync(srcFolder, rootFolder, fromSide, toSide, _capture, cancellationToken);
        Logger.LogInformation("Diff: from={From} to={To} files={TotalFiles}", fromSide.GitRef, toSide.GitRef ?? "<working tree>", summary.TotalFiles);

        // Both lines name the two compared points and no environment: this command contacts none.
        var (fromName, toName) = (Name(fromSide), Name(toSide));
        summary.WriteTree(Console, $"No changes between {Markup.Escape(fromName)} and {Markup.Escape(toName)}.", verbose);

        if (writeTo is not null)
            await summary.WriteChangesFileAsync(writeTo, solutionName, $"Compared: {fromName} -> {toName}",
                writeWhenEmpty: true, cancellationToken);

        return (int)ExitCode.Success;
    }

    /// <summary>How a side reads in a message — the ref itself, or the files on disk.</summary>
    static string Name(SolutionChangeSummary.ComparisonSide side) => side.GitRef ?? "working tree";

    /// <summary>Turns the two options into the two sides of the comparison.</summary>
    /// <remarks>
    /// <c>--from</c> moves the left side; the right side stays the files on disk unless <c>--to</c> says
    /// otherwise. So <c>--to</c> alone has no meaning — it would leave the left side at HEAD, which is what
    /// a bare run already does, and silently ignoring half the invocation is worse than refusing it.
    /// </remarks>
    /// <exception cref="FlowlineException"><see cref="ExitCode.ValidationFailed"/> for <c>--to</c> without <c>--from</c>.</exception>
    internal static (SolutionChangeSummary.ComparisonSide From, SolutionChangeSummary.ComparisonSide To) ResolveSides(string? from, string? to)
    {
        var fromRef = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        var toRef = string.IsNullOrWhiteSpace(to) ? null : to.Trim();

        if (toRef is not null && fromRef is null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "--to sets the right side of the comparison, so it needs a left side too. Add --from <ref>.");

        return (fromRef is null ? SolutionChangeSummary.ComparisonSide.Head : new SolutionChangeSummary.ComparisonSide(fromRef),
                toRef is null ? SolutionChangeSummary.ComparisonSide.WorkingTree : new SolutionChangeSummary.ComparisonSide(toRef));
    }

    /// <summary>Refuses to compare history where there is none.</summary>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ConfigInvalid"/>, matching <c>GitUtils.AssertGitRepoAsync</c> — the same missing
    /// prerequisite should not carry two exit codes.
    /// </exception>
    internal static void EnsureGitRepository(string rootFolder)
    {
        if (GitUtils.FindRepositoryRoot(rootFolder) is not null) return;

        throw new FlowlineException(ExitCode.ConfigInvalid,
            $"No Git repo at '{ConsolePath.FormatRelativePath(rootFolder, markup: false)}' — 'diff' compares two points in git history. Run 'git init' first, or run it inside a repository.");
    }

    /// <summary>Finds the unpacked solution XML through the solution file.</summary>
    /// <remarks>
    /// The solution file names the Dataverse solution project, and the unpacked source sits in <c>src/</c>
    /// under that project's folder — the same resolution <c>sync</c> and <c>deploy</c> use, so a relocated
    /// solution folder is followed rather than assumed. <see cref="SolutionFileLayout.LoadAsync"/> throws
    /// naming the solution file when there is none.
    /// </remarks>
    /// <exception cref="FlowlineException"><see cref="ExitCode.NotFound"/> when the source folder is missing.</exception>
    internal static async Task<(string SrcFolder, string SolutionName)> ResolveSourceFolderAsync(string rootFolder, CancellationToken cancellationToken)
    {
        var layout = await SolutionFileLayout.LoadAsync(rootFolder, cancellationToken);
        var srcFolder = Path.Combine(layout.DataverseSolutionFolder, SrcFolderName);

        if (!Directory.Exists(srcFolder))
            throw new FlowlineException(ExitCode.NotFound,
                $"No unpacked solution source at '{ConsolePath.FormatRelativePath(srcFolder, rootFolder, markup: false)}' — 'diff' reads that XML. Run 'flowline sync' to unpack it first.");

        // The .cdsproj carries the solution's identity, so the written report is headed by the same name
        // sync writes.
        return (srcFolder, Path.GetFileNameWithoutExtension(layout.DataverseSolutionProjectPath));
    }
}
