using CliWrap;
using CliWrap.Buffered;
using Flowline.Core.OrphanCleanup;

namespace Flowline.Services;

// KTD1's CLI-side implementation of IComponentProvenanceLookup. KTD2: anchored to the checkout
// (projectRoot), never to whichever source root the compare was handed — on deploy that source root
// is a temp extraction with no history at all. Every git command runs with projectRoot as its
// explicit working directory (git resolves the enclosing repository itself).
public sealed class GitComponentProvenanceLookup : IComponentProvenanceLookup
{
    readonly string _projectRoot;

    // KTD6: cached only within this instance's run, never across runs — a fresh lookup instance is
    // expected per command invocation. Probing once avoids repeating rev-parse/config calls per orphan.
    bool? _usable;

    // Test-only seam: lets a test observe every git invocation (e.g. to assert no history query ran
    // once the checkout was found shallow/partial) without adding a mocking layer for what is, in
    // production, always the real git binary.
    internal Action<IReadOnlyList<string>>? OnGitInvocation { get; set; }

    public GitComponentProvenanceLookup(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public async Task<ComponentProvenance> ResolveAsync(string? checkoutSolutionSrcRoot, ComponentSourceLocation location, CancellationToken ct)
    {
        var rebasedPath = RebaseOntoProjectRoot(checkoutSolutionSrcRoot, location.RelativePath);
        if (rebasedPath is null) return ComponentProvenance.Undetermined;

        try
        {
            if (!await IsUsableAsync(ct)) return ComponentProvenance.Undetermined;

            return location.Kind switch
            {
                SourceLocationKind.File or SourceLocationKind.Folder => await ResolveByRemovalAsync(rebasedPath, ct),
                SourceLocationKind.Inline => await ResolveInlineAsync(rebasedPath, location.InlineMarkers, ct),
                _ => ComponentProvenance.Undetermined,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return ComponentProvenance.Undetermined; }
    }

    // KTD2: rebases location.RelativePath (checkout-relative to checkoutSolutionSrcRoot) onto _projectRoot,
    // since every git command below runs with _projectRoot as its working directory. Null (no checkout
    // mapping, e.g. a stand-alone deploy artifact) or a rebased path that escapes _projectRoot (different
    // checkout, unrelated temp directory) both mean "can't answer" — R8/the plan require this to read
    // Undetermined, never NeverInSource, so neither case may reach a git command at all.
    string? RebaseOntoProjectRoot(string? checkoutSolutionSrcRoot, string locationRelativePath)
    {
        if (string.IsNullOrWhiteSpace(checkoutSolutionSrcRoot)) return null;

        var absolute = Path.GetFullPath(Path.Combine(checkoutSolutionSrcRoot, locationRelativePath));
        var relativeToProjectRoot = Path.GetRelativePath(_projectRoot, absolute);

        if (relativeToProjectRoot.StartsWith("..") || Path.IsPathRooted(relativeToProjectRoot))
            return null;

        return relativeToProjectRoot.Replace('\\', '/');
    }

    // Shallow or partial history would otherwise fetch every historical version of a shared file on
    // demand (and fail with no network) once a history query ran, so this gate runs before any of them.
    async Task<bool> IsUsableAsync(CancellationToken ct)
    {
        if (_usable.HasValue) return _usable.Value;

        var (shallowExit, shallowOut) = await RunGitAsync(["rev-parse", "--is-shallow-repository"], ct);
        if (shallowExit != 0 || shallowOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return (_usable = false).Value;

        // `git config --get-all <key>` exits 1 (empty output) when the key is absent, 0 when present.
        // Any other exit code means the probe itself could not answer, which is treated the same as
        // "partial" per the instruction to short-circuit when the state cannot be established reliably.
        foreach (var key in new[] { "remote.origin.promisor", "remote.origin.partialclonefilter" })
        {
            var (exit, output) = await RunGitAsync(["config", "--get-all", key], ct);
            if (exit == 0 && !string.IsNullOrWhiteSpace(output)) return (_usable = false).Value;
            if (exit != 0 && exit != 1) return (_usable = false).Value;
        }

        return (_usable = true).Value;
    }

    // File and Folder both resolve the same way: the full add/delete lifecycle of the pathspec, most
    // recent event decides. A folder's pathspec matches every file beneath it, so the same rule ("no
    // file left with a later status than D") covers both without separate code paths.
    async Task<ComponentProvenance> ResolveByRemovalAsync(string rebasedPath, CancellationToken ct)
    {
        var (exitCode, stdOut) = await RunGitAsync(
            ["log", "--format=C%x09%H%x09%an%x09%aI%x09%s", "--name-status", "--diff-filter=AD", "--", Pathspec(rebasedPath)],
            ct);
        if (exitCode != 0) return ComponentProvenance.Undetermined;

        var commits = ParseNameStatusLog(SplitLines(stdOut));

        // Newest-first git-log order, so the first commit touching a given path is its latest event.
        var latestStatusByPath = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        foreach (var commit in commits)
            foreach (var (status, path) in commit.Files)
                latestStatusByPath.TryAdd(path, status);

        // Complete AD history with nothing left at status D anywhere is affirmative evidence either way
        // it happens: never touched at all, or touched and still present — R8 treats both as NeverInSource.
        if (latestStatusByPath.Count == 0 || latestStatusByPath.Values.Any(s => s != 'D'))
            return ComponentProvenance.NeverInSource;

        return ComponentProvenance.Declared(commits[0].Commit);
    }

    // -S is a pickaxe search on occurrence count, not a definitive add/delete log the way --diff-filter
    // is for File/Folder — so unlike ResolveByRemovalAsync, "found nothing affirmative" here can never
    // stand in for NeverInSource. Only an unambiguous single removal answers as Declared; everything
    // else — no candidate, an addition-only candidate, or more than one distinct removal commit across
    // markers — is Undetermined.
    async Task<ComponentProvenance> ResolveInlineAsync(string rebasedPath, IReadOnlyList<string> markers, CancellationToken ct)
    {
        if (markers.Count == 0) return ComponentProvenance.Undetermined;

        var pathspec = Pathspec(rebasedPath);
        var removalCommits = new Dictionary<string, RemovalCommit>(StringComparer.Ordinal);

        foreach (var marker in markers)
        {
            var (logExit, logOut) = await RunGitAsync(
                ["log", "--format=%H%x09%an%x09%aI%x09%s", "-S", marker, "--", pathspec], ct);
            if (logExit != 0) return ComponentProvenance.Undetermined;

            foreach (var line in SplitLines(logOut))
            {
                var parts = line.Split('\t', 4);
                if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[2], out var date)) continue;
                var sha = parts[0];

                var (showExit, showOut) = await RunGitAsync(
                    ["show", "--format=", "--unified=0", sha, "--", pathspec], ct);
                if (showExit != 0) return ComponentProvenance.Undetermined;

                // "---"/"+++" are the diff's file-header lines, not content — excluding "---" keeps a
                // marker that happens to appear in the file's own path (e.g. an entity folder name)
                // from being misread as a content removal.
                var isRemoval = SplitLines(showOut).Any(l =>
                    l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal) && l.Contains(marker, StringComparison.Ordinal));

                if (isRemoval)
                    removalCommits.TryAdd(sha, new RemovalCommit(sha, parts[1], date, parts[3]));
            }
        }

        return removalCommits.Count == 1
            ? ComponentProvenance.Declared(removalCommits.Values.Single())
            : ComponentProvenance.Undetermined;
    }

    static string Pathspec(string rebasedPath) => $":(icase){rebasedPath}";

    static string[] SplitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    static List<(RemovalCommit Commit, List<(char Status, string Path)> Files)> ParseNameStatusLog(string[] lines)
    {
        var commits = new List<(RemovalCommit, List<(char, string)>)>();
        foreach (var line in lines)
        {
            if (line.StartsWith("C\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t', 5);
                if (parts.Length < 5 || !DateTimeOffset.TryParse(parts[3], out var date)) continue;
                commits.Add((new RemovalCommit(parts[1], parts[2], date, parts[4]), []));
            }
            else if (commits.Count > 0)
            {
                var parts = line.Split('\t', 2);
                if (parts is [{ Length: > 0 } status, var path])
                    commits[^1].Item2.Add((status[0], path));
            }
        }
        return commits;
    }

    async Task<(int ExitCode, string StdOut)> RunGitAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        OnGitInvocation?.Invoke(args);

        var result = await Cli.Wrap("git")
            .WithWorkingDirectory(_projectRoot)
            .WithArguments(a =>
            {
                foreach (var arg in args) a.Add(arg);
            })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        return (result.ExitCode, result.StandardOutput);
    }
}
