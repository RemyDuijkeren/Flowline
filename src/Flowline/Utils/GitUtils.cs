using CliWrap;
using CliWrap.Buffered;
using Spectre.Console;

namespace Flowline;

public static class GitUtils
{
    public static async Task<string> AssertGitInstalledAsync()
    {
        try
        {
            var result = await Cli.Wrap("git")
                                  .WithArguments("--version")
                                  .ExecuteBufferedAsync();

            // Extract the version from the output (format: "git version X.Y.Z")
            var output = result.StandardOutput.Trim();
            if (output.StartsWith("git version "))
            {
                return output.Substring("git version ".Length);
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

    public static async Task<(string? remoteName, string? remoteUrl)> GetRemoteUrlAsync()
    {
        // Retrieve remote of the current branch and show it
        string? remoteName = null;
        string? remoteUrl = null;
        try
        {
            // Get upstream ref of the current branch (e.g., "origin/main")
            var upstreamResult = await Cli.Wrap("git")
                                          .WithArguments("rev-parse --abbrev-ref --symbolic-full-name @{u}")
                                          .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                                          .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                          .ExecuteBufferedAsync();

            var upstream = upstreamResult.StandardOutput.Trim();

            if (!string.IsNullOrWhiteSpace(upstream) && upstream.Contains('/'))
            {
                remoteName = upstream.Split('/')[0].Trim();
            }

            if (!string.IsNullOrWhiteSpace(remoteName))
            {
                var remoteUrlResult = await Cli.Wrap("git")
                                               .WithArguments(args => args.Add("remote").Add("get-url").Add(remoteName))
                                               .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                                               .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                               .ExecuteBufferedAsync();

                if (remoteUrlResult.ExitCode == 0)
                    remoteUrl = remoteUrlResult.StandardOutput.Trim();
            }

            // Fallback: try origin
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                var originResult = await Cli.Wrap("git")
                                            .WithArguments("remote get-url origin")
                                            .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                                            .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                            .ExecuteBufferedAsync();

                if (originResult.ExitCode == 0)
                    remoteUrl = originResult.StandardOutput.Trim();
            }

            // Fallback: first available remote
            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                var remotesResult = await Cli.Wrap("git")
                                             .WithArguments("remote")
                                             .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                                             .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                             .ExecuteBufferedAsync();

                var firstRemote = remotesResult.StandardOutput
                                               .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                               .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(firstRemote))
                {
                    var firstUrlResult = await Cli.Wrap("git")
                                                  .WithArguments(args => args.Add("remote").Add("get-url").Add(firstRemote))
                                                  .ExecuteBufferedAsync();

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
}
