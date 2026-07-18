using CliWrap;
using CliWrap.Buffered;
using Flowline.Core;
using Flowline.Diagnostics;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline;

public static class GitUtils
{
    public static async Task<string> AssertGitInstalledAsync(SubprocessCapture capture, bool verbose = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await capture.Apply(
                                  Cli.Wrap("git")
                                  .WithArguments("--version"))
                                  .ExecuteBufferedAsync(cancellationToken);

            // Extract the version from the output (format: "git version X.Y.Z")
            var output = result.StandardOutput.Trim();
            if (output.StartsWith("git version "))
            {
                string gitVersion = output.Substring("git version ".Length);
                AnsiConsole.MarkupLine("Git's good");
                if (verbose)
                {
                    AnsiConsole.MarkupLine($"[dim]Git version: {gitVersion}[/]");
                }
                return gitVersion;
            }

            return "Unknown";
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[red]Git isn't available. Install it from https://git-scm.com/.[/]");
            Environment.Exit(1);
            return string.Empty; // This line will never be reached due to Environment.Exit
        }
    }

    public static async Task<string?> GetCurrentBranchAsync(SubprocessCapture? capture = null, CancellationToken cancellationToken = default, string? workingDirectory = null)
    {
        try
        {
            var cmd = Cli.Wrap("git");
            if (workingDirectory != null)
                cmd = cmd.WithWorkingDirectory(workingDirectory);

            var finalCmd = cmd.WithArguments("rev-parse --abbrev-ref HEAD")
                              .WithValidation(CommandResultValidation.None);
            var result = await (capture?.Apply(finalCmd, suppressErrors: true) ?? finalCmd)
                              .ExecuteBufferedAsync(cancellationToken);

            if (result.ExitCode != 0) return null;
            var branch = result.StandardOutput.Trim();
            return string.IsNullOrWhiteSpace(branch) ? null : branch == "HEAD" ? "(detached)" : branch;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<(string? remoteName, string? remoteUrl)> GetRemoteUrlAsync(SubprocessCapture capture, CancellationToken cancellationToken = default)
    {
        // Retrieve remote of the current branch and show it
        string? remoteName = null;
        string? remoteUrl = null;
        try
        {
            // Get upstream ref of the current branch (e.g., "origin/main")
            var upstreamResult = await capture.Apply(
                                          Cli.Wrap("git")
                                          .WithArguments("rev-parse --abbrev-ref --symbolic-full-name @{u}")
                                          .WithValidation(CommandResultValidation.None),
                                          suppressErrors: true)
                                          .ExecuteBufferedAsync(cancellationToken);

            var upstream = upstreamResult.ExitCode == 0 ? upstreamResult.StandardOutput.Trim() : null;

            if (!string.IsNullOrWhiteSpace(upstream) && upstream.Contains('/'))
            {
                remoteName = upstream.Split('/')[0].Trim();
            }

            if (!string.IsNullOrWhiteSpace(remoteName))
            {
                var remoteUrlResult = await capture.Apply(
                                               Cli.Wrap("git")
                                               .WithArguments(args => args.Add("remote").Add("get-url").Add(remoteName))
                                               .WithValidation(CommandResultValidation.None),
                                               suppressErrors: true)
                                               .ExecuteBufferedAsync(cancellationToken);

                if (remoteUrlResult.ExitCode == 0)
                    remoteUrl = remoteUrlResult.StandardOutput.Trim();
            }

            // Fallback: try origin
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                var originResult = await capture.Apply(
                                        Cli.Wrap("git")
                                        .WithArguments("remote get-url origin")
                                        .WithValidation(CommandResultValidation.None),
                                        suppressErrors: true)
                                        .ExecuteBufferedAsync(cancellationToken);

                if (originResult.ExitCode == 0)
                    remoteUrl = originResult.StandardOutput.Trim();
            }

            // Fallback: first available remote
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                var remotesResult = await capture.Apply(
                                         Cli.Wrap("git")
                                         .WithArguments("remote")
                                         .WithValidation(CommandResultValidation.None),
                                         suppressErrors: true)
                                         .ExecuteBufferedAsync(cancellationToken);

                var firstRemote = remotesResult.StandardOutput
                                               .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                               .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(firstRemote))
                {
                    var firstUrlResult = await capture.Apply(
                                              Cli.Wrap("git")
                                              .WithArguments(args => args.Add("remote").Add("get-url").Add(firstRemote))
                                              .WithValidation(CommandResultValidation.None),
                                              suppressErrors: true)
                                              .ExecuteBufferedAsync(cancellationToken);

                    if (firstUrlResult.ExitCode == 0)
                        remoteUrl = firstUrlResult.StandardOutput.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to retrieve remote: {ex.Message}[/]");
        }

        return (remoteName, remoteUrl);
    }

    // Relative paths are required for untracked file reporting on some platforms;
    // absolute paths can silently omit untracked entries when the directory is new
    private static (Command Cmd, string[] PathArgs) BuildGitPathCommand(IEnumerable<string> paths, string? workingDirectory)
    {
        var cmd = Cli.Wrap("git");
        if (workingDirectory != null)
            cmd = cmd.WithWorkingDirectory(workingDirectory);

        var pathArgs = paths.Select(path =>
            workingDirectory != null && Path.IsPathRooted(path)
                ? Path.GetRelativePath(workingDirectory, path)
                : path).ToArray();

        return (cmd, pathArgs);
    }

    public static Task<IReadOnlyList<string>> GetUncommittedChangesInPathAsync(string path, string? workingDirectory = null, SubprocessCapture? capture = null, CancellationToken cancellationToken = default) =>
        GetUncommittedChangesInPathAsync([path], workingDirectory, capture, cancellationToken);

    // Single git invocation covering every given path — callers that need to scope both a clean-check and a
    // cache-key to the same explicit path set (e.g. DeployCommand's deployment-input list) must never shell
    // out per-path, since that would let the two scopes drift independently.
    public static async Task<IReadOnlyList<string>> GetUncommittedChangesInPathAsync(IEnumerable<string> paths, string? workingDirectory = null, SubprocessCapture? capture = null, CancellationToken cancellationToken = default)
    {
        var (cmd, pathArgs) = BuildGitPathCommand(paths, workingDirectory);

        var finalCmd = cmd.WithArguments(args =>
        {
            args.Add("status").Add("--porcelain").Add("--");
            foreach (var pathArg in pathArgs) args.Add(pathArg);
        });
        var result = await (capture?.Apply(finalCmd) ?? finalCmd)
                           .ExecuteBufferedAsync(cancellationToken);

        return result.StandardOutput
                     .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(line => line[3..]) // porcelain v1: "XY filename" — 2-char status + space
                     .ToList();
    }

    public static Task<string?> GetLastCommitShaForPathAsync(string path, string? workingDirectory = null, SubprocessCapture? capture = null, CancellationToken cancellationToken = default) =>
        GetLastCommitShaForPathAsync([path], workingDirectory, capture, cancellationToken);

    // Returns the single most-recent commit touching ANY of the given paths (`git log -1 -- path1 path2`
    // already resolves to exactly that) — one process call, so a multi-path cache key always resolves to
    // one SHA.
    public static async Task<string?> GetLastCommitShaForPathAsync(IEnumerable<string> paths, string? workingDirectory = null, SubprocessCapture? capture = null, CancellationToken cancellationToken = default)
    {
        var (cmd, pathArgs) = BuildGitPathCommand(paths, workingDirectory);

        var finalCmd = cmd.WithArguments(args =>
                          {
                              args.Add("log").Add("-1").Add("--format=%H").Add("--");
                              foreach (var pathArg in pathArgs) args.Add(pathArg);
                          })
                          .WithValidation(CommandResultValidation.None);
        var result = await (capture?.Apply(finalCmd, suppressErrors: true) ?? finalCmd)
                           .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0) return null;
        var sha = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    public static async Task CreateTagAsync(string tagName, string? workingDirectory, CancellationToken cancellationToken = default)
    {
        var cmd = Cli.Wrap("git");
        if (workingDirectory != null)
            cmd = cmd.WithWorkingDirectory(workingDirectory);

        var result = await cmd
                           .WithArguments(args => args.Add("tag").Add(tagName))
                           .WithValidation(CommandResultValidation.None)
                           .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
            throw new FlowlineException(ExitCode.GeneralError, $"Failed to create git tag '{tagName}'. Tag may already exist — use --no-tag or remove it with: git tag -d {tagName}");
    }

    public static async Task AssertGitRepoAsync(string rootFolder, SubprocessCapture capture, bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Path.Combine(rootFolder, ".git")))
            throw new FlowlineException(ExitCode.ConfigInvalid, "No Git repo found. Run 'git init' or 'git clone' first.");

        AnsiConsole.MarkupLine("You're in a Git repo");

        // Check if remote URL is configured
        (string? remoteName, string? remoteUrl) = await GetRemoteUrlAsync(capture, cancellationToken);
        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            if (verbose)
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]Remote URL: [link]{remoteUrl}[/] ({remoteName})[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No remote configured — run 'git remote add <name> <url>' to set one up.[/]");
        }
    }
}
