using System.ComponentModel;
using CliWrap;
using CliWrap.Buffered;
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
        [CommandArgument(0, "[from]")]
        [Description("Git ref to compare from — a commit, tag, or branch (default: HEAD). 'A..B' is shorthand for 'A B'; 'A...B' compares the merge base of A and B against B")]
        public string? From { get; set; }

        [CommandArgument(1, "[to]")]
        [Description("Git ref to compare to (default: the working tree, so uncommitted and untracked files count)")]
        public string? To { get; set; }

        [CommandOption("--exit-code")]
        [Description("Exit 22 when changes were found, 0 when there were none (like 'git diff --exit-code'). Failures keep their own code")]
        public bool ExitCodeOnChanges { get; set; }

        [CommandOption("--write [FILE]")]
        [Description("Write the report to a file. Bare: CHANGES.md in the project root. With a value: a relative path resolves against the current folder")]
        public FlagValue<string> Write { get; set; } = null!;
    }

    /// <summary>The unpacked solution source lives in <c>src/</c> beside the <c>.cdsproj</c>.</summary>
    const string SrcFolderName = "src";

    /// <summary>Default target of a bare <c>--write</c>, resolved against the project root — never the
    /// source root, which is the folder the file listing scans.</summary>
    internal const string ChangesFileName = "CHANGES.md";

    // A repo cloned by Flowline has a .flowline, so project mode still resolves the root from any subfolder.
    // Without one, the working directory is used and the missing-solution-file error below is what the user
    // sees — the accurate problem for a folder that isn't a solution repo at all.
    protected override bool RequiresFlowlineProject => false;

    /// <summary>Skips the git/dotnet/pac probe.</summary>
    /// <remarks>
    /// The base probe requires the PAC CLI, which this command never uses — it reads git and local files
    /// only. The one thing it does need — a git repository — is checked in
    /// <see cref="EnsureGitRepositoryAsync"/> with a message about comparing history rather than about
    /// setup. The update notice is untouched: <c>FlowlineCommand.ExecuteAsync</c> prints it before this
    /// runs, and it returns immediately on a non-interactive console anyway.
    /// </remarks>
    protected override Task CheckSetupAsync(Settings settings, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var write = ResolveWriteTarget(settings.Write.IsSet, settings.Write.Value, RootFolder);
        return DiffAsync(RootFolder, settings.From, settings.To, write?.Path,
            settings.Verbose, settings.ExitCodeOnChanges, cancellationToken, write?.Bare ?? false);
    }

    /// <summary>Turns <c>--write</c> into the file it names, or <c>null</c> when the flag wasn't passed.</summary>
    /// <remarks>
    /// A bare flag anchors <see cref="ChangesFileName"/> to the project root, so it lands next to the
    /// solution wherever the command was run from. An explicit value resolves against the current folder
    /// instead, which is what <c>generate</c>, <c>push</c> and <c>sln add</c> do with a relative path — a
    /// path the user typed points where they typed it. A blank value carries no path, so it's the bare
    /// flag.
    /// </remarks>
    internal static (string Path, bool Bare)? ResolveWriteTarget(bool isSet, string? value, string rootFolder)
    {
        if (!isSet) return null;

        return string.IsNullOrWhiteSpace(value)
            ? (Path.Combine(rootFolder, ChangesFileName), true)
            : (Path.GetFullPath(value.Trim()), false);
    }

    /// <summary>Runs the comparison against <paramref name="rootFolder"/> and renders it.</summary>
    /// <remarks>
    /// Takes the root and the two refs rather than reading <c>Settings</c>, and is <c>internal</c>, so every
    /// path is exercisable against a temp repository without running the base command pipeline.
    /// </remarks>
    internal async Task<int> DiffAsync(string rootFolder, string? from, string? to, string? writeTo, bool verbose, bool exitCodeOnChanges, CancellationToken cancellationToken, bool bareWrite = false)
    {
        var spec = ParseRefSpec(from, to);
        await EnsureGitRepositoryAsync(rootFolder, _capture, cancellationToken);

        var (fromRef, toSide) = spec.MergeBase
            ? await ResolveMergeBaseAsync(rootFolder, spec, cancellationToken)
            : ResolveSides(spec.From, spec.To);

        var (srcFolder, solutionName) = await ResolveSourceFolderAsync(rootFolder, cancellationToken);
        if (writeTo is not null) EnsureWriteTargetOutsideSource(writeTo, srcFolder, rootFolder);

        var summary = await SolutionChangeSummary.ComputeAsync(srcFolder, rootFolder, fromRef, toSide, _capture, cancellationToken);
        Logger.LogInformation("Diff: from={From} to={To} files={TotalFiles}", fromRef, toSide.GitRef ?? "<working tree>", summary.TotalFiles);

        // Both lines name the two compared points and no environment: this command contacts none.
        var (fromName, toName) = (fromRef, Name(toSide));
        summary.WriteTree(Console, $"No changes between {Markup.Escape(fromName)} and {Markup.Escape(toName)}.", verbose,
            OverflowHint(writeTo));

        if (writeTo is not null)
        {
            // A named target is rewritten even when nothing changed, so re-running a release range never
            // leaves a stale file looking current. The bare default is CHANGES.md at the project root —
            // sync's own report — so a no-change run leaves it alone rather than replacing sync's answer
            // with this one.
            var writeWhenEmpty = !bareWrite;
            await summary.WriteChangesFileAsync(writeTo, solutionName, $"Compared: {fromName} -> {toName}",
                writeWhenEmpty, cancellationToken);

            var display = ConsolePath.FormatRelativePath(writeTo, rootFolder);
            if (summary.TotalFiles > 0 || writeWhenEmpty)
                Console.Ok($"Wrote {display}");
            else
                Console.Skip($"No changes, so {display} is untouched");
        }

        // Any changed file counts, including one the parser can't name as a component (Other/Solution.xml),
        // so a version-only bump is a change. Same number the tree and the written report are built from.
        return (int)(exitCodeOnChanges && summary.TotalFiles > 0 ? ExitCode.ChangesFound : ExitCode.Success);
    }

    /// <summary>How the right side reads in a message — the ref itself, or the files on disk.</summary>
    static string Name(SolutionChangeSummary.ComparisonSide side) => side.GitRef ?? "working tree";

    /// <summary>The two positionals once any <c>A..B</c>/<c>A...B</c> range syntax has been split out of
    /// them — always a plain pair from here on.</summary>
    internal readonly record struct RefSpec(string From, string? To, bool MergeBase);

    /// <summary>Splits git's range syntax out of the raw positionals, so every later step only ever sees a
    /// plain ref pair.</summary>
    /// <remarks>
    /// Pure and synchronous: a <c>...</c> range still names two refs here, not yet a merge base — that git
    /// call needs a working directory and happens afterward, in <see cref="ResolveMergeBaseAsync"/>.
    /// </remarks>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ValidationFailed"/> for a range positional combined with a second one (two ways
    /// of naming the right side at once), or a range missing either of its two sides.
    /// </exception>
    internal static RefSpec ParseRefSpec(string? from, string? to)
    {
        var left = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        var right = string.IsNullOrWhiteSpace(to) ? null : to.Trim();

        if (left is null) return new RefSpec("HEAD", right, false);

        var mergeBase = left.Contains("...", StringComparison.Ordinal);
        var separator = mergeBase ? "..." : "..";
        if (!left.Contains(separator, StringComparison.Ordinal)) return new RefSpec(left, right, false);

        if (right is not null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{left}' is already a range — pass a range or two refs, not both. Drop '{right}'.");

        var idx = left.IndexOf(separator, StringComparison.Ordinal);
        var (rangeFrom, rangeTo) = (left[..idx], left[(idx + separator.Length)..]);

        if (string.IsNullOrWhiteSpace(rangeFrom))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{left}' is missing the ref before '{separator}'. Use '<ref>{left}'.");
        if (string.IsNullOrWhiteSpace(rangeTo))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{left}' is missing the ref after '{separator}'. Use '{left}<ref>'.");

        return new RefSpec(rangeFrom, rangeTo, mergeBase);
    }

    /// <summary>Turns a parsed, non-range ref pair into the two sides of the comparison.</summary>
    /// <remarks>A missing left side defaults to HEAD — the same default a bare run uses.</remarks>
    internal static (string From, SolutionChangeSummary.ComparisonSide To) ResolveSides(string? from, string? to)
    {
        var fromRef = string.IsNullOrWhiteSpace(from) ? "HEAD" : from.Trim();
        var toRef = string.IsNullOrWhiteSpace(to) ? null : to.Trim();

        return (fromRef, toRef is null ? SolutionChangeSummary.ComparisonSide.WorkingTree : new SolutionChangeSummary.ComparisonSide(toRef));
    }

    /// <summary>Resolves an <c>A...B</c> range to the merge base of A and B, compared against B.</summary>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ValidationFailed"/> naming both refs when they share no common history.
    /// </exception>
    async Task<(string From, SolutionChangeSummary.ComparisonSide To)> ResolveMergeBaseAsync(string rootFolder, RefSpec spec, CancellationToken cancellationToken)
    {
        var mergeBase = await GitUtils.GetMergeBaseAsync(spec.From, spec.To!, rootFolder, _capture, cancellationToken);
        if (mergeBase is null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{spec.From}' and '{spec.To}' share no common history — 'diff' can't compute a merge base between them.");

        return (mergeBase, new SolutionChangeSummary.ComparisonSide(spec.To));
    }

    /// <summary>Refuses to compare history where there is none.</summary>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ConfigInvalid"/>, matching <c>GitUtils.AssertGitRepoAsync</c> — the same missing
    /// prerequisite should not carry two exit codes.
    /// </exception>
    /// <remarks>
    /// Asks git rather than looking for a <c>.git</c> entry: a worktree or submodule <c>.git</c> file whose
    /// gitdir pointer is stale exists on disk but isn't a usable repository, and every listing below would
    /// then fail as a broken comparison instead of as the missing prerequisite it is.
    /// </remarks>
    internal static async Task EnsureGitRepositoryAsync(string rootFolder, SubprocessCapture? capture = null, CancellationToken cancellationToken = default)
    {
        var usable = Directory.Exists(rootFolder);
        if (usable)
        {
            var cmd = Cli.Wrap("git")
                         .WithWorkingDirectory(rootFolder)
                         .WithArguments(args => args.Add("rev-parse").Add("--git-dir"))
                         .WithValidation(CommandResultValidation.None);
            try
            {
                var result = await (capture?.Apply(cmd, suppressErrors: true) ?? cmd).ExecuteBufferedAsync(cancellationToken);
                usable = result.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Same failure and exit code GitUtils gives for a missing git binary. This command skips the
                // setup probe that would otherwise have caught it, so it has to answer for itself.
                throw new FlowlineException(ExitCode.GeneralError, "Git isn't available. Install it from https://git-scm.com/.");
            }
        }
        if (usable) return;

        throw new FlowlineException(ExitCode.ConfigInvalid,
            $"No usable Git repo at '{ConsolePath.FormatRelativePath(rootFolder, markup: false)}' — 'diff' compares two points in git history. Run 'git init' first, or run it inside a repository.");
    }

    /// <summary>Keeps the written report out of the folder the comparison scans.</summary>
    /// <remarks>
    /// A report inside <c>src/</c> is listed as a change by the next run, which pins <c>--exit-code</c> to
    /// <see cref="ExitCode.ChangesFound"/> forever and puts generated output in the unpacked solution.
    /// </remarks>
    /// <exception cref="FlowlineException"><see cref="ExitCode.ValidationFailed"/> for a target inside the source folder.</exception>
    /// <summary>Where to send someone whose component has more sub-changes than the tree shows.</summary>
    /// <remarks>
    /// The tree caps named sub-changes and <c>--verbose</c> does not lift the cap, so this hint is the only
    /// route offered and it has to name one that exists. <c>sync</c> can always say "see CHANGES.md" because
    /// it always writes it; this command writes nothing unless asked, so with no target the route is the flag.
    /// </remarks>
    internal static string OverflowHint(string? writeTo) =>
        writeTo is null ? "pass --write for the full list" : $"see {Path.GetFileName(writeTo)}";

    internal static void EnsureWriteTargetOutsideSource(string writeTo, string srcFolder, string rootFolder)
    {
        var relative = Path.GetRelativePath(srcFolder, writeTo);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return;

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"--write target '{ConsolePath.FormatRelativePath(writeTo, rootFolder, markup: false)}' is inside the unpacked solution source that 'diff' compares, so every later run would report the report itself as a change. Pick a path outside '{ConsolePath.FormatRelativePath(srcFolder, rootFolder, markup: false)}'.");
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
