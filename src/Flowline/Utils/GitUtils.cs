using CliWrap;
using CliWrap.Buffered;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline;

public static class GitUtils
{
    public static async Task<string> AssertGitInstalledAsync(bool verbose = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Cli.Wrap("git")
                                  .WithArguments("--version")
                                  .WithToolExecutionLog(verbose)
                                  .ExecuteBufferedAsync(cancellationToken);

            // Extract the version from the output (format: "git version X.Y.Z")
            var output = result.StandardOutput.Trim();
            if (output.StartsWith("git version "))
            {
                string gitVersion = output.Substring("git version ".Length);
                AnsiConsole.MarkupLine("Git checked out");
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
            AnsiConsole.MarkupLine("[red]Git (git) is not installed or not in PATH. Please install: https://git-scm.com/.[/]");
            Environment.Exit(1);
            return string.Empty; // This line will never be reached due to Environment.Exit
        }
    }

    public static async Task<(string? remoteName, string? remoteUrl)> GetRemoteUrlAsync(bool verbose = true, CancellationToken cancellationToken = default)
    {
        // Retrieve remote of the current branch and show it
        string? remoteName = null;
        string? remoteUrl = null;
        try
        {
            // Get upstream ref of the current branch (e.g., "origin/main")
            var upstreamResult = await Cli.Wrap("git")
                                          .WithArguments("rev-parse --abbrev-ref --symbolic-full-name @{u}")
                                          .WithToolExecutionLog(verbose)
                                          .ExecuteBufferedAsync(cancellationToken);

            var upstream = upstreamResult.StandardOutput.Trim();

            if (!string.IsNullOrWhiteSpace(upstream) && upstream.Contains('/'))
            {
                remoteName = upstream.Split('/')[0].Trim();
            }

            if (!string.IsNullOrWhiteSpace(remoteName))
            {
                var remoteUrlResult = await Cli.Wrap("git")
                                               .WithArguments(args => args.Add("remote").Add("get-url").Add(remoteName))
                                               .WithToolExecutionLog(verbose)
                                               .ExecuteBufferedAsync(cancellationToken);

                if (remoteUrlResult.ExitCode == 0)
                    remoteUrl = remoteUrlResult.StandardOutput.Trim();
            }

            // Fallback: try origin
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                var originResult = await Cli.Wrap("git")
                                            .WithArguments("remote get-url origin")
                                            .WithToolExecutionLog(verbose)
                                            .ExecuteBufferedAsync(cancellationToken);

                if (originResult.ExitCode == 0)
                    remoteUrl = originResult.StandardOutput.Trim();
            }

            // Fallback: first available remote
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                var remotesResult = await Cli.Wrap("git")
                                             .WithArguments("remote")
                                             .WithToolExecutionLog(verbose)
                                             .ExecuteBufferedAsync(cancellationToken);

                var firstRemote = remotesResult.StandardOutput
                                               .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                               .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(firstRemote))
                {
                    var firstUrlResult = await Cli.Wrap("git")
                                                  .WithArguments(args => args.Add("remote").Add("get-url").Add(firstRemote))
                                                  .WithToolExecutionLog(verbose)
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

    public static async Task<bool> IsRepoCleanAsync(bool verbose = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Cli.Wrap("git")
                                  .WithArguments("status --porcelain")
                                  .WithToolExecutionLog(verbose)
                                  .ExecuteBufferedAsync(cancellationToken);

            return string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task AssertRepoCleanAsync(bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (!await IsRepoCleanAsync(verbose, cancellationToken))
        {
            AnsiConsole.MarkupLine("[red]Uncommitted changes found in Git repository. Please commit or stash your changes before deploying.[/]");
            Environment.Exit(1);
        }
    }

    public static async Task AssertGitRepoAsync(string rootFolder, bool verbose = true, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Path.Combine(rootFolder, ".git")))
        {
            AnsiConsole.MarkupLine("[red]No git repository found. Please run 'git init' or 'git clone' first.[/]");
            Environment.Exit(1);
            return;
        }

        AnsiConsole.MarkupLine("Current folder is in Git territory");

        // Check if remote URL is configured
        (string? remoteName, string? remoteUrl) = await GetRemoteUrlAsync(verbose, cancellationToken);
        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            if (verbose)
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]Remote URL: [link]{remoteUrl}[/] ({remoteName})[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No remote configured for current Git repository. Please configure a remote URL using 'git remote add <name> <url>'.[/]");
        }
    }
}
